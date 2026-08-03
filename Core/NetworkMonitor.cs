using System;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading.Tasks;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Monitors internet connectivity and provides network status checks
    /// </summary>
    public class NetworkMonitor
    {
        private static readonly HttpClient _connectivityClient = new HttpClient 
        { 
            Timeout = TimeSpan.FromSeconds(5) 
        };

        public static event EventHandler<bool> ConnectivityChanged;

        private static volatile bool _lastKnownStatus = true;

        /// <summary>
        /// Checks if internet connection is available
        /// </summary>
        public static async Task<bool> IsInternetAvailable()
        {
            // Quick check: Is network interface available?
            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                UpdateConnectivityStatus(false);
                return false;
            }

            // Comprehensive check: Try to reach multiple endpoints
            try
            {
                // Test 1: Ping Google DNS (fast, reliable)
                using (var ping = new Ping())
                {
                    var reply = await ping.SendPingAsync("8.8.8.8", 3000);
                    if (reply.Status == IPStatus.Success)
                    {
                        UpdateConnectivityStatus(true);
                        return true;
                    }
                }

                // Test 2: HTTP request to Cloudflare (alternative)
                using (var response = await _connectivityClient.GetAsync("https://1.1.1.1", 
                    HttpCompletionOption.ResponseHeadersRead))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        UpdateConnectivityStatus(true);
                        return true;
                    }
                }
            }
            catch
            {
                // All checks failed
            }

            UpdateConnectivityStatus(false);
            return false;
        }

        /// <summary>
        /// Checks if blockchain APIs are reachable (more thorough than basic internet check)
        /// </summary>
        public static async Task<BlockchainApiStatus> CheckBlockchainApiAccess()
        {
            var status = new BlockchainApiStatus();

            // Test Bitcoin API
            try
            {
                using (var btcResponse = await _connectivityClient.GetAsync("https://blockstream.info/api/blocks/tip/height"))
                    status.BitcoinApiReachable = btcResponse.IsSuccessStatusCode;
            }
            catch { status.BitcoinApiReachable = false; }

            // Test Ethereum API
            try
            {
                using (var ethResponse = await _connectivityClient.GetAsync("https://api.etherscan.io/api?module=stats&action=ethprice"))
                    status.EthereumApiReachable = ethResponse.IsSuccessStatusCode;
            }
            catch { status.EthereumApiReachable = false; }

            // Test Solana API
            try
            {
                using (var solResponse = await _connectivityClient.GetAsync("https://api.mainnet-beta.solana.com"))
                    status.SolanaApiReachable = solResponse.IsSuccessStatusCode;
            }
            catch { status.SolanaApiReachable = false; }

            status.AllApisReachable = status.BitcoinApiReachable && 
                                      status.EthereumApiReachable && 
                                      status.SolanaApiReachable;

            return status;
        }

        /// <summary>
        /// Gets current network speed estimate (useful for deciding batch sizes)
        /// </summary>
        public static async Task<NetworkSpeed> EstimateNetworkSpeed()
        {
            try
            {
                var startTime = DateTime.Now;
                
                // Download a small file (Cloudflare's 1MB test file)
                using (var response = await _connectivityClient.GetAsync("https://speed.cloudflare.com/__down?bytes=100000"))
                {
                    var data = await response.Content.ReadAsByteArrayAsync();
                    
                    var duration = (DateTime.Now - startTime).TotalSeconds;
                    if (duration <= 0) duration = 0.001; // Prevent divide by zero
                    var speedKbps = (data.Length / 1024.0) / duration;

                    if (speedKbps > 1000) return NetworkSpeed.Fast;
                    if (speedKbps > 200) return NetworkSpeed.Medium;
                    return NetworkSpeed.Slow;
                }
            }
            catch
            {
                return NetworkSpeed.Unknown;
            }
        }

        private static void UpdateConnectivityStatus(bool isConnected)
        {
            var previous = _lastKnownStatus;
            _lastKnownStatus = isConnected;
            if (previous != isConnected)
            {
                ConnectivityChanged?.Invoke(null, isConnected);
            }
        }

        public class BlockchainApiStatus
        {
            public bool BitcoinApiReachable { get; set; }
            public bool EthereumApiReachable { get; set; }
            public bool SolanaApiReachable { get; set; }
            public bool AllApisReachable { get; set; }

            public string GetStatusMessage()
            {
                if (AllApisReachable)
                    return "✓ All blockchain APIs reachable";

                var issues = new System.Collections.Generic.List<string>();
                if (!BitcoinApiReachable) issues.Add("Bitcoin");
                if (!EthereumApiReachable) issues.Add("Ethereum");
                if (!SolanaApiReachable) issues.Add("Solana");

                return $"⚠️ Cannot reach: {string.Join(", ", issues)} APIs";
            }
        }

        public enum NetworkSpeed
        {
            Unknown,
            Slow,      // < 200 KB/s - recommend small batches
            Medium,    // 200-1000 KB/s - normal operation
            Fast       // > 1000 KB/s - can use larger batches
        }
    }
}
