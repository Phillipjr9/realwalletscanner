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
        private readonly HashSet<UInt128> _memoryCache;
        private readonly Queue<UInt128> _memoryCacheOrder = new Queue<UInt128>();
        private readonly HashSet<UInt128> _fundedWhitelist = new HashSet<UInt128>();
        private bool _hasEvictedFromMemoryCache;
        private const int MaxMemoryCacheSize = 500_000; // Cap to prevent multi-GB RAM usage
        private const int BinaryFormatMagic = -2; // Header marker for compact 16-byte UInt128 hash store

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
        private const int IncrementalSaveThreshold = 500;
        
        // Auto-save interval
        private readonly Timer _autoSaveTimer;
        private bool _isDirty;

        public long TotalTracked => _totalTracked;
        public int Count => (int)Math.Min(int.MaxValue, _totalTracked);
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
            
            _memoryCache = new HashSet<UInt128>();

            // Cluster sharding configuration (only active when explicitly enabled in AppSettings)
            _clusterShardingEnabled = settings.EnableClusterSharding;
            _clusterNodeIndex = settings.ClusterNodeIndex;
            _clusterNodeCount = settings.ClusterNodeCount;

            if (_clusterShardingEnabled)
            {
                var envNodeIndex = Environment.GetEnvironmentVariable("WRT_CLUSTER_NODE_INDEX");
                var envNodeCount = Environment.GetEnvironmentVariable("WRT_CLUSTER_NODE_COUNT");

                if (int.TryParse(envNodeCount, out var parsedNodeCount) && parsedNodeCount > 0)
                    _clusterNodeCount = parsedNodeCount;

                if (int.TryParse(envNodeIndex, out var parsedNodeIndex) && parsedNodeIndex >= 0)
                    _clusterNodeIndex = parsedNodeIndex;
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

        private static readonly HashSet<string> KnownTrivialTestVectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about",
            "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon art",
            "zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo wrong",
            "zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo vote",
            "legal winner thank year wave sausage worth useful legal winner thank yellow",
            "letter advice cage absurd amount doctor acoustic avoid letter advice cage above",
            "void come effort suffer camp survey warrior heavy shoot primary clutch crush",
            "all all all all all all all all all all all all",
            "bacon bacon bacon bacon bacon bacon bacon bacon bacon bacon bacon bacon",
            "bacon bacon bacon bacon bacon bacon bacon bacon bacon bacon bacon bacon bacon bacon bacon bacon bacon bacon bacon bacon bacon bacon bacon bacon"
        };

        private static readonly HashSet<string> KnownTrivialPrivateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "0000000000000000000000000000000000000000000000000000000000000000",
            "0000000000000000000000000000000000000000000000000000000000000001",
            "0000000000000000000000000000000000000000000000000000000000000002",
            "0000000000000000000000000000000000000000000000000000000000000003",
            "0000000000000000000000000000000000000000000000000000000000000010",
            "1111111111111111111111111111111111111111111111111111111111111111",
            "2222222222222222222222222222222222222222222222222222222222222222",
            "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef",
            "cafebabecafebabecafebabecafebabecafebabecafebabecafebabecafebabe",
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" // SHA256("")
        };

        /// <summary>
        /// Strictly restricts invalid secp256k1 private keys (not 64 hex chars, 0, or >= curve order N),
        /// repeated single-byte keys (010101..., ffffff...), and known public test-vector keys (0x01, SHA256(""), etc.).
        /// </summary>
        public static bool IsRestrictedTrivialPrivateKey(string hexKey)
        {
            if (string.IsNullOrWhiteSpace(hexKey))
                return true;

            var clean = hexKey.Trim().ToLowerInvariant();
            if (clean.StartsWith("pk:")) clean = clean.Substring(3).Trim();
            if (clean.StartsWith("0x")) clean = clean.Substring(2).Trim();

            if (clean.Length != 64)
                return true;

            if (KnownTrivialPrivateKeys.Contains(clean))
                return true;

            // Block repeated 2-char hex byte across all 32 bytes (e.g. 050505...05, aaaaaa...aa)
            string firstByte = clean.Substring(0, 2);
            bool allSameByte = true;
            for (int i = 2; i < 64; i += 2)
            {
                if (clean[i] != firstByte[0] || clean[i + 1] != firstByte[1])
                {
                    allSameByte = false;
                    break;
                }
            }
            if (allSameByte)
                return true;

            // Block keys >= secp256k1 curve order N (fffffffffffffffffffffffffffffffebaaedce6af48a03bbfd25e8cd0364141)
            if (clean.StartsWith("ffffffffffffffffffffffffffffffff"))
                return true;

            return false;
        }

        /// <summary>
        /// Strictly restricts known BIP39 test vectors, repeated-word mnemonics (e.g. "abandon abandon..."),
        /// consecutive duplicate words, and low-distinct-word degenerate patterns that have zero realistic chance of holding funds.
        /// </summary>
        public static bool IsRestrictedTrivialMnemonic(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return true;

            var normalized = string.Join(" ", input.Trim().ToLowerInvariant()
                .Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries));

            if (KnownTrivialTestVectors.Contains(normalized))
                return true;

            // Block any phrase or brainwallet containing 3+ consecutive "abandon" words or the test vector
            if (normalized.Contains("abandon abandon") || normalized.Contains("zoo zoo zoo") || normalized.Contains("bacon bacon bacon"))
                return true;

            if (normalized.StartsWith("pk:"))
                return IsRestrictedTrivialPrivateKey(normalized);

            if (normalized.StartsWith("addr:"))
                return false;

            // Extract mnemonic portion if prefixed with deriv: or passphrase: or brain:
            var checkPart = normalized;
            if (checkPart.StartsWith("brain:"))
                checkPart = checkPart.Substring("brain:".Length).Trim();
            else if (checkPart.StartsWith("passphrase:"))
            {
                var parts = checkPart.Split(':');
                if (parts.Length >= 2) checkPart = parts[1];
            }
            else if (checkPart.StartsWith("deriv:"))
            {
                var parts = checkPart.Split(':');
                if (parts.Length >= 2) checkPart = parts[1];
            }

            if (KnownTrivialTestVectors.Contains(checkPart) || checkPart.Contains("abandon abandon"))
                return true;

            var words = checkPart.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 12 || words.Length == 15 || words.Length == 18 || words.Length == 21 || words.Length == 24)
            {
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < words.Length; i++)
                {
                    // Block adjacent identical words (e.g. "abandon abandon ...", "able able ...")
                    if (i > 0 && string.Equals(words[i], words[i - 1], StringComparison.OrdinalIgnoreCase))
                        return true;

                    counts.TryGetValue(words[i], out int c);
                    c++;
                    // Block if any single word repeats 3 or more times
                    if (c >= 3)
                        return true;
                    counts[words[i]] = c;
                }

                // Require high word diversity (at least 9 distinct words for 12-word seed, 16 for 24-word seed)
                int minDistinct = words.Length >= 24 ? 16 : (words.Length * 3) / 4;
                if (counts.Count < minDistinct)
                    return true;
            }

            return false;
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

            // STRICT TRIVIAL/REPEATED-WORD RESTRICTION: Never scan "abandon abandon..." or degenerate patterns
            if (IsRestrictedTrivialMnemonic(mnemonic))
            {
                lock (_lock)
                {
                    _duplicatesAvoided++;
                }
                return false;
            }
            
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
                // Funded wallets are whitelisted so they can always be re-inspected
                if (_fundedWhitelist.Contains(hash))
                {
                    return true;
                }

                // Fast bloom filter check first
                if (BloomContains(hash))
                {
                    // Check memory cache, or rely on bloom filter if older entries were evicted
                    if (_memoryCache.Contains(hash) || _hasEvictedFromMemoryCache || _totalTracked > _memoryCache.Count)
                    {
                        _duplicatesAvoided++;
                        return false; // Already seen
                    }
                }
                
                // New mnemonic - add to both bloom filter and cache
                BloomAdd(hash);
                
                // Cap memory cache using FIFO eviction instead of wiping all entries
                while (_memoryCache.Count >= MaxMemoryCacheSize && _memoryCacheOrder.Count > 0)
                {
                    var oldest = _memoryCacheOrder.Dequeue();
                    _memoryCache.Remove(oldest);
                    _hasEvictedFromMemoryCache = true;
                }
                if (_memoryCache.Add(hash))
                {
                    _memoryCacheOrder.Enqueue(hash);
                }
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

            if (IsRestrictedTrivialMnemonic(mnemonic))
                return true;
            
            var normalized = NormalizeMnemonic(mnemonic);
            var hash = ComputeHash(normalized);
            
            lock (_lock)
            {
                if (_fundedWhitelist.Contains(hash))
                    return false;

                // Bloom filter says definitely not seen
                if (!BloomContains(hash))
                    return false;
                
                // Check memory cache for certainty, or fall back to bloom filter if entries were evicted
                return _memoryCache.Contains(hash) || _hasEvictedFromMemoryCache || _totalTracked > _memoryCache.Count;
            }
        }

        /// <summary>
        /// Checks whether an address or private key was already scanned and confirmed empty across any section.
        /// </summary>
        public bool IsAddressOrKeyScannedEmpty(string addressOrKey)
        {
            if (string.IsNullOrWhiteSpace(addressOrKey))
                return false;

            var normalized = addressOrKey.Trim().ToLowerInvariant();
            if (WasScanned("addr:" + normalized) || WasScanned("pk:" + normalized))
            {
                lock (_lock)
                {
                    _duplicatesAvoided++;
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Strict cross-section check: verifies that neither the wallet's mnemonic, private key,
        /// nor primary BTC/ETH addresses have been previously scanned as empty.
        /// Returns true if the wallet is NEW (should be scanned), or false if it was already scanned empty.
        /// </summary>
        public bool TryMarkWalletCandidate(CryptoWalletGenerator.WalletInfo wallet, string extraKey = null)
        {
            if (wallet == null)
                return false;

            // Check if any identifier of this wallet was already confirmed empty
            if (!string.IsNullOrWhiteSpace(extraKey) && WasScanned(extraKey))
            {
                lock (_lock) { _duplicatesAvoided++; }
                return false;
            }
            if (!string.IsNullOrWhiteSpace(wallet.Mnemonic) && WasScanned(wallet.Mnemonic))
            {
                lock (_lock) { _duplicatesAvoided++; }
                return false;
            }
            if (!string.IsNullOrWhiteSpace(wallet.PrivateKey) && WasScanned("pk:" + wallet.PrivateKey.Trim().ToLowerInvariant()))
            {
                lock (_lock) { _duplicatesAvoided++; }
                return false;
            }
            if (!string.IsNullOrWhiteSpace(wallet.BitcoinAddress) &&
                (WasScanned("addr:" + wallet.BitcoinAddress.Trim().ToLowerInvariant()) ||
                 FundedAddressFilter.BTCEmpty.IsKnownEmpty(wallet.BitcoinAddress)))
            {
                lock (_lock) { _duplicatesAvoided++; }
                return false;
            }
            if (!string.IsNullOrWhiteSpace(wallet.EthereumAddress) &&
                (WasScanned("addr:" + wallet.EthereumAddress.Trim().ToLowerInvariant()) ||
                 FundedAddressFilter.ETHEmpty.IsKnownEmpty(wallet.EthereumAddress)))
            {
                lock (_lock) { _duplicatesAvoided++; }
                return false;
            }

            // Mark primary input as in-flight/scanned so concurrent threads don't repeat it
            if (!string.IsNullOrWhiteSpace(extraKey))
                return TryMarkAsScanned(extraKey);
            if (!string.IsNullOrWhiteSpace(wallet.Mnemonic))
                return TryMarkAsScanned(wallet.Mnemonic);
            if (!string.IsNullOrWhiteSpace(wallet.PrivateKey))
                return TryMarkAsScanned("pk:" + wallet.PrivateKey.Trim().ToLowerInvariant());

            return true;
        }

        /// <summary>
        /// Permanently records a confirmed-empty wallet across ALL identifiers (mnemonic, private key,
        /// BTC/ETH/SOL addresses, and empty address Bloom filters) so it can NEVER repeat in any section.
        /// </summary>
        public void MarkWalletConfirmedEmpty(CryptoWalletGenerator.WalletInfo wallet, string extraKey = null)
        {
            if (!string.IsNullOrWhiteSpace(extraKey))
                TryMarkAsScanned(extraKey);

            if (wallet == null)
                return;

            if (!string.IsNullOrWhiteSpace(wallet.Mnemonic))
                TryMarkAsScanned(wallet.Mnemonic);

            if (!string.IsNullOrWhiteSpace(wallet.PrivateKey))
                TryMarkAsScanned("pk:" + wallet.PrivateKey.Trim().ToLowerInvariant());

            if (!string.IsNullOrWhiteSpace(wallet.BitcoinAddress))
            {
                TryMarkAsScanned("addr:" + wallet.BitcoinAddress.Trim().ToLowerInvariant());
                FundedAddressFilter.BTCEmpty.MarkAsEmpty(wallet.BitcoinAddress);
            }
            if (!string.IsNullOrWhiteSpace(wallet.BitcoinAddressSegWit))
            {
                TryMarkAsScanned("addr:" + wallet.BitcoinAddressSegWit.Trim().ToLowerInvariant());
                FundedAddressFilter.BTCEmpty.MarkAsEmpty(wallet.BitcoinAddressSegWit);
            }
            if (!string.IsNullOrWhiteSpace(wallet.BitcoinAddressNativeSegWit))
            {
                TryMarkAsScanned("addr:" + wallet.BitcoinAddressNativeSegWit.Trim().ToLowerInvariant());
                FundedAddressFilter.BTCEmpty.MarkAsEmpty(wallet.BitcoinAddressNativeSegWit);
            }
            if (!string.IsNullOrWhiteSpace(wallet.BitcoinAddressTaproot))
            {
                TryMarkAsScanned("addr:" + wallet.BitcoinAddressTaproot.Trim().ToLowerInvariant());
                FundedAddressFilter.BTCEmpty.MarkAsEmpty(wallet.BitcoinAddressTaproot);
            }
            if (!string.IsNullOrWhiteSpace(wallet.EthereumAddress))
            {
                TryMarkAsScanned("addr:" + wallet.EthereumAddress.Trim().ToLowerInvariant());
                FundedAddressFilter.ETHEmpty.MarkAsEmpty(wallet.EthereumAddress);
            }
            if (!string.IsNullOrWhiteSpace(wallet.SolanaAddress))
            {
                TryMarkAsScanned("addr:" + wallet.SolanaAddress.Trim().ToLowerInvariant());
            }
        }

        /// <summary>
        /// Whitelists a funded wallet (> 0 balance) so that it is never treated as an empty wallet
        /// and can always be re-inspected across tabs.
        /// </summary>
        public void MarkWalletConfirmedFunded(CryptoWalletGenerator.WalletInfo wallet, string extraKey = null)
        {
            lock (_lock)
            {
                void WhitelistKey(string k)
                {
                    if (string.IsNullOrWhiteSpace(k)) return;
                    var h = ComputeHash(NormalizeMnemonic(k));
                    _fundedWhitelist.Add(h);
                    _memoryCache.Remove(h);
                }

                WhitelistKey(extraKey);
                if (wallet != null)
                {
                    WhitelistKey(wallet.Mnemonic);
                    if (!string.IsNullOrWhiteSpace(wallet.PrivateKey))
                        WhitelistKey("pk:" + wallet.PrivateKey.Trim().ToLowerInvariant());
                    if (!string.IsNullOrWhiteSpace(wallet.BitcoinAddress))
                        WhitelistKey("addr:" + wallet.BitcoinAddress.Trim().ToLowerInvariant());
                    if (!string.IsNullOrWhiteSpace(wallet.BitcoinAddressSegWit))
                        WhitelistKey("addr:" + wallet.BitcoinAddressSegWit.Trim().ToLowerInvariant());
                    if (!string.IsNullOrWhiteSpace(wallet.BitcoinAddressNativeSegWit))
                        WhitelistKey("addr:" + wallet.BitcoinAddressNativeSegWit.Trim().ToLowerInvariant());
                    if (!string.IsNullOrWhiteSpace(wallet.BitcoinAddressTaproot))
                        WhitelistKey("addr:" + wallet.BitcoinAddressTaproot.Trim().ToLowerInvariant());
                    if (!string.IsNullOrWhiteSpace(wallet.EthereumAddress))
                        WhitelistKey("addr:" + wallet.EthereumAddress.Trim().ToLowerInvariant());
                    if (!string.IsNullOrWhiteSpace(wallet.SolanaAddress))
                        WhitelistKey("addr:" + wallet.SolanaAddress.Trim().ToLowerInvariant());
                }
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

        private void BloomAdd(UInt128 hash)
        {
            Span<int> indices = stackalloc int[BloomHashCount];
            FillBloomIndices(hash, indices);
            for (int i = 0; i < BloomHashCount; i++)
            {
                int index = indices[i];
                int byteIndex = index / 8;
                int bitIndex = index % 8;
                _bloomFilter[byteIndex] |= (byte)(1 << bitIndex);
            }
        }

        private bool BloomContains(UInt128 hash)
        {
            Span<int> indices = stackalloc int[BloomHashCount];
            FillBloomIndices(hash, indices);
            for (int i = 0; i < BloomHashCount; i++)
            {
                int index = indices[i];
                int byteIndex = index / 8;
                int bitIndex = index % 8;
                if ((_bloomFilter[byteIndex] & (1 << bitIndex)) == 0)
                    return false;
            }
            return true;
        }

        private void FillBloomIndices(UInt128 hash, Span<int> indices)
        {
            // Zero-allocation double-hashing from 128-bit SHA-256 prefix
            ulong h1 = (ulong)(hash >> 64);
            ulong h2 = (ulong)hash;
            if (h2 == 0) h2 = 0x9E3779B97F4A7C15UL;
            uint bitsCount = (uint)(_bloomFilterSize * 8);

            for (int i = 0; i < BloomHashCount; i++)
            {
                ulong combined = h1 + (ulong)i * h2 + (ulong)(i * i);
                // Mix bits (MurmurHash3 finalizer)
                combined ^= combined >> 33;
                combined *= 0xff51afd7ed558ccdUL;
                combined ^= combined >> 33;
                indices[i] = (int)((uint)combined % bitsCount);
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

                // Load hash cache (supports both compact BinaryFormatMagic=-2 and legacy string format)
                bool migratedLegacyStore = false;
                if (File.Exists(_hashStorePath))
                {
                    try
                    {
                        using var reader = new BinaryReader(File.OpenRead(_hashStorePath));
                        var firstInt = reader.ReadInt32();

                        if (firstInt == BinaryFormatMagic)
                        {
                            var count = reader.ReadInt32();
                            if (count < 0 || count > 100_000_000)
                                throw new InvalidDataException($"Invalid mnemonic hash count: {count}");

                            int skipCount = Math.Max(0, count - MaxMemoryCacheSize);
                            if (skipCount > 0)
                            {
                                _hasEvictedFromMemoryCache = true;
                                reader.BaseStream.Seek((long)skipCount * 16, SeekOrigin.Current);
                            }

                            for (int i = skipCount; i < count; i++)
                            {
                                ulong upper = reader.ReadUInt64();
                                ulong lower = reader.ReadUInt64();
                                var hash = new UInt128(upper, lower);
                                if (_memoryCache.Add(hash))
                                {
                                    _memoryCacheOrder.Enqueue(hash);
                                }
                            }
                        }
                        else
                        {
                            // Legacy string format
                            var count = firstInt;
                            if (count < 0 || count > 100_000_000)
                                throw new InvalidDataException($"Invalid mnemonic hash count: {count}");

                            migratedLegacyStore = true;
                            int skipCount = Math.Max(0, count - MaxMemoryCacheSize);
                            if (skipCount > 0)
                            {
                                _hasEvictedFromMemoryCache = true;
                            }
                            for (int i = 0; i < skipCount; i++)
                            {
                                reader.ReadString();
                            }

                            for (int i = skipCount; i < count; i++)
                            {
                                var hashStr = reader.ReadString();
                                if (!string.IsNullOrWhiteSpace(hashStr) && TryParseHexToUInt128(hashStr, out var parsedHash))
                                {
                                    if (_memoryCache.Add(parsedHash))
                                    {
                                        _memoryCacheOrder.Enqueue(parsedHash);
                                    }
                                }
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
                        _memoryCacheOrder.Clear();
                    }
                }

                DebugLogger.Log($"MnemonicTracker loaded: {_totalTracked:N0} mnemonics tracked, " +
                                  $"{_duplicatesAvoided:N0} duplicates avoided since {_firstTrackedDate:d}");
                
                // If bloom filter was reset, migrated from legacy format, or empty, rebuild from cached hashes
                if (_memoryCache.Count > 0)
                {
                    bool bloomEmpty = migratedLegacyStore;
                    if (!bloomEmpty)
                    {
                        bloomEmpty = true;
                        int step = Math.Max(1, _bloomFilterSize / 1000);
                        for (int i = 0; i < _bloomFilterSize; i += step)
                        {
                            if (_bloomFilter[i] != 0) { bloomEmpty = false; break; }
                        }
                    }
                    if (bloomEmpty)
                    {
                        Array.Clear(_bloomFilter, 0, _bloomFilter.Length);
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

                    // Save compact 16-byte UInt128 hash cache (for verification against bloom false positives)
                    using var writer = new BinaryWriter(File.Create(_hashStorePath));
                    writer.Write(BinaryFormatMagic);
                    writer.Write(_memoryCache.Count);
                    foreach (var hash in _memoryCache)
                    {
                        writer.Write((ulong)(hash >> 64));
                        writer.Write((ulong)hash);
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

        private bool ShouldProcessOnThisNode(UInt128 hash)
        {
            if (!_clusterShardingEnabled || _clusterNodeCount <= 1)
                return true;

            uint bucketSeed = (uint)(hash >> 96);
            int assignedNode = (int)(bucketSeed % (uint)_clusterNodeCount);
            return assignedNode == _clusterNodeIndex;
        }

        private static string NormalizeMnemonic(string mnemonic)
        {
            // Normalize: lowercase, trim, single spaces
            return string.Join(" ", mnemonic.ToLowerInvariant()
                .Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static UInt128 ComputeHash(string input)
        {
            // Zero-allocation SHA-256 -> 128-bit compact hash (never stores the actual mnemonic)
            Span<byte> utf8 = stackalloc byte[Math.Min(512, Encoding.UTF8.GetMaxByteCount(input.Length))];
            int written = Encoding.UTF8.GetBytes(input.AsSpan(), utf8);
            Span<byte> hashBytes = stackalloc byte[32];
            SHA256.HashData(utf8[..written], hashBytes);
            ulong upper = BitConverter.ToUInt64(hashBytes[..8]);
            ulong lower = BitConverter.ToUInt64(hashBytes.Slice(8, 8));
            return new UInt128(upper, lower);
        }

        private static bool TryParseHexToUInt128(string hex, out UInt128 result)
        {
            result = default;
            if (string.IsNullOrWhiteSpace(hex) || hex.Length < 32)
                return false;
            try
            {
                Span<byte> bytes = stackalloc byte[16];
                for (int i = 0; i < 16; i++)
                {
                    bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
                }
                ulong upper = BitConverter.ToUInt64(bytes[..8]);
                ulong lower = BitConverter.ToUInt64(bytes.Slice(8, 8));
                result = new UInt128(upper, lower);
                return true;
            }
            catch
            {
                return false;
            }
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
                _memoryCacheOrder.Clear();
                _hasEvictedFromMemoryCache = false;
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
            // CRITICAL: Flush any unsaved empty wallets to disk BEFORE reloading so switching
            // tabs or starting a new scan never loses recently scanned empty wallets!
            if (_isDirty)
            {
                Flush();
            }

            lock (_lock)
            {
                Array.Clear(_bloomFilter, 0, _bloomFilter.Length);
                _memoryCache.Clear();
                _memoryCacheOrder.Clear();
                _hasEvictedFromMemoryCache = false;
                // Rebuild bloom from disk so truly-checked mnemonics are still skipped
                LoadFromDisk();
            }
            DebugLogger.Log($"[MnemonicTracker] Session synchronized: {_totalTracked:N0} previously scanned empty wallets loaded from disk");
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
                    writer.WriteLine($"{(ulong)(hash >> 64):X16}{(ulong)hash:X16}");
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
                if ((hash.Length == 32 || hash.Length == 64) && TryParseHexToUInt128(hash, out var parsedHash))
                {
                    lock (_lock)
                    {
                        if (_memoryCache.Add(parsedHash))
                        {
                            _memoryCacheOrder.Enqueue(parsedHash);
                            BloomAdd(parsedHash);
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
