using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Encrypts sensitive data like SMTP passwords using Windows Data Protection API
    /// </summary>
    public static class EncryptionHelper
    {
        private const string ENCRYPTION_PREFIX = "ENC:";

        // Use Windows DPAPI for secure encryption tied to this user account
        public static string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
                return plainText;

            try
            {
                byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
                byte[] encryptedBytes = ProtectedData.Protect(
                    plainBytes,
                    null, // No additional entropy
                    DataProtectionScope.CurrentUser // Only this Windows user can decrypt
                );
                return ENCRYPTION_PREFIX + Convert.ToBase64String(encryptedBytes);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Encryption failed: {ex.Message}");
                return plainText; // Fallback to plain text if encryption fails
            }
        }

        public static string Decrypt(string encryptedText)
        {
            if (string.IsNullOrEmpty(encryptedText))
                return encryptedText;

            try
            {
                // Strip prefix if present
                var base64 = encryptedText.StartsWith(ENCRYPTION_PREFIX)
                    ? encryptedText.Substring(ENCRYPTION_PREFIX.Length)
                    : encryptedText; // Backward compatibility: try without prefix

                byte[] encryptedBytes = Convert.FromBase64String(base64);
                byte[] plainBytes = ProtectedData.Unprotect(
                    encryptedBytes,
                    null,
                    DataProtectionScope.CurrentUser
                );
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch
            {
                // If decryption fails, assume it's already plain text (backward compatibility)
                return encryptedText;
            }
        }

        /// <summary>
        /// Checks if a string appears to be encrypted (has encryption prefix)
        /// </summary>
        public static bool IsEncrypted(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            return text.StartsWith(ENCRYPTION_PREFIX);
        }
    }
}
