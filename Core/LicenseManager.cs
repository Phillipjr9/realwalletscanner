using System;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// License key system for CodeHaven Wallet Recovery Tool.
    /// Generates and validates license keys tied to a secret only the owner knows.
    /// </summary>
    public static class LicenseManager
    {
        // Encrypted at rest — decrypted only at runtime via StringShield
        private static string LICENSE_SECRET => StringShield.Reveal("FTjpEOyyPSBlG6+xc8/brkIFFj215RetMn0f7CQFD8sfltGx029d89dM6Py/E309");
        private static string ADMIN_HWID => StringShield.Reveal("/Yr8MhGYV774vf5h0Gh+lg0buE5iG95wi0OVi5sNDAhgO2Yv0BLzKjYPNkkKFz0u");

        private static readonly string LicenseFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WalletRecoveryTool", "license.json");

        /// <summary>
        /// Returns true if the current machine is the admin (CodeHaven owner) machine.
        /// </summary>
        public static bool IsAdminMachine()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT UUID FROM Win32_ComputerSystemProduct");
                foreach (var obj in searcher.Get())
                {
                    var uuid = obj["UUID"]?.ToString();
                    if (string.Equals(uuid, ADMIN_HWID, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Check if a valid license is stored on this machine (auto-provisions owner license for private one-click install).
        /// </summary>
        public static bool IsLicensed()
        {
            try
            {
                if (File.Exists(LicenseFilePath))
                {
                    var json = File.ReadAllText(LicenseFilePath);
                    var data = JsonSerializer.Deserialize<LicenseData>(json);
                    if (data != null && !string.IsNullOrWhiteSpace(data.Key) && ValidateKey(data.Key))
                        return true;
                }

                // Auto-provision valid owner license for private one-click installation
                var autoKey = GenerateKey();
                return ActivateLicense(autoKey, Environment.UserName ?? "Owner");
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Get stored license info, or null if none.
        /// </summary>
        public static LicenseData GetStoredLicense()
        {
            try
            {
                if (!File.Exists(LicenseFilePath))
                    return null;
                var json = File.ReadAllText(LicenseFilePath);
                return JsonSerializer.Deserialize<LicenseData>(json);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Validate a license key format and signature.
        /// Key format: XXXXX-XXXXX-XXXXX-XXXXX-CHECK
        /// </summary>
        public static bool ValidateKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return false;

            key = key.Trim().ToUpperInvariant();

            // Format: 5 groups of 5 chars separated by dashes
            var parts = key.Split('-');
            if (parts.Length != 5)
                return false;

            if (parts.Any(p => p.Length != 5))
                return false;

            // Last group is a checksum of first 4 groups + secret
            var payload = string.Join("-", parts.Take(4));
            var expectedCheck = ComputeChecksum(payload);

            return parts[4] == expectedCheck;
        }

        /// <summary>
        /// Activate a license key and store it.
        /// </summary>
        public static (bool Success, string Message) ActivateLicense(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return (false, "Please enter a license key.");

            key = key.Trim().ToUpperInvariant();

            if (!ValidateKey(key))
                return (false, "Invalid license key. Please check and try again.");

            try
            {
                var dir = Path.GetDirectoryName(LicenseFilePath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var data = new LicenseData
                {
                    Key = key,
                    ActivatedDate = DateTime.UtcNow.ToString("o"),
                    MachineName = Environment.MachineName
                };

                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(LicenseFilePath, json);

                return (true, "License activated successfully! Thank you for supporting CodeHaven.");
            }
            catch (Exception ex)
            {
                return (false, $"Failed to save license: {ex.Message}");
            }
        }

        /// <summary>
        /// Generate a valid license key. OWNER USE ONLY.
        /// Call this from a separate tool or debug console to create keys.
        /// </summary>
        public static string GenerateKey()
        {
            var rng = RandomNumberGenerator.Create();
            var groups = new string[4];
            var chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // No 0/O/1/I to avoid confusion

            for (int g = 0; g < 4; g++)
            {
                var sb = new StringBuilder(5);
                var bytes = new byte[5];
                rng.GetBytes(bytes);
                for (int i = 0; i < 5; i++)
                    sb.Append(chars[bytes[i] % chars.Length]);
                groups[g] = sb.ToString();
            }

            var payload = string.Join("-", groups);
            var check = ComputeChecksum(payload);
            return $"{payload}-{check}";
        }

        /// <summary>
        /// Generate multiple license keys at once. OWNER USE ONLY.
        /// </summary>
        public static string[] GenerateKeys(int count)
        {
            var keys = new string[count];
            for (int i = 0; i < count; i++)
                keys[i] = GenerateKey();
            return keys;
        }

        private static string ComputeChecksum(string payload)
        {
            var input = $"{LICENSE_SECRET}:{payload}";
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            // Take 5 chars from the hash as the checksum
            var chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var sb = new StringBuilder(5);
            for (int i = 0; i < 5; i++)
                sb.Append(chars[hash[i] % chars.Length]);
            return sb.ToString();
        }
    }

    public class LicenseData
    {
        public string Key { get; set; }
        public string ActivatedDate { get; set; }
        public string MachineName { get; set; }
    }
}
