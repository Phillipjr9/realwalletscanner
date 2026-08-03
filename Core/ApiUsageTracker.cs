using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Timers;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Tracks API usage across all blockchain services to prevent hitting rate limits.
    /// Persists daily usage counts and fires warnings at 80%, 90%, 95% thresholds.
    /// </summary>
    public static class ApiUsageTracker
    {
        // API daily limits (free tier)
        public static readonly ConcurrentDictionary<string, int> DailyLimits = new ConcurrentDictionary<string, int>
        {
            ["Etherscan"] = 100000,
            ["BlockCypher"] = 200,        // 200/hour, ~4800/day but conservative
            ["Polygonscan"] = 100000,
            ["BscScan"] = 100000,
            ["QuickNode"] = 100000,
            ["TronGrid"] = 50000,
            ["Blockstream"] = 50000,      // No official limit, but be conservative
            ["Mempool"] = 50000           // No official limit, but be conservative
        };

        // Current usage counts
        private static ConcurrentDictionary<string, int> _usageCounts = new ConcurrentDictionary<string, int>();
        private static DateTime _trackingDate = DateTime.Today;
        private static readonly object _fileLock = new object();
        private static Timer _saveTimer;
        
        // Events for UI notifications
        public static event EventHandler<ApiUsageWarningEventArgs> UsageWarning;
        public static event EventHandler<ApiUsageWarningEventArgs> LimitReached;
        
        // File path for persistent storage
        private static readonly string _usageFilePath;
        
        // Warning thresholds
        private static readonly int[] WarningThresholds = { 80, 90, 95, 99 };
        private static ConcurrentDictionary<string, int> _lastWarningLevel = new ConcurrentDictionary<string, int>();

        static ApiUsageTracker()
        {
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WalletRecoveryTool", "Cache");
            
            Directory.CreateDirectory(appDataPath);
            _usageFilePath = Path.Combine(appDataPath, "api_usage.json");
            
            LoadUsage();
            
            // Auto-save every 30 seconds
            _saveTimer = new Timer(30000);
            _saveTimer.Elapsed += (s, e) => SaveUsage();
            _saveTimer.AutoReset = true;
            _saveTimer.Start();
        }

        /// <summary>
        /// Dispose the auto-save timer and flush usage to disk. Call on app shutdown.
        /// </summary>
        public static void Shutdown()
        {
            try
            {
                _saveTimer?.Stop();
                _saveTimer?.Dispose();
                _saveTimer = null;
                SaveUsage(); // Final flush
            }
            catch { /* swallow during shutdown */ }
        }

        /// <summary>
        /// Record an API call and check for warnings
        /// </summary>
        public static void RecordCall(string apiName, int callCount = 1)
        {
            // Reset if new day
            if (DateTime.Today != _trackingDate)
            {
                ResetDailyCounters();
            }

            // Increment counter
            _usageCounts.AddOrUpdate(apiName, callCount, (key, oldValue) => oldValue + callCount);

            // Check thresholds
            CheckThresholds(apiName);
        }

        /// <summary>
        /// Get current usage for an API
        /// </summary>
        public static (int used, int limit, double percent) GetUsage(string apiName)
        {
            if (DateTime.Today != _trackingDate)
            {
                ResetDailyCounters();
            }

            var used = _usageCounts.GetOrAdd(apiName, 0);
            var limit = DailyLimits.GetOrAdd(apiName, 100000);
            var percent = limit > 0 ? (double)used / limit * 100 : 0;
            
            return (used, limit, percent);
        }

        /// <summary>
        /// Get summary of all API usage
        /// </summary>
        public static string GetUsageSummary()
        {
            if (DateTime.Today != _trackingDate)
            {
                ResetDailyCounters();
            }

            var summary = "📊 API Usage Today:\n";
            foreach (var api in DailyLimits.Keys)
            {
                var (used, limit, percent) = GetUsage(api);
                var status = percent >= 95 ? "🔴" : percent >= 80 ? "🟡" : "🟢";
                summary += $"{status} {api}: {used:N0}/{limit:N0} ({percent:F1}%)\n";
            }
            return summary;
        }

        /// <summary>
        /// Check if API is near limit
        /// </summary>
        public static bool IsNearLimit(string apiName, int thresholdPercent = 80)
        {
            var (_, _, percent) = GetUsage(apiName);
            return percent >= thresholdPercent;
        }

        /// <summary>
        /// Check if API has reached limit
        /// </summary>
        public static bool HasReachedLimit(string apiName)
        {
            var (used, limit, _) = GetUsage(apiName);
            return used >= limit;
        }

        /// <summary>
        /// Get remaining calls for an API
        /// </summary>
        public static int GetRemainingCalls(string apiName)
        {
            var (used, limit, _) = GetUsage(apiName);
            return Math.Max(0, limit - used);
        }

        /// <summary>
        /// Update daily limit for an API (user configurable)
        /// </summary>
        public static void SetDailyLimit(string apiName, int limit)
        {
            DailyLimits[apiName] = limit;
        }

        private static void CheckThresholds(string apiName)
        {
            var (used, limit, percent) = GetUsage(apiName);
            
            // Get last warning level for this API
            var lastLevel = _lastWarningLevel.GetOrAdd(apiName, 0);
            
            // Check each threshold
            foreach (var threshold in WarningThresholds)
            {
                if (percent >= threshold && lastLevel < threshold)
                {
                    _lastWarningLevel[apiName] = threshold;
                    
                    var remaining = limit - used;
                    var args = new ApiUsageWarningEventArgs
                    {
                        ApiName = apiName,
                        UsedCalls = used,
                        DailyLimit = limit,
                        PercentUsed = percent,
                        RemainingCalls = remaining,
                        ThresholdReached = threshold
                    };

                    if (threshold >= 100 || used >= limit)
                    {
                        LimitReached?.Invoke(null, args);
                    }
                    else
                    {
                        UsageWarning?.Invoke(null, args);
                    }
                    
                    break; // Only fire one warning per call
                }
            }
        }

        private static void ResetDailyCounters()
        {
            _trackingDate = DateTime.Today;
            _usageCounts.Clear();
            _lastWarningLevel.Clear();
            SaveUsage();
        }

        private static void LoadUsage()
        {
            try
            {
                lock (_fileLock)
                {
                    if (File.Exists(_usageFilePath))
                    {
                        var json = File.ReadAllText(_usageFilePath);
                        var data = JsonSerializer.Deserialize<ApiUsageData>(json);
                        
                        if (data != null && data.Date == DateTime.Today.ToString("yyyy-MM-dd"))
                        {
                            _trackingDate = DateTime.Today;
                            _usageCounts = new ConcurrentDictionary<string, int>(data.Counts);
                        }
                        else
                        {
                            // New day - reset counters
                            ResetDailyCounters();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Error loading API usage: {ex.Message}");
            }
        }

        public static void SaveUsage()
        {
            try
            {
                lock (_fileLock)
                {
                    var data = new ApiUsageData
                    {
                        Date = _trackingDate.ToString("yyyy-MM-dd"),
                        Counts = new System.Collections.Generic.Dictionary<string, int>(_usageCounts)
                    };
                    
                    var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_usageFilePath, json);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Error saving API usage: {ex.Message}");
            }
        }

        private class ApiUsageData
        {
            public string Date { get; set; }
            public System.Collections.Generic.Dictionary<string, int> Counts { get; set; }
        }
    }

    public class ApiUsageWarningEventArgs : EventArgs
    {
        public string ApiName { get; set; }
        public int UsedCalls { get; set; }
        public int DailyLimit { get; set; }
        public double PercentUsed { get; set; }
        public int RemainingCalls { get; set; }
        public int ThresholdReached { get; set; }

        public string GetWarningMessage()
        {
            if (PercentUsed >= 100 || RemainingCalls <= 0)
            {
                return $"🔴 {ApiName} LIMIT REACHED! {UsedCalls:N0}/{DailyLimit:N0} calls used today. Wait until tomorrow or use backup API.";
            }
            return $"⚠️ {ApiName}: {PercentUsed:F0}% used ({UsedCalls:N0}/{DailyLimit:N0}). Only {RemainingCalls:N0} calls remaining today!";
        }
    }
}
