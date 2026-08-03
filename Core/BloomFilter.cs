using System;
using System.Collections;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// High-performance Bloom Filter for address deduplication
    /// Uses ~10 bits per entry vs ~100 bytes for HashSet
    /// Supports 100M+ addresses with only 125MB memory
    /// </summary>
    public class BloomFilter : IDisposable
    {
        private readonly BitArray _bitArray;
        private readonly int _hashFunctionCount;
        private readonly int _bitArraySize;
        private readonly object _lock = new object();
        private long _itemCount;

        // For memory-mapped persistence
        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _accessor;
        private readonly string _persistPath;
        private readonly bool _isPersistent;

        /// <summary>
        /// Creates a new Bloom filter
        /// </summary>
        /// <param name="expectedItems">Expected number of items</param>
        /// <param name="falsePositiveRate">Desired false positive rate (0.01 = 1%)</param>
        /// <param name="persistPath">Optional path to persist filter to disk</param>
        public BloomFilter(long expectedItems, double falsePositiveRate = 0.01, string persistPath = null)
        {
            // Calculate optimal size and hash count
            // m = -n * ln(p) / (ln(2)^2)
            // k = (m/n) * ln(2)
            _bitArraySize = (int)Math.Ceiling(-expectedItems * Math.Log(falsePositiveRate) / (Math.Log(2) * Math.Log(2)));
            _hashFunctionCount = (int)Math.Ceiling((_bitArraySize / (double)expectedItems) * Math.Log(2));

            // Cap hash functions at 10 for performance
            _hashFunctionCount = Math.Min(_hashFunctionCount, 10);

            _persistPath = persistPath;
            _isPersistent = !string.IsNullOrEmpty(persistPath);

            if (_isPersistent && File.Exists(persistPath))
            {
                LoadFromFile();
            }
            else
            {
                _bitArray = new BitArray(_bitArraySize);
                
                if (_isPersistent)
                {
                    InitializeMemoryMappedFile();
                }
            }

            DebugLogger.Log($"BloomFilter initialized: {_bitArraySize:N0} bits ({_bitArraySize / 8 / 1024 / 1024:F1} MB), {_hashFunctionCount} hash functions");
        }

        /// <summary>
        /// Adds an address to the filter
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(string item)
        {
            var hashes = GetHashes(item);
            
            lock (_lock)
            {
                foreach (var hash in hashes)
                {
                    int index = (int)(hash % (uint)_bitArraySize);
                    _bitArray[index] = true;
                }
                Interlocked.Increment(ref _itemCount);
            }
        }

        /// <summary>
        /// Adds multiple items at once (more efficient)
        /// </summary>
        public void AddRange(string[] items)
        {
            lock (_lock)
            {
                foreach (var item in items)
                {
                    var hashes = GetHashes(item);
                    foreach (var hash in hashes)
                    {
                        int index = (int)(hash % (uint)_bitArraySize);
                        _bitArray[index] = true;
                    }
                    Interlocked.Increment(ref _itemCount);
                }
            }
        }

        /// <summary>
        /// Checks if an address might be in the filter
        /// Returns false = DEFINITELY not in filter
        /// Returns true = PROBABLY in filter (may be false positive)
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MightContain(string item)
        {
            var hashes = GetHashes(item);
            
            lock (_lock)
            {
                foreach (var hash in hashes)
                {
                    int index = (int)(hash % (uint)_bitArraySize);
                    if (!_bitArray[index])
                        return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Thread-safe add-if-not-exists, returns true if item was new
        /// </summary>
        public bool TryAdd(string item)
        {
            var hashes = GetHashes(item);
            
            lock (_lock)
            {
                // Check if already exists
                bool exists = true;
                foreach (var hash in hashes)
                {
                    int index = (int)(hash % (uint)_bitArraySize);
                    if (!_bitArray[index])
                    {
                        exists = false;
                        break;
                    }
                }

                if (exists)
                    return false; // Item already exists

                // Add the item
                foreach (var hash in hashes)
                {
                    int index = (int)(hash % (uint)_bitArraySize);
                    _bitArray[index] = true;
                }
                Interlocked.Increment(ref _itemCount);
                return true; // Item was new
            }
        }

        /// <summary>
        /// Gets the current estimated false positive rate
        /// </summary>
        public double CurrentFalsePositiveRate
        {
            get
            {
                // (1 - e^(-kn/m))^k
                double exponent = -_hashFunctionCount * _itemCount / (double)_bitArraySize;
                return Math.Pow(1 - Math.Exp(exponent), _hashFunctionCount);
            }
        }

        /// <summary>
        /// Gets the number of items added
        /// </summary>
        public long Count => _itemCount;

        /// <summary>
        /// Gets the memory usage in bytes
        /// </summary>
        public long MemoryUsageBytes => _bitArraySize / 8;

        /// <summary>
        /// Generates hash values using double hashing technique
        /// h(i) = h1 + i*h2
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint[] GetHashes(string item)
        {
            var bytes = Encoding.UTF8.GetBytes(item);
            
            // Use MurmurHash3 for speed
            uint hash1 = MurmurHash3(bytes, 0);
            uint hash2 = MurmurHash3(bytes, hash1);

            var hashes = new uint[_hashFunctionCount];
            for (int i = 0; i < _hashFunctionCount; i++)
            {
                hashes[i] = hash1 + (uint)i * hash2;
            }
            return hashes;
        }

        /// <summary>
        /// MurmurHash3 - extremely fast non-cryptographic hash
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint MurmurHash3(byte[] data, uint seed)
        {
            const uint c1 = 0xcc9e2d51;
            const uint c2 = 0x1b873593;
            const int r1 = 15;
            const int r2 = 13;
            const uint m = 5;
            const uint n = 0xe6546b64;

            uint hash = seed;
            int length = data.Length;
            int blockCount = length / 4;

            // Process 4-byte blocks
            for (int i = 0; i < blockCount; i++)
            {
                uint k = BitConverter.ToUInt32(data, i * 4);
                k *= c1;
                k = RotateLeft(k, r1);
                k *= c2;

                hash ^= k;
                hash = RotateLeft(hash, r2);
                hash = hash * m + n;
            }

            // Process remaining bytes
            uint tail = 0;
            int tailIndex = blockCount * 4;
            switch (length & 3)
            {
                case 3:
                    tail ^= (uint)data[tailIndex + 2] << 16;
                    goto case 2;
                case 2:
                    tail ^= (uint)data[tailIndex + 1] << 8;
                    goto case 1;
                case 1:
                    tail ^= data[tailIndex];
                    tail *= c1;
                    tail = RotateLeft(tail, r1);
                    tail *= c2;
                    hash ^= tail;
                    break;
            }

            // Finalization
            hash ^= (uint)length;
            hash ^= hash >> 16;
            hash *= 0x85ebca6b;
            hash ^= hash >> 13;
            hash *= 0xc2b2ae35;
            hash ^= hash >> 16;

            return hash;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint RotateLeft(uint x, int r) => (x << r) | (x >> (32 - r));

        /// <summary>
        /// Saves filter to disk
        /// </summary>
        public void SaveToFile(string path = null)
        {
            path ??= _persistPath;
            if (string.IsNullOrEmpty(path)) return;

            var bytes = new byte[(_bitArraySize + 7) / 8];
            _bitArray.CopyTo(bytes, 0);

            using var fs = new FileStream(path, FileMode.Create);
            using var writer = new BinaryWriter(fs);
            
            writer.Write(_bitArraySize);
            writer.Write(_hashFunctionCount);
            writer.Write(_itemCount);
            writer.Write(bytes);
        }

        /// <summary>
        /// Loads filter from disk
        /// </summary>
        private void LoadFromFile()
        {
            if (!File.Exists(_persistPath)) return;

            using var fs = new FileStream(_persistPath, FileMode.Open);
            using var reader = new BinaryReader(fs);

            int size = reader.ReadInt32();
            int hashCount = reader.ReadInt32();
            _itemCount = reader.ReadInt64();
            
            var bytes = reader.ReadBytes((size + 7) / 8);
            
            // Note: We can't reassign readonly fields, so this is handled in constructor
            for (int i = 0; i < bytes.Length; i++)
            {
                for (int bit = 0; bit < 8 && i * 8 + bit < _bitArraySize; bit++)
                {
                    _bitArray[i * 8 + bit] = (bytes[i] & (1 << bit)) != 0;
                }
            }

            DebugLogger.Log($"BloomFilter loaded: {_itemCount:N0} items from {_persistPath}");
        }

        private void InitializeMemoryMappedFile()
        {
            try
            {
                long fileSize = (_bitArraySize + 7) / 8 + 24; // Header + data
                _mmf = MemoryMappedFile.CreateFromFile(_persistPath, FileMode.Create, null, fileSize);
                _accessor = _mmf.CreateViewAccessor();
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Failed to create memory-mapped file: {ex.Message}");
            }
        }

        /// <summary>
        /// Clears all items from the filter
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _bitArray.SetAll(false);
                _itemCount = 0;
            }
        }

        public void Dispose()
        {
            if (_isPersistent)
            {
                SaveToFile();
            }

            _accessor?.Dispose();
            _mmf?.Dispose();
        }
    }

    /// <summary>
    /// Memory-mapped address cache for huge datasets
    /// Survives app restarts and supports 100M+ addresses
    /// </summary>
    public class MemoryMappedAddressCache : IDisposable
    {
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _accessor;
        private readonly string _filePath;
        private readonly int _maxEntries;
        private readonly int _entrySize = 64; // Fixed-size entry (address hash + metadata)
        private int _currentCount;
        private readonly object _lock = new object();

        public MemoryMappedAddressCache(string filePath, int maxEntries = 100_000_000)
        {
            _filePath = filePath;
            _maxEntries = maxEntries;

            long fileSize = (long)maxEntries * _entrySize + 16; // Header + entries

            try
            {
                bool isNew = !File.Exists(filePath);
                _mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.OpenOrCreate, null, fileSize);
                _accessor = _mmf.CreateViewAccessor();

                if (isNew)
                {
                    // Initialize header
                    _accessor.Write(0, maxEntries);
                    _accessor.Write(4, 0); // Current count
                    _accessor.Write(8, DateTime.UtcNow.ToBinary());
                    _currentCount = 0;
                }
                else
                {
                    // Read existing header
                    _currentCount = _accessor.ReadInt32(4);
                }

                DebugLogger.Log($"MemoryMappedAddressCache: {_currentCount:N0}/{_maxEntries:N0} entries ({filePath})");
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Failed to create memory-mapped cache: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Adds an address hash to the cache
        /// </summary>
        public bool TryAdd(string address)
        {
            if (_currentCount >= _maxEntries)
                return false;

            var hash = ComputeHash(address);
            long position = 16 + (long)_currentCount * _entrySize;

            lock (_lock)
            {
                // Write hash
                for (int i = 0; i < 32; i++)
                    _accessor.Write(position + i, hash[i]);

                // Write timestamp
                _accessor.Write(position + 32, DateTime.UtcNow.ToBinary());

                _currentCount++;
                _accessor.Write(4, _currentCount);
            }

            return true;
        }

        /// <summary>
        /// Checks if an address might be in the cache (uses binary search)
        /// </summary>
        public bool Contains(string address)
        {
            var hash = ComputeHash(address);
            
            // Simple linear scan for now (could be optimized with sorted insertion + binary search)
            for (int i = 0; i < _currentCount; i++)
            {
                long position = 16 + (long)i * _entrySize;
                bool match = true;
                
                for (int j = 0; j < 32; j++)
                {
                    if (_accessor.ReadByte(position + j) != hash[j])
                    {
                        match = false;
                        break;
                    }
                }
                
                if (match)
                    return true;
            }
            
            return false;
        }

        private byte[] ComputeHash(string address)
        {
            using var sha256 = SHA256.Create();
            return sha256.ComputeHash(Encoding.UTF8.GetBytes(address));
        }

        public int Count => _currentCount;
        public int Capacity => _maxEntries;

        public void Dispose()
        {
            _accessor?.Dispose();
            _mmf?.Dispose();
        }
    }
}
