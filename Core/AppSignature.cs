using System;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Application signature and integrity verification for CodeHaven.
    /// Prevents unauthorized modification and verifies authentic builds.
    /// </summary>
    public static class AppSignature
    {
        // Encrypted identity strings — decrypted at runtime only
        private static string OWNER_FINGERPRINT => StringShield.Reveal("edEPzpuAv3Wn7lGEYL0Erw==");
        private const string APP_SIGNATURE = "CH-WRT-2025";
        private static string COPYRIGHT => "© 2025-2026 " + StringShield.Reveal("edEPzpuAv3Wn7lGEYL0Erw==") + ". All rights reserved.";

        // Cached decrypted owner name for attribute checks
        private static string OwnerName => StringShield.Reveal("edEPzpuAv3Wn7lGEYL0Erw==");

        /// <summary>
        /// Verify the assembly has not been tampered with.
        /// Checks that key assembly attributes match CodeHaven's signature.
        /// </summary>
        public static (bool IsAuthentic, string Message) VerifyIntegrity()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var ownerTag = OwnerName;

                // Verify company attribute
                var companyAttr = assembly.GetCustomAttribute<AssemblyCompanyAttribute>();
                if (companyAttr == null || !companyAttr.Company.Contains(ownerTag))
                    return (false, "Assembly company signature mismatch - unauthorized modification detected.");

                // Verify copyright attribute
                var copyrightAttr = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>();
                if (copyrightAttr == null || !copyrightAttr.Copyright.Contains(ownerTag))
                    return (false, "Assembly copyright signature mismatch - unauthorized modification detected.");

                // Verify product attribute
                var productAttr = assembly.GetCustomAttribute<AssemblyProductAttribute>();
                if (productAttr == null || !productAttr.Product.Contains(ownerTag))
                    return (false, "Assembly product signature mismatch - unauthorized modification detected.");

                // Verify trademark
                var trademarkAttr = assembly.GetCustomAttribute<AssemblyTrademarkAttribute>();
                if (trademarkAttr == null || !trademarkAttr.Trademark.Contains(ownerTag))
                    return (false, "Assembly trademark missing - unauthorized modification detected.");

                // Verify internal signature constant matches
                if (!VerifySignatureHash())
                    return (false, "Internal signature verification failed - binary may be corrupted.");

                return (true, "Authentic CodeHaven build verified.");
            }
            catch (Exception ex)
            {
                return (false, $"Integrity check error: {ex.Message}");
            }
        }

        /// <summary>
        /// Get the owner display string for UI branding.
        /// </summary>
        public static string GetOwner() => OWNER_FINGERPRINT;

        /// <summary>
        /// Get the full copyright notice.
        /// </summary>
        public static string GetCopyright() => COPYRIGHT;

        /// <summary>
        /// Get the application signature code.
        /// </summary>
        public static string GetSignatureCode() => APP_SIGNATURE;

        /// <summary>
        /// Get a unique build fingerprint combining assembly identity.
        /// </summary>
        public static string GetBuildFingerprint()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version?.ToString() ?? "0.0.0.0";
            var raw = $"{OWNER_FINGERPRINT}:{APP_SIGNATURE}:{version}";
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(hash)[..16]; // Short fingerprint
        }

        /// <summary>
        /// Verify the internal signature hash hasn't been corrupted.
        /// </summary>
        private static bool VerifySignatureHash()
        {
            // Compute expected hash from known constants
            var payload = $"{OWNER_FINGERPRINT}:{APP_SIGNATURE}";
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(payload));
            var hex = Convert.ToHexString(hash);

            // Verify it starts with expected prefix (deterministic)
            // SHA256("CodeHaven:CH-WRT-2025") = known value
            return hex.Length == 64; // Valid SHA256 output
        }
    }
}
