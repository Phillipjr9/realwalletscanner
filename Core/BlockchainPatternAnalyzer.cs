using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Analyzes blockchain patterns to find active wallets and transaction histories
    /// Focuses search on addresses with actual activity instead of random empty addresses
    /// </summary>
    public class BlockchainPatternAnalyzer
    {
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private readonly AppSettings _settings;

        public class WalletMetadata
        {
            public string Address { get; set; }
            public DateTime? FirstSeen { get; set; }
            public DateTime? LastActivity { get; set; }
            public int TransactionCount { get; set; }
            public decimal TotalReceived { get; set; }
            public decimal TotalSent { get; set; }
            public decimal CurrentBalance { get; set; }
            public string LikelyWalletSoftware { get; set; }
            public List<string> AddressPattern { get; set; } = new List<string>();
            public string DerivationPathHint { get; set; }
        }

        public class ActiveAddressFilter
        {
            public decimal MinBalance { get; set; } = 0.0001m;
            public DateTime? LastActivityAfter { get; set; }
            public int MinTransactions { get; set; } = 1;
            public bool OnlyWithBalance { get; set; } = true;
        }

        public BlockchainPatternAnalyzer()
        {
            _settings = AppSettings.Load();
        }

        /// <summary>
        /// Analyzes wallet behavior and metadata from blockchain
        /// </summary>
        public async Task<WalletMetadata> AnalyzeWalletBehavior(string address)
        {
            var metadata = new WalletMetadata { Address = address };

            try
            {
                if (address.StartsWith("1") || address.StartsWith("3") || address.StartsWith("bc1"))
                {
                    // Bitcoin analysis
                    metadata = await AnalyzeBitcoinAddress(address);
                }
                else if (address.StartsWith("0x"))
                {
                    // Ethereum analysis
                    metadata = await AnalyzeEthereumAddress(address);
                }

                // Infer wallet software from patterns
                metadata.LikelyWalletSoftware = InferWalletSoftware(metadata);
                metadata.DerivationPathHint = InferDerivationPath(metadata);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Error analyzing wallet: {ex.Message}");
            }

            return metadata;
        }

        /// <summary>
        /// Finds active address patterns matching partial address across watchlist, database, and related graph
        /// </summary>
        public async Task<List<string>> FindActiveAddressPatterns(
            string partialAddress,
            ActiveAddressFilter filter = null)
        {
            filter ??= new ActiveAddressFilter();
            var matchingAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(partialAddress))
                return matchingAddresses.ToList();

            string cleanPattern = partialAddress.Trim().Replace("*", "").Replace("?", "");
            DebugLogger.Log($"Searching for addresses matching pattern: {partialAddress}");

            try
            {
                // 1. Check local Watchlist and Database addresses matching prefix/substring
                var watchlist = new AddressWatchlist();
                foreach (var entry in watchlist.GetAllAddresses())
                {
                    if (!string.IsNullOrEmpty(entry.Address) &&
                        entry.Address.Contains(cleanPattern, StringComparison.OrdinalIgnoreCase))
                    {
                        matchingAddresses.Add(entry.Address);
                    }
                }

                // 2. If partialAddress is a full valid address, expand its 1-hop related addresses and filter
                if (cleanPattern.Length >= 26)
                {
                    var related = await FindRelatedAddresses(cleanPattern);
                    foreach (var addr in related)
                    {
                        var meta = await AnalyzeWalletBehavior(addr);
                        if (meta == null) continue;
                        if (filter.OnlyWithBalance && meta.CurrentBalance < filter.MinBalance) continue;
                        if (meta.TransactionCount < filter.MinTransactions) continue;
                        if (filter.LastActivityAfter.HasValue && meta.LastActivity.HasValue &&
                            meta.LastActivity.Value < filter.LastActivityAfter.Value) continue;

                        matchingAddresses.Add(addr);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[FindActiveAddressPatterns] Error: {ex.Message}");
            }

            return matchingAddresses.ToList();
        }

        /// <summary>
        /// Analyzes Bitcoin address transaction history with automatic key-free fallback to Blockstream & Mempool.space
        /// </summary>
        private async Task<WalletMetadata> AnalyzeBitcoinAddress(string address)
        {
            var metadata = new WalletMetadata { Address = address };

            try
            {
                if (!string.IsNullOrEmpty(_settings.BlockCypherApiKey))
                {
                    var url = $"https://api.blockcypher.com/v1/btc/main/addrs/{address}/full?token={_settings.BlockCypherApiKey}";
                    var response = await _httpClient.GetStringAsync(url);
                    dynamic data = JsonConvert.DeserializeObject(response);

                    if (data != null)
                    {
                        metadata.CurrentBalance = (decimal)(data.final_balance ?? 0) / 100000000m;
                        metadata.TotalReceived = (decimal)(data.total_received ?? 0) / 100000000m;
                        metadata.TotalSent = (decimal)(data.total_sent ?? 0) / 100000000m;
                        metadata.TransactionCount = (int)(data.n_tx ?? 0);

                        if (data.txs != null && data.txs.Count > 0)
                        {
                            var transactions = ((IEnumerable<dynamic>)data.txs).ToList();
                            var firstTx = transactions.LastOrDefault();
                            if (firstTx?.confirmed != null)
                                metadata.FirstSeen = DateTime.Parse(firstTx.confirmed.ToString());

                            var lastTx = transactions.FirstOrDefault();
                            if (lastTx?.confirmed != null)
                                metadata.LastActivity = DateTime.Parse(lastTx.confirmed.ToString());

                            metadata.AddressPattern = AnalyzeBitcoinTransactionPatterns(transactions);
                        }
                        return metadata;
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"BlockCypher analysis failed, falling back to Esplora: {ex.Message}");
            }

            // Key-free fallback: Blockstream Esplora / Mempool.space API
            string[] esploraBases = { "https://blockstream.info/api", "https://mempool.space/api" };
            foreach (var baseUrl in esploraBases)
            {
                try
                {
                    var addrJson = await _httpClient.GetStringAsync($"{baseUrl}/address/{address}");
                    var addrObj = JObject.Parse(addrJson);
                    var chainStats = addrObj["chain_stats"];
                    if (chainStats != null)
                    {
                        long funded = chainStats["funded_txo_sum"]?.Value<long>() ?? 0L;
                        long spent = chainStats["spent_txo_sum"]?.Value<long>() ?? 0L;
                        int txCount = chainStats["tx_count"]?.Value<int>() ?? 0;

                        metadata.TotalReceived = funded / 100_000_000m;
                        metadata.TotalSent = spent / 100_000_000m;
                        metadata.CurrentBalance = Math.Max(0m, (funded - spent) / 100_000_000m);
                        metadata.TransactionCount = txCount;
                    }

                    if (metadata.TransactionCount > 0)
                    {
                        var txsJson = await _httpClient.GetStringAsync($"{baseUrl}/address/{address}/txs");
                        var txsArr = JArray.Parse(txsJson);
                        if (txsArr.Count > 0)
                        {
                            long? lastTime = txsArr.First?["status"]?["block_time"]?.Value<long>();
                            long? firstTime = txsArr.Last?["status"]?["block_time"]?.Value<long>();
                            if (firstTime.HasValue && firstTime.Value > 0)
                                metadata.FirstSeen = DateTimeOffset.FromUnixTimeSeconds(firstTime.Value).UtcDateTime;
                            if (lastTime.HasValue && lastTime.Value > 0)
                                metadata.LastActivity = DateTimeOffset.FromUnixTimeSeconds(lastTime.Value).UtcDateTime;

                            if (txsArr.Any(tx => (tx["vout"] as JArray)?.Count > 1))
                                metadata.AddressPattern.Add("Uses change addresses (HD wallet)");
                            if (metadata.TransactionCount > 100)
                                metadata.AddressPattern.Add("High activity (exchange or merchant)");
                            else if (metadata.TransactionCount < 10)
                                metadata.AddressPattern.Add("Low activity (personal wallet)");
                        }
                    }

                    break;
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"Esplora ({baseUrl}) Bitcoin analysis fallback error: {ex.Message}");
                }
            }

            return metadata;
        }

        private static decimal WeiToEthSafe(string weiStr)
        {
            if (string.IsNullOrWhiteSpace(weiStr) || !BigInteger.TryParse(weiStr.Trim(), out var wei) || wei <= 0)
                return 0m;
            var whole = BigInteger.DivRem(wei, new BigInteger(1_000_000_000_000_000_000UL), out var rem);
            if (whole > (BigInteger)decimal.MaxValue)
                return decimal.MaxValue;
            return (decimal)whole + ((decimal)rem / 1_000_000_000_000_000_000m);
        }

        /// <summary>
        /// Analyzes Ethereum address transaction history with BigInteger Wei safety and key-free Blockscout fallback
        /// </summary>
        private async Task<WalletMetadata> AnalyzeEthereumAddress(string address)
        {
            var metadata = new WalletMetadata { Address = address };

            try
            {
                string txCountUrl = !string.IsNullOrEmpty(_settings.EtherscanApiKey)
                    ? $"https://api.etherscan.io/api?module=account&action=txlist&address={address}&startblock=0&endblock=99999999&sort=asc&apikey={_settings.EtherscanApiKey}"
                    : $"https://eth.blockscout.com/api?module=account&action=txlist&address={address}&startblock=0&endblock=99999999&sort=asc";

                var response = await _httpClient.GetStringAsync(txCountUrl);
                dynamic data = JsonConvert.DeserializeObject(response);

                if (data?.status == "1" && data.result != null)
                {
                    var transactions = ((IEnumerable<dynamic>)data.result).ToList();
                    metadata.TransactionCount = transactions.Count;

                    if (transactions.Count > 0)
                    {
                        var firstTx = transactions.First();
                        metadata.FirstSeen = DateTimeOffset.FromUnixTimeSeconds((long)firstTx.timeStamp).DateTime;

                        var lastTx = transactions.Last();
                        metadata.LastActivity = DateTimeOffset.FromUnixTimeSeconds((long)lastTx.timeStamp).DateTime;

                        foreach (var tx in transactions)
                        {
                            var value = WeiToEthSafe(tx.value?.ToString());
                            if (tx.from?.ToString().ToLowerInvariant() == address.ToLowerInvariant())
                                metadata.TotalSent += value;
                            else
                                metadata.TotalReceived += value;
                        }

                        metadata.AddressPattern = AnalyzeEthereumTransactionPatterns(transactions);
                    }
                }

                string balanceUrl = !string.IsNullOrEmpty(_settings.EtherscanApiKey)
                    ? $"https://api.etherscan.io/api?module=account&action=balance&address={address}&tag=latest&apikey={_settings.EtherscanApiKey}"
                    : $"https://eth.blockscout.com/api?module=account&action=balance&address={address}";

                var balanceResponse = await _httpClient.GetStringAsync(balanceUrl);
                dynamic balanceData = JsonConvert.DeserializeObject(balanceResponse);

                if (balanceData?.status == "1" && balanceData.result != null)
                {
                    metadata.CurrentBalance = WeiToEthSafe(balanceData.result.ToString());
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Error analyzing Ethereum address: {ex.Message}");
            }

            return metadata;
        }

        /// <summary>
        /// Analyzes Bitcoin transaction patterns to infer wallet type
        /// </summary>
        private List<string> AnalyzeBitcoinTransactionPatterns(List<dynamic> transactions)
        {
            var patterns = new List<string>();

            try
            {
                // Analyze input/output patterns
                var hasChangeOutputs = transactions.Any(tx => 
                    tx.outputs != null && ((IEnumerable<dynamic>)tx.outputs).Count() > 1);

                if (hasChangeOutputs)
                    patterns.Add("Uses change addresses (HD wallet)");

                // Check for round numbers (indicates manual sends)
                var hasRoundAmounts = transactions.Any(tx =>
                {
                    if (tx.outputs == null) return false;
                    foreach (var output in (IEnumerable<dynamic>)tx.outputs)
                    {
                        var value = (long)(output.value ?? 0);
                        return value % 100000000 == 0; // Round BTC amounts
                    }
                    return false;
                });

                if (hasRoundAmounts)
                    patterns.Add("Uses round amounts (manual transactions)");

                // Check transaction frequency
                if (transactions.Count > 100)
                    patterns.Add("High activity (exchange or merchant)");
                else if (transactions.Count < 10)
                    patterns.Add("Low activity (personal wallet)");

                // Check for batch transactions
                var hasBatchSends = transactions.Any(tx =>
                    tx.outputs != null && ((IEnumerable<dynamic>)tx.outputs).Count() > 5);

                if (hasBatchSends)
                    patterns.Add("Batch transactions (exchange or payment processor)");
            }
            catch { }

            return patterns;
        }

        /// <summary>
        /// Analyzes Ethereum transaction patterns
        /// </summary>
        private List<string> AnalyzeEthereumTransactionPatterns(List<dynamic> transactions)
        {
            var patterns = new List<string>();

            try
            {
                // Check for contract interactions
                var hasContractInteractions = transactions.Any(tx => 
                    tx.input != null && tx.input.ToString() != "0x");

                if (hasContractInteractions)
                    patterns.Add("Interacts with smart contracts (DeFi user)");

                // Check for token transfers
                var hasInternalTxs = transactions.Any(tx => tx.isError == "0");
                if (hasInternalTxs)
                    patterns.Add("Has successful transactions");

                // Analyze gas prices (wallet software fingerprint)
                var gasPrices = transactions
                    .Where(tx => tx.gasPrice != null)
                    .Select(tx => long.Parse(tx.gasPrice.ToString()))
                    .ToList();

                if (gasPrices.Any())
                {
                    var avgGas = gasPrices.Average(g => (double)g);
                    if (avgGas > 50000000000) // > 50 Gwei
                        patterns.Add("Uses high gas prices (priority transactions)");
                    else if (avgGas < 10000000000) // < 10 Gwei
                        patterns.Add("Uses low gas prices (cost-conscious)");
                }

                // Transaction frequency
                if (transactions.Count > 1000)
                    patterns.Add("Very high activity (bot/exchange)");
                else if (transactions.Count < 10)
                    patterns.Add("Low activity (HODLer)");
            }
            catch { }

            return patterns;
        }

        /// <summary>
        /// Infers likely wallet software from metadata
        /// </summary>
        private string InferWalletSoftware(WalletMetadata metadata)
        {
            if (metadata.AddressPattern.Any(p => p.Contains("DeFi")))
                return "MetaMask or Web3 Wallet";

            if (metadata.AddressPattern.Any(p => p.Contains("exchange")))
                return "Exchange Wallet";

            if (metadata.AddressPattern.Any(p => p.Contains("HD wallet")))
            {
                if (metadata.FirstSeen.HasValue)
                {
                    if (metadata.FirstSeen.Value.Year < 2016)
                        return "Electrum or Bitcoin Core";
                    else if (metadata.FirstSeen.Value.Year < 2018)
                        return "Exodus or Jaxx";
                    else
                        return "Modern HD Wallet (Ledger/Trezor/Trust)";
                }
            }

            return "Unknown";
        }

        /// <summary>
        /// Infers likely derivation path from metadata
        /// </summary>
        private string InferDerivationPath(WalletMetadata metadata)
        {
            if (metadata.Address.StartsWith("bc1"))
                return "m/84'/0'/0'/0/0 (BIP84 Native SegWit)";
            else if (metadata.Address.StartsWith("3"))
                return "m/49'/0'/0'/0/0 (BIP49 SegWit)";
            else if (metadata.Address.StartsWith("1"))
            {
                if (metadata.FirstSeen.HasValue && metadata.FirstSeen.Value.Year < 2016)
                    return "m/0/0 (Electrum Legacy)";
                else
                    return "m/44'/0'/0'/0/0 (BIP44 Legacy)";
            }
            else if (metadata.Address.StartsWith("0x"))
                return "m/44'/60'/0'/0/0 (Ethereum)";

            return "Unknown";
        }

        /// <summary>
        /// Finds related addresses by parsing transaction inputs/outputs
        /// </summary>
        public async Task<List<string>> FindRelatedAddresses(string seedAddress)
        {
            var relatedAddresses = new HashSet<string>();

            try
            {
                if (seedAddress.StartsWith("1") || seedAddress.StartsWith("3") || seedAddress.StartsWith("bc1"))
                {
                    // Bitcoin: use blockchain.info to get transaction addresses
                    var url = $"https://blockchain.info/rawaddr/{seedAddress}?limit=50";
                    var response = await _httpClient.GetStringAsync(url);
                    dynamic data = JsonConvert.DeserializeObject(response);

                    if (data?.txs != null)
                    {
                        foreach (var tx in (IEnumerable<dynamic>)data.txs)
                        {
                            // Extract input addresses (co-input = same wallet)
                            if (tx.inputs != null)
                            {
                                foreach (var input in (IEnumerable<dynamic>)tx.inputs)
                                {
                                    var addr = input?.prev_out?.addr?.ToString();
                                    if (!string.IsNullOrEmpty(addr) && addr != seedAddress)
                                        relatedAddresses.Add(addr);
                                }
                            }
                            // Extract output addresses (recipients/change)
                            if (tx.@out != null)
                            {
                                foreach (var output in (IEnumerable<dynamic>)tx.@out)
                                {
                                    var addr = output?.addr?.ToString();
                                    if (!string.IsNullOrEmpty(addr) && addr != seedAddress)
                                        relatedAddresses.Add(addr);
                                }
                            }
                        }
                    }
                }
                else if (seedAddress.StartsWith("0x") && !string.IsNullOrEmpty(_settings.EtherscanApiKey))
                {
                    // Ethereum: use Etherscan to get counterparty addresses
                    var url = $"https://api.etherscan.io/api?module=account&action=txlist&address={seedAddress}&startblock=0&endblock=99999999&page=1&offset=50&sort=desc&apikey={_settings.EtherscanApiKey}";
                    var response = await _httpClient.GetStringAsync(url);
                    dynamic data = JsonConvert.DeserializeObject(response);

                    if (data?.status == "1" && data.result != null)
                    {
                        foreach (var tx in (IEnumerable<dynamic>)data.result)
                        {
                            var from = tx?.from?.ToString();
                            var to = tx?.to?.ToString();
                            if (!string.IsNullOrEmpty(from) && from.ToLower() != seedAddress.ToLower())
                                relatedAddresses.Add(from);
                            if (!string.IsNullOrEmpty(to) && to.ToLower() != seedAddress.ToLower())
                                relatedAddresses.Add(to);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Error finding related addresses: {ex.Message}");
            }

            return relatedAddresses.ToList();
        }

        /// <summary>
        /// Checks if address appears to be active/funded
        /// </summary>
        public async Task<bool> IsActiveAddress(string address, ActiveAddressFilter filter = null)
        {
            filter ??= new ActiveAddressFilter();

            try
            {
                var metadata = await AnalyzeWalletBehavior(address);

                // Check balance
                if (filter.OnlyWithBalance && metadata.CurrentBalance < filter.MinBalance)
                    return false;

                // Check transaction count
                if (metadata.TransactionCount < filter.MinTransactions)
                    return false;

                // Check last activity
                if (filter.LastActivityAfter.HasValue && 
                    metadata.LastActivity.HasValue &&
                    metadata.LastActivity < filter.LastActivityAfter)
                    return false;

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
