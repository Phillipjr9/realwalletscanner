using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Security manager for password protection and data encryption
    /// </summary>
    public class SecurityManager
    {
        private const int KEY_SIZE = 256;
        private const int ITERATIONS = 10000;
        // Static salt only for EncryptData/DecryptData (master-password-based AES encryption)
        private const string ENCRYPTION_SALT = "WalletRecoveryTool_v1.0_EncSalt";

        private static string _masterPassword = null;
        private static bool _isLocked = true;

        /// <summary>
        /// Check if application is currently locked
        /// </summary>
        public static bool IsLocked => _isLocked;

        private static string GetSecurityFilePath()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WalletRecoveryTool");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "security.json");
        }

        /// <summary>
        /// Set up initial password protection
        /// </summary>
        public static bool SetupPassword(string password)
        {
            if (string.IsNullOrWhiteSpace(password))
                return false;

            try
            {
                // Generate a random salt for this user
                var saltBytes = new byte[32];
                using (var rng = RandomNumberGenerator.Create())
                    rng.GetBytes(saltBytes);
                var salt = Convert.ToBase64String(saltBytes);

                var passwordHash = HashPassword(password, salt);
                var securitySettings = new SecuritySettings
                {
                    PasswordHash = passwordHash,
                    Salt = salt,
                    IsPasswordProtected = true,
                    CreatedDate = DateTime.Now
                };

                File.WriteAllText(GetSecurityFilePath(), JsonConvert.SerializeObject(securitySettings, Formatting.Indented));
                _masterPassword = password;
                _isLocked = false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Verify password and unlock application
        /// </summary>
        public static bool UnlockApplication(string password)
        {
            try
            {
                var securityFile = GetSecurityFilePath();
                if (!File.Exists(securityFile))
                {
                    _isLocked = false;
                    return true; // No password set
                }

                var json = File.ReadAllText(securityFile);
                var settings = JsonConvert.DeserializeObject<SecuritySettings>(json);

                if (!settings.IsPasswordProtected)
                {
                    _isLocked = false;
                    return true;
                }

                var passwordHash = HashPassword(password, settings.Salt ?? "WalletRecoveryTool_v1.0_Salt");
                if (passwordHash == settings.PasswordHash)
                {
                    _masterPassword = password;
                    _isLocked = false;
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Lock the application
        /// </summary>
        public static void LockApplication()
        {
            _masterPassword = null;
            _isLocked = true;
        }

        /// <summary>
        /// Check if password protection is enabled
        /// </summary>
        public static bool IsPasswordProtectionEnabled()
        {
            try
            {
                var securityFile = GetSecurityFilePath();
                if (!File.Exists(securityFile))
                    return false;

                var json = File.ReadAllText(securityFile);
                var settings = JsonConvert.DeserializeObject<SecuritySettings>(json);
                return settings.IsPasswordProtected;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Encrypt sensitive data
        /// </summary>
        public static string EncryptData(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
                return plainText;

            if (string.IsNullOrEmpty(_masterPassword))
                throw new InvalidOperationException("Encryption requires a master password to be set. Please set a password first.");

            try
            {
                using (var aes = Aes.Create())
                using (var key = new Rfc2898DeriveBytes(_masterPassword, Encoding.UTF8.GetBytes(ENCRYPTION_SALT), ITERATIONS, HashAlgorithmName.SHA256))
                {
                    aes.Key = key.GetBytes(KEY_SIZE / 8);
                    aes.GenerateIV(); // Use random IV instead of deterministic

                    using (var encryptor = aes.CreateEncryptor())
                    using (var ms = new MemoryStream())
                    {
                        // Prepend IV to ciphertext for decryption
                        ms.Write(aes.IV, 0, aes.IV.Length);
                        using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                        using (var sw = new StreamWriter(cs))
                        {
                            sw.Write(plainText);
                        }
                        return Convert.ToBase64String(ms.ToArray());
                    }
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Encryption failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Decrypt encrypted data
        /// </summary>
        public static string DecryptData(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText))
                return cipherText;

            if (string.IsNullOrEmpty(_masterPassword))
                throw new InvalidOperationException("Decryption requires a master password to be set.");

            try
            {
                var cipherBytes = Convert.FromBase64String(cipherText);
                
                using (var aes = Aes.Create())
                using (var key = new Rfc2898DeriveBytes(_masterPassword, Encoding.UTF8.GetBytes(ENCRYPTION_SALT), ITERATIONS, HashAlgorithmName.SHA256))
                {
                    aes.Key = key.GetBytes(KEY_SIZE / 8);
                    
                    // Extract IV from prepended bytes (new format)
                    if (cipherBytes.Length > aes.IV.Length)
                    {
                        var iv = new byte[aes.IV.Length];
                        Array.Copy(cipherBytes, 0, iv, 0, iv.Length);
                        aes.IV = iv;
                        
                        var actualCipher = new byte[cipherBytes.Length - iv.Length];
                        Array.Copy(cipherBytes, iv.Length, actualCipher, 0, actualCipher.Length);
                        
                        using (var decryptor = aes.CreateDecryptor())
                        using (var ms = new MemoryStream(actualCipher))
                        using (var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                        using (var sr = new StreamReader(cs))
                        {
                            return sr.ReadToEnd();
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException("Cipher text is too short to contain IV.");
                    }
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Decryption failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Securely wipe sensitive data from memory
        /// </summary>
        public static void SecureWipeString(ref string sensitiveData)
        {
            if (string.IsNullOrEmpty(sensitiveData))
                return;

            // Overwrite string in memory (not foolproof in .NET but better than nothing)
            unsafe
            {
                fixed (char* ptr = sensitiveData)
                {
                    for (int i = 0; i < sensitiveData.Length; i++)
                    {
                        ptr[i] = '\0';
                    }
                }
            }
            sensitiveData = null;
        }

        /// <summary>
        /// Hash password using SHA256
        /// </summary>
        private static string HashPassword(string password, string salt)
        {
            using (var sha256 = SHA256.Create())
            {
                var saltedPassword = password + salt;
                var bytes = Encoding.UTF8.GetBytes(saltedPassword);
                var hash = sha256.ComputeHash(bytes);
                return Convert.ToBase64String(hash);
            }
        }

        private class SecuritySettings
        {
            public string PasswordHash { get; set; }
            public string Salt { get; set; }
            public bool IsPasswordProtected { get; set; }
            public DateTime CreatedDate { get; set; }
        }
    }
}
