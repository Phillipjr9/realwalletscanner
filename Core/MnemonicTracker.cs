using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Persistent mnemonic tracker to avoid rescanning the same mnemonics
    /// even across sessions (days, months, years later).
    /// Stores SHA-256 hashes of mnemonics (never the actual phrases).
    /// </summary>
    public class MnemonicTracker : IDisposable
    {
        private readonly string _dataDirectory;
        private readonly string _bloomFilterPath;
        private readonly string _hashStorePath;
        private readonly string _statsPath;
        
        private readonly object _lock = new object();
        private readonly HashSet<string> _memoryCache;
        private const int MaxMemoryCacheSize = 500_000; // Cap to prevent multi-GB RAM usage

        // Multi-PC deterministic sharding (optional)
        private readonly bool _clusterShardingEnabled;
        private readonly int _clusterNodeIndex;
        private readonly int _clusterNodeCount;
        
        // Bloom filter for fast "definitely not seen" checks
        private readonly byte[] _bloomFilter;
        private readonly int _bloomFilterSize;
        private const int BloomFilterPreferredSize = 16 * 1024 * 1024;  // 16MB = 128 million bits
        private const int BloomFilterFallbackSize = 4 * 1024 * 1024;    // 4MB fallback for low-memory systems
        private const int BloomHashCount = 7; // Optimal for 1% false positive with 100M items
        
        private long _totalTracked;
        private long _duplicatesAvoided;
        private DateTime _firstTrackedDate;
        private bool _disposed;
        private long _newEntriesSinceLastSave;
        private int _saveInProgress;
        private const int IncrementalSaveThreshold = 5000;
        
        // Auto-save interval
        private readonly Timer _autoSaveTimer;
        private bool _isDirty;

        public long TotalTracked => _totalTracked;
        public long DuplicatesAvoided => _duplicatesAvoided;
        public DateTime FirstTrackedDate => _firstTrackedDate;

        /// <summary>
        /// Creates a new mnemonic tracker with persistent storage
        /// </summary>
        public MnemonicTracker()
        {
            var settings = AppSettings.Current;

            _dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WalletRecoveryTool", "MnemonicTracker");
            
            Directory.CreateDirectory(_dataDirectory);
            
            _bloomFilterPath = Path.Combine(_dataDirectory, "mnemonic_bloom.bin");
            _hashStorePath = Path.Combine(_dataDirectory, "mnemonic_hashes.bin");
            _statsPath = Path.Combine(_dataDirectory, "tracker_stats.json");
            
            _memoryCache = new HashSet<string>();

            // Cluster sharding configuration
            // Supports environment variable overrides for easy per-PC install config:
            //   WRT_CLUSTER_NODE_INDEX (0-based), WRT_CLUSTER_NODE_COUNT
            _clusterShardingEnabled = settings.EnableClusterSharding;
            _clusterNodeIndex = settings.ClusterNodeIndex;
            _clusterNodeCount = settings.ClusterNodeCount;

            var envNodeIndex = Environment.GetEnvironmentVariable("WRT_CLUSTER_NODE_INDEX");
            var envNodeCount = Environment.GetEnvironmentVariable("WRT_CLUSTER_NODE_COUNT");

            // Optional per-device cookie fallback (written by oneclick installer)
            // Path: %APPDATA%\WalletRecoveryTool\cluster_cookie.json
            try
            {
                var cookiePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "WalletRecoveryTool",
                    "cluster_cookie.json");

                if (File.Exists(cookiePath))
                {
                    var cookieJson = File.ReadAllText(cookiePath);
                    var cookie = JsonSerializer.Deserialize<ClusterCookie>(cookieJson);
                    if (cookie != null)
                    {
                        if (string.IsNullOrWhiteSpace(envNodeCount) && cookie.NodeCount > 0)
                        {
                            envNodeCount = cookie.NodeCount.ToString();
                        }

                        if (string.IsNullOrWhiteSpace(envNodeIndex) && cookie.NodeIndex >= 0)
                        {
                            envNodeIndex = cookie.NodeIndex.ToString();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"MnemonicTracker: Failed to read cluster cookie, using defaults/env. {ex.Message}");
            }

            if (int.TryParse(envNodeCount, out var parsedNodeCount) && parsedNodeCount > 0)
            {
                _clusterNodeCount = parsedNodeCount;
                _clusterShardingEnabled = parsedNodeCount > 1 || _clusterShardingEnabled;
            }

            if (int.TryParse(envNodeIndex, out var parsedNodeIndex) && parsedNodeIndex >= 0)
            {
                _clusterNodeIndex = parsedNodeIndex;
                _clusterShardingEnabled = true;
            }

            if (_clusterNodeCount < 1) _clusterNodeCount = 1;
            if (_clusterNodeIndex < 0) _clusterNodeIndex = 0;
            if (_clusterNodeIndex >= _clusterNodeCount) _clusterNodeIndex = _clusterNodeCount - 1;
            
            // Attempt preferred size, fall back to smaller allocation on OOM
            try
            {
                _bloomFilter = new byte[BloomFilterPreferredSize];
                _bloomFilterSize = BloomFilterPreferredSize;
            }
            catch (OutOfMemoryException)
            {
                _bloomFilter = new byte[BloomFilterFallbackSize];
                _bloomFilterSize = BloomFilterFallbackSize;
                DebugLogger.Log("MnemonicTracker: Using reduced bloom filter size (16MB) due to memory constraints");
            }
            
            LoadFromDisk();

            if (_clusterShardingEnabled && _clusterNodeCount > 1)
            {
                DebugLogger.Log($"MnemonicTracker cluster sharding enabled: node {_clusterNodeIndex + 1}/{_clusterNodeCount}");
            }
            
            // Auto-save every minute to reduce duplicate rescans after restarts/crashes
            _autoSaveTimer = new Timer(_ => SaveToDiskAsync().Wait(), null, 
                TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        /// <summary>
        /// Checks if a mnemonic has been seen before and marks it as seen if not
        /// Returns true if this is a NEW mnemonic (not seen before)
        /// Returns false if this mnemonic was already scanned
        /// </summary>
        public bool TryMarkAsScanned(string mnemonic)
        {
            if (string.IsNullOrWhiteSpace(mnemonic))
                return false;
            
            // Normalize mnemonic (lowercase, single spaces)
            var normalized = NormalizeMnemonic(mnemonic);
            var hash = ComputeHash(normalized);

            // Deterministic partitioning across machines
            if (!ShouldProcessOnThisNode(hash))
            {
                lock (_lock)
                {
                    _duplicatesAvoided++;
                }
                return false;
            }
            
            lock (_lock)
            {
                // Fast bloom filter check first
                if (BloomContains(hash))
                {
                    // Might be false positive, check memory cache
                    if (_memoryCache.Contains(hash))
                    {
                        _duplicatesAvoided++;
                        return false; // Already seen
                    }
                }
                
                // New mnemonic - add to both bloom filter and cache
                BloomAdd(hash);
                
                // Cap memory cache to prevent multi-GB RAM usage
                // Bloom filter alone handles dedup for evicted entries (with small FP rate)
                if (_memoryCache.Count >= MaxMemoryCacheSize)
                {
                    _memoryCache.Clear();
                }
                _memoryCache.Add(hash);
                _totalTracked++;
                _isDirty = true;
                var newSinceSave = Interlocked.Increment(ref _newEntriesSinceLastSave);

                // Trigger periodic background persistence during long scans
                if (newSinceSave >= IncrementalSaveThreshold)
                {
                    TriggerBackgroundSave();
                }
                
                return true; // New mnemonic
            }
        }

        private void TriggerBackgroundSave()
        {
            if (Interlocked.CompareExchange(ref _saveInProgress, 1, 0) != 0)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await SaveToDiskAsync();
                }
                finally
                {
                    Interlocked.Exchange(ref _saveInProgress, 0);
                }
            });
        }

        /// <summary>
        /// Checks if a mnemonic was previously scanned WITHOUT marking it
        /// </summary>
        public bool WasScanned(string mnemonic)
        {
            if (string.IsNullOrWhiteSpace(mnemonic))
                return false;
            
            var normalized = NormalizeMnemonic(mnemonic);
            var hash = ComputeHash(normalized);
            
            lock (_lock)
            {
                // Bloom filter says definitely not seen
                if (!BloomContains(hash))
                    return false;
                
                // Check memory cache for certainty
                return _memoryCache.Contains(hash);
            }
        }

        /// <summary>
        /// Bulk check and mark multiple mnemonics
        /// Returns list of NEW mnemonics only
        /// </summary>
        public List<string> FilterNewMnemonics(IEnumerable<string> mnemonics)
        {
            var newMnemonics = new List<string>();
            
            foreach (var mnemonic in mnemonics)
            {
                if (TryMarkAsScanned(mnemonic))
                {
                    newMnemonics.Add(mnemonic);
                }
            }
            
            return newMnemonics;
        }

        /// <summary>
        /// Gets statistics about tracking
        /// </summary>
        public TrackerStats GetStats()
        {
            return new TrackerStats
            {
                TotalMnemonicsTracked = _totalTracked,
                DuplicatesAvoided = _duplicatesAvoided,
                FirstTrackedDate = _firstTrackedDate,
                MemoryCacheSize = _memoryCache.Count,
                BloomFilterSizeMB = _bloomFilterSize / (1024 * 1024),
                EstimatedFalsePositiveRate = CalculateFalsePositiveRate()
            };
        }

        #region Bloom Filter Operations

        private void BloomAdd(string hash)
        {
            foreach (var index in GetBloomIndices(hash))
            {
                int byteIndex = index / 8;
                int bitIndex = index % 8;
                _bloomFilter[byteIndex] |= (byte)(1 << bitIndex);
            }
        }

        private bool BloomContains(string hash)
        {
            foreach (var index in GetBloomIndices(hash))
            {
                int byteIndex = index / 8;
                int bitIndex = index % 8;
                if ((_bloomFilter[byteIndex] & (1 << bitIndex)) == 0)
                    return false;
            }
            return true;
        }

        private IEnumerable<int> GetBloomIndices(string hash)
        {
            // Use different parts of the hash for multiple indices.
            // Reuse SHA256.HashData (static, no allocation) instead of creating
            // a new SHA256 instance per index — 7x fewer allocations per call.
            var hashBytes = Encoding.UTF8.GetBytes(hash);
            int bitsCount = _bloomFilterSize * 8;
            var combined = new byte[hashBytes.Length + 1];
            Buffer.BlockCopy(hashBytes, 0, combined, 0, hashBytes.Length);

            for (int i = 0; i < BloomHashCount; i++)
            {
                combined[^1] = (byte)i;
                var h = SHA256.HashData(combined);
                int index = Math.Abs(BitConverter.ToInt32(h, 0)) % bitsCount;
                yield return index;
            }
        }

        private double CalculateFalsePositiveRate()
        {
            // p = (1 - e^(-k*n/m))^k
            // k = hash functions, n = items, m = bits
            double k = BloomHashCount;
            double n = _totalTracked;
            double m = _bloomFilterSize * 8.0;
            
            if (n == 0) return 0;
            
            double p = Math.Pow(1 - Math.Exp(-k * n / m), k);
            return p * 100; // Return as percentage
        }

        #endregion

        #region Persistence

        private void LoadFromDisk()
        {
            try
            {
                // Load stats
                if (File.Exists(_statsPath))
                {
                    var json = File.ReadAllText(_statsPath);
                    var stats = System.Text.Json.JsonSerializer.Deserialize<SavedStats>(json);
                    if (stats != null)
                    {
                        _totalTracked = stats.TotalTracked;
                        _duplicatesAvoided = stats.DuplicatesAvoided;
                        _firstTrackedDate = stats.FirstTrackedDate;
                    }
                }
                else
                {
                    _firstTrackedDate = DateTime.UtcNow;
                }

                // Load bloom filter only if size matches current configuration
                if (File.Exists(_bloomFilterPath))
                {
                    var fileInfo = new FileInfo(_bloomFilterPath);
                    if (fileInfo.Length == _bloomFilterSize)
                    {
                        var bloomData = File.ReadAllBytes(_bloomFilterPath);
                        Buffer.BlockCopy(bloomData, 0, _bloomFilter, 0, _bloomFilterSize);
                    }
                    else
                    {
                        // Size changed (e.g., reduced from 128MB to 16MB) — rebuild from hash cache
                        DebugLogger.Log($"MnemonicTracker: Bloom filter size changed ({fileInfo.Length / 1024 / 1024}MB → {_bloomFilterSize / 1024 / 1024}MB), rebuilding...");
                    }
                }

                // Load hash cache (only last MaxMemoryCacheSize entries to limit RAM)
                if (File.Exists(_hashStorePath))
                {
                    try
                    {
                        using var reader = new BinaryReader(File.OpenRead(_hashStorePath));
                        var count = reader.ReadInt32();

                        // Basic sanity bound to guard against corrupted headers
                        if (count < 0 || count > 100_000_000)
                            throw new InvalidDataException($"Invalid mnemonic hash count: {count}");

                        // Skip older entries to cap memory usage
                        int skipCount = Math.Max(0, count - MaxMemoryCacheSize);
                        for (int i = 0; i < skipCount; i++)
                        {
                            reader.ReadString(); // skip old entry (bloom filter already has it)
                        }

                        for (int i = skipCount; i < count; i++)
                        {
                            var hash = reader.ReadString();
                            if (!string.IsNullOrWhiteSpace(hash))
                            {
                                _memoryCache.Add(hash);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Log($"MnemonicTracker hash store corrupted, resetting cache file: {ex.Message}");

                        // Keep app startup healthy by resetting only the corrupted hash store
                        try
                        {
                            var backupPath = _hashStorePath + ".corrupt." + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                            File.Move(_hashStorePath, backupPath, true);
                        }
                        catch
                        {
                            try { File.Delete(_hashStorePath); } catch { }
                        }

                        _memoryCache.Clear();
                    }
                }

                DebugLogger.Log($"MnemonicTracker loaded: {_totalTracked:N0} mnemonics tracked, " +
                                  $"{_duplicatesAvoided:N0} duplicates avoided since {_firstTrackedDate:d}");
                
                // If bloom filter was reset (size change) or failed to load, rebuild from cached hashes.
                // Sample 1000 evenly-spaced bytes across the full filter to reliably detect an empty filter,
                // avoiding the previous bug where only the first 100 bytes were checked (often all zero
                // even on a populated filter, causing unnecessary and wasteful rebuilds every startup).
                if (_memoryCache.Count > 0)
                {
                    bool bloomEmpty = true;
                    int step = Math.Max(1, _bloomFilterSize / 1000);
                    for (int i = 0; i < _bloomFilterSize; i += step)
                    {
                        if (_bloomFilter[i] != 0) { bloomEmpty = false; break; }
                    }
                    if (bloomEmpty)
                    {
                        foreach (var hash in _memoryCache)
                            BloomAdd(hash);
                        DebugLogger.Log($"MnemonicTracker: Rebuilt bloom filter from {_memoryCache.Count:N0} cached hashes");
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Error loading mnemonic tracker: {ex.Message}");
                _firstTrackedDate = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Saves tracker state to disk
        /// </summary>
        public async Task SaveToDiskAsync()
        {
            if (!_isDirty)
                return;
            
            try
            {
                lock (_lock)
                {
                    // Save stats
                    var stats = new SavedStats
                    {
                        TotalTracked = _totalTracked,
                        DuplicatesAvoided = _duplicatesAvoided,
                        FirstTrackedDate = _firstTrackedDate,
                        LastSaved = DateTime.UtcNow
                    };
                    var json = System.Text.Json.JsonSerializer.Serialize(stats, 
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_statsPath, json);

                    // Save bloom filter
                    File.WriteAllBytes(_bloomFilterPath, _bloomFilter);

                    // Save hash cache (for verification against bloom false positives)
                    using var writer = new BinaryWriter(File.Create(_hashStorePath));
                    writer.Write(_memoryCache.Count);
                    foreach (var hash in _memoryCache)
                    {
                        writer.Write(hash);
                    }
                    
                    _isDirty = false;
                    Interlocked.Exchange(ref _newEntriesSinceLastSave, 0);
                }
                
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Error saving mnemonic tracker: {ex.Message}");
            }
        }

        /// <summary>
        /// Forces immediate save to disk
        /// </summary>
        public void Flush()
        {
            _isDirty = true;
            SaveToDiskAsync().Wait();
        }

        private class SavedStats
        {
            public long TotalTracked { get; set; }
            public long DuplicatesAvoided { get; set; }
            public DateTime FirstTrackedDate { get; set; }
            public DateTime LastSaved { get; set; }
        }

        #endregion

        #region Helpers

        private bool ShouldProcessOnThisNode(string hash)
        {
            if (!_clusterShardingEnabled || _clusterNodeCount <= 1)
                return true;

            try
            {
                // Use first 8 hex chars of SHA-256 hash for stable bucket assignment
                uint bucketSeed = Convert.ToUInt32(hash.Substring(0, 8), 16);
                int assignedNode = (int)(bucketSeed % (uint)_clusterNodeCount);
                return assignedNode == _clusterNodeIndex;
            }
            catch
            {
                // Fail-safe: process locally if parsing fails
                return true;
            }
        }

        private static string NormalizeMnemonic(string mnemonic)
        {
            // Normalize: lowercase, trim, single spaces
            return string.Join(" ", mnemonic.ToLowerInvariant()
                .Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string ComputeHash(string input)
        {
            // Use SHA-256 to create a fixed-size hash
            // We never store the actual mnemonic, only its hash
            using var sha256 = SHA256.Create();
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes);
        }

        #endregion

        #region Maintenance

        /// <summary>
        /// Clears all tracked mnemonics (use with caution!)
        /// </summary>
        public void ClearAll()
        {
            lock (_lock)
            {
                Array.Clear(_bloomFilter, 0, _bloomFilter.Length);
                _memoryCache.Clear();
                _totalTracked = 0;
                _duplicatesAvoided = 0;
                _firstTrackedDate = DateTime.UtcNow;
                _isDirty = true;
            }
            
            Flush();
        }

        /// <summary>
        /// Resets only the in-memory state so mnemonics that were skipped due to
        /// API failures in a previous session get rechecked. Does NOT delete disk data.
        /// Call this at the start of each new scan session.
        /// </summary>
        public void ResetSession()
        {
            lock (_lock)
            {
                Array.Clear(_bloomFilter, 0, _bloomFilter.Length);
                _memoryCache.Clear();
                // Rebuild bloom from disk so truly-checked mnemonics are still skipped
                LoadFromDisk();
            }
            DebugLogger.Log($"[MnemonicTracker] Session reset: {_totalTracked:N0} previously scanned mnemonics reloaded from disk");
        }

        /// <summary>
        /// Gets the storage location
        /// </summary>
        public string GetStorageLocation() => _dataDirectory;

        /// <summary>
        /// Exports all tracked hashes (for backup)
        /// </summary>
        public async Task ExportAsync(string filePath)
        {
            lock (_lock)
            {
                using var writer = new StreamWriter(filePath);
                writer.WriteLine($"# Mnemonic Tracker Export");
                writer.WriteLine($"# Total: {_totalTracked}");
                writer.WriteLine($"# Since: {_firstTrackedDate:O}");
                writer.WriteLine($"# Exported: {DateTime.UtcNow:O}");
                writer.WriteLine();
                
                foreach (var hash in _memoryCache)
                {
                    writer.WriteLine(hash);
                }
            }
            
            await Task.CompletedTask;
        }

        /// <summary>
        /// Imports hashes from backup
        /// </summary>
        public async Task ImportAsync(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Import file not found", filePath);
            
            int imported = 0;
            
            using var reader = new StreamReader(filePath);
            string line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                    continue;
                
                var hash = line.Trim();
                if (hash.Length == 64) // SHA-256 hex length
                {
                    lock (_lock)
                    {
                        if (_memoryCache.Add(hash))
                        {
                            BloomAdd(hash);
                            _totalTracked++;
                            imported++;
                        }
                    }
                }
            }
            
            _isDirty = true;
            await SaveToDiskAsync();
            
            DebugLogger.Log($"Imported {imported:N0} mnemonic hashes");
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed)
                return;
            
            _disposed = true;
            _autoSaveTimer?.Dispose();
            
            // Final save
            Flush();
        }

        #endregion

        public class TrackerStats
        {
            public long TotalMnemonicsTracked { get; set; }
            public long DuplicatesAvoided { get; set; }
            public DateTime FirstTrackedDate { get; set; }
            public int MemoryCacheSize { get; set; }
            public int BloomFilterSizeMB { get; set; }
            public double EstimatedFalsePositiveRate { get; set; }
        }

        private class ClusterCookie
        {
            public string DeviceId { get; set; }
            public int NodeIndex { get; set; }
            public int NodeCount { get; set; }
            public string InstalledAt { get; set; }
        }
    }

    /// <summary>
    /// Global singleton for mnemonic tracking across the application
    /// </summary>
    public static class GlobalMnemonicTracker
    {
        private static readonly Lazy<MnemonicTracker> _instance = 
            new Lazy<MnemonicTracker>(() => new MnemonicTracker());
        
        public static MnemonicTracker Instance => _instance.Value;
        
        /// <summary>
        /// Check if mnemonic is new and mark it as scanned
        /// Returns true if NEW, false if already scanned before
        /// </summary>
        public static bool IsNew(string mnemonic) => Instance.TryMarkAsScanned(mnemonic);
        
        /// <summary>
        /// Check if mnemonic was scanned before (without marking)
        /// </summary>
        public static bool WasScanned(string mnemonic) => Instance.WasScanned(mnemonic);
        
        /// <summary>
        /// Filter list to only new mnemonics
        /// </summary>
        public static List<string> FilterNew(IEnumerable<string> mnemonics) => 
            Instance.FilterNewMnemonics(mnemonics);
    }
}
