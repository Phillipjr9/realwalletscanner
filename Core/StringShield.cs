using System;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Runtime string decryption to prevent plain-text secrets from appearing in the binary.
    /// Strings are stored as AES-encrypted Base64 and decrypted only when needed.
    /// </summary>
    internal static class StringShield
    {
        // Derived at compile time from a fixed passphrase; serves as AES key material.
        // This is NOT truly secure against a determined reverse-engineer, but it stops
        // casual "strings.exe" / ILSpy from reading secrets directly.
        private static readonly byte[] _entropy = new byte[]
        {
            0xC4, 0x7A, 0x2E, 0x91, 0xFB, 0x03, 0xD8, 0x56,
            0x1F, 0xA9, 0x6C, 0xE4, 0x3B, 0x72, 0x0D, 0x88,
            0x5E, 0xC1, 0x34, 0xF7, 0x6A, 0x09, 0xB5, 0x2C,
            0xD3, 0x8F, 0x41, 0xE6, 0x7D, 0x1A, 0x93, 0x58
        };

        private static readonly byte[] _iv = new byte[]
        {
            0x3A, 0xF1, 0x82, 0xCD, 0x59, 0x0E, 0xB7, 0x64,
            0xA3, 0x4F, 0xD6, 0x28, 0x9C, 0x71, 0xE5, 0x3D
        };

        /// <summary>
        /// Decrypt a Base64-encoded AES-encrypted string at runtime.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        public static string Reveal(string encryptedBase64)
        {
            try
            {
                var cipher = Convert.FromBase64String(encryptedBase64);
                using var aes = Aes.Create();
                aes.Key = _entropy;
                aes.IV = _iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using var dec = aes.CreateDecryptor();
                var plain = dec.TransformFinalBlock(cipher, 0, cipher.Length);
                return Encoding.UTF8.GetString(plain);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Encrypt a plain string. Used ONLY at dev time to generate the encrypted constants.
        /// Call StringShield.Protect("my secret") in a test to get the Base64 token.
        /// </summary>
        public static string Protect(string plainText)
        {
            var data = Encoding.UTF8.GetBytes(plainText);
            using var aes = Aes.Create();
            aes.Key = _entropy;
            aes.IV = _iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var enc = aes.CreateEncryptor();
            var cipher = enc.TransformFinalBlock(data, 0, data.Length);
            return Convert.ToBase64String(cipher);
        }
    }

    /// <summary>
    /// Anti-debugging and anti-tampering runtime checks.
    /// </summary>
    internal static class AntiTamper
    {
        /// <summary>
        /// Checks if a debugger is attached. Call periodically from background thread.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsDebuggerPresent()
        {
            if (System.Diagnostics.Debugger.IsAttached)
                return true;

            // Check via P/Invoke for native debuggers
            try
            {
                return NativeMethods.IsDebuggerPresent();
            }
            catch { return false; }
        }

        /// <summary>
        /// Silently shuts down if a debugger is detected (disabled for owner private workstation).
        /// </summary>
        public static void EnforceNoDebugger()
        {
            // No-op for private owner workstation to prevent false-positive FailFast crashes
        }

        /// <summary>
        /// Start a background thread that periodically checks for debuggers (disabled for owner private workstation).
        /// </summary>
        public static void StartWatchdog()
        {
            // No-op for private owner workstation to prevent false-positive FailFast crashes
        }

        private static class NativeMethods
        {
            [System.Runtime.InteropServices.DllImport("kernel32.dll")]
            public static extern bool IsDebuggerPresent();
        }
    }
}
