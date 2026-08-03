using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Real-time cryptocurrency price ticker using CoinGecko API
    /// Updates prices in background and provides cached access
    /// </summary>
    public static class LivePriceTicker
    {
        private static readonly HttpClient _httpClient;
        private static readonly HttpClient _fallbackClient;
        private static readonly ConcurrentDictionary<string, decimal> _prices = new();
        private static readonly ConcurrentDictionary<string, decimal> _priceChanges = new();
        private static DateTime _lastUpdate = DateTime.MinValue;
        private static Timer _updateTimer;
        private static volatile bool _isRunning;
        private static readonly object _startStopLock = new();

        static LivePriceTicker()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "WalletRecoveryTool/1.0");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
            
            _fallbackClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _fallbackClient.DefaultRequestHeaders.Add("User-Agent", "WalletRecoveryTool/1.0");
            _fallbackClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        // Events
        public static event EventHandler<PriceUpdateEventArgs> PricesUpdated;

        // Crypto IDs for CoinGecko
        private static readonly Dictionary<string, string> _coinIds = new()
        {
            ["BTC"] = "bitcoin",
            ["ETH"] = "ethereum",
            ["SOL"] = "solana",
            ["USDT"] = "tether",
            ["LTC"] = "litecoin",
            ["DOGE"] = "dogecoin",
            ["MATIC"] = "matic-network",
            ["BNB"] = "binancecoin",
            ["XRP"] = "ripple",
            ["ADA"] = "cardano"
        };

        /// <summary>
        /// Start the price ticker with specified update interval
        /// </summary>
        public static void Start(int updateIntervalSeconds = 60)
        {
            lock (_startStopLock)
            {
                if (_isRunning) return;
                
                _isRunning = true;
                
                // Initial fetch
                Task.Run(async () => await UpdatePricesAsync());
                
                // Start periodic updates
                _updateTimer = new Timer(
                    async _ => await UpdatePricesAsync(),
                    null,
                    TimeSpan.FromSeconds(updateIntervalSeconds),
                    TimeSpan.FromSeconds(updateIntervalSeconds));
            }
        }

        /// <summary>
        /// Stop the price ticker
        /// </summary>
        public static void Stop()
        {
            lock (_startStopLock)
            {
                _isRunning = false;
                _updateTimer?.Dispose();
                _updateTimer = null;
            }
        }

        /// <summary>
        /// Get current price for a cryptocurrency
        /// </summary>
        public static decimal GetPrice(string symbol)
        {
            symbol = symbol.ToUpper();
            return _prices.TryGetValue(symbol, out var price) ? price : 0;
        }

        /// <summary>
        /// Get 24h price change percentage
        /// </summary>
        public static decimal GetPriceChange(string symbol)
        {
            symbol = symbol.ToUpper();
            return _priceChanges.TryGetValue(symbol, out var change) ? change : 0;
        }

        /// <summary>
        /// Get formatted price string with change indicator
        /// </summary>
        public static string GetFormattedPrice(string symbol)
        {
            var price = GetPrice(symbol);
            var change = GetPriceChange(symbol);
            
            if (price == 0)
                return $"{symbol}: --";

            var changeIcon = change >= 0 ? "📈" : "📉";
            var changeColor = change >= 0 ? "+" : "";
            
            return $"{symbol}: ${price:N2} {changeIcon} {changeColor}{change:F1}%";
        }

        /// <summary>
        /// Get all prices as formatted summary
        /// </summary>
        public static string GetPriceSummary()
        {
            var btcPrice = GetPrice("BTC");
            var ethPrice = GetPrice("ETH");
            var solPrice = GetPrice("SOL");
            
            if (btcPrice == 0 && ethPrice == 0)
                return "Loading prices...";

            var btcChange = GetPriceChange("BTC");
            var ethChange = GetPriceChange("ETH");
            
            var btcIcon = btcChange >= 0 ? "📈" : "📉";
            var ethIcon = ethChange >= 0 ? "📈" : "📉";
            
            return $"BTC: ${btcPrice:N0} {btcIcon} | ETH: ${ethPrice:N0} {ethIcon} | SOL: ${solPrice:N2}";
        }

        /// <summary>
        /// Calculate USD value for given balances
        /// </summary>
        public static decimal CalculateUsdValue(Dictionary<string, decimal> balances)
        {
            decimal total = 0;
            
            foreach (var balance in balances)
            {
                var price = GetPrice(balance.Key);
                total += balance.Value * price;
            }
            
            return total;
        }

        // CoinCap ID mapping
        private static readonly Dictionary<string, string> _coinCapIds = new()
        {
            ["BTC"] = "bitcoin", ["ETH"] = "ethereum", ["SOL"] = "solana",
            ["USDT"] = "tether", ["LTC"] = "litecoin", ["DOGE"] = "dogecoin",
            ["MATIC"] = "polygon", ["BNB"] = "binance-coin", ["XRP"] = "xrp", ["ADA"] = "cardano"
        };

        /// <summary>
        /// Update prices from CoinGecko API, fallback to CoinCap
        /// </summary>
        private static async Task UpdatePricesAsync()
        {
            // Try CoinGecko first
            if (await TryUpdateFromCoinGecko()) return;
            // Fallback to CoinCap
            await TryUpdateFromCoinCap();
        }

        private static async Task<bool> TryUpdateFromCoinGecko()
        {
            try
            {
                var ids = string.Join(",", _coinIds.Values);
                var url = $"https://api.coingecko.com/api/v3/simple/price?ids={ids}&vs_currencies=usd&include_24hr_change=true";
                
                var response = await _httpClient.GetAsync(url).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return false;
                
                var content = await response.Content.ReadAsStringAsync();
                dynamic data = JsonConvert.DeserializeObject(content);
                
                foreach (var kvp in _coinIds)
                {
                    try
                    {
                        var coinData = data[kvp.Value];
                        if (coinData != null)
                        {
                            decimal price = coinData.usd ?? 0;
                            decimal change = coinData.usd_24h_change ?? 0;
                            _prices[kvp.Key] = price;
                            _priceChanges[kvp.Key] = change;
                        }
                    }
                    catch { }
                }
                
                _lastUpdate = DateTime.Now;
                PricesUpdated?.Invoke(null, new PriceUpdateEventArgs
                {
                    Prices = new Dictionary<string, decimal>(_prices),
                    Changes = new Dictionary<string, decimal>(_priceChanges),
                    UpdateTime = _lastUpdate
                });
                return true;
            }
            catch { return false; }
        }

        private static async Task<bool> TryUpdateFromCoinCap()
        {
            try
            {
                var ids = string.Join(",", _coinCapIds.Values);
                var url = $"https://api.coincap.io/v2/assets?ids={ids}";
                var response = await _fallbackClient.GetAsync(url).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return false;

                var content = await response.Content.ReadAsStringAsync();
                dynamic data = JsonConvert.DeserializeObject(content);
                
                foreach (var item in data.data)
                {
                    string coinId = item.id;
                    foreach (var kvp in _coinCapIds)
                    {
                        if (kvp.Value == coinId)
                        {
                            if (decimal.TryParse(item.priceUsd.ToString(), System.Globalization.NumberStyles.Any, 
                                System.Globalization.CultureInfo.InvariantCulture, out decimal price))
                            {
                                _prices[kvp.Key] = price;
                            }
                            string changeStr = item.changePercent24Hr?.ToString();
                            if (changeStr != null && decimal.TryParse(changeStr,
                                System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal change))
                            {
                                _priceChanges[kvp.Key] = change;
                            }
                            break;
                        }
                    }
                }
                
                _lastUpdate = DateTime.Now;
                PricesUpdated?.Invoke(null, new PriceUpdateEventArgs
                {
                    Prices = new Dictionary<string, decimal>(_prices),
                    Changes = new Dictionary<string, decimal>(_priceChanges),
                    UpdateTime = _lastUpdate
                });
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Force immediate price update
        /// </summary>
        public static async Task RefreshAsync()
        {
            await UpdatePricesAsync();
        }

        /// <summary>
        /// Get last update time
        /// </summary>
        public static DateTime LastUpdate => _lastUpdate;

        /// <summary>
        /// Check if prices are stale (older than 5 minutes)
        /// </summary>
        public static bool ArePricesStale => (DateTime.Now - _lastUpdate).TotalMinutes > 5;
    }

    public class PriceUpdateEventArgs : EventArgs
    {
        public Dictionary<string, decimal> Prices { get; set; }
        public Dictionary<string, decimal> Changes { get; set; }
        public DateTime UpdateTime { get; set; }
    }
}
