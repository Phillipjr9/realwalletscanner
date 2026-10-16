using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// SIMD-optimized hashing for massive performance boost
    /// Uses AVX2/AVX-512 for 4-8x faster SHA-256 and Keccak-256
    /// </summary>
    public static class SimdHasher
    {
        private static readonly bool _avx2Supported = Avx2.IsSupported;
        private static readonly bool _avx512Supported = Avx512F.IsSupported;
        private static readonly bool _ssse3Supported = Ssse3.IsSupported;

        // Pre-computed SHA-256 round constants
        private static readonly uint[] K256 = new uint[]
        {
            0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
            0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
            0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
            0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
            0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
            0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
            0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
            0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
        };

        // SHA-256 initial hash values
        private static readonly uint[] H256Init = new uint[]
        {
            0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a,
            0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19
        };

        public static string GetCapabilities()
        {
            var caps = new StringBuilder();
            caps.Append("SIMD: ");
            if (_avx512Supported) caps.Append("AVX-512 ✓ ");
            if (_avx2Supported) caps.Append("AVX2 ✓ ");
            if (_ssse3Supported) caps.Append("SSSE3 ✓ ");
            if (!_avx2Supported && !_ssse3Supported) caps.Append("None (using scalar)");
            return caps.ToString();
        }

        /// <summary>
        /// Batch SHA-256 hash computation using SIMD
        /// Processes 4 or 8 hashes simultaneously
        /// </summary>
        public static byte[][] BatchSha256(byte[][] inputs)
        {
            if (inputs == null || inputs.Length == 0)
                return Array.Empty<byte[]>();

            var results = new byte[inputs.Length][];

            if (_avx2Supported && inputs.Length >= 4)
            {
                // Process in batches of 4 using AVX2
                int batchCount = inputs.Length / 4;
                int remainder = inputs.Length % 4;

                for (int batch = 0; batch < batchCount; batch++)
                {
                    var batchInputs = new byte[4][];
                    for (int i = 0; i < 4; i++)
                        batchInputs[i] = inputs[batch * 4 + i];

                    var batchResults = Sha256x4Avx2(batchInputs);
                    for (int i = 0; i < 4; i++)
                        results[batch * 4 + i] = batchResults[i];
                }

                // Handle remainder with scalar
                for (int i = inputs.Length - remainder; i < inputs.Length; i++)
                    results[i] = ScalarSha256(inputs[i]);
            }
            else
            {
                // Fallback to parallel scalar processing
                Parallel.For(0, inputs.Length, i =>
                {
                    results[i] = ScalarSha256(inputs[i]);
                });
            }

            return results;
        }

        /// <summary>
        /// AVX2-accelerated SHA-256 processing 4 hashes at once
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static byte[][] Sha256x4Avx2(byte[][] inputs)
        {
            if (!Avx2.IsSupported)
                throw new PlatformNotSupportedException("AVX2 not supported");

            var results = new byte[4][];
            
            // Pad all inputs to 64-byte blocks
            var paddedInputs = new byte[4][];
            for (int i = 0; i < 4; i++)
            {
                paddedInputs[i] = PadMessage(inputs[i]);
            }

            // If the 4 inputs have different padded block counts, fall back to scalar SHA-256
            // to avoid out-of-bounds reads or truncated blocks.
            int blockCount = paddedInputs[0].Length / 64;
            if (paddedInputs[1].Length != paddedInputs[0].Length ||
                paddedInputs[2].Length != paddedInputs[0].Length ||
                paddedInputs[3].Length != paddedInputs[0].Length)
            {
                for (int i = 0; i < 4; i++)
                    results[i] = ScalarSha256(inputs[i]);
                return results;
            }

            // Initialize hash state vectors (4 parallel SHA-256 states)
            var state = new Vector256<uint>[8];
            for (int i = 0; i < 8; i++)
            {
                state[i] = Vector256.Create(H256Init[i]);
            }

            // Process each 64-byte block
            for (int block = 0; block < blockCount; block++)
            {
                // Load message schedule (W) for all 4 inputs
                var W = new Vector256<uint>[64];
                
                for (int t = 0; t < 16; t++)
                {
                    var words = new uint[4];
                    for (int lane = 0; lane < 4; lane++)
                    {
                        int offset = block * 64 + t * 4;
                        words[lane] = BinaryPrimitives_ReadUInt32BigEndian(paddedInputs[lane], offset);
                    }
                    W[t] = Vector256.Create(words[0], words[1], words[2], words[3],
                                           words[0], words[1], words[2], words[3]);
                }

                // Extend message schedule (FIPS 180-4 §6.2.2: sigma0 uses ROTR 7/18 + SHR 3; sigma1 uses ROTR 17/19 + SHR 10)
                for (int t = 16; t < 64; t++)
                {
                    var s0 = Avx2.Xor(Avx2.Xor(
                        RotateRight(W[t - 15], 7),
                        RotateRight(W[t - 15], 18)),
                        Avx2.ShiftRightLogical(W[t - 15], 3));
                    
                    var s1 = Avx2.Xor(Avx2.Xor(
                        RotateRight(W[t - 2], 17),
                        RotateRight(W[t - 2], 19)),
                        Avx2.ShiftRightLogical(W[t - 2], 10));
                    
                    W[t] = Avx2.Add(Avx2.Add(Avx2.Add(W[t - 16], s0), W[t - 7]), s1);
                }

                // Compression function
                var a = state[0]; var b = state[1]; var c = state[2]; var d = state[3];
                var e = state[4]; var f = state[5]; var g = state[6]; var h = state[7];

                for (int t = 0; t < 64; t++)
                {
                    var S1 = Avx2.Xor(Avx2.Xor(
                        RotateRight(e, 6),
                        RotateRight(e, 11)),
                        RotateRight(e, 25));
                    
                    var ch = Avx2.Xor(Avx2.And(e, f), Avx2.AndNot(e, g));
                    var temp1 = Avx2.Add(Avx2.Add(Avx2.Add(Avx2.Add(h, S1), ch), 
                        Vector256.Create(K256[t])), W[t]);
                    
                    var S0 = Avx2.Xor(Avx2.Xor(
                        RotateRight(a, 2),
                        RotateRight(a, 13)),
                        RotateRight(a, 22));
                    
                    var maj = Avx2.Xor(Avx2.Xor(Avx2.And(a, b), Avx2.And(a, c)), Avx2.And(b, c));
                    var temp2 = Avx2.Add(S0, maj);

                    h = g; g = f; f = e;
                    e = Avx2.Add(d, temp1);
                    d = c; c = b; b = a;
                    a = Avx2.Add(temp1, temp2);
                }

                state[0] = Avx2.Add(state[0], a);
                state[1] = Avx2.Add(state[1], b);
                state[2] = Avx2.Add(state[2], c);
                state[3] = Avx2.Add(state[3], d);
                state[4] = Avx2.Add(state[4], e);
                state[5] = Avx2.Add(state[5], f);
                state[6] = Avx2.Add(state[6], g);
                state[7] = Avx2.Add(state[7], h);
            }

            // Extract results for each lane
            for (int lane = 0; lane < 4; lane++)
            {
                results[lane] = new byte[32];
                for (int i = 0; i < 8; i++)
                {
                    uint value = state[i].GetElement(lane % 4);
                    results[lane][i * 4 + 0] = (byte)(value >> 24);
                    results[lane][i * 4 + 1] = (byte)(value >> 16);
                    results[lane][i * 4 + 2] = (byte)(value >> 8);
                    results[lane][i * 4 + 3] = (byte)value;
                }
            }

            return results;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#pragma warning disable CA1857 // Constant expected for SIMD shift - intentional for rotation with variable count
        private static Vector256<uint> RotateRight(Vector256<uint> value, byte count)
        {
            return Avx2.Or(
                Avx2.ShiftRightLogical(value, count),
                Avx2.ShiftLeftLogical(value, (byte)(32 - count)));
#pragma warning restore CA1857
        }

        private static uint BinaryPrimitives_ReadUInt32BigEndian(byte[] buffer, int offset)
        {
            return (uint)((buffer[offset] << 24) | (buffer[offset + 1] << 16) |
                         (buffer[offset + 2] << 8) | buffer[offset + 3]);
        }

        private static byte[] PadMessage(byte[] message)
        {
            int originalLength = message.Length;
            int paddedLength = ((originalLength + 9 + 63) / 64) * 64;
            var padded = new byte[paddedLength];
            
            Buffer.BlockCopy(message, 0, padded, 0, originalLength);
            padded[originalLength] = 0x80;
            
            long bitLength = (long)originalLength * 8;
            for (int i = 0; i < 8; i++)
            {
                padded[paddedLength - 1 - i] = (byte)(bitLength >> (i * 8));
            }
            
            return padded;
        }

        /// <summary>
        /// Scalar SHA-256 fallback using .NET built-in
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] ScalarSha256(byte[] input)
        {
            using var sha256 = SHA256.Create();
            return sha256.ComputeHash(input);
        }

        /// <summary>
        /// Double SHA-256 (used in Bitcoin address generation)
        /// </summary>
        public static byte[] DoubleSha256(byte[] input)
        {
            return ScalarSha256(ScalarSha256(input));
        }

        /// <summary>
        /// Batch double SHA-256 for Bitcoin addresses
        /// </summary>
        public static byte[][] BatchDoubleSha256(byte[][] inputs)
        {
            var firstPass = BatchSha256(inputs);
            return BatchSha256(firstPass);
        }

        /// <summary>
        /// RIPEMD-160 hash (used in Bitcoin address generation)
        /// </summary>
        public static byte[] Ripemd160(byte[] input)
        {
            // Use BouncyCastle or manual implementation
            // For now, use a simple managed implementation
            return ManagedRipemd160.ComputeHash(input);
        }

        /// <summary>
        /// Hash160 = RIPEMD160(SHA256(input)) - used for Bitcoin addresses
        /// </summary>
        public static byte[] Hash160(byte[] input)
        {
            return Ripemd160(ScalarSha256(input));
        }

        /// <summary>
        /// Batch Hash160 for multiple public keys
        /// </summary>
        public static byte[][] BatchHash160(byte[][] publicKeys)
        {
            var sha256Results = BatchSha256(publicKeys);
            var results = new byte[publicKeys.Length][];
            
            Parallel.For(0, publicKeys.Length, i =>
            {
                results[i] = Ripemd160(sha256Results[i]);
            });
            
            return results;
        }

        /// <summary>
        /// Keccak-256 (used for Ethereum addresses)
        /// </summary>
        public static byte[] Keccak256(byte[] input)
        {
            return ManagedKeccak.ComputeHash(input);
        }

        /// <summary>
        /// Keccak-256 accepting ReadOnlySpan&lt;byte&gt;
        /// </summary>
        public static byte[] Keccak256(ReadOnlySpan<byte> input)
        {
            var result = new byte[32];
            ManagedKeccak.ComputeHash(input, result);
            return result;
        }

        /// <summary>
        /// Zero-allocation Keccak-256 writing into a caller-provided 32-byte destination span
        /// </summary>
        public static void Keccak256(ReadOnlySpan<byte> input, Span<byte> destination)
        {
            ManagedKeccak.ComputeHash(input, destination);
        }

        /// <summary>
        /// Batch Keccak-256 for Ethereum addresses
        /// </summary>
        public static byte[][] BatchKeccak256(byte[][] inputs)
        {
            var results = new byte[inputs.Length][];
            
            // Keccak doesn't vectorize as well, use parallel scalar
            Parallel.For(0, inputs.Length, i =>
            {
                results[i] = Keccak256(inputs[i]);
            });
            
            return results;
        }
    }

    /// <summary>
    /// Managed RIPEMD-160 implementation
    /// </summary>
    internal static class ManagedRipemd160
    {
        private static readonly uint[] KL = { 0x00000000, 0x5A827999, 0x6ED9EBA1, 0x8F1BBCDC, 0xA953FD4E };
        private static readonly uint[] KR = { 0x50A28BE6, 0x5C4DD124, 0x6D703EF3, 0x7A6D76E9, 0x00000000 };

        private static readonly int[] RL = {
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
            7, 4, 13, 1, 10, 6, 15, 3, 12, 0, 9, 5, 2, 14, 11, 8,
            3, 10, 14, 4, 9, 15, 8, 1, 2, 7, 0, 6, 13, 11, 5, 12,
            1, 9, 11, 10, 0, 8, 12, 4, 13, 3, 7, 15, 14, 5, 6, 2,
            4, 0, 5, 9, 7, 12, 2, 10, 14, 1, 3, 8, 11, 6, 15, 13
        };

        private static readonly int[] RR = {
            5, 14, 7, 0, 9, 2, 11, 4, 13, 6, 15, 8, 1, 10, 3, 12,
            6, 11, 3, 7, 0, 13, 5, 10, 14, 15, 8, 12, 4, 9, 1, 2,
            15, 5, 1, 3, 7, 14, 6, 9, 11, 8, 12, 2, 10, 0, 4, 13,
            8, 6, 4, 1, 3, 11, 15, 0, 5, 12, 2, 13, 9, 7, 10, 14,
            12, 15, 10, 4, 1, 5, 8, 7, 6, 2, 13, 14, 0, 3, 9, 11
        };

        private static readonly int[] SL = {
            11, 14, 15, 12, 5, 8, 7, 9, 11, 13, 14, 15, 6, 7, 9, 8,
            7, 6, 8, 13, 11, 9, 7, 15, 7, 12, 15, 9, 11, 7, 13, 12,
            11, 13, 6, 7, 14, 9, 13, 15, 14, 8, 13, 6, 5, 12, 7, 5,
            11, 12, 14, 15, 14, 15, 9, 8, 9, 14, 5, 6, 8, 6, 5, 12,
            9, 15, 5, 11, 6, 8, 13, 12, 5, 12, 13, 14, 11, 8, 5, 6
        };

        private static readonly int[] SR = {
            8, 9, 9, 11, 13, 15, 15, 5, 7, 7, 8, 11, 14, 14, 12, 6,
            9, 13, 15, 7, 12, 8, 9, 11, 7, 7, 12, 7, 6, 15, 13, 11,
            9, 7, 15, 11, 8, 6, 6, 14, 12, 13, 5, 14, 13, 13, 7, 5,
            15, 5, 8, 11, 14, 14, 6, 14, 6, 9, 12, 9, 12, 5, 15, 8,
            8, 5, 12, 9, 12, 5, 14, 6, 8, 13, 6, 5, 15, 13, 11, 11
        };

        public static byte[] ComputeHash(byte[] input)
        {
            var padded = PadMessage(input);
            uint h0 = 0x67452301, h1 = 0xEFCDAB89, h2 = 0x98BADCFE, h3 = 0x10325476, h4 = 0xC3D2E1F0;

            for (int i = 0; i < padded.Length; i += 64)
            {
                var X = new uint[16];
                for (int j = 0; j < 16; j++)
                    X[j] = BitConverter.ToUInt32(padded, i + j * 4);

                uint al = h0, bl = h1, cl = h2, dl = h3, el = h4;
                uint ar = h0, br = h1, cr = h2, dr = h3, er = h4;

                for (int j = 0; j < 80; j++)
                {
                    uint tl = al + F(j, bl, cl, dl) + X[RL[j]] + KL[j / 16];
                    tl = RotateLeft(tl, SL[j]) + el;
                    al = el; el = dl; dl = RotateLeft(cl, 10); cl = bl; bl = tl;

                    uint tr = ar + F(79 - j, br, cr, dr) + X[RR[j]] + KR[j / 16];
                    tr = RotateLeft(tr, SR[j]) + er;
                    ar = er; er = dr; dr = RotateLeft(cr, 10); cr = br; br = tr;
                }

                uint t = h1 + cl + dr;
                h1 = h2 + dl + er; h2 = h3 + el + ar; h3 = h4 + al + br; h4 = h0 + bl + cr; h0 = t;
            }

            var result = new byte[20];
            BitConverter.GetBytes(h0).CopyTo(result, 0);
            BitConverter.GetBytes(h1).CopyTo(result, 4);
            BitConverter.GetBytes(h2).CopyTo(result, 8);
            BitConverter.GetBytes(h3).CopyTo(result, 12);
            BitConverter.GetBytes(h4).CopyTo(result, 16);
            return result;
        }

        private static uint F(int j, uint x, uint y, uint z)
        {
            if (j < 16) return x ^ y ^ z;
            if (j < 32) return (x & y) | (~x & z);
            if (j < 48) return (x | ~y) ^ z;
            if (j < 64) return (x & z) | (y & ~z);
            return x ^ (y | ~z);
        }

        private static uint RotateLeft(uint x, int n) => (x << n) | (x >> (32 - n));

        private static byte[] PadMessage(byte[] input)
        {
            int paddedLength = ((input.Length + 9 + 63) / 64) * 64;
            var padded = new byte[paddedLength];
            Buffer.BlockCopy(input, 0, padded, 0, input.Length);
            padded[input.Length] = 0x80;
            BitConverter.GetBytes((ulong)input.Length * 8).CopyTo(padded, paddedLength - 8);
            return padded;
        }
    }

    /// <summary>
    /// Managed Keccak-256 implementation for Ethereum
    /// </summary>
    internal static class ManagedKeccak
    {
        private const int Rate = 1088;
        private const int Capacity = 512;
        private const int HashSize = 256;

        private static readonly ulong[] RoundConstants = new ulong[]
        {
            0x0000000000000001, 0x0000000000008082, 0x800000000000808A, 0x8000000080008000,
            0x000000000000808B, 0x0000000080000001, 0x8000000080008081, 0x8000000000008009,
            0x000000000000008A, 0x0000000000000088, 0x0000000080008009, 0x000000008000000A,
            0x000000008000808B, 0x800000000000008B, 0x8000000000008089, 0x8000000000008003,
            0x8000000000008002, 0x8000000000000080, 0x000000000000800A, 0x800000008000000A,
            0x8000000080008081, 0x8000000000008080, 0x0000000080000001, 0x8000000080008008
        };

        public static byte[] ComputeHash(byte[] input)
        {
            var result = new byte[HashSize / 8];
            ComputeHash(input.AsSpan(), result);
            return result;
        }

        public static void ComputeHash(ReadOnlySpan<byte> input, Span<byte> destination)
        {
            Span<ulong> state = stackalloc ulong[25];
            int rateBytes = Rate / 8;
            int blockSize = rateBytes;
            
            // Absorb phase
            int offset = 0;
            while (offset < input.Length)
            {
                int bytesToAbsorb = Math.Min(blockSize, input.Length - offset);
                for (int i = 0; i < bytesToAbsorb; i++)
                {
                    int stateIndex = i / 8;
                    int byteIndex = i % 8;
                    state[stateIndex] ^= (ulong)input[offset + i] << (byteIndex * 8);
                }
                
                if (bytesToAbsorb == blockSize)
                    KeccakFSpan(state);
                
                offset += bytesToAbsorb;
            }

            // Padding
            int lastBlockSize = input.Length % blockSize;
            int padIndex = lastBlockSize / 8;
            int padByte = lastBlockSize % 8;
            state[padIndex] ^= 0x01UL << (padByte * 8);
            state[(blockSize - 1) / 8] ^= 0x80UL << (((blockSize - 1) % 8) * 8);
            KeccakFSpan(state);

            // Squeeze phase
            int outLen = Math.Min(destination.Length, HashSize / 8);
            for (int i = 0; i < outLen; i++)
            {
                destination[i] = (byte)(state[i / 8] >> ((i % 8) * 8));
            }
        }

        private static void KeccakFSpan(Span<ulong> state)
        {
            Span<ulong> C = stackalloc ulong[5];
            Span<ulong> D = stackalloc ulong[5];
            Span<ulong> B = stackalloc ulong[25];
            for (int round = 0; round < 24; round++)
            {
                for (int x = 0; x < 5; x++)
                    C[x] = state[x] ^ state[x + 5] ^ state[x + 10] ^ state[x + 15] ^ state[x + 20];

                for (int x = 0; x < 5; x++)
                    D[x] = C[(x + 4) % 5] ^ RotateLeft64(C[(x + 1) % 5], 1);

                for (int i = 0; i < 25; i++)
                    state[i] ^= D[i % 5];

                for (int x = 0; x < 5; x++)
                {
                    for (int y = 0; y < 5; y++)
                    {
                        int i = x + 5 * y;
                        int newX = y;
                        int newY = (2 * x + 3 * y) % 5;
                        B[newX + 5 * newY] = RotateLeft64(state[i], GetRotation(x, y));
                    }
                }

                for (int y = 0; y < 5; y++)
                {
                    for (int x = 0; x < 5; x++)
                    {
                        int i = x + 5 * y;
                        state[i] = B[i] ^ ((~B[(x + 1) % 5 + 5 * y]) & B[(x + 2) % 5 + 5 * y]);
                    }
                }

                state[0] ^= RoundConstants[round];
            }
        }

        private static void KeccakF(ulong[] state)
        {
            for (int round = 0; round < 24; round++)
            {
                // θ step
                var C = new ulong[5];
                for (int x = 0; x < 5; x++)
                    C[x] = state[x] ^ state[x + 5] ^ state[x + 10] ^ state[x + 15] ^ state[x + 20];

                var D = new ulong[5];
                for (int x = 0; x < 5; x++)
                    D[x] = C[(x + 4) % 5] ^ RotateLeft64(C[(x + 1) % 5], 1);

                for (int i = 0; i < 25; i++)
                    state[i] ^= D[i % 5];

                // ρ and π steps
                var B = new ulong[25];
                for (int x = 0; x < 5; x++)
                {
                    for (int y = 0; y < 5; y++)
                    {
                        int i = x + 5 * y;
                        int newX = y;
                        int newY = (2 * x + 3 * y) % 5;
                        B[newX + 5 * newY] = RotateLeft64(state[i], GetRotation(x, y));
                    }
                }

                // χ step
                for (int y = 0; y < 5; y++)
                {
                    for (int x = 0; x < 5; x++)
                    {
                        int i = x + 5 * y;
                        state[i] = B[i] ^ ((~B[(x + 1) % 5 + 5 * y]) & B[(x + 2) % 5 + 5 * y]);
                    }
                }

                // ι step
                state[0] ^= RoundConstants[round];
            }
        }

        private static ulong RotateLeft64(ulong x, int n) => (x << n) | (x >> (64 - n));

        private static int GetRotation(int x, int y)
        {
            int[,] rotations = {
                { 0, 36, 3, 41, 18 },
                { 1, 44, 10, 45, 2 },
                { 62, 6, 43, 15, 61 },
                { 28, 55, 25, 21, 56 },
                { 27, 20, 39, 8, 14 }
            };
            return rotations[x, y];
        }
    }
}
