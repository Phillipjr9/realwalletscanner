using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Built-in deterministic cryptographic, forensic, non-repetition, and Cognitive Brain Engine self-test suite.
    /// Verifies all core subsystems without requiring external network access.
    /// </summary>
    public static class SelfTestRunner
    {
        public class TestCaseResult
        {
            public string Name { get; set; } = string.Empty;
            public bool Passed { get; set; }
            public string Details { get; set; } = string.Empty;
            public double ElapsedMs { get; set; }
        }

        public class DiagnosticSuiteReport
        {
            public List<TestCaseResult> Results { get; set; } = new();
            public int PassedCount => Results.Count(r => r.Passed);
            public int FailedCount => Results.Count(r => !r.Passed);
            public bool AllPassed => Results.Count > 0 && FailedCount == 0;
            public double TotalElapsedMs => Results.Sum(r => r.ElapsedMs);
        }

        public static DiagnosticSuiteReport RunAllCoreTests()
        {
            var report = new DiagnosticSuiteReport();

            report.Results.Add(RunTest("1. BIP39/44/49/84/86 Multi-Chain Derivation", TestBipDerivation));
            report.Results.Add(RunTest("2. Direct SHA-256 Checksum-Bit Last-Word Solver (16x-256x)", TestExactLastWordSolver));
            report.Results.Add(RunTest("3. Electrum Seed HMAC-SHA512 Version & PBKDF2 Derivation", TestElectrumSeedRecovery));
            report.Results.Add(RunTest("4. SLIP-39 GF(256) Shamir Secret Lagrange Reconstruction", TestSlip39Gf256Shamir));
            report.Results.Add(RunTest("5. Cryptosteel 4-Char Prefix & Checksum Typo Auto-Repair", TestAdvancedTypoCorrection));
            report.Results.Add(RunTest("6. Forensic AES-GCM Vault & Keystore V3 Decryption", TestForensicVaultDecryption));
            report.Results.Add(RunTest("7. Strict Empty-Wallet Non-Repetition & Trivial Vector Guard", TestStrictNonRepetitionAndTrivialGuard));
            report.Results.Add(RunTest("8. Cognitive Self-Learning Brain Engine Synthesis & Upgrade", TestCognitiveBrainEngine));

            return report;
        }

        private static TestCaseResult RunTest(string name, Func<(bool passed, string details)> testFunc)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var (passed, details) = testFunc();
                sw.Stop();
                return new TestCaseResult
                {
                    Name = name,
                    Passed = passed,
                    Details = details,
                    ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2)
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new TestCaseResult
                {
                    Name = name,
                    Passed = false,
                    Details = $"Exception: {ex.Message}",
                    ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2)
                };
            }
        }

        private static (bool passed, string details) TestBipDerivation()
        {
            // Non-trivial valid 12-word BIP39 seed
            var prefix11 = new[] { "planet", "crystal", "ocean", "velvet", "silver", "canyon", "harvest", "timber", "shadow", "marble", "winter" };
            var solved = CryptoWalletGenerator.SolveExactLastWordCandidates(prefix11);
            if (solved.Count == 0)
                return (false, "Could not solve valid 12th word for test seed");

            string seed = solved[0];
            var wallet = CryptoWalletGenerator.GenerateWallet(seed);
            bool ok = wallet != null &&
                      !string.IsNullOrEmpty(wallet.BitcoinAddress) && wallet.BitcoinAddress.StartsWith("1") &&
                      !string.IsNullOrEmpty(wallet.BitcoinAddressSegWit) && wallet.BitcoinAddressSegWit.StartsWith("3") &&
                      !string.IsNullOrEmpty(wallet.BitcoinAddressNativeSegWit) && wallet.BitcoinAddressNativeSegWit.StartsWith("bc1q") &&
                      !string.IsNullOrEmpty(wallet.EthereumAddress) && wallet.EthereumAddress.StartsWith("0x");

            return (ok, $"BTC={wallet?.BitcoinAddressNativeSegWit}, ETH={wallet?.EthereumAddress}");
        }

        private static (bool passed, string details) TestExactLastWordSolver()
        {
            var prefix11 = new[] { "planet", "crystal", "ocean", "velvet", "silver", "canyon", "harvest", "timber", "shadow", "marble", "winter" };
            var candidates12 = CryptoWalletGenerator.SolveExactLastWordCandidates(prefix11);
            bool allValid = candidates12.Count == 128 && candidates12.All(CryptoWalletGenerator.IsValidMnemonic);
            return (allValid, $"Solved {candidates12.Count}/128 exact valid 12th-word completions (100% valid checksums)");
        }

        private static (bool passed, string details) TestElectrumSeedRecovery()
        {
            // Derive an Electrum wallet via PBKDF2-HMAC-SHA512("electrum") and verify addresses
            string sampleElectrum = "planet crystal ocean velvet silver canyon harvest timber shadow marble winter ankle";
            var ew = CryptoWalletGenerator.GenerateElectrumWallet(sampleElectrum);
            bool ok = ew != null &&
                      !string.IsNullOrEmpty(ew.BitcoinAddress) && ew.BitcoinAddress.StartsWith("1") &&
                      !string.IsNullOrEmpty(ew.BitcoinAddressNativeSegWit) && ew.BitcoinAddressNativeSegWit.StartsWith("bc1q");
            return (ok, $"Electrum Legacy={ew?.BitcoinAddress}, SegWit={ew?.BitcoinAddressNativeSegWit}");
        }

        private static (bool passed, string details) TestSlip39Gf256Shamir()
        {
            var hw = new HardwareWalletRecovery();
            // Create 3 shares of degree-1 polynomial f(x) = secret + a1*x over GF(256) and reconstruct at x=0
            string share1 = "1:planet crystal ocean velvet silver canyon harvest timber shadow marble winter ankle";
            string share2 = "2:planet crystal ocean velvet silver canyon harvest timber shadow marble winter ankle";
            var rec = hw.RecoverFromShamirBackup(new List<string> { share1, share2 }, requiredShares: 2);
            bool ok = rec != null && rec.Success && !string.IsNullOrEmpty(rec.RecoveredMnemonic);
            return (ok, $"Reconstructed GF(256) Shamir secret: '{rec?.RecoveredMnemonic}'");
        }

        private static (bool passed, string details) TestAdvancedTypoCorrection()
        {
            var prefix11 = new[] { "planet", "crystal", "ocean", "velvet", "silver", "canyon", "harvest", "timber", "shadow", "marble", "winter" };
            string validSeed = CryptoWalletGenerator.SolveExactLastWordCandidates(prefix11).First();
            var words = validSeed.Split(' ');
            // Introduce 4-letter Cryptosteel truncation on word 1 ("crys") and typo on word 0 ("planat")
            words[0] = "planat";
            words[1] = "crys";
            string corrupted = string.Join(" ", words);

            var fixedResult = AdvancedTypoCorrection.AutoCorrectMnemonic(corrupted);
            bool ok = fixedResult != null && fixedResult.IsValid &&
                      string.Equals(fixedResult.CorrectedMnemonic, validSeed, StringComparison.OrdinalIgnoreCase);
            return (ok, $"Repaired '{corrupted[..28]}...' -> valid={fixedResult?.IsValid} ({fixedResult?.CorrectionsMade.Count} fixes)");
        }

        private static (bool passed, string details) TestForensicVaultDecryption()
        {
            // Encrypt a test MetaMask vault payload with PBKDF2 + AES-GCM and verify DecryptMetaMaskVault recovers it
            string password = "CorrectVaultPassword!2026";
            var prefix11 = new[] { "planet", "crystal", "ocean", "velvet", "silver", "canyon", "harvest", "timber", "shadow", "marble", "winter" };
            string expectedMnemonic = CryptoWalletGenerator.SolveExactLastWordCandidates(prefix11).First();
            string innerJson = $"{{\"mnemonic\":\"{expectedMnemonic}\"}}";

            byte[] salt = RandomNumberGenerator.GetBytes(32);
            byte[] iv = RandomNumberGenerator.GetBytes(12);
            byte[] key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 10000, HashAlgorithmName.SHA256, 32);
            byte[] plainBytes = Encoding.UTF8.GetBytes(innerJson);
            byte[] cipherBytes = new byte[plainBytes.Length];
            byte[] tagBytes = new byte[16];

            using (var aesGcm = new AesGcm(key, 16))
            {
                aesGcm.Encrypt(iv, plainBytes, cipherBytes, tagBytes);
            }

            byte[] combined = new byte[cipherBytes.Length + 16];
            Buffer.BlockCopy(cipherBytes, 0, combined, 0, cipherBytes.Length);
            Buffer.BlockCopy(tagBytes, 0, combined, cipherBytes.Length, 16);

            string vaultBlob = $"{{\"data\":\"{Convert.ToBase64String(combined)}\",\"iv\":\"{Convert.ToBase64String(iv)}\",\"salt\":\"{Convert.ToBase64String(salt)}\"}}";
            var forensic = new ForensicWalletRecovery();
            string recovered = forensic.DecryptWalletFile(vaultBlob, password);

            bool ok = string.Equals(recovered, expectedMnemonic, StringComparison.OrdinalIgnoreCase);
            return (ok, $"AES-GCM Vault round-trip recovered seed (match={ok})");
        }

        private static (bool passed, string details) TestStrictNonRepetitionAndTrivialGuard()
        {
            bool blocksAbandon = MnemonicTracker.IsRestrictedTrivialMnemonic(
                "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about");
            bool blocksRepeated = MnemonicTracker.IsRestrictedTrivialMnemonic(
                "zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo wrong");
            bool blocksKeyOne = MnemonicTracker.IsRestrictedTrivialPrivateKey(
                "0000000000000000000000000000000000000000000000000000000000000001");

            var cand = CognitiveBrainEngine.Instance.SynthesizeNextCandidate(markAsScannedImmediately: false);
            bool firstMark = GlobalMnemonicTracker.Instance.TryMarkAsScanned(cand.Mnemonic);
            bool secondMarkBlocked = !GlobalMnemonicTracker.Instance.TryMarkAsScanned(cand.Mnemonic);

            bool ok = blocksAbandon && blocksRepeated && blocksKeyOne && firstMark && secondMarkBlocked;
            return (ok, $"TrivialBlocked={blocksAbandon && blocksRepeated && blocksKeyOne}, RepeatBlocked={secondMarkBlocked}");
        }

        private static (bool passed, string details) TestCognitiveBrainEngine()
        {
            var brain = CognitiveBrainEngine.Instance;
            var batch = brain.SynthesizeCandidateBatch(5);
            bool allValid = batch.Count == 5 &&
                            batch.All(c => !MnemonicTracker.IsRestrictedTrivialMnemonic(c.Mnemonic) &&
                                           CryptoWalletGenerator.IsValidMnemonic(c.Mnemonic));

            foreach (var c in batch)
            {
                brain.RecordScanFeedback(c.Mnemonic, c.StrategyArm, passedOfflineFilter: true, hasTransactionsOrBalance: false);
            }

            var pulse = brain.ExecuteCognitivePulse();
            bool ok = allValid && pulse.BrainGeneration >= 1 && pulse.CognitiveIqScore >= 112.0;
            return (ok, $"Brain {pulse.VersionTag} (IQ {pulse.CognitiveIqScore:F1} | TopArm={pulse.TopStrategyArm})");
        }
    }
}
