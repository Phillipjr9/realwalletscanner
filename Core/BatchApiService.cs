using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RestSharp;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Batch API service for checking multiple addresses in single API calls
    /// Dramatically reduces API calls and improves throughput
    /// </summary>
    public class BatchApiService
    {
        private readonly AppSettings _settings;
        private readonly HttpClientPool _clientPool;
        private readonly RateLimiter _etherscanLimiter;
        private readonly RateLimiter _blockcypherLimiter;

        // Batch sizes per API (optimized for throughput)
        private const int EtherscanBatchSize = 20;      // Etherscan supports up to 20 addresses
        private const int BscScanBatchSize = 20;        // BSCScan supports up to 20 addresses  
        private const int PolygonScanBatchSize = 20;    // PolygonScan supports up to 20 addresses
        private const int BlockCypherBatchSize = 200;   // Blockchain.info multiaddr supports large batches
        private const int SolanaBatchSize = 100;        // Solana RPC supports batch requests
        private const int LitecoinBatchSize = 50;       // Blockcypher LTC batch
        private const int DogecoinBatchSize = 50;       // Blockcypher DOGE batch

        public BatchApiService(AppSettings settings = null)
        {
            _settings = settings ?? AppSettings.Load();
            _clientPool = HttpClientPool.Instance;
            
            // Rate limiters based on API tier
            _etherscanLimiter = new RateLimiter(string.IsNullOrEmpty(_settings.EtherscanApiKey) ? 1 : 5);
            _blockcypherLimiter = new RateLimiter(string.IsNullOrEmpty(_settings.BlockCypherApiKey) ? 3 : 30);
        }

        #region Ethereum Batch API

        /// <summary>
        /// Gets ETH balances for multiple addresses in a single API call
        /// </summary>
        public async Task<Dictionary<string, decimal>> GetEthereumBalancesBatch(
            IEnumerable<string> addresses,
            CancellationToken cancellationToken = default)
        {
            var results = new Dictionary<string, decimal>();
            var addressList = addresses.ToList();

            if (!addressList.Any())
                return results;

            // Process in batches of 20
            var batches = addressList
                .Select((addr, idx) => new { addr, idx })
                .GroupBy(x => x.idx / EtherscanBatchSize)
                .Select(g => g.Select(x => x.addr).ToList())
                .ToList();

            foreach (var batch in batches)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                await _etherscanLimiter.WaitForTokenAsync(cancellationToken);

                try
                {
                    var batchResults = await GetEthereumBatchInternal(batch, cancellationToken);
                    foreach (var kvp in batchResults)
                    {
                        results[kvp.Key] = kvp.Value;
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"Ethereum batch API error: {ex.Message}");
                    // Add zeros for failed addresses
                    foreach (var addr in batch)
                    {
                        if (!results.ContainsKey(addr))
                            results[addr] = 0;
                    }
                }
            }

            return results;
        }

        private async Task<Dictionary<string, decimal>> GetEthereumBatchInternal(
            List<string> addresses,
            CancellationToken cancellationToken)
        {
            var results = new Dictionary<string, decimal>();
            var addressParam = string.Join(",", addresses);

            // Use Etherscan V2 API with chainid=1 for mainnet
            var apiKey = _settings.EtherscanApiKey;
            var url = $"https://api.etherscan.io/v2/api?chainid=1&module=account&action=balancemulti&address={addressParam}&tag=latest";
            
            if (!string.IsNullOrEmpty(apiKey))
                url += $"&apikey={apiKey}";

            var client = _clientPool.GetClient("https://api.etherscan.io");
            var response = await client.GetAsync(url, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var data = JObject.Parse(json);

                if (data["status"]?.ToString() == "1" && data["result"] is JArray resultArray)
                {
                    foreach (var item in resultArray)
                    {
                        var address = item["account"]?.ToString()?.ToLower();
                        var balanceWei = item["balance"]?.ToString() ?? "0";

                        if (!string.IsNullOrEmpty(address) && System.Numerics.BigInteger.TryParse(balanceWei, out var wei))
                        {
                            var ethVal = (decimal)(wei / 1_000_000_000_000_000_000) + (decimal)(wei % 1_000_000_000_000_000_000) / 1_000_000_000_000_000_000m;
                            results[address] = ethVal;
                            if (ethVal == 0)
                            {
                                FundedAddressFilter.ETHEmpty.MarkAsEmpty(address);
                                GlobalMnemonicTracker.Instance.TryMarkAsScanned("addr:" + address);
                            }
                        }
                    }
                }
            }

            return results;
        }

        #endregion

        #region EVM Chains Batch API (Polygon, BSC)

        /// <summary>
        /// Gets balances for multiple addresses on Polygon
        /// </summary>
        public async Task<Dictionary<string, decimal>> GetPolygonBalancesBatch(
            IEnumerable<string> addresses,
            CancellationToken cancellationToken = default)
        {
            return await GetEvmBalancesBatch(
                addresses,
                "https://api.etherscan.io/v2/api?chainid=137",
                !string.IsNullOrEmpty(_settings.PolygonscanApiKey) ? _settings.PolygonscanApiKey : _settings.EtherscanApiKey,
                cancellationToken);
        }

        /// <summary>
        /// Gets balances for multiple addresses on BSC
        /// </summary>
        public async Task<Dictionary<string, decimal>> GetBscBalancesBatch(
            IEnumerable<string> addresses,
            CancellationToken cancellationToken = default)
        {
            return await GetEvmBalancesBatch(
                addresses,
                "https://api.etherscan.io/v2/api?chainid=56",
                !string.IsNullOrEmpty(_settings.BscscanApiKey) ? _settings.BscscanApiKey : _settings.EtherscanApiKey,
                cancellationToken);
        }

        private async Task<Dictionary<string, decimal>> GetEvmBalancesBatch(
            IEnumerable<string> addresses,
            string baseUrl,
            string apiKey,
            CancellationToken cancellationToken)
        {
            var results = new Dictionary<string, decimal>();
            var addressList = addresses.ToList();

            if (!addressList.Any())
                return results;

            var batches = addressList
                .Select((addr, idx) => new { addr, idx })
                .GroupBy(x => x.idx / 20)
                .Select(g => g.Select(x => x.addr).ToList())
                .ToList();

            foreach (var batch in batches)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var addressParam = string.Join(",", batch);
                    var separator = baseUrl.Contains("?") ? "&" : "?";
                    var url = $"{baseUrl}{separator}module=account&action=balancemulti&address={addressParam}&tag=latest";
                    
                    if (!string.IsNullOrEmpty(apiKey))
                        url += $"&apikey={apiKey}";

                    var client = _clientPool.GetClient(baseUrl);
                    var response = await client.GetAsync(url, cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync(cancellationToken);
                        var data = JObject.Parse(json);

                        if (data["status"]?.ToString() == "1" && data["result"] is JArray resultArray)
                        {
                            foreach (var item in resultArray)
                            {
                                var address = item["account"]?.ToString()?.ToLower();
                                var balanceWei = item["balance"]?.ToString() ?? "0";

                                if (!string.IsNullOrEmpty(address) && System.Numerics.BigInteger.TryParse(balanceWei, out var wei))
                                {
                                    results[address] = (decimal)(wei / 1_000_000_000_000_000_000) + (decimal)(wei % 1_000_000_000_000_000_000) / 1_000_000_000_000_000_000m;
                                }
                            }
                        }
                    }

                    // Rate limiting handled by _etherscanLimiter.WaitForTokenAsync() above
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"EVM batch API error: {ex.Message}");
                }
            }

            return results;
        }

        #endregion

        #region Bitcoin Batch API

        /// <summary>
        /// Gets BTC balances for multiple addresses using Blockchain.com API
        /// </summary>
        public async Task<Dictionary<string, decimal>> GetBitcoinBalancesBatch(
            IEnumerable<string> addresses,
            CancellationToken cancellationToken = default)
        {
            var results = new Dictionary<string, decimal>();
            var addressList = addresses.ToList();

            if (!addressList.Any())
                return results;

            // Blockchain.com supports up to 100 addresses per request
            var batches = addressList
                .Select((addr, idx) => new { addr, idx })
                .GroupBy(x => x.idx / BlockCypherBatchSize)
                .Select(g => g.Select(x => x.addr).ToList())
                .ToList();

            foreach (var batch in batches)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    // Use Blockchain.com's multiaddr API
                    var addressParam = string.Join("|", batch);
                    var url = $"https://blockchain.info/multiaddr?active={addressParam}&n=0";

                    var client = _clientPool.GetClient("https://blockchain.info");
                    var response = await client.GetAsync(url, cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync(cancellationToken);
                        var data = JObject.Parse(json);

                        if (data["addresses"] is JArray addressArray)
                        {
                            foreach (var item in addressArray)
                            {
                                var address = item["address"]?.ToString();
                                var balanceSatoshi = item["final_balance"]?.Value<long>() ?? 0;

                                if (!string.IsNullOrEmpty(address))
                                {
                                    var btcVal = balanceSatoshi / 100_000_000m;
                                    results[address] = btcVal;
                                    if (btcVal == 0)
                                    {
                                        FundedAddressFilter.BTCEmpty.MarkAsEmpty(address);
                                        GlobalMnemonicTracker.Instance.TryMarkAsScanned("addr:" + address.ToLowerInvariant());
                                    }
                                }
                            }
                        }
                    }

                    // Rate limiting handled by _blockcypherLimiter.WaitForTokenAsync() above
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"Bitcoin batch API error: {ex.Message}");
                }
            }

            return results;
        }

        #endregion

        #region Solana Batch API

        /// <summary>
        /// Gets SOL balances for multiple addresses using JSON-RPC batch
        /// </summary>
        public async Task<Dictionary<string, decimal>> GetSolanaBalancesBatch(
            IEnumerable<string> addresses,
            CancellationToken cancellationToken = default)
        {
            var results = new Dictionary<string, decimal>();
            var addressList = addresses.ToList();

            if (!addressList.Any())
                return results;

            // Build JSON-RPC batch request
            var batches = addressList
                .Select((addr, idx) => new { addr, idx })
                .GroupBy(x => x.idx / SolanaBatchSize)
                .Select(g => g.Select(x => x.addr).ToList())
                .ToList();

            var rpcUrl = !string.IsNullOrEmpty(_settings.QuickNodeApiKey)
                ? $"https://orbital-proud-tree.solana-mainnet.quiknode.pro/{_settings.QuickNodeApiKey}/"
                : "https://api.mainnet-beta.solana.com";

            foreach (var batch in batches)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var requests = batch.Select((addr, idx) => new
                    {
                        jsonrpc = "2.0",
                        id = idx,
                        method = "getBalance",
                        @params = new object[] { addr }
                    }).ToArray();

                    var requestBody = JsonConvert.SerializeObject(requests);

                    var client = _clientPool.GetClient(rpcUrl);
                    var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                    var response = await client.PostAsync(rpcUrl, content, cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync(cancellationToken);
                        var responses = JArray.Parse(json);

                        for (int i = 0; i < responses.Count && i < batch.Count; i++)
                        {
                            var address = batch[i];
                            var result = responses[i]["result"];
                            
                            if (result != null && result["value"] != null)
                            {
                                var lamports = result["value"].Value<long>();
                                results[address] = lamports / 1_000_000_000m; // Lamports to SOL
                            }
                            else
                            {
                                results[address] = 0;
                            }
                        }
                    }

                    // Rate limiting handled by rate limiter
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"Solana batch API error: {ex.Message}");
                }
            }

            return results;
        }

        #endregion

        #region Combined Batch Check

        /// <summary>
        /// Checks all balances for a wallet using batch APIs
        /// </summary>
        public async Task<Dictionary<string, decimal>> CheckAllBalancesBatch(
            CryptoWalletGenerator.WalletInfo wallet,
            bool scanBitcoin = true,
            bool scanEthereum = true,
            bool scanSolana = false,
            bool scanPolygon = false,
            bool scanBnb = false,
            CancellationToken cancellationToken = default)
        {
            var tasks = new List<Task<Dictionary<string, decimal>>>();
            var balances = new ConcurrentDictionary<string, decimal>();

            // Collect all addresses by chain
            if (scanBitcoin)
            {
                var btcAddresses = new List<string> { wallet.BitcoinAddress };
                if (!string.IsNullOrEmpty(wallet.BitcoinAddressSegWit))
                    btcAddresses.Add(wallet.BitcoinAddressSegWit);
                if (!string.IsNullOrEmpty(wallet.BitcoinAddressNativeSegWit))
                    btcAddresses.Add(wallet.BitcoinAddressNativeSegWit);

                tasks.Add(Task.Run(async () =>
                {
                    var results = await GetBitcoinBalancesBatch(btcAddresses, cancellationToken);
                    return results.ToDictionary(
                        kvp => $"BTC:{kvp.Key}",
                        kvp => kvp.Value);
                }, cancellationToken));
            }

            if (scanEthereum && !string.IsNullOrEmpty(wallet.EthereumAddress))
            {
                tasks.Add(Task.Run(async () =>
                {
                    var results = await GetEthereumBalancesBatch(
                        new[] { wallet.EthereumAddress },
                        cancellationToken);
                    return results.ToDictionary(
                        kvp => $"ETH:{kvp.Key}",
                        kvp => kvp.Value);
                }, cancellationToken));
            }

            if (scanSolana && !string.IsNullOrEmpty(wallet.SolanaAddress))
            {
                tasks.Add(Task.Run(async () =>
                {
                    var results = await GetSolanaBalancesBatch(
                        new[] { wallet.SolanaAddress },
                        cancellationToken);
                    return results.ToDictionary(
                        kvp => $"SOL:{kvp.Key}",
                        kvp => kvp.Value);
                }, cancellationToken));
            }

            if (scanPolygon && !string.IsNullOrEmpty(wallet.PolygonAddress))
            {
                tasks.Add(Task.Run(async () =>
                {
                    var results = await GetPolygonBalancesBatch(
                        new[] { wallet.PolygonAddress },
                        cancellationToken);
                    return results.ToDictionary(
                        kvp => $"MATIC:{kvp.Key}",
                        kvp => kvp.Value);
                }, cancellationToken));
            }

            if (scanBnb && !string.IsNullOrEmpty(wallet.BnbAddress))
            {
                tasks.Add(Task.Run(async () =>
                {
                    var results = await GetBscBalancesBatch(
                        new[] { wallet.BnbAddress },
                        cancellationToken);
                    return results.ToDictionary(
                        kvp => $"BNB:{kvp.Key}",
                        kvp => kvp.Value);
                }, cancellationToken));
            }

            // Wait for all tasks
            var allResults = await Task.WhenAll(tasks);

            // Combine results
            var combinedResults = new Dictionary<string, decimal>();
            foreach (var result in allResults)
            {
                foreach (var kvp in result)
                {
                    combinedResults[kvp.Key] = kvp.Value;
                }
            }

            return combinedResults;
        }

        /// <summary>
        /// Checks balances for multiple wallets efficiently using batching
        /// </summary>
        public async Task<Dictionary<string, Dictionary<string, decimal>>> CheckMultipleWalletsBatch(
            IEnumerable<CryptoWalletGenerator.WalletInfo> wallets,
            bool scanBitcoin = true,
            bool scanEthereum = true,
            bool scanSolana = false,
            bool scanPolygon = false,
            bool scanBnb = false,
            CancellationToken cancellationToken = default)
        {
            var walletList = wallets.ToList();
            var results = new Dictionary<string, Dictionary<string, decimal>>();

            // Collect all addresses by chain type
            var btcAddresses = new List<(string wallet, string address)>();
            var ethAddresses = new List<(string wallet, string address)>();
            var solAddresses = new List<(string wallet, string address)>();
            var maticAddresses = new List<(string wallet, string address)>();
            var bnbAddresses = new List<(string wallet, string address)>();

            foreach (var wallet in walletList)
            {
                var walletKey = wallet.Mnemonic ?? wallet.PrivateKey ?? wallet.BitcoinAddress;
                results[walletKey] = new Dictionary<string, decimal>();

                if (scanBitcoin)
                {
                    if (!string.IsNullOrEmpty(wallet.BitcoinAddress))
                        btcAddresses.Add((walletKey, wallet.BitcoinAddress));
                    if (!string.IsNullOrEmpty(wallet.BitcoinAddressSegWit))
                        btcAddresses.Add((walletKey, wallet.BitcoinAddressSegWit));
                    if (!string.IsNullOrEmpty(wallet.BitcoinAddressNativeSegWit))
                        btcAddresses.Add((walletKey, wallet.BitcoinAddressNativeSegWit));
                }

                if (scanEthereum && !string.IsNullOrEmpty(wallet.EthereumAddress))
                    ethAddresses.Add((walletKey, wallet.EthereumAddress));

                if (scanSolana && !string.IsNullOrEmpty(wallet.SolanaAddress))
                    solAddresses.Add((walletKey, wallet.SolanaAddress));

                if (scanPolygon && !string.IsNullOrEmpty(wallet.PolygonAddress))
                    maticAddresses.Add((walletKey, wallet.PolygonAddress));

                if (scanBnb && !string.IsNullOrEmpty(wallet.BnbAddress))
                    bnbAddresses.Add((walletKey, wallet.BnbAddress));
            }

            // Batch check each chain type
            var tasks = new List<Task>();

            if (btcAddresses.Any())
            {
                tasks.Add(Task.Run(async () =>
                {
                    var balances = await GetBitcoinBalancesBatch(
                        btcAddresses.Select(x => x.address).Distinct(),
                        cancellationToken);
                    
                    foreach (var (walletKey, address) in btcAddresses)
                    {
                        if (balances.TryGetValue(address, out var balance))
                        {
                            lock (results)
                            {
                                if (!results[walletKey].ContainsKey("BTC"))
                                    results[walletKey]["BTC"] = 0;
                                results[walletKey]["BTC"] += balance;
                            }
                        }
                    }
                }, cancellationToken));
            }

            if (ethAddresses.Any())
            {
                tasks.Add(Task.Run(async () =>
                {
                    var balances = await GetEthereumBalancesBatch(
                        ethAddresses.Select(x => x.address).Distinct(),
                        cancellationToken);
                    
                    foreach (var (walletKey, address) in ethAddresses)
                    {
                        if (balances.TryGetValue(address.ToLower(), out var balance))
                        {
                            lock (results)
                            {
                                results[walletKey]["ETH"] = balance;
                            }
                        }
                    }
                }, cancellationToken));
            }

            if (solAddresses.Any())
            {
                tasks.Add(Task.Run(async () =>
                {
                    var balances = await GetSolanaBalancesBatch(
                        solAddresses.Select(x => x.address).Distinct(),
                        cancellationToken);
                    
                    foreach (var (walletKey, address) in solAddresses)
                    {
                        if (balances.TryGetValue(address, out var balance))
                        {
                            lock (results)
                            {
                                results[walletKey]["SOL"] = balance;
                            }
                        }
                    }
                }, cancellationToken));
            }

            if (maticAddresses.Any())
            {
                tasks.Add(Task.Run(async () =>
                {
                    var balances = await GetPolygonBalancesBatch(
                        maticAddresses.Select(x => x.address).Distinct(),
                        cancellationToken);
                    
                    foreach (var (walletKey, address) in maticAddresses)
                    {
                        if (balances.TryGetValue(address.ToLower(), out var balance))
                        {
                            lock (results)
                            {
                                results[walletKey]["MATIC"] = balance;
                            }
                        }
                    }
                }, cancellationToken));
            }

            if (bnbAddresses.Any())
            {
                tasks.Add(Task.Run(async () =>
                {
                    var balances = await GetBscBalancesBatch(
                        bnbAddresses.Select(x => x.address).Distinct(),
                        cancellationToken);
                    
                    foreach (var (walletKey, address) in bnbAddresses)
                    {
                        if (balances.TryGetValue(address.ToLower(), out var balance))
                        {
                            lock (results)
                            {
                                results[walletKey]["BNB"] = balance;
                            }
                        }
                    }
                }, cancellationToken));
            }

            await Task.WhenAll(tasks);

            return results;
        }

        #endregion

        #region Litecoin Batch API

        /// <summary>
        /// Gets LTC balances for multiple addresses using BlockCypher API
        /// </summary>
        public async Task<Dictionary<string, decimal>> GetLitecoinBalancesBatch(
            IEnumerable<string> addresses,
            CancellationToken cancellationToken = default)
        {
            var results = new Dictionary<string, decimal>();
            var addressList = addresses.ToList();

            if (!addressList.Any())
                return results;

            var batches = addressList
                .Select((addr, idx) => new { addr, idx })
                .GroupBy(x => x.idx / LitecoinBatchSize)
                .Select(g => g.Select(x => x.addr).ToList())
                .ToList();

            foreach (var batch in batches)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                await _blockcypherLimiter.WaitForTokenAsync(cancellationToken);

                try
                {
                    var addressParam = string.Join(";", batch);
                    var url = $"https://api.blockcypher.com/v1/ltc/main/addrs/{addressParam}/balance";

                    if (!string.IsNullOrEmpty(_settings.BlockCypherApiKey))
                        url += $"?token={_settings.BlockCypherApiKey}";

                    var client = _clientPool.GetClient("https://api.blockcypher.com");
                    var response = await client.GetAsync(url, cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync(cancellationToken);

                        if (batch.Count == 1)
                        {
                            var data = JObject.Parse(json);
                            var address = data["address"]?.ToString();
                            var balanceSatoshi = data["balance"]?.Value<long>() ?? 0;
                            if (!string.IsNullOrEmpty(address))
                                results[address] = balanceSatoshi / 100_000_000m;
                        }
                        else
                        {
                            var dataArray = JArray.Parse(json);
                            foreach (var item in dataArray)
                            {
                                var address = item["address"]?.ToString();
                                var balanceSatoshi = item["balance"]?.Value<long>() ?? 0;
                                if (!string.IsNullOrEmpty(address))
                                    results[address] = balanceSatoshi / 100_000_000m;
                            }
                        }
                    }

                    // Rate limiting handled by _blockcypherLimiter
                }
                catch (Exception ex)
                {
                    DebugLogger.LogError("LTC batch API", ex);
                }
            }

            return results;
        }

        #endregion

        #region Dogecoin Batch API

        /// <summary>
        /// Gets DOGE balances for multiple addresses using BlockCypher API
        /// </summary>
        public async Task<Dictionary<string, decimal>> GetDogecoinBalancesBatch(
            IEnumerable<string> addresses,
            CancellationToken cancellationToken = default)
        {
            var results = new Dictionary<string, decimal>();
            var addressList = addresses.ToList();

            if (!addressList.Any())
                return results;

            var batches = addressList
                .Select((addr, idx) => new { addr, idx })
                .GroupBy(x => x.idx / DogecoinBatchSize)
                .Select(g => g.Select(x => x.addr).ToList())
                .ToList();

            foreach (var batch in batches)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                await _blockcypherLimiter.WaitForTokenAsync(cancellationToken);

                try
                {
                    var addressParam = string.Join(";", batch);
                    var url = $"https://api.blockcypher.com/v1/doge/main/addrs/{addressParam}/balance";

                    if (!string.IsNullOrEmpty(_settings.BlockCypherApiKey))
                        url += $"?token={_settings.BlockCypherApiKey}";

                    var client = _clientPool.GetClient("https://api.blockcypher.com");
                    var response = await client.GetAsync(url, cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync(cancellationToken);

                        if (batch.Count == 1)
                        {
                            var data = JObject.Parse(json);
                            var address = data["address"]?.ToString();
                            var balanceSatoshi = data["balance"]?.Value<long>() ?? 0;
                            if (!string.IsNullOrEmpty(address))
                                results[address] = balanceSatoshi / 100_000_000m;
                        }
                        else
                        {
                            var dataArray = JArray.Parse(json);
                            foreach (var item in dataArray)
                            {
                                var address = item["address"]?.ToString();
                                var balanceSatoshi = item["balance"]?.Value<long>() ?? 0;
                                if (!string.IsNullOrEmpty(address))
                                    results[address] = balanceSatoshi / 100_000_000m;
                            }
                        }
                    }

                    // Rate limiting handled by _blockcypherLimiter
                }
                catch (Exception ex)
                {
                    DebugLogger.LogError("DOGE batch API", ex);
                }
            }

            return results;
        }

        #endregion
    }
}
