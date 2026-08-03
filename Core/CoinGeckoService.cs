using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// CoinGecko API service for real-time cryptocurrency prices in USD.
    /// Free API - no key required, but rate limited (10-50 calls/minute).
    /// </summary>
    public static class CoinGeckoService
    {
        private static readonly HttpClient _httpClient;
        private static readonly HttpClient _fallbackClient;

        static CoinGeckoService()
        {
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri("https://api.coingecko.com/api/v3/"),
                Timeout = TimeSpan.FromSeconds(10)
            };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "WalletRecoveryTool/1.0");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
            
            _fallbackClient = new HttpClient
            {
                BaseAddress = new Uri("https://api.coincap.io/v2/"),
                Timeout = TimeSpan.FromSeconds(10)
            };
            _fallbackClient.DefaultRequestHeaders.Add("User-Agent", "WalletRecoveryTool/1.0");
            _fallbackClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        // Price cache with 5-minute TTL
        private static Dictionary<string, decimal> _priceCache = new Dictionary<string, decimal>();
        private static DateTime _lastFetch = DateTime.MinValue;
        private static readonly TimeSpan _cacheTTL = TimeSpan.FromMinutes(5);
        private static readonly object _cacheLock = new object();
        private static readonly SemaphoreSlim _fetchSemaphore = new SemaphoreSlim(1, 1);

        // CoinGecko IDs for supported cryptocurrencies
        private static readonly Dictionary<string, string> _coinIds = new Dictionary<string, string>
        {
            { "BTC", "bitcoin" },
            { "ETH", "ethereum" },
            { "SOL", "solana" },
            { "USDT", "tether" },
            { "LTC", "litecoin" },
            { "DOGE", "dogecoin" },
            { "MATIC", "matic-network" },
            { "BNB", "binancecoin" },
            { "TRX", "tron" },
            { "XRP", "ripple" }
        };

        /// <summary>
        /// Get current USD price for a cryptocurrency symbol.
        /// </summary>
        /// <param name="symbol">Crypto symbol (BTC, ETH, SOL, etc.)</param>
        /// <returns>USD price or 0 if unavailable</returns>
        public static async Task<decimal> GetPriceUSD(string symbol)
        {
            await RefreshCacheIfNeeded();
            
            lock (_cacheLock)
            {
                if (_priceCache.TryGetValue(symbol.ToUpper(), out var price))
                    return price;
            }
            
            return 0m;
        }

        /// <summary>
        /// Get all cached prices.
        /// </summary>
        public static async Task<Dictionary<string, decimal>> GetAllPrices()
        {
            await RefreshCacheIfNeeded();
            
            lock (_cacheLock)
            {
                return new Dictionary<string, decimal>(_priceCache);
            }
        }

        /// <summary>
        /// Calculate total USD value from crypto balances.
        /// </summary>
        /// <param name="balances">Dictionary of symbol -> amount</param>
        /// <returns>Total USD value</returns>
        public static async Task<decimal> CalculateTotalUSD(Dictionary<string, decimal> balances)
        {
            if (balances == null || balances.Count == 0)
                return 0m;

            var prices = await GetAllPrices();
            decimal total = 0m;

            foreach (var balance in balances)
            {
                if (balance.Value > 0 && prices.TryGetValue(balance.Key.ToUpper(), out var price))
                {
                    total += balance.Value * price;
                }
            }

            return total;
        }

        /// <summary>
        /// Format USD value for display.
        /// </summary>
        public static string FormatUSD(decimal usdValue)
        {
            if (usdValue >= 1_000_000)
                return $"${usdValue:N0}"; // $1,234,567
            if (usdValue >= 1000)
                return $"${usdValue:N2}"; // $1,234.56
            if (usdValue >= 1)
                return $"${usdValue:F2}"; // $12.34
            return $"${usdValue:F4}"; // $0.0012
        }

        /// <summary>
        /// Refresh price cache if older than TTL.
        /// </summary>
        private static async Task RefreshCacheIfNeeded()
        {
            lock (_cacheLock)
            {
                if (DateTime.Now - _lastFetch < _cacheTTL)
                    return; // Cache still valid
            }

            // Use semaphore for double-check locking — only one thread fetches
            if (!await _fetchSemaphore.WaitAsync(0))
                return; // Another thread is already fetching

            try
            {
                // Double-check after acquiring semaphore
                lock (_cacheLock)
                {
                    if (DateTime.Now - _lastFetch < _cacheTTL)
                        return;
                }
                // Try CoinGecko first, then fallback to CoinCap
                bool fetched = await TryFetchCoinGecko() || await TryFetchCoinCap();
                
                if (!fetched)
                {
                    DebugLogger.LogEvent("PRICE_ERROR", "All price APIs failed");
                    lock (_cacheLock) { _lastFetch = DateTime.Now; } // Don't spam retries
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogError("COINGECKO_FETCH", ex);
            }
            finally
            {
                _fetchSemaphore.Release();
            }
        }

        private static async Task<bool> TryFetchCoinGecko()
        {
            try
            {
                var ids = string.Join(",", _coinIds.Values);
                var url = $"simple/price?ids={ids}&vs_currencies=usd";

                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    DebugLogger.LogEvent("COINGECKO_ERROR", $"API returned {response.StatusCode}");
                    return false;
                }

                var json = await response.Content.ReadAsStringAsync();
                var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, decimal>>>(json);
                if (data == null) return false;

                lock (_cacheLock)
                {
                    _priceCache.Clear();
                    foreach (var kvp in _coinIds)
                    {
                        if (data.TryGetValue(kvp.Value, out var priceData) && 
                            priceData.TryGetValue("usd", out var usdPrice))
                            _priceCache[kvp.Key] = usdPrice;
                    }
                    _lastFetch = DateTime.Now;
                }
                DebugLogger.LogEvent("COINGECKO_REFRESH", $"Prices updated: BTC=${_priceCache.GetValueOrDefault("BTC"):N0}");
                return true;
            }
            catch { return false; }
        }

        // CoinCap ID mapping (different from CoinGecko)
        private static readonly Dictionary<string, string> _coinCapIds = new Dictionary<string, string>
        {
            { "BTC", "bitcoin" }, { "ETH", "ethereum" }, { "SOL", "solana" },
            { "USDT", "tether" }, { "LTC", "litecoin" }, { "DOGE", "dogecoin" },
            { "MATIC", "polygon" }, { "BNB", "binance-coin" }, { "TRX", "tron" }, { "XRP", "xrp" }
        };

        private static async Task<bool> TryFetchCoinCap()
        {
            try
            {
                var ids = string.Join(",", _coinCapIds.Values);
                var url = $"assets?ids={ids}";
                var response = await _fallbackClient.GetAsync(url);
                if (!response.IsSuccessStatusCode) return false;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var dataArray = doc.RootElement.GetProperty("data");

                lock (_cacheLock)
                {
                    _priceCache.Clear();
                    foreach (var item in dataArray.EnumerateArray())
                    {
                        var coinId = item.GetProperty("id").GetString();
                        var priceStr = item.GetProperty("priceUsd").GetString();
                        if (decimal.TryParse(priceStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var price))
                        {
                            foreach (var kvp in _coinCapIds)
                            {
                                if (kvp.Value == coinId)
                                {
                                    _priceCache[kvp.Key] = price;
                                    break;
                                }
                            }
                        }
                    }
                    _lastFetch = DateTime.Now;
                }
                DebugLogger.LogEvent("COINCAP_REFRESH", $"Prices updated via CoinCap: BTC=${_priceCache.GetValueOrDefault("BTC"):N0}");
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Force refresh the price cache.
        /// </summary>
        public static async Task ForceRefresh()
        {
            lock (_cacheLock)
            {
                _lastFetch = DateTime.MinValue;
            }
            await RefreshCacheIfNeeded();
        }

        /// <summary>
        /// Get last cache update time.
        /// </summary>
        public static DateTime GetLastUpdateTime()
        {
            lock (_cacheLock)
            {
                return _lastFetch;
            }
        }

        /// <summary>
        /// Check if prices are available in cache.
        /// </summary>
        public static bool HasPrices()
        {
            lock (_cacheLock)
            {
                return _priceCache.Count > 0;
            }
        }
    }
}
