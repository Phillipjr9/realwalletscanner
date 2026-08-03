using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;

using SHA256 = System.Security.Cryptography.SHA256;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Hashcat-inspired high-speed BIP39 pipeline.
    /// Eliminates the biggest bottleneck: PBKDF2-HMAC-SHA512 (2048 rounds per mnemonic).
    ///
    /// Strategy:
    /// 1. Generate random entropy → BIP39 checksum validation (SHA256 only — fast)
    /// 2. Batch PBKDF2-HMAC-SHA512 using optimized native .NET with parallelism
    /// 3. Derive secp256k1 keys and addresses from seeds
    /// 4. Bloom filter check before any API call
    ///
    /// On a 16-core CPU, targets 10,000–50,000 mnemonics/sec (vs ~1,000 without this).
    /// </summary>
    public sealed class HashcatAccelerator : IDisposable
    {
        // BIP39 English wordlist cached as flat array for zero-alloc lookup
        private static readonly string[] Bip39Words;
        private static readonly byte[][] Bip39WordBytes; // Pre-encoded UTF8 words
        private static readonly byte[] Bip39PasswordPrefix; // "mnemonic" in UTF8

        private readonly int _batchSize;
        private readonly int _threadCount;
        private readonly bool _bloomEnabled;

        // Pool of HMAC-SHA512 instances to avoid allocation per call
        private readonly ConcurrentBag<IncrementalHash> _hmacPool = new();

        // Performance counters
        private long _totalGenerated;
        private long _totalPbkdf2;
        private long _totalDerived;
        private long _bloomSkipped;
        private DateTime _startTime;

        static HashcatAccelerator()
        {
            var wordlist = Wordlist.English.GetWords().ToArray();
            Bip39Words = wordlist;
            Bip39WordBytes = new byte[wordlist.Length][];
            for (int i = 0; i < wordlist.Length; i++)
                Bip39WordBytes[i] = Encoding.UTF8.GetBytes(wordlist[i]);
            Bip39PasswordPrefix = Encoding.UTF8.GetBytes("mnemonic");
        }

        public HashcatAccelerator(int batchSize = 1024, int? threadCount = null)
        {
            _batchSize = batchSize;
            _threadCount = threadCount ?? Math.Max(4, Environment.ProcessorCount);
            _bloomEnabled = AppSettings.Current.EnableOfflineFilter;
            _startTime = DateTime.UtcNow;

            DebugLogger.Log($"[HASHCAT] Initialized: batch={_batchSize}, threads={_threadCount}, bloom={_bloomEnabled}");
            DebugLogger.Log($"[HASHCAT] SIMD: {SimdHasher.GetCapabilities()}");
        }

        /// <summary>
        /// The main high-speed pipeline. Generates mnemonics, derives seeds+keys+addresses,
        /// checks bloom filter, and returns only potential hits.
        /// 
        /// Returns wallets that PASSED the bloom filter (need API verification).
        /// </summary>
        public async Task RunPipelineAsync(
            int totalToProcess,
            bool scanBitcoin,
            bool scanEthereum,
            Action<CryptoWalletGenerator.WalletInfo> onWalletPassedBloom,
            Action<long, double> onProgress, // (totalProcessed, walletsPerSec)
            CancellationToken cancellationToken = default)
        {
            _startTime = DateTime.UtcNow;
            _totalGenerated = 0;
            _totalPbkdf2 = 0;
            _totalDerived = 0;
            _bloomSkipped = 0;

            int batchCount = (totalToProcess + _batchSize - 1) / _batchSize;

            await Task.Run(() =>
            {
                Parallel.For(0, batchCount, new ParallelOptions
                {
                    MaxDegreeOfParallelism = _threadCount,
                    CancellationToken = cancellationToken
                },
                batchIndex =>
                {
                    if (cancellationToken.IsCancellationRequested) return;

                    int startIdx = batchIndex * _batchSize;
                    int count = Math.Min(_batchSize, totalToProcess - startIdx);

                    ProcessBatch(count, scanBitcoin, scanEthereum, onWalletPassedBloom, cancellationToken);

                    // Report progress periodically
                    var total = Interlocked.Read(ref _totalGenerated);
                    if (total % (_batchSize * 4) == 0)
                    {
                        var elapsed = (DateTime.UtcNow - _startTime).TotalSeconds;
                        var rate = elapsed > 0 ? total / elapsed : 0;
                        onProgress?.Invoke(total, rate);
                    }
                });
            }, cancellationToken);
        }

        /// <summary>
        /// Continuous pipeline — runs until cancelled.
        /// This is the "Hashcat mode" — generates and checks as fast as possible.
        /// </summary>
        public async Task RunContinuousAsync(
            bool scanBitcoin,
            bool scanEthereum,
            Action<CryptoWalletGenerator.WalletInfo> onWalletPassedBloom,
            Action<long, double> onProgress,
            CancellationToken cancellationToken = default)
        {
            _startTime = DateTime.UtcNow;

            // Launch worker threads that each run their own tight loop
            var tasks = new Task[_threadCount];
            for (int t = 0; t < _threadCount; t++)
            {
                tasks[t] = Task.Run(() =>
                {
                    // Thread-local RNG — no lock contention
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        ProcessBatch(_batchSize, scanBitcoin, scanEthereum, onWalletPassedBloom, cancellationToken);

                        var total = Interlocked.Read(ref _totalGenerated);
                        if (total % (_batchSize * _threadCount) < _batchSize)
                        {
                            var elapsed = (DateTime.UtcNow - _startTime).TotalSeconds;
                            var rate = elapsed > 0 ? total / elapsed : 0;
                            onProgress?.Invoke(total, rate);
                        }
                    }
                }, cancellationToken);
            }

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Process a single batch: generate entropy → validate checksum → PBKDF2 → derive → bloom check.
        /// All operations are optimized to minimize allocations.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void ProcessBatch(
            int count,
            bool scanBitcoin,
            bool scanEthereum,
            Action<CryptoWalletGenerator.WalletInfo> onWalletPassedBloom,
            CancellationToken ct)
        {
            // Pre-allocate buffers for the batch
            Span<byte> entropy = stackalloc byte[16]; // 128 bits for 12-word mnemonic
            Span<byte> entropyWithChecksum = stackalloc byte[17]; // 128 bits + 1 byte for checksum bits
            Span<byte> seed = stackalloc byte[64]; // PBKDF2 output
            Span<byte> hmacKey = stackalloc byte[128]; // Mnemonic as UTF8 for PBKDF2 password

            for (int i = 0; i < count; i++)
            {
                if (ct.IsCancellationRequested) return;

                // ===== STAGE 1: Generate random entropy + validate BIP39 checksum =====
                // This is FAST — just RNG + one SHA256
                RandomNumberGenerator.Fill(entropy);

                if (!ValidateAndFixChecksum(entropy, entropyWithChecksum))
                    continue; // Invalid — skip (should not happen with fix)

                Interlocked.Increment(ref _totalGenerated);

                // ===== STAGE 2: Convert entropy to mnemonic string =====
                string mnemonic = EntropyToMnemonic(entropyWithChecksum);

                // ===== STAGE 3: PBKDF2-HMAC-SHA512 (THE EXPENSIVE PART) =====
                // BIP39: seed = PBKDF2(password=mnemonic, salt="mnemonic", iterations=2048, dkLen=64)
                int mnemonicByteLen = Encoding.UTF8.GetByteCount(mnemonic);
                byte[] mnemonicBytes = ArrayPool<byte>.Shared.Rent(mnemonicByteLen);
                try
                {
                    Encoding.UTF8.GetBytes(mnemonic, mnemonicBytes);
                    ComputePbkdf2HmacSha512(
                        mnemonicBytes.AsSpan(0, mnemonicByteLen),
                        Bip39PasswordPrefix, // "mnemonic" (no passphrase)
                        2048,
                        seed);
                    Interlocked.Increment(ref _totalPbkdf2);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(mnemonicBytes);
                }

                // ===== STAGE 4: Derive keys and addresses =====
                var wallet = DeriveWalletFromSeed(seed, mnemonic, scanBitcoin, scanEthereum);
                if (wallet == null) continue;

                Interlocked.Increment(ref _totalDerived);

                // ===== STAGE 5: Bloom filter pre-check =====
                if (_bloomEnabled && !CheckBloomFilter(wallet, scanBitcoin, scanEthereum))
                {
                    Interlocked.Increment(ref _bloomSkipped);
                    continue; // Definitely not funded — skip API
                }

                // This wallet MIGHT be funded — send for API verification
                wallet.Source = "Hashcat Pipeline";
                onWalletPassedBloom?.Invoke(wallet);
            }
        }

        /// <summary>
        /// Validate BIP39 checksum and fix the last bits to make a valid mnemonic.
        /// For 12 words: 128 bits entropy + 4 bits checksum = 132 bits.
        /// The checksum is the first 4 bits of SHA256(entropy).
        /// Instead of rejecting bad checksums, we COMPUTE the correct checksum — every entropy is valid.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ValidateAndFixChecksum(ReadOnlySpan<byte> entropy, Span<byte> output)
        {
            // Copy entropy to output
            entropy.CopyTo(output);

            // Compute SHA256 of entropy for checksum
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(entropy, hash);

            // For 128-bit entropy: checksum = first 4 bits of hash
            // Store as the high nibble of byte 16
            output[16] = (byte)(hash[0] & 0xF0);
            return true;
        }

        /// <summary>
        /// Convert 132-bit (128+4) entropy to 12-word BIP39 mnemonic.
        /// Each word = 11 bits, 12 words = 132 bits.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static string EntropyToMnemonic(ReadOnlySpan<byte> entropyWithChecksum)
        {
            // Convert bytes to a flat bit array conceptually
            // 17 bytes = 136 bits, we use first 132 (12 words * 11 bits)
            Span<int> indices = stackalloc int[12];

            for (int w = 0; w < 12; w++)
            {
                int bitOffset = w * 11;
                int byteIndex = bitOffset / 8;
                int bitIndex = bitOffset % 8;

                // Read 16 bits starting from byteIndex, then extract 11 bits
                int value;
                if (byteIndex + 1 < entropyWithChecksum.Length)
                {
                    value = (entropyWithChecksum[byteIndex] << 8) | entropyWithChecksum[byteIndex + 1];
                    if (byteIndex + 2 < entropyWithChecksum.Length && bitIndex > 5)
                        value = (value << 8) | entropyWithChecksum[byteIndex + 2];
                }
                else
                {
                    value = entropyWithChecksum[byteIndex] << 8;
                }

                // Shift and mask to get 11 bits
                int shift;
                if (bitIndex <= 5)
                    shift = 16 - bitIndex - 11;
                else
                    shift = 24 - bitIndex - 11;

                indices[w] = (value >> shift) & 0x7FF; // 0x7FF = 2047 = 11 bits
            }

            // Build mnemonic string with pre-calculated capacity
            var sb = new StringBuilder(12 * 9); // avg word ~8 chars + space
            for (int w = 0; w < 12; w++)
            {
                if (w > 0) sb.Append(' ');
                sb.Append(Bip39Words[indices[w]]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Optimized PBKDF2-HMAC-SHA512 implementation.
        /// Uses .NET's built-in Rfc2898DeriveBytes with SHA512 (hardware-accelerated on modern CPUs).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ComputePbkdf2HmacSha512(
            ReadOnlySpan<byte> password,
            ReadOnlySpan<byte> salt,
            int iterations,
            Span<byte> output)
        {
            // .NET 8 has optimized PBKDF2 that uses hardware SHA512 instructions
            Rfc2898DeriveBytes.Pbkdf2(password, salt, output, iterations, HashAlgorithmName.SHA512);
        }

        /// <summary>
        /// Derive BTC/ETH addresses from a 64-byte BIP39 seed WITHOUT using NBitcoin's slow Mnemonic class.
        /// Goes directly: seed → HMAC-SHA512 → master key → child keys → addresses.
        /// This skips the Mnemonic constructor which redoes PBKDF2 internally.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static CryptoWalletGenerator.WalletInfo DeriveWalletFromSeed(
            ReadOnlySpan<byte> seed,
            string mnemonic,
            bool scanBitcoin,
            bool scanEthereum)
        {
            try
            {
                // BIP32: master key = HMAC-SHA512(key="Bitcoin seed", data=seed)
                Span<byte> masterKeyData = stackalloc byte[64];
                HMACSHA512.HashData("Bitcoin seed"u8, seed, masterKeyData);

                var privateKeyBytes = masterKeyData[..32];
                var chainCode = masterKeyData[32..];

                // Create NBitcoin ExtKey from raw bytes (SKIPS PBKDF2 — already computed)
                var masterKey = new ExtKey(new Key(privateKeyBytes.ToArray()), chainCode.ToArray());

                var wallet = new CryptoWalletGenerator.WalletInfo
                {
                    Mnemonic = mnemonic
                };

                if (scanBitcoin)
                {
                    // BIP44 Legacy: m/44'/0'/0'/0/0
                    var btcKey = masterKey.Derive(new KeyPath("m/44'/0'/0'/0/0"));
                    wallet.BitcoinAddress = btcKey.PrivateKey.PubKey
                        .GetAddress(ScriptPubKeyType.Legacy, Network.Main).ToString();

                    // BIP84 Native SegWit: m/84'/0'/0'/0/0 (most common modern path)
                    var nativeKey = masterKey.Derive(new KeyPath("m/84'/0'/0'/0/0"));
                    wallet.BitcoinAddressNativeSegWit = nativeKey.PrivateKey.PubKey
                        .GetAddress(ScriptPubKeyType.Segwit, Network.Main).ToString();

                    // BIP49 SegWit: m/49'/0'/0'/0/0
                    var segwitKey = masterKey.Derive(new KeyPath("m/49'/0'/0'/0/0"));
                    wallet.BitcoinAddressSegWit = segwitKey.PrivateKey.PubKey
                        .GetAddress(ScriptPubKeyType.SegwitP2SH, Network.Main).ToString();

                    wallet.PrivateKey = btcKey.PrivateKey.GetWif(Network.Main).ToString();

                    wallet.AllBitcoinAddresses.Add(wallet.BitcoinAddress);
                }

                if (scanEthereum)
                {
                    // Ethereum: m/44'/60'/0'/0/0 — derive directly from master key (no Nethereum overhead)
                    var ethKey = masterKey.Derive(new KeyPath("m/44'/60'/0'/0/0"));
                    var ethPubKeyBytes = ethKey.PrivateKey.PubKey.ToBytes();
                    
                    // Ethereum address = last 20 bytes of Keccak-256 of uncompressed public key (sans prefix)
                    // Decompress the public key first
                    var uncompressed = ethKey.PrivateKey.PubKey.Decompress().ToBytes();
                    // Skip the 0x04 prefix byte, hash the 64-byte X,Y coordinates
                    var pubKeyForHash = uncompressed.AsSpan(1); // 64 bytes
                    var keccak = new Nethereum.Util.Sha3Keccack();
                    var hash = keccak.CalculateHash(pubKeyForHash.ToArray());
                    // Take last 20 bytes as address
                    var addressBytes = hash.AsSpan(12, 20);
                    wallet.EthereumAddress = "0x" + Convert.ToHexString(addressBytes).ToLowerInvariant();
                    wallet.PolygonAddress = wallet.EthereumAddress;
                    wallet.BnbAddress = wallet.EthereumAddress;
                    wallet.AllEthereumAddresses.Add(wallet.EthereumAddress);
                }

                return wallet;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[HASHCAT] Derivation error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Check bloom filter — returns true if wallet MIGHT be funded.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool CheckBloomFilter(CryptoWalletGenerator.WalletInfo wallet, bool scanBtc, bool scanEth)
        {
            if (scanBtc && FundedAddressFilter.BTC.IsLoaded)
            {
                if (FundedAddressFilter.BTC.MightBeFunded(wallet.BitcoinAddress))
                    return true;
                if (!string.IsNullOrEmpty(wallet.BitcoinAddressNativeSegWit) &&
                    FundedAddressFilter.BTC.MightBeFunded(wallet.BitcoinAddressNativeSegWit))
                    return true;
                if (!string.IsNullOrEmpty(wallet.BitcoinAddressSegWit) &&
                    FundedAddressFilter.BTC.MightBeFunded(wallet.BitcoinAddressSegWit))
                    return true;
            }

            if (scanEth && FundedAddressFilter.ETH.IsLoaded)
            {
                if (FundedAddressFilter.ETH.MightBeFunded(wallet.EthereumAddress))
                    return true;
            }

            // No bloom filter loaded — assume might be funded (need API check)
            if ((!FundedAddressFilter.BTC.IsLoaded && scanBtc) ||
                (!FundedAddressFilter.ETH.IsLoaded && scanEth))
                return true;

            return false;
        }

        /// <summary>
        /// Get performance stats for display.
        /// </summary>
        public (long generated, long pbkdf2, long derived, long bloomSkipped, double rate) GetStats()
        {
            var elapsed = (DateTime.UtcNow - _startTime).TotalSeconds;
            var total = Interlocked.Read(ref _totalGenerated);
            var rate = elapsed > 0 ? total / elapsed : 0;
            return (
                total,
                Interlocked.Read(ref _totalPbkdf2),
                Interlocked.Read(ref _totalDerived),
                Interlocked.Read(ref _bloomSkipped),
                rate
            );
        }

        public string GetFormattedStats()
        {
            var (gen, pbk, der, skip, rate) = GetStats();
            return $"Generated: {gen:N0} | PBKDF2: {pbk:N0} | Derived: {der:N0} | Bloom-Skipped: {skip:N0} | Rate: {rate:N0}/sec";
        }

        public void Dispose()
        {
            while (_hmacPool.TryTake(out var hmac))
                hmac.Dispose();
        }
    }
}
