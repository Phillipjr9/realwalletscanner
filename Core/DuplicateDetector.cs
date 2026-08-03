using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Tracks checked addresses/mnemonics to avoid duplicate API calls
    /// Uses BloomFilter for memory-efficient in-memory deduplication
    /// Persists to disk for cross-session deduplication
    /// </summary>
    public static class DuplicateDetector
    {
        // BloomFilter for fast, memory-efficient membership checks (10 bits/entry vs 100 bytes/entry)
        private static readonly BloomFilter _addressBloom = new BloomFilter(5_000_000, 0.01); // 5M addresses, 1% FP (saves ~80MB vs 50M)
        private static readonly BloomFilter _mnemonicBloom = new BloomFilter(2_000_000, 0.01); // 2M mnemonics, 1% FP (saves ~14MB vs 10M)
        
        // Disk persistence still uses dictionaries (only recent entries saved)
        private static readonly ConcurrentDictionary<string, DateTime> _checkedAddresses = new();
        private static readonly ConcurrentDictionary<string, DateTime> _checkedMnemonics = new();
        private static readonly object _fileLock = new();
        private const int MaxDictSize = 50_000; // Cap in-memory dict to prevent unbounded growth
        
        private static readonly string _dataFolder;
        private static readonly string _addressesFile;
        private static readonly string _mnemonicsFile;
        
        // Statistics
        public static long TotalAddressesChecked => _addressBloom.Count;
        public static long TotalMnemonicsChecked => _mnemonicBloom.Count;
        private static long _duplicatesSkipped;
        public static long DuplicatesSkipped => Interlocked.Read(ref _duplicatesSkipped);

        static DuplicateDetector()
        {
            _dataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WalletRecoveryTool", "Cache");
            
            Directory.CreateDirectory(_dataFolder);
            _addressesFile = Path.Combine(_dataFolder, "checked_addresses.json");
            _mnemonicsFile = Path.Combine(_dataFolder, "checked_mnemonics.json");
            
            LoadFromDisk();
        }

        /// <summary>
        /// Check if address was already checked (returns true if duplicate)
        /// </summary>
        public static bool IsAddressChecked(string address)
        {
            if (string.IsNullOrEmpty(address)) return false;
            
            var key = address.ToLowerInvariant();
            if (_addressBloom.MightContain(key))
            {
                Interlocked.Increment(ref _duplicatesSkipped);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Mark address as checked
        /// </summary>
        public static void MarkAddressChecked(string address)
        {
            if (string.IsNullOrEmpty(address)) return;
            
            var key = address.ToLowerInvariant();
            _addressBloom.Add(key);
            _checkedAddresses.TryAdd(key, DateTime.Now);
            
            // Prevent unbounded dict growth (bloom filter handles dedup, dict is just for persistence)
            if (_checkedAddresses.Count > MaxDictSize)
            {
                _checkedAddresses.Clear();
            }
            
            // Auto-save every 1000 entries
            if (_checkedAddresses.Count % 1000 == 0)
            {
                SaveToDiskAsync();
            }
        }

        /// <summary>
        /// Check if mnemonic was already checked (returns true if duplicate)
        /// </summary>
        public static bool IsMnemonicChecked(string mnemonic)
        {
            if (string.IsNullOrEmpty(mnemonic)) return false;
            
            // Normalize mnemonic (lowercase, single spaces)
            var key = NormalizeMnemonic(mnemonic);
            if (_mnemonicBloom.MightContain(key))
            {
                Interlocked.Increment(ref _duplicatesSkipped);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Mark mnemonic as checked
        /// </summary>
        public static void MarkMnemonicChecked(string mnemonic)
        {
            if (string.IsNullOrEmpty(mnemonic)) return;
            
            var key = NormalizeMnemonic(mnemonic);
            _mnemonicBloom.Add(key);
            _checkedMnemonics.TryAdd(key, DateTime.Now);
            
            // Prevent unbounded dict growth
            if (_checkedMnemonics.Count > MaxDictSize)
            {
                _checkedMnemonics.Clear();
            }
            
            // Auto-save every 1000 entries
            if (_checkedMnemonics.Count % 1000 == 0)
            {
                SaveToDiskAsync();
            }
        }

        /// <summary>
        /// Check and mark address in one call (returns true if was duplicate)
        /// </summary>
        public static bool CheckAndMarkAddress(string address)
        {
            if (IsAddressChecked(address))
                return true;
            
            MarkAddressChecked(address);
            return false;
        }

        /// <summary>
        /// Check and mark mnemonic in one call (returns true if was duplicate)
        /// </summary>
        public static bool CheckAndMarkMnemonic(string mnemonic)
        {
            if (IsMnemonicChecked(mnemonic))
                return true;
            
            MarkMnemonicChecked(mnemonic);
            return false;
        }

        /// <summary>
        /// Normalize mnemonic for comparison
        /// </summary>
        private static string NormalizeMnemonic(string mnemonic)
        {
            return string.Join(" ", mnemonic.ToLowerInvariant()
                .Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>
        /// Clear all cached duplicates
        /// </summary>
        public static void ClearAll()
        {
            _checkedAddresses.Clear();
            _checkedMnemonics.Clear();
            Interlocked.Exchange(ref _duplicatesSkipped, 0);
            // Note: BloomFilters cannot be cleared; they stay with old data
            // This is acceptable as they only cause false positives (skips), not false negatives
            
            try
            {
                if (File.Exists(_addressesFile)) File.Delete(_addressesFile);
                if (File.Exists(_mnemonicsFile)) File.Delete(_mnemonicsFile);
            }
            catch { }
        }

        /// <summary>
        /// Clear entries older than specified days
        /// </summary>
        public static int ClearOlderThan(int days)
        {
            var cutoff = DateTime.Now.AddDays(-days);
            int removed = 0;

            var oldAddresses = _checkedAddresses.Where(x => x.Value < cutoff).Select(x => x.Key).ToList();
            foreach (var key in oldAddresses)
            {
                if (_checkedAddresses.TryRemove(key, out _))
                    removed++;
            }

            var oldMnemonics = _checkedMnemonics.Where(x => x.Value < cutoff).Select(x => x.Key).ToList();
            foreach (var key in oldMnemonics)
            {
                if (_checkedMnemonics.TryRemove(key, out _))
                    removed++;
            }

            SaveToDiskAsync();
            return removed;
        }

        /// <summary>
        /// Save to disk asynchronously
        /// </summary>
        public static void SaveToDiskAsync()
        {
            System.Threading.Tasks.Task.Run(() => SaveToDisk());
        }

        /// <summary>
        /// Save tracked entries to disk
        /// </summary>
        public static void SaveToDisk()
        {
            lock (_fileLock)
            {
                try
                {
                    // Only save recent entries (last 30 days) to limit file size
                    var cutoff = DateTime.Now.AddDays(-30);
                    
                    var recentAddresses = _checkedAddresses
                        .Where(x => x.Value > cutoff)
                        .ToDictionary(x => x.Key, x => x.Value);
                    
                    var recentMnemonics = _checkedMnemonics
                        .Where(x => x.Value > cutoff)
                        .ToDictionary(x => x.Key, x => x.Value);
                    
                    File.WriteAllText(_addressesFile, JsonSerializer.Serialize(recentAddresses));
                    File.WriteAllText(_mnemonicsFile, JsonSerializer.Serialize(recentMnemonics));
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"Error saving duplicate cache: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Load from disk
        /// </summary>
        private static void LoadFromDisk()
        {
            lock (_fileLock)
            {
                try
                {
                    if (File.Exists(_addressesFile))
                    {
                        var json = File.ReadAllText(_addressesFile);
                        var data = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json);
                        if (data != null)
                        {
                            foreach (var kvp in data)
                            {
                                _checkedAddresses.TryAdd(kvp.Key, kvp.Value);
                                _addressBloom.Add(kvp.Key); // Seed bloom filter from disk
                            }
                        }
                    }

                    if (File.Exists(_mnemonicsFile))
                    {
                        var json = File.ReadAllText(_mnemonicsFile);
                        var data = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json);
                        if (data != null)
                        {
                            foreach (var kvp in data)
                            {
                                _checkedMnemonics.TryAdd(kvp.Key, kvp.Value);
                                _mnemonicBloom.Add(kvp.Key); // Seed bloom filter from disk
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"Error loading duplicate cache: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Get statistics summary
        /// </summary>
        public static string GetStatsSummary()
        {
            return $"Addresses: {TotalAddressesChecked:N0} | Mnemonics: {TotalMnemonicsChecked:N0} | Skipped: {DuplicatesSkipped:N0}";
        }
    }
}
