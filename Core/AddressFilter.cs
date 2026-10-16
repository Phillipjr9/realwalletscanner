using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// PRE-FILTERING: Skip known empty addresses to save API calls
    /// Maintains a local database of previously checked empty addresses
    /// Dramatically reduces wasted API quota on repeated scans
    /// </summary>
    public static class AddressFilter
    {
        // Cache of known empty addresses (never had balance)
        private static readonly ConcurrentDictionary<string, DateTime> _knownEmptyAddresses 
            = new ConcurrentDictionary<string, DateTime>();
        
        // File path for persistent storage
        private static readonly string _cacheFilePath;
        private static readonly object _fileLock = new object();
        private static DateTime _lastSave = DateTime.MinValue;
        private static readonly TimeSpan _saveInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan _emptyAddressExpiry = TimeSpan.FromHours(4); // Re-check after 4 hours (wallets can be funded anytime)
        
        // Statistics
        private static long _skippedCount = 0;
        private static long _totalChecked = 0;
        
        static AddressFilter()
        {
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WalletRecoveryTool", "Cache");
            
            Directory.CreateDirectory(appDataPath);
            _cacheFilePath = Path.Combine(appDataPath, "empty_addresses.json");
            
            LoadCache();
        }
        
        /// <summary>
        /// Check if an address should be skipped (known empty)
        /// </summary>
        public static bool ShouldSkip(string address)
        {
            if (string.IsNullOrEmpty(address)) return true;
            
            _totalChecked++;
            
            if (_knownEmptyAddresses.TryGetValue(address, out var lastChecked))
            {
                // If checked within expiry period, skip it
                if (DateTime.Now - lastChecked < _emptyAddressExpiry)
                {
                    _skippedCount++;
                    return true;
                }
                
                // Expired - remove and re-check
                _knownEmptyAddresses.TryRemove(address, out _);
            }
            
            return false;
        }
        
        /// <summary>
        /// Mark an address as empty (0 balance)
        /// </summary>
        public static void MarkAsEmpty(string address)
        {
            if (string.IsNullOrEmpty(address)) return;
            
            _knownEmptyAddresses[address] = DateTime.Now;
            
            // Auto-save periodically
            if (DateTime.Now - _lastSave > _saveInterval)
            {
                SaveCacheAsync();
            }
        }
        
        /// <summary>
        /// Remove an address from the empty list (found balance)
        /// </summary>
        public static void MarkAsNotEmpty(string address)
        {
            if (string.IsNullOrEmpty(address)) return;
            _knownEmptyAddresses.TryRemove(address, out _);
        }
        
        /// <summary>
        /// Get statistics
        /// </summary>
        public static (long skipped, long total, double savedPercent) GetStatistics()
        {
            var percent = _totalChecked > 0 ? (double)_skippedCount / _totalChecked * 100 : 0;
            return (_skippedCount, _totalChecked, percent);
        }
        
        /// <summary>
        /// Get the number of cached empty addresses
        /// </summary>
        public static int CachedAddressCount => _knownEmptyAddresses.Count;
        
        /// <summary>
        /// Clear the cache (force re-check all addresses)
        /// </summary>
        public static void ClearCache()
        {
            _knownEmptyAddresses.Clear();
            _skippedCount = 0;
            _totalChecked = 0;
            
            try
            {
                if (File.Exists(_cacheFilePath))
                    File.Delete(_cacheFilePath);
            }
            catch { /* Ignore file errors */ }
        }
        
        /// <summary>
        /// Load cache from disk
        /// </summary>
        private static void LoadCache()
        {
            try
            {
                if (File.Exists(_cacheFilePath))
                {
                    lock (_fileLock)
                    {
                        var json = File.ReadAllText(_cacheFilePath);
                        var data = JsonConvert.DeserializeObject<Dictionary<string, DateTime>>(json);
                        
                        if (data != null)
                        {
                            // Only load non-expired entries
                            var cutoff = DateTime.Now - _emptyAddressExpiry;
                            foreach (var kvp in data.Where(k => k.Value > cutoff))
                            {
                                _knownEmptyAddresses[kvp.Key] = kvp.Value;
                            }
                            
                            DebugLogger.Log($"AddressFilter loaded {_knownEmptyAddresses.Count:N0} cached empty addresses");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Error loading address cache: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Save cache to disk asynchronously
        /// </summary>
        private static void SaveCacheAsync()
        {
            _lastSave = DateTime.Now;
            
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    lock (_fileLock)
                    {
                        // Only save recent entries to keep file size manageable
                        var cutoff = DateTime.Now - _emptyAddressExpiry;
                        var dataToSave = _knownEmptyAddresses
                            .Where(k => k.Value > cutoff)
                            .Take(1000000) // Max 1M entries
                            .ToDictionary(k => k.Key, v => v.Value);
                        
                        var json = JsonConvert.SerializeObject(dataToSave, Formatting.None);
                        File.WriteAllText(_cacheFilePath, json);
                        
                        DebugLogger.Log($"AddressFilter saved {dataToSave.Count:N0} empty addresses to cache");
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"Error saving address cache: {ex.Message}");
                }
            });
        }
        
        /// <summary>
        /// Force save now (call on app exit)
        /// </summary>
        public static void SaveNow()
        {
            try
            {
                lock (_fileLock)
                {
                    var cutoff = DateTime.Now - _emptyAddressExpiry;
                    var dataToSave = _knownEmptyAddresses
                        .Where(k => k.Value > cutoff)
                        .Take(1000000)
                        .ToDictionary(k => k.Key, v => v.Value);
                    
                    var json = JsonConvert.SerializeObject(dataToSave, Formatting.None);
                    File.WriteAllText(_cacheFilePath, json);
                }
            }
            catch { /* Ignore on shutdown */ }
        }
        
        #region KNOWN NEVER-FUNDED ADDRESSES
        
        /// <summary>
        /// Pre-computed list of known empty address patterns
        /// These patterns indicate addresses that were never funded
        /// </summary>
        private static readonly HashSet<string> _knownEmptyPatterns = new HashSet<string>
        {
            // Burn / unspendable addresses (never funded with spendable keys)
            "1111111111111111111114oLvT2", // Zero hash160 burn address
        };
        
        /// <summary>
        /// Check if address matches known empty patterns
        /// </summary>
        public static bool IsKnownEmptyPattern(string address)
        {
            if (string.IsNullOrEmpty(address)) return true;
            
            // Check exact matches
            if (_knownEmptyPatterns.Contains(address)) return true;
            
            // Check for obviously invalid/test patterns
            if (address.StartsWith("1111111111")) return true;
            if (address.All(c => c == '1' || c == 'A')) return true;
            
            return false;
        }
        
        #endregion
    }
}
