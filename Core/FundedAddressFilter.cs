using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// OFFLINE PRE-FILTER: Bloom filter of funded Bitcoin/Ethereum addresses.
    /// Loaded from a text file (one address per line) at startup.
    /// Allows checking 50,000+ addresses/sec locally — only call APIs for matches.
    /// 
    /// Usage:
    ///   1. Place a funded address list at AppData/WalletRecoveryTool/FundedAddresses/btc_funded.txt
    ///   2. On first load, builds a bloom filter and saves it as .bloom for fast reload
    ///   3. SmartWalletHunter checks addresses here BEFORE making API calls
    ///   4. Only bloom filter matches trigger an API call to confirm balance
    /// 
    /// Data sources:
    ///   - Bitcoin UTXO dumps (all addresses with current balance)
    ///   - Historical funded address lists from blockchain explorers
    ///   - Custom address lists (one address per line, any format)
    /// </summary>
    public class FundedAddressFilter
    {
        private BitArray _bitArray;
        private int _bitArraySize;
        private int _hashFunctionCount;
        private long _addressCount;
        private long _expectedCapacity; // Track capacity for overflow detection
        private bool _isLoaded;
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();

        private static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WalletRecoveryTool", "FundedAddresses");

        private static readonly string BtcBloomPath = Path.Combine(DataDir, "btc_funded.bloom");
        private static readonly string EthBloomPath = Path.Combine(DataDir, "eth_funded.bloom");

        // Singleton instances for BTC and ETH
        private static FundedAddressFilter _btcInstance;
        private static FundedAddressFilter _ethInstance;
        private static readonly object _initLock = new object();

        public bool IsLoaded => _isLoaded;
        public long AddressCount => _addressCount;

        /// <summary>
        /// Get the BTC funded address filter (singleton, lazy-loaded)
        /// </summary>
        public static FundedAddressFilter BTC
        {
            get
            {
                if (_btcInstance == null)
                {
                    lock (_initLock)
                    {
                        _btcInstance ??= new FundedAddressFilter();
                    }
                }
                return _btcInstance;
            }
        }

        /// <summary>
        /// Get the ETH funded address filter (singleton, lazy-loaded)
        /// </summary>
        public static FundedAddressFilter ETH
        {
            get
            {
                if (_ethInstance == null)
                {
                    lock (_initLock)
                    {
                        _ethInstance ??= new FundedAddressFilter();
                    }
                }
                return _ethInstance;
            }
        }

        public FundedAddressFilter()
        {
            Directory.CreateDirectory(DataDir);
        }

        /// <summary>
        /// Initialize BTC filter from address list file or saved bloom filter
        /// </summary>
        public static void InitializeBTC()
        {
            var txtFiles = FindAddressFiles("btc");
            if (File.Exists(BtcBloomPath))
            {
                BTC.LoadBloomFilter(BtcBloomPath);
                DebugLogger.Log($"[FundedFilter] BTC bloom filter loaded: {BTC.AddressCount:N0} addresses");
            }
            else if (txtFiles.Count > 0)
            {
                BTC.BuildFromTextFiles(txtFiles, BtcBloomPath);
                DebugLogger.Log($"[FundedFilter] BTC bloom filter built from {txtFiles.Count} files: {BTC.AddressCount:N0} addresses");
            }
            else
            {
                DebugLogger.Log("[FundedFilter] No BTC funded address files found. Offline mode disabled for BTC.");
                DebugLogger.Log($"[FundedFilter] Place address files in: {DataDir}");
                DebugLogger.Log("[FundedFilter] Accepted filenames: btc_funded.txt, btc_addresses.txt, funded_btc*.txt");
            }
        }

        /// <summary>
        /// Initialize ETH filter from address list file or saved bloom filter
        /// </summary>
        public static void InitializeETH()
        {
            var txtFiles = FindAddressFiles("eth");
            if (File.Exists(EthBloomPath))
            {
                ETH.LoadBloomFilter(EthBloomPath);
                DebugLogger.Log($"[FundedFilter] ETH bloom filter loaded: {ETH.AddressCount:N0} addresses");
            }
            else if (txtFiles.Count > 0)
            {
                ETH.BuildFromTextFiles(txtFiles, EthBloomPath);
                DebugLogger.Log($"[FundedFilter] ETH bloom filter built from {txtFiles.Count} files: {ETH.AddressCount:N0} addresses");
            }
            else
            {
                DebugLogger.Log("[FundedFilter] No ETH funded address files found. Offline mode disabled for ETH.");
            }
        }

        /// <summary>
        /// Find address text files matching pattern for the given crypto
        /// </summary>
        private static List<string> FindAddressFiles(string crypto)
        {
            if (!Directory.Exists(DataDir)) return new List<string>();

            var fullChainName = string.Equals(crypto, "btc", StringComparison.OrdinalIgnoreCase)
                ? "bitcoin"
                : string.Equals(crypto, "eth", StringComparison.OrdinalIgnoreCase)
                    ? "ethereum"
                    : crypto;

            var patterns = new[]
            {
                $"{crypto}_funded.txt",
                $"{crypto}_addresses.txt",
                $"funded_{crypto}*.txt",
                $"{crypto}_utxo*.txt",
                $"{crypto}_balances*.txt",
                $"{crypto}*.txt",
                $"{crypto}*.tsv",
                $"{crypto}*.csv",
                $"blockchair_{crypto}*.tsv",
                $"blockchair_{fullChainName}*.tsv"
            };

            var files = new HashSet<string>();
            foreach (var pattern in patterns)
            {
                foreach (var file in Directory.GetFiles(DataDir, pattern, SearchOption.TopDirectoryOnly))
                {
                    files.Add(file);
                }
            }
            return files.ToList();
        }

        /// <summary>
        /// Import an address file from any location (copies to data directory)
        /// </summary>
        public static string ImportAddressFile(string sourceFilePath, string crypto)
        {
            Directory.CreateDirectory(DataDir);
            var ext = Path.GetExtension(sourceFilePath);
            if (string.IsNullOrEmpty(ext)) ext = ".txt";
            var destName = $"{crypto}_funded_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";
            var destPath = Path.Combine(DataDir, destName);
            File.Copy(sourceFilePath, destPath, overwrite: false);
            DebugLogger.Log($"[FundedFilter] Imported {sourceFilePath} → {destPath}");
            return destPath;
        }

        /// <summary>
        /// Build bloom filter from one or more text files (one address per line)
        /// </summary>
        public void BuildFromTextFiles(List<string> filePaths, string bloomSavePath)
        {
            // First pass: count lines to size the bloom filter
            long totalLines = 0;
            foreach (var path in filePaths)
            {
                using var reader = new StreamReader(path);
                while (reader.ReadLine() != null)
                    totalLines++;
            }

            if (totalLines == 0)
            {
                DebugLogger.Log("[FundedFilter] No addresses found in text files.");
                return;
            }

            // Initialize bloom filter: 0.1% false positive rate (1 in 1000)
            InitializeBloom(totalLines, 0.001);

            // Second pass: add each address
            long loaded = 0;
            long skipped = 0;
            long lastReport = 0;
            foreach (var path in filePaths)
            {
                DebugLogger.Log($"[FundedFilter] Loading {path}...");
                using var reader = new StreamReader(path);
                
                // Detect column layout from header (Blockchair TSV has: block_id, transaction_hash, index, time, value, value_usd, recipient, ...)
                int addressColumn = 0; // Default: first column
                string headerLine = reader.ReadLine();
                if (headerLine != null && headerLine.Contains('\t'))
                {
                    var headers = headerLine.Split('\t');
                    for (int i = 0; i < headers.Length; i++)
                    {
                        var h = headers[i].Trim().ToLowerInvariant();
                        if (h == "recipient" || h == "address" || h == "addr")
                        {
                            addressColumn = i;
                            break;
                        }
                    }
                    DebugLogger.Log($"[FundedFilter] Detected TSV with {headers.Length} columns, address column: {addressColumn} ({(addressColumn < headers.Length ? headers[addressColumn].Trim() : "?")})");
                }
                else if (headerLine != null)
                {
                    // Not a TSV header — treat as an address line
                    var firstAddr = ExtractAddress(headerLine);
                    if (firstAddr != null) { AddAddress(firstAddr); loaded++; }
                }
                
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var address = ExtractAddressFromLine(line, addressColumn);
                    if (address == null) { skipped++; continue; }

                    AddAddress(address);
                    loaded++;

                    if (loaded - lastReport >= 1000000)
                    {
                        lastReport = loaded;
                        DebugLogger.Log($"[FundedFilter] Loaded {loaded:N0}/{totalLines:N0} addresses (skipped {skipped:N0})...");
                    }
                }
            }

            _addressCount = loaded;
            _isLoaded = true;
            DebugLogger.Log($"[FundedFilter] Total: {loaded:N0} addresses loaded, {skipped:N0} non-address rows skipped");

            // Save bloom filter for fast reload next time
            SaveBloomFilter(bloomSavePath);
            DebugLogger.Log($"[FundedFilter] Bloom filter saved to {bloomSavePath} ({new FileInfo(bloomSavePath).Length / 1024 / 1024:F1} MB)");
        }

        /// <summary>
        /// Extract a valid address from a TSV/CSV line using the detected column index
        /// </summary>
        private static string ExtractAddressFromLine(string line, int column)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;

            string field;
            if (line.Contains('\t'))
            {
                var parts = line.Split('\t');
                if (column >= parts.Length) return null;
                field = parts[column].Trim();
            }
            else if (line.Contains(','))
            {
                var parts = line.Split(',');
                if (column >= parts.Length) return null;
                field = parts[column].Trim();
            }
            else
            {
                field = line.Trim();
            }

            return IsValidCryptoAddress(field) ? field : null;
        }

        /// <summary>
        /// Extract address from a single-column or simple line
        /// </summary>
        private static string ExtractAddress(string line)
        {
            return ExtractAddressFromLine(line, 0);
        }

        /// <summary>
        /// Check if a string looks like a valid crypto address
        /// </summary>
        private static bool IsValidCryptoAddress(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 20) return false;
            // Skip nulldata/script entries (Blockchair outputs d-xxx for nulldata)
            if (s.StartsWith("d-") || s.StartsWith('#') || s.StartsWith("//")) return false;
            // Bitcoin addresses: 1/3/bc1, Ethereum: 0x
            if (s.StartsWith('1') || s.StartsWith('3') || s.StartsWith("bc1") || s.StartsWith("0x"))
                return true;
            // Accept any alphanumeric string 26+ chars as potential address
            if (s.Length >= 26 && !s.Contains(' '))
                return true;
            return false;
        }

        /// <summary>
        /// Check if an address MIGHT be funded (bloom filter lookup)
        /// Returns true if the address might be in the set (possible false positive)
        /// Returns false if the address is DEFINITELY NOT in the set (guaranteed)
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MightBeFunded(string address)
        {
            // NOTE: StrictScanMode does NOT bypass this filter — the bloom filter is always used as a pre-filter.
            // StrictScanMode only means we verify any MATCH via live API (never skip a positive hit).
            if (!_isLoaded || _bitArray == null) return true; // If no filter loaded, assume all could be funded (fall back to API)

            var hashes = GetHashes(address);

            _lock.EnterReadLock();
            try
            {
                foreach (var hash in hashes)
                {
                    int index = (int)(hash % (uint)_bitArraySize);
                    if (!_bitArray[index]) return false; // Definitely not in set
                }
                return true; // Might be in set
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Check multiple addresses at once, return only those that might be funded
        /// </summary>
        public List<string> FilterPotentiallyFunded(List<string> addresses)
        {
            // NOTE: StrictScanMode does NOT bypass this filter — see MightBeFunded note above.
            if (!_isLoaded) return addresses; // No filter → check all via API

            return addresses.Where(MightBeFunded).ToList();
        }

        private void InitializeBloom(long expectedItems, double falsePositiveRate)
        {
            _expectedCapacity = expectedItems;
            _bitArraySize = (int)Math.Min(
                (long)Math.Ceiling(-expectedItems * Math.Log(falsePositiveRate) / (Math.Log(2) * Math.Log(2))),
                int.MaxValue - 1);
            _hashFunctionCount = Math.Min(
                (int)Math.Ceiling((_bitArraySize / (double)expectedItems) * Math.Log(2)),
                10);

            _bitArray = new BitArray(_bitArraySize);
            DebugLogger.Log($"[FundedFilter] Bloom filter: {_bitArraySize:N0} bits ({_bitArraySize / 8 / 1024 / 1024:F1} MB), {_hashFunctionCount} hash functions, FPR={falsePositiveRate}");
        }

        private void AddAddress(string address)
        {
            var hashes = GetHashes(address);
            foreach (var hash in hashes)
            {
                int index = (int)(hash % (uint)_bitArraySize);
                _bitArray[index] = true;
            }
        }

        private void SaveBloomFilter(string path)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(fs);

            // Header
            writer.Write((int)1); // version
            writer.Write(_bitArraySize);
            writer.Write(_hashFunctionCount);
            writer.Write(_addressCount);

            // Bit array as byte array
            var bytes = new byte[(_bitArraySize + 7) / 8];
            _bitArray.CopyTo(bytes, 0);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private void LoadBloomFilter(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            var version = reader.ReadInt32();
            _bitArraySize = reader.ReadInt32();
            _hashFunctionCount = reader.ReadInt32();
            _addressCount = reader.ReadInt64();

            var byteCount = reader.ReadInt32();
            var bytes = reader.ReadBytes(byteCount);
            _bitArray = new BitArray(bytes);

            _isLoaded = true;
        }

        /// <summary>
        /// Force rebuild of bloom filter (e.g., after importing new address files)
        /// </summary>
        public static void RebuildBTC()
        {
            if (File.Exists(BtcBloomPath)) File.Delete(BtcBloomPath);
            _btcInstance = new FundedAddressFilter();
            InitializeBTC();
        }

        public static void RebuildETH()
        {
            if (File.Exists(EthBloomPath)) File.Delete(EthBloomPath);
            _ethInstance = new FundedAddressFilter();
            InitializeETH();
        }

        /// <summary>
        /// Get the data directory path (for UI display / file browser)
        /// </summary>
        public static string GetDataDirectory() => DataDir;

        // ================================================================
        // AUTO-LEARNING "KNOWN EMPTY" FILTER
        // No files needed — automatically grows as the app checks addresses via API.
        // When an address is checked and has zero balance, it's added to this filter.
        // Next time the same address appears, it's skipped instantly (no API call).
        // Persisted across sessions — cumulative speed improvement over time.
        // ================================================================

        private static readonly string BtcEmptyBloomPath = Path.Combine(DataDir, "btc_empty.bloom");
        private static readonly string EthEmptyBloomPath = Path.Combine(DataDir, "eth_empty.bloom");

        private static FundedAddressFilter _btcEmptyInstance;
        private static FundedAddressFilter _ethEmptyInstance;
        private static readonly object _emptyInitLock = new object();

        private string _autoSavePath;
        private long _dirtyCount = 0;
        private const long AUTO_SAVE_THRESHOLD = 50000; // Save every 50K new addresses

        /// <summary>
        /// Auto-learning filter for BTC addresses confirmed empty (singleton)
        /// </summary>
        public static FundedAddressFilter BTCEmpty
        {
            get
            {
                if (_btcEmptyInstance == null)
                {
                    lock (_emptyInitLock)
                    {
                        _btcEmptyInstance ??= new FundedAddressFilter();
                    }
                }
                return _btcEmptyInstance;
            }
        }

        /// <summary>
        /// Auto-learning filter for ETH addresses confirmed empty (singleton)
        /// </summary>
        public static FundedAddressFilter ETHEmpty
        {
            get
            {
                if (_ethEmptyInstance == null)
                {
                    lock (_emptyInitLock)
                    {
                        _ethEmptyInstance ??= new FundedAddressFilter();
                    }
                }
                return _ethEmptyInstance;
            }
        }

        /// <summary>
        /// Initialize auto-learning BTC empty filter (load from disk or create new)
        /// </summary>
        public static void InitializeBTCEmpty()
        {
            if (File.Exists(BtcEmptyBloomPath))
            {
                BTCEmpty.LoadBloomFilter(BtcEmptyBloomPath);
                BTCEmpty._autoSavePath = BtcEmptyBloomPath;
                DebugLogger.Log($"[EmptyFilter] BTC known-empty filter loaded: {BTCEmpty.AddressCount:N0} addresses cached");
            }
            else
            {
                // Pre-allocate for 2M addresses at 1% FPR (~3MB memory)
                BTCEmpty.InitializeBloom(2_000_000, 0.01);
                BTCEmpty._isLoaded = true;
                BTCEmpty._autoSavePath = BtcEmptyBloomPath;
                DebugLogger.Log("[EmptyFilter] BTC known-empty filter created (auto-learning mode, no files needed)");
            }
        }

        /// <summary>
        /// Initialize auto-learning ETH empty filter (load from disk or create new)
        /// </summary>
        public static void InitializeETHEmpty()
        {
            if (File.Exists(EthEmptyBloomPath))
            {
                ETHEmpty.LoadBloomFilter(EthEmptyBloomPath);
                ETHEmpty._autoSavePath = EthEmptyBloomPath;
                DebugLogger.Log($"[EmptyFilter] ETH known-empty filter loaded: {ETHEmpty.AddressCount:N0} addresses cached");
            }
            else
            {
                ETHEmpty.InitializeBloom(2_000_000, 0.01);
                ETHEmpty._isLoaded = true;
                ETHEmpty._autoSavePath = EthEmptyBloomPath;
                DebugLogger.Log("[EmptyFilter] ETH known-empty filter created (auto-learning mode, no files needed)");
            }
        }

        /// <summary>
        /// Mark an address as confirmed empty (add to bloom filter).
        /// Called after API returns zero balance. Thread-safe.
        /// Enforced strictly across all modes so confirmed-empty addresses never repeat.
        /// </summary>
        public void MarkAsEmpty(string address)
        {
            if (string.IsNullOrEmpty(address) || !_isLoaded || _bitArray == null) return;

            // Warn if bloom filter is at/over capacity (false positive rate degrades sharply)
            if (_expectedCapacity > 0 && _addressCount >= _expectedCapacity)
            {
                if (_addressCount == _expectedCapacity || _addressCount % 100000 == 0)
                {
                    DebugLogger.Log($"[EmptyFilter] ⚠️ WARNING: Bloom filter at capacity ({_addressCount:N0}/{_expectedCapacity:N0}). " +
                        $"False positive rate is now degraded. Consider restarting the app to reset the empty filter.");
                }
            }

            var hashes = GetHashes(address);
            _lock.EnterWriteLock();
            try
            {
                foreach (var hash in hashes)
                {
                    int index = (int)(hash % (uint)_bitArraySize);
                    _bitArray[index] = true;
                }
                Interlocked.Increment(ref _addressCount);
                var dirty = Interlocked.Increment(ref _dirtyCount);

                // Auto-save periodically
                if (dirty >= AUTO_SAVE_THRESHOLD && !string.IsNullOrEmpty(_autoSavePath))
                {
                    Interlocked.Exchange(ref _dirtyCount, 0);
                    try
                    {
                        SaveBloomFilter(_autoSavePath);
                        DebugLogger.Log($"[EmptyFilter] Auto-saved: {_addressCount:N0} known-empty addresses");
                    }
                    catch { } // Don't crash on save failure
                }
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Check if an address was previously checked and found empty.
        /// Returns true if the address is PROBABLY empty (was seen before with zero balance).
        /// Returns false if we have NO record of this address (needs API check).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsKnownEmpty(string address)
        {
            if (!_isLoaded || _bitArray == null || _addressCount == 0) return false;

            var hashes = GetHashes(address);
            _lock.EnterReadLock();
            try
            {
                foreach (var hash in hashes)
                {
                    int index = (int)(hash % (uint)_bitArraySize);
                    if (!_bitArray[index]) return false; // Definitely NOT in empty set
                }
                return true; // Probably in empty set
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Filter out addresses that are known to be empty. Returns only addresses that need API checking.
        /// </summary>
        public List<string> FilterOutKnownEmpty(List<string> addresses)
        {
            if (!_isLoaded || _addressCount == 0) return addresses;
            return addresses.Where(a => !IsKnownEmpty(a)).ToList();
        }

        /// <summary>
        /// Mark multiple addresses as empty in one call (after batch API check returns zeroes)
        /// </summary>
        public void MarkMultipleAsEmpty(IEnumerable<string> addresses)
        {
            if (!_isLoaded || _bitArray == null) return;
            foreach (var address in addresses)
            {
                MarkAsEmpty(address);
            }
        }

        /// <summary>
        /// Save all auto-learning filters to disk (call on app shutdown)
        /// </summary>
        public static void SaveAllEmptyFilters()
        {
            try
            {
                if (_btcEmptyInstance?._isLoaded == true && _btcEmptyInstance._addressCount > 0)
                {
                    _btcEmptyInstance.SaveBloomFilter(BtcEmptyBloomPath);
                    DebugLogger.Log($"[EmptyFilter] BTC empty filter saved: {_btcEmptyInstance.AddressCount:N0} addresses");
                }
                if (_ethEmptyInstance?._isLoaded == true && _ethEmptyInstance._addressCount > 0)
                {
                    _ethEmptyInstance.SaveBloomFilter(EthEmptyBloomPath);
                    DebugLogger.Log($"[EmptyFilter] ETH empty filter saved: {_ethEmptyInstance.AddressCount:N0} addresses");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[EmptyFilter] Save error: {ex.Message}");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint[] GetHashes(string item)
        {
            // Normalize Ethereum (0x) addresses to lowercase so EIP-55 checksummed addresses
            // match lowercase funded address lists and bloom filters.
            if (item != null && item.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                item = item.ToLowerInvariant();
            }

            // Use MurmurHash3-inspired double hashing for speed
            var bytes = Encoding.UTF8.GetBytes(item ?? string.Empty);
            uint hash1 = MurmurHash3(bytes, 0);
            uint hash2 = MurmurHash3(bytes, hash1);

            var hashes = new uint[_hashFunctionCount];
            for (int i = 0; i < _hashFunctionCount; i++)
            {
                hashes[i] = hash1 + (uint)i * hash2;
            }
            return hashes;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint MurmurHash3(byte[] data, uint seed)
        {
            uint h = seed;
            int length = data.Length;
            int blocks = length / 4;

            for (int i = 0; i < blocks; i++)
            {
                uint k = BitConverter.ToUInt32(data, i * 4);
                k *= 0xcc9e2d51;
                k = (k << 15) | (k >> 17);
                k *= 0x1b873593;
                h ^= k;
                h = (h << 13) | (h >> 19);
                h = h * 5 + 0xe6546b64;
            }

            // Tail
            uint tail = 0;
            int tailStart = blocks * 4;
            switch (length - tailStart)
            {
                case 3: tail |= (uint)data[tailStart + 2] << 16; goto case 2;
                case 2: tail |= (uint)data[tailStart + 1] << 8; goto case 1;
                case 1:
                    tail |= data[tailStart];
                    tail *= 0xcc9e2d51;
                    tail = (tail << 15) | (tail >> 17);
                    tail *= 0x1b873593;
                    h ^= tail;
                    break;
            }

            h ^= (uint)length;
            h ^= h >> 16;
            h *= 0x85ebca6b;
            h ^= h >> 13;
            h *= 0xc2b2ae35;
            h ^= h >> 16;

            return h;
        }

        /// <summary>
        /// Auto-download top funded BTC and ETH addresses from blockchain APIs
        /// and build bloom filters for offline pre-checking.
        /// Call this on startup if no bloom filter files exist yet.
        /// </summary>
        public static async Task<(int btcCount, int ethCount)> DownloadFundedAddressesAsync(
            CancellationToken cancellationToken = default,
            Action<string> statusCallback = null)
        {
            Directory.CreateDirectory(DataDir);
            int btcTotal = 0, ethTotal = 0;

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(30);
            httpClient.DefaultRequestHeaders.Add("User-Agent", "WalletRecoveryTool/1.0");

            // --- Download BTC funded addresses ---
            var btcFile = Path.Combine(DataDir, "btc_funded.txt");
            if (!File.Exists(btcFile) && !File.Exists(BtcBloomPath))
            {
                statusCallback?.Invoke("Downloading funded BTC addresses...");
                var btcAddresses = new HashSet<string>();

                // Source 1: Blockchair top addresses (100 per page, up to 50 pages = 5000)
                for (int offset = 0; offset < 5000 && !cancellationToken.IsCancellationRequested; offset += 100)
                {
                    try
                    {
                        var url = $"https://api.blockchair.com/bitcoin/addresses?s=balance(desc)&limit=100&offset={offset}";
                        var response = await httpClient.GetStringAsync(url, cancellationToken);
                        var json = JObject.Parse(response);
                        var data = json["data"] as JArray;
                        if (data == null || data.Count == 0) break;

                        foreach (var item in data)
                        {
                            var addr = item["address"]?.ToString();
                            if (!string.IsNullOrEmpty(addr))
                                btcAddresses.Add(addr);
                        }

                        statusCallback?.Invoke($"BTC: {btcAddresses.Count:N0} addresses downloaded...");

                        // Rate limit: Blockchair free tier = 30 req/min
                        await Task.Delay(2100, cancellationToken);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        DebugLogger.Log($"[FundedFilter] Blockchair BTC page {offset} error: {ex.Message}");
                        if (btcAddresses.Count > 0) break; // Got some data, stop on error
                        await Task.Delay(5000, cancellationToken); // Wait longer on first error
                    }
                }

                // Source 2: BlockCypher top addresses (if Blockchair didn't work well)
                if (btcAddresses.Count < 100 && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        var url = "https://api.blockcypher.com/v1/btc/main";
                        var response = await httpClient.GetStringAsync(url, cancellationToken);
                        var json = JObject.Parse(response);
                        // BlockCypher main endpoint gives chain stats, not address lists
                        // But we tried
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Log($"[FundedFilter] BlockCypher fallback error: {ex.Message}");
                    }
                }

                if (btcAddresses.Count > 0)
                {
                    await File.WriteAllLinesAsync(btcFile, btcAddresses, cancellationToken);
                    btcTotal = btcAddresses.Count;
                    statusCallback?.Invoke($"BTC: {btcTotal:N0} addresses saved. Building bloom filter...");
                    DebugLogger.Log($"[FundedFilter] Downloaded {btcTotal:N0} BTC funded addresses");
                }
            }

            // --- Download ETH funded addresses ---
            var ethFile = Path.Combine(DataDir, "eth_funded.txt");
            if (!File.Exists(ethFile) && !File.Exists(EthBloomPath))
            {
                statusCallback?.Invoke("Downloading funded ETH addresses...");
                var ethAddresses = new HashSet<string>();

                // Source: Blockchair top ETH addresses
                for (int offset = 0; offset < 5000 && !cancellationToken.IsCancellationRequested; offset += 100)
                {
                    try
                    {
                        var url = $"https://api.blockchair.com/ethereum/addresses?s=balance(desc)&limit=100&offset={offset}";
                        var response = await httpClient.GetStringAsync(url, cancellationToken);
                        var json = JObject.Parse(response);
                        var data = json["data"] as JArray;
                        if (data == null || data.Count == 0) break;

                        foreach (var item in data)
                        {
                            var addr = item["address"]?.ToString()?.ToLowerInvariant();
                            if (!string.IsNullOrEmpty(addr))
                                ethAddresses.Add(addr);
                        }

                        statusCallback?.Invoke($"ETH: {ethAddresses.Count:N0} addresses downloaded...");
                        await Task.Delay(2100, cancellationToken);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        DebugLogger.Log($"[FundedFilter] Blockchair ETH page {offset} error: {ex.Message}");
                        if (ethAddresses.Count > 0) break;
                        await Task.Delay(5000, cancellationToken);
                    }
                }

                if (ethAddresses.Count > 0)
                {
                    await File.WriteAllLinesAsync(ethFile, ethAddresses, cancellationToken);
                    ethTotal = ethAddresses.Count;
                    statusCallback?.Invoke($"ETH: {ethTotal:N0} addresses saved. Building bloom filter...");
                    DebugLogger.Log($"[FundedFilter] Downloaded {ethTotal:N0} ETH funded addresses");
                }
            }

            // Build bloom filters from downloaded files
            if (btcTotal > 0)
            {
                try
                {
                    InitializeBTC();
                    statusCallback?.Invoke($"BTC bloom filter built: {BTC.AddressCount:N0} addresses");
                }
                catch (Exception ex) { DebugLogger.Log($"[FundedFilter] BTC bloom build error: {ex.Message}"); }
            }

            if (ethTotal > 0)
            {
                try
                {
                    InitializeETH();
                    statusCallback?.Invoke($"ETH bloom filter built: {ETH.AddressCount:N0} addresses");
                }
                catch (Exception ex) { DebugLogger.Log($"[FundedFilter] ETH bloom build error: {ex.Message}"); }
            }

            statusCallback?.Invoke($"Done! BTC: {btcTotal:N0}, ETH: {ethTotal:N0} funded addresses loaded");
            return (btcTotal, ethTotal);
        }

        /// <summary>
        /// Check if bloom filter data already exists (either .bloom or .txt files)
        /// </summary>
        public static bool HasBloomData()
        {
            if (!Directory.Exists(DataDir)) return false;
            return File.Exists(BtcBloomPath) || File.Exists(EthBloomPath)
                || FindAddressFiles("btc").Count > 0 || FindAddressFiles("eth").Count > 0;
        }

        /// <summary>
        /// Check how many funded addresses are loaded (for UI status display)
        /// </summary>
        public static (long btcCount, long ethCount, bool btcLoaded, bool ethLoaded) GetStatus()
        {
            return (
                _btcInstance?.AddressCount ?? 0,
                _ethInstance?.AddressCount ?? 0,
                _btcInstance?.IsLoaded ?? false,
                _ethInstance?.IsLoaded ?? false
            );
        }

        /// <summary>
        /// Download the FULL Blockchair Bitcoin address dump (gzipped TSV, ~2-3 GB → ~50M addresses).
        /// This is the #1 performance optimization — eliminates 99.99% of API calls.
        /// </summary>
        public static async Task<long> DownloadFullBlockchairDumpAsync(
            CancellationToken cancellationToken = default,
            Action<string> statusCallback = null,
            IProgress<double> progressCallback = null)
        {
            Directory.CreateDirectory(DataDir);

            var tsvPath = Path.Combine(DataDir, "blockchair_bitcoin_addresses.tsv");
            var gzPath = tsvPath + ".gz";
            var downloadUrl = "https://gz.blockchair.com/bitcoin/addresses/blockchair_bitcoin_addresses_latest.tsv.gz";

            // Skip if we already have data
            if (File.Exists(BtcBloomPath))
            {
                statusCallback?.Invoke($"BTC bloom filter already exists ({BTC.AddressCount:N0} addresses). Delete btc_funded.bloom to rebuild.");
                return BTC.AddressCount;
            }

            if (File.Exists(tsvPath))
            {
                statusCallback?.Invoke("TSV file exists. Building bloom filter...");
                InitializeBTC();
                return BTC.AddressCount;
            }

            // Download the gzipped TSV
            statusCallback?.Invoke("Downloading Blockchair BTC address dump (~2-3 GB)...");
            DebugLogger.Log($"[FundedFilter] Downloading {downloadUrl}");

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromHours(2); // Large file
            httpClient.DefaultRequestHeaders.Add("User-Agent", "WalletRecoveryTool/2.0");

            try
            {
                using var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? 0;
                var totalMB = totalBytes / 1024.0 / 1024.0;

                using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var fileStream = new FileStream(gzPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);

                var buffer = new byte[65536];
                long downloadedBytes = 0;
                long lastReportBytes = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    downloadedBytes += bytesRead;

                    if (downloadedBytes - lastReportBytes > 10 * 1024 * 1024) // Report every 10MB
                    {
                        lastReportBytes = downloadedBytes;
                        var dlMB = downloadedBytes / 1024.0 / 1024.0;
                        var pct = totalBytes > 0 ? (double)downloadedBytes / totalBytes * 100 : 0;
                        statusCallback?.Invoke($"Downloading: {dlMB:F0}/{totalMB:F0} MB ({pct:F1}%)");
                        progressCallback?.Report(pct);
                    }
                }

                statusCallback?.Invoke($"Downloaded {downloadedBytes / 1024.0 / 1024.0:F0} MB. Decompressing...");
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[FundedFilter] Download failed: {ex.Message}");
                statusCallback?.Invoke($"Download failed: {ex.Message}");
                if (File.Exists(gzPath)) File.Delete(gzPath);
                return 0;
            }

            // Decompress .gz → .tsv
            try
            {
                statusCallback?.Invoke("Decompressing TSV file (this may take a few minutes)...");
                using var gzFileStream = new FileStream(gzPath, FileMode.Open, FileAccess.Read);
                using var gzipStream = new System.IO.Compression.GZipStream(gzFileStream, System.IO.Compression.CompressionMode.Decompress);
                using var outStream = new FileStream(tsvPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);

                var buffer = new byte[65536];
                long totalDecompressed = 0;
                long lastReport = 0;
                int bytesRead;

                while ((bytesRead = await gzipStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await outStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    totalDecompressed += bytesRead;

                    if (totalDecompressed - lastReport > 100 * 1024 * 1024)
                    {
                        lastReport = totalDecompressed;
                        statusCallback?.Invoke($"Decompressing: {totalDecompressed / 1024.0 / 1024.0:F0} MB...");
                    }
                }

                statusCallback?.Invoke($"Decompressed: {totalDecompressed / 1024.0 / 1024.0:F0} MB");
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[FundedFilter] Decompression failed: {ex.Message}");
                statusCallback?.Invoke($"Decompression failed: {ex.Message}");
                return 0;
            }
            finally
            {
                // Delete .gz to save disk space
                if (File.Exists(gzPath)) File.Delete(gzPath);
            }

            // Build bloom filter from TSV
            statusCallback?.Invoke("Building bloom filter from address dump (2-5 minutes)...");
            InitializeBTC();
            statusCallback?.Invoke($"Bloom filter ready: {BTC.AddressCount:N0} funded BTC addresses loaded!");

            return BTC.AddressCount;
        }
    }
}
