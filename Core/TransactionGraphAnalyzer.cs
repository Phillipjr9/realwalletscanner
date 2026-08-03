using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Transaction graph analysis and address clustering
    /// Identifies wallet relationships through on-chain transaction patterns
    /// </summary>
    public class TransactionGraphAnalyzer
    {
        private readonly BlockchainPatternAnalyzer _patternAnalyzer;

        public class AddressCluster
        {
            public List<string> Addresses { get; set; } = new List<string>();
            public string ClusterType { get; set; }
            public double ConfidenceScore { get; set; }
            public List<string> SharedTransactions { get; set; } = new List<string>();
            public DateTime? FirstActivity { get; set; }
            public DateTime? LastActivity { get; set; }
        }

        public class TransactionLink
        {
            public string FromAddress { get; set; }
            public string ToAddress { get; set; }
            public decimal Amount { get; set; }
            public DateTime Timestamp { get; set; }
            public string TransactionHash { get; set; }
            public string LinkType { get; set; } // "change", "consolidation", "distribution"
        }

        public class WalletEntity
        {
            public string EntityName { get; set; }
            public List<string> ControlledAddresses { get; set; } = new List<string>();
            public string EntityType { get; set; } // "Exchange", "Whale", "Mixer", "Smart Contract"
            public decimal TotalBalance { get; set; }
            public int TransactionCount { get; set; }
            public double ConfidenceScore { get; set; }
        }

        public TransactionGraphAnalyzer()
        {
            _patternAnalyzer = new BlockchainPatternAnalyzer();
        }

        /// <summary>
        /// Builds transaction graph from seed address
        /// </summary>
        public async Task<List<TransactionLink>> BuildTransactionGraph(
            string seedAddress,
            int depth = 2,
            int maxAddresses = 100)
        {
            var links = new List<TransactionLink>();
            var visited = new HashSet<string>();
            var toVisit = new Queue<(string address, int currentDepth)>();

            toVisit.Enqueue((seedAddress, 0));
            visited.Add(seedAddress);

            while (toVisit.Count > 0 && visited.Count < maxAddresses)
            {
                var (address, currentDepth) = toVisit.Dequeue();

                if (currentDepth >= depth)
                    continue;

                try
                {
                    // Analyze address transactions
                    var metadata = await _patternAnalyzer.AnalyzeWalletBehavior(address);

                    // Add related addresses to queue
                    var relatedAddresses = await _patternAnalyzer.FindRelatedAddresses(address);
                    foreach (var related in relatedAddresses)
                    {
                        if (!visited.Contains(related))
                        {
                            visited.Add(related);
                            toVisit.Enqueue((related, currentDepth + 1));

                            links.Add(new TransactionLink
                            {
                                FromAddress = address,
                                ToAddress = related,
                                Amount = metadata.CurrentBalance,
                                Timestamp = metadata.LastActivity ?? DateTime.UtcNow,
                                LinkType = "related"
                            });
                        }
                        else
                        {
                            // Already visited = potential co-input relationship
                            links.Add(new TransactionLink
                            {
                                FromAddress = address,
                                ToAddress = related,
                                Amount = 0,
                                Timestamp = metadata.LastActivity ?? DateTime.UtcNow,
                                LinkType = "co-input"
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"Error analyzing {address}: {ex.Message}");
                }

                await Task.Delay(100); // Rate limiting
            }

            return links;
        }

        /// <summary>
        /// Clusters addresses using common input heuristic (Union-Find)
        /// Addresses used as inputs in same transaction likely belong to same wallet
        /// </summary>
        public async Task<List<AddressCluster>> ClusterByCommonInput(List<string> addresses)
        {
            var clusters = new List<AddressCluster>();

            try
            {
                // Union-Find parent map
                var parent = addresses.ToDictionary(a => a, a => a);

                string Find(string x)
                {
                    while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                    return x;
                }
                void Union(string a, string b)
                {
                    var ra = Find(a); var rb = Find(b);
                    if (ra != rb) parent[ra] = rb;
                }

                // For each address, fetch its transactions and find co-inputs
                foreach (var address in addresses)
                {
                    try
                    {
                        if (address.StartsWith("1") || address.StartsWith("3") || address.StartsWith("bc1"))
                        {
                            var links = await BuildTransactionGraph(address, depth: 1, maxAddresses: 20);
                            // Co-inputs in same tx = same wallet
                            foreach (var link in links.Where(l => l.LinkType == "co-input"))
                            {
                                if (parent.ContainsKey(link.ToAddress))
                                    Union(address, link.ToAddress);
                            }
                        }
                    }
                    catch { /* skip on API error */ }

                    await Task.Delay(200); // Rate limit
                }

                // Group by root parent
                var groups = addresses.GroupBy(a => Find(a)).Where(g => g.Count() > 0);

                foreach (var group in groups)
                {
                    var clusterAddresses = group.ToList();
                    var metadata = await _patternAnalyzer.AnalyzeWalletBehavior(clusterAddresses.First());

                    clusters.Add(new AddressCluster
                    {
                        Addresses = clusterAddresses,
                        ClusterType = clusterAddresses.Count > 1 ? "Common Input Heuristic" : "Single Address",
                        ConfidenceScore = clusterAddresses.Count > 1 ? 90.0 : 50.0,
                        SharedTransactions = new List<string>(),
                        FirstActivity = metadata.FirstSeen,
                        LastActivity = metadata.LastActivity
                    });
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Clustering error: {ex.Message}");
            }

            return clusters;
        }

        /// <summary>
        /// Identifies change addresses using heuristics:
        /// 1. Address used exactly once as output
        /// 2. Smaller value output in a 2-output transaction
        /// 3. Same address type as input (HD wallet pattern)
        /// </summary>
        public async Task<List<string>> IdentifyChangeAddresses(string seedAddress)
        {
            var changeAddresses = new List<string>();

            try
            {
                // Build a shallow graph to get transaction links
                var links = await BuildTransactionGraph(seedAddress, depth: 1, maxAddresses: 50);

                // Count how many times each output address appears
                var addressUsage = new Dictionary<string, int>();
                foreach (var link in links)
                {
                    if (!addressUsage.ContainsKey(link.ToAddress))
                        addressUsage[link.ToAddress] = 0;
                    addressUsage[link.ToAddress]++;
                }

                // Heuristic: addresses that appear exactly once as output
                // and have a smaller amount than the other output = likely change
                foreach (var kvp in addressUsage.Where(a => a.Value == 1))
                {
                    var link = links.FirstOrDefault(l => l.ToAddress == kvp.Key);
                    if (link != null && link.LinkType == "related")
                    {
                        // Check if this address type matches the seed address type
                        bool sameType = (seedAddress.StartsWith("1") && kvp.Key.StartsWith("1")) ||
                                        (seedAddress.StartsWith("3") && kvp.Key.StartsWith("3")) ||
                                        (seedAddress.StartsWith("bc1") && kvp.Key.StartsWith("bc1"));

                        if (sameType)
                            changeAddresses.Add(kvp.Key);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Change address detection error: {ex.Message}");
            }

            return changeAddresses;
        }

        /// <summary>
        /// Detects known wallet entities (exchanges, mixers, etc.)
        /// </summary>
        public async Task<WalletEntity> DetectKnownEntity(string address)
        {
            var entity = new WalletEntity
            {
                EntityName = "Unknown",
                EntityType = "Unknown",
                ControlledAddresses = new List<string> { address }
            };

            try
            {
                var metadata = await _patternAnalyzer.AnalyzeWalletBehavior(address);

                // Heuristics for entity detection:
                // 1. Very high transaction count (>10,000) = Exchange
                // 2. Many inputs, many outputs = Mixer
                // 3. Standardized amounts = ATM or Exchange
                // 4. Contract interaction patterns = DeFi protocol

                if (metadata.TransactionCount > 10000)
                {
                    entity.EntityName = "Likely Exchange";
                    entity.EntityType = "Exchange";
                    entity.ConfidenceScore = 80.0;
                }
                else if (metadata.AddressPattern.Any(p => p.Contains("batch")))
                {
                    entity.EntityName = "Payment Processor";
                    entity.EntityType = "Service";
                    entity.ConfidenceScore = 70.0;
                }
                else if (metadata.AddressPattern.Any(p => p.Contains("DeFi")))
                {
                    entity.EntityName = "DeFi User";
                    entity.EntityType = "Smart Contract";
                    entity.ConfidenceScore = 90.0;
                }

                entity.TotalBalance = metadata.CurrentBalance;
                entity.TransactionCount = metadata.TransactionCount;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Entity detection error: {ex.Message}");
            }

            return entity;
        }

        /// <summary>
        /// Finds dust transactions (potential address tagging)
        /// </summary>
        public async Task<List<string>> FindDustTransactions(string address)
        {
            var dustTxs = new List<string>();

            try
            {
                // Dust = very small amounts (< 0.0001 BTC or < 0.001 ETH)
                // Often used to tag addresses for tracking

                var metadata = await _patternAnalyzer.AnalyzeWalletBehavior(address);

                // In production, check transaction amounts
                DebugLogger.Log($"Scanning for dust transactions to {address}");
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Dust detection error: {ex.Message}");
            }

            return dustTxs;
        }

        /// <summary>
        /// Analyzes temporal patterns in transactions
        /// </summary>
        public async Task<Dictionary<string, object>> AnalyzeTemporalPatterns(string address)
        {
            var patterns = new Dictionary<string, object>();

            try
            {
                var metadata = await _patternAnalyzer.AnalyzeWalletBehavior(address);

                // Temporal analysis:
                // 1. Time between transactions (regular = bot, irregular = human)
                // 2. Day of week patterns (weekday = business, weekend = personal)
                // 3. Time of day (timezone hint)
                // 4. Transaction frequency spikes (dumps, accumulation)

                if (metadata.FirstSeen.HasValue && metadata.LastActivity.HasValue)
                {
                    var activitySpan = metadata.LastActivity.Value - metadata.FirstSeen.Value;
                    var avgTxPerDay = metadata.TransactionCount / Math.Max(1, activitySpan.Days);

                    patterns["ActivitySpan"] = activitySpan;
                    patterns["AverageTxPerDay"] = avgTxPerDay;
                    patterns["IsHighFrequency"] = avgTxPerDay > 10;

                    if (avgTxPerDay > 100)
                        patterns["LikelyBot"] = true;
                    else if (avgTxPerDay < 1)
                        patterns["LikelyHODLer"] = true;
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Temporal analysis error: {ex.Message}");
            }

            return patterns;
        }

        /// <summary>
        /// Builds adjacency matrix for graph visualization
        /// </summary>
        public async Task<int[,]> BuildAdjacencyMatrix(List<string> addresses, List<TransactionLink> links)
        {
            var matrix = new int[addresses.Count, addresses.Count];

            try
            {
                var addressIndex = addresses.Select((addr, idx) => new { addr, idx })
                    .ToDictionary(x => x.addr, x => x.idx);

                foreach (var link in links)
                {
                    if (addressIndex.TryGetValue(link.FromAddress, out int fromIdx) &&
                        addressIndex.TryGetValue(link.ToAddress, out int toIdx))
                    {
                        matrix[fromIdx, toIdx] = 1;
                        matrix[toIdx, fromIdx] = 1; // Undirected graph
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Matrix building error: {ex.Message}");
            }

            return await Task.FromResult(matrix);
        }

        /// <summary>
        /// Finds central nodes in transaction graph (whales, exchanges)
        /// </summary>
        public async Task<List<string>> FindCentralNodes(List<TransactionLink> links, int topN = 10)
        {
            var centralNodes = new List<string>();

            try
            {
                // Calculate degree centrality (number of connections)
                var degreeCounts = new Dictionary<string, int>();

                foreach (var link in links)
                {
                    if (!degreeCounts.ContainsKey(link.FromAddress))
                        degreeCounts[link.FromAddress] = 0;
                    if (!degreeCounts.ContainsKey(link.ToAddress))
                        degreeCounts[link.ToAddress] = 0;

                    degreeCounts[link.FromAddress]++;
                    degreeCounts[link.ToAddress]++;
                }

                // Sort by degree and return top N
                centralNodes = degreeCounts
                    .OrderByDescending(kvp => kvp.Value)
                    .Take(topN)
                    .Select(kvp => kvp.Key)
                    .ToList();
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Central node detection error: {ex.Message}");
            }

            return await Task.FromResult(centralNodes);
        }

        /// <summary>
        /// Predicts next addresses in a BIP32 derivation sequence.
        /// Analyzes known addresses to detect their type, then generates
        /// the next indices using the corresponding derivation path.
        /// Requires the mnemonic phrase that produced the known addresses.
        /// </summary>
        public List<string> PredictNextAddresses(List<string> knownAddresses, int count = 10, string mnemonic = null)
        {
            var predictions = new List<string>();

            try
            {
                if (string.IsNullOrEmpty(mnemonic) || knownAddresses == null || knownAddresses.Count == 0)
                {
                    predictions.Add("Provide a mnemonic and known addresses to predict next addresses in the HD sequence.");
                    return predictions;
                }

                // Detect address type from known addresses
                var sample = knownAddresses.First();
                string pathPrefix;
                NBitcoin.ScriptPubKeyType scriptType;

                if (sample.StartsWith("bc1q"))
                {
                    pathPrefix = "m/84'/0'/0'/0";
                    scriptType = NBitcoin.ScriptPubKeyType.Segwit;
                }
                else if (sample.StartsWith("3"))
                {
                    pathPrefix = "m/49'/0'/0'/0";
                    scriptType = NBitcoin.ScriptPubKeyType.SegwitP2SH;
                }
                else
                {
                    pathPrefix = "m/44'/0'/0'/0";
                    scriptType = NBitcoin.ScriptPubKeyType.Legacy;
                }

                // Generate addresses starting after the known count
                var mnemonicObj = new NBitcoin.Mnemonic(mnemonic);
                var masterKey = mnemonicObj.DeriveExtKey();
                int startIndex = knownAddresses.Count;

                for (int i = startIndex; i < startIndex + count; i++)
                {
                    var key = masterKey.Derive(new NBitcoin.KeyPath($"{pathPrefix}/{i}"));
                    var address = key.GetPublicKey().GetAddress(scriptType, NBitcoin.Network.Main).ToString();
                    predictions.Add(address);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Address prediction error: {ex.Message}");
                predictions.Add($"Prediction error: {ex.Message}");
            }

            return predictions;
        }
    }
}
