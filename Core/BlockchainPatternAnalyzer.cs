using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

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
        /// Finds active address patterns matching partial address
        /// </summary>
        public async Task<List<string>> FindActiveAddressPatterns(
            string partialAddress,
            ActiveAddressFilter filter = null)
        {
            filter ??= new ActiveAddressFilter();
            var matchingAddresses = new List<string>();

            // This would require blockchain explorer API with pattern matching
            // For now, we'll focus on transaction graph analysis
            DebugLogger.Log($"Searching for addresses matching pattern: {partialAddress}");

            // Implementation would depend on specific blockchain explorer APIs
            // BlockCypher, Blockchain.com, etc. offer pattern search

            return matchingAddresses;
        }

        /// <summary>
        /// Analyzes Bitcoin address transaction history
        /// </summary>
        private async Task<WalletMetadata> AnalyzeBitcoinAddress(string address)
        {
            var metadata = new WalletMetadata { Address = address };

            try
            {
                if (string.IsNullOrEmpty(_settings.BlockCypherApiKey))
                {
                    DebugLogger.Log("BlockCypher API key required for address analysis");
                    return metadata;
                }

                var url = $"https://api.blockcypher.com/v1/btc/main/addrs/{address}/full?token={_settings.BlockCypherApiKey}";
                var response = await _httpClient.GetStringAsync(url);
                dynamic data = JsonConvert.DeserializeObject(response);

                if (data != null)
                {
                    metadata.CurrentBalance = (decimal)(data.final_balance ?? 0) / 100000000m;
                    metadata.TotalReceived = (decimal)(data.total_received ?? 0) / 100000000m;
                    metadata.TotalSent = (decimal)(data.total_sent ?? 0) / 100000000m;
                    metadata.TransactionCount = (int)(data.n_tx ?? 0);

                    // Parse transaction dates
                    if (data.txs != null && data.txs.Count > 0)
                    {
                        var transactions = ((IEnumerable<dynamic>)data.txs).ToList();
                        
                        // First transaction
                        var firstTx = transactions.LastOrDefault();
                        if (firstTx?.confirmed != null)
                        {
                            metadata.FirstSeen = DateTime.Parse(firstTx.confirmed.ToString());
                        }

                        // Last transaction
                        var lastTx = transactions.FirstOrDefault();
                        if (lastTx?.confirmed != null)
                        {
                            metadata.LastActivity = DateTime.Parse(lastTx.confirmed.ToString());
                        }

                        // Analyze transaction patterns
                        metadata.AddressPattern = AnalyzeBitcoinTransactionPatterns(transactions);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Error analyzing Bitcoin address: {ex.Message}");
            }

            return metadata;
        }

        /// <summary>
        /// Analyzes Ethereum address transaction history
        /// </summary>
        private async Task<WalletMetadata> AnalyzeEthereumAddress(string address)
        {
            var metadata = new WalletMetadata { Address = address };

            try
            {
                if (string.IsNullOrEmpty(_settings.EtherscanApiKey))
                {
                    DebugLogger.Log("Etherscan API key required for address analysis");
                    return metadata;
                }

                // Get transaction count
                var txCountUrl = $"https://api.etherscan.io/api?module=account&action=txlist&address={address}&startblock=0&endblock=99999999&sort=asc&apikey={_settings.EtherscanApiKey}";
                var response = await _httpClient.GetStringAsync(txCountUrl);
                dynamic data = JsonConvert.DeserializeObject(response);

                if (data?.status == "1" && data.result != null)
                {
                    var transactions = ((IEnumerable<dynamic>)data.result).ToList();
                    metadata.TransactionCount = transactions.Count;

                    if (transactions.Count > 0)
                    {
                        // First transaction
                        var firstTx = transactions.First();
                        metadata.FirstSeen = DateTimeOffset.FromUnixTimeSeconds((long)firstTx.timeStamp).DateTime;

                        // Last transaction
                        var lastTx = transactions.Last();
                        metadata.LastActivity = DateTimeOffset.FromUnixTimeSeconds((long)lastTx.timeStamp).DateTime;

                        // Calculate totals
                        foreach (var tx in transactions)
                        {
                            var value = decimal.Parse(tx.value.ToString()) / 1000000000000000000m;
                            
                            if (tx.from.ToString().ToLower() == address.ToLower())
                                metadata.TotalSent += value;
                            else
                                metadata.TotalReceived += value;
                        }

                        // Analyze patterns
                        metadata.AddressPattern = AnalyzeEthereumTransactionPatterns(transactions);
                    }
                }

                // Get current balance
                var balanceUrl = $"https://api.etherscan.io/api?module=account&action=balance&address={address}&tag=latest&apikey={_settings.EtherscanApiKey}";
                var balanceResponse = await _httpClient.GetStringAsync(balanceUrl);
                dynamic balanceData = JsonConvert.DeserializeObject(balanceResponse);

                if (balanceData?.status == "1")
                {
                    metadata.CurrentBalance = decimal.Parse(balanceData.result.ToString()) / 1000000000000000000m;
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
