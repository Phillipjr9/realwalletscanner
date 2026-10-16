using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using Nethereum.HdWallet;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Advanced Web3 features for wallet recovery and analysis
    /// </summary>
    public class Web3AdvancedFeatures
    {
        /// <summary>
        /// Generates multiple wallet addresses using HD derivation paths
        /// Useful for finding funds in non-standard derivation paths
        /// </summary>
        public static Dictionary<string, string> GenerateHDPathVariations(string mnemonic, int accountCount = 5)
        {
            var addresses = new Dictionary<string, string>();
            
            try
            {
                var mnemonicObj = new Mnemonic(mnemonic);
                var masterKey = mnemonicObj.DeriveExtKey();

                // Bitcoin standard paths
                for (int i = 0; i < accountCount; i++)
                {
                    // BIP44 Legacy (m/44'/0'/0'/0/i)
                    var legacyPath = $"m/44'/0'/0'/0/{i}";
                    var legacyKey = masterKey.Derive(new KeyPath(legacyPath));
                    addresses[$"BTC Legacy [{legacyPath}]"] = 
                        legacyKey.PrivateKey.PubKey.GetAddress(ScriptPubKeyType.Legacy, Network.Main).ToString();

                    // BIP49 SegWit (m/49'/0'/0'/0/i)
                    var segwitPath = $"m/49'/0'/0'/0/{i}";
                    var segwitKey = masterKey.Derive(new KeyPath(segwitPath));
                    addresses[$"BTC SegWit [{segwitPath}]"] = 
                        segwitKey.PrivateKey.PubKey.GetAddress(ScriptPubKeyType.SegwitP2SH, Network.Main).ToString();

                    // BIP84 Native SegWit (m/84'/0'/0'/0/i)
                    var nativePath = $"m/84'/0'/0'/0/{i}";
                    var nativeKey = masterKey.Derive(new KeyPath(nativePath));
                    addresses[$"BTC Native SegWit [{nativePath}]"] = 
                        nativeKey.PrivateKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.Main).ToString();
                }

                // Ethereum paths
                var ethWallet = new Wallet(mnemonic, "");
                for (int i = 0; i < accountCount; i++)
                {
                    var ethAccount = ethWallet.GetAccount(i);
                    addresses[$"ETH/MATIC/BNB [m/44'/60'/0'/0/{i}]"] = ethAccount.Address;
                }

                // Alternative Bitcoin paths (some wallets use non-standard paths)
                for (int i = 0; i < accountCount; i++)
                {
                    // Electrum-style path (m/0/i)
                    var electrumPath = $"m/0/{i}";
                    var electrumKey = masterKey.Derive(new KeyPath(electrumPath));
                    addresses[$"BTC Electrum [{electrumPath}]"] = 
                        electrumKey.PrivateKey.PubKey.GetAddress(ScriptPubKeyType.Legacy, Network.Main).ToString();
                }

                return addresses;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"HD path generation failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Batch balance check with progress reporting
        /// More efficient for checking multiple addresses
        /// </summary>
        public static async Task<Dictionary<string, decimal>> CheckMultipleAddressesAsync(
            Dictionary<string, string> addresses, 
            BalanceChecker checker,
            IProgress<int> progress = null)
        {
            var results = new Dictionary<string, decimal>();
            var totalCount = addresses.Count;
            var checkedCount = 0;

            // Group by cryptocurrency type for batch processing
            var ethAddresses = addresses
                .Where(x => x.Key.Contains("ETH") || x.Key.Contains("MATIC") || x.Key.Contains("BNB"))
                .Select(x => x.Value)
                .Distinct()
                .ToList();

            // Batch check Ethereum addresses (single API call)
            if (ethAddresses.Any())
            {
                var ethBalances = await checker.GetEthereumBalancesBatch(ethAddresses);
                foreach (var kvp in addresses.Where(x => ethAddresses.Contains(x.Value)))
                {
                    results[kvp.Key] = ethBalances.ContainsKey(kvp.Value) ? ethBalances[kvp.Value] : 0;
                    checkedCount++;
                    progress?.Report((checkedCount * 100) / totalCount);
                }
            }

            // Check Bitcoin addresses individually (no batch API available)
            var btcAddresses = addresses.Where(x => x.Key.Contains("BTC")).ToList();
            foreach (var btcAddr in btcAddresses)
            {
                try
                {
                    results[btcAddr.Key] = await checker.GetBitcoinBalance(btcAddr.Value);
                }
                catch
                {
                    results[btcAddr.Key] = 0;
                }
                
                checkedCount++;
                progress?.Report((checkedCount * 100) / totalCount);
            }

            return results;
        }

        /// <summary>
        /// Validates mnemonic and provides detailed diagnostics
        /// </summary>
        public static MnemonicValidationResult ValidateMnemonic(string mnemonic)
        {
            var result = new MnemonicValidationResult { OriginalInput = mnemonic };

            if (string.IsNullOrWhiteSpace(mnemonic))
            {
                result.IsValid = false;
                result.ErrorMessage = "Mnemonic is empty";
                return result;
            }

            var words = mnemonic.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            result.WordCount = words.Length;

            // Check word count
            if (words.Length != 12 && words.Length != 15 && words.Length != 18 && words.Length != 21 && words.Length != 24)
            {
                result.IsValid = false;
                result.ErrorMessage = $"Invalid word count: {words.Length}. Must be 12, 15, 18, 21, or 24 words.";
                return result;
            }

            // Check against BIP39 wordlist
            var wordlist = Wordlist.English.GetWords().ToHashSet();
            var invalidWords = words.Where(w => !wordlist.Contains(w.ToLower())).ToList();
            
            if (invalidWords.Any())
            {
                result.IsValid = false;
                result.InvalidWords = invalidWords;
                result.ErrorMessage = $"Invalid words found: {string.Join(", ", invalidWords)}";
                
                // Suggest similar words
                result.Suggestions = new Dictionary<string, List<string>>();
                foreach (var invalidWord in invalidWords)
                {
                    var similar = FindSimilarWords(invalidWord, wordlist);
                    result.Suggestions[invalidWord] = similar;
                }
                
                return result;
            }

            // Validate checksum
            try
            {
                var mnemonicObj = new Mnemonic(mnemonic, Wordlist.English);
                if (!mnemonicObj.IsValidChecksum)
                {
                    result.IsValid = false;
                    result.ErrorMessage = "Checksum validation failed: invalid BIP39 checksum";
                    return result;
                }
                result.IsValid = true;
                result.ErrorMessage = "Mnemonic is valid";
                return result;
            }
            catch (Exception ex)
            {
                result.IsValid = false;
                result.ErrorMessage = $"Checksum validation failed: {ex.Message}";
                return result;
            }
        }

        private static List<string> FindSimilarWords(string word, HashSet<string> wordlist)
        {
            // Find words with similar spelling (Levenshtein distance <= 2)
            return wordlist
                .Where(w => LevenshteinDistance(word.ToLower(), w) <= 2)
                .Take(5)
                .ToList();
        }

        private static int LevenshteinDistance(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1)) return s2?.Length ?? 0;
            if (string.IsNullOrEmpty(s2)) return s1.Length;

            var d = new int[s1.Length + 1, s2.Length + 1];

            for (int i = 0; i <= s1.Length; i++)
                d[i, 0] = i;
            for (int j = 0; j <= s2.Length; j++)
                d[0, j] = j;

            for (int i = 1; i <= s1.Length; i++)
            {
                for (int j = 1; j <= s2.Length; j++)
                {
                    int cost = (s2[j - 1] == s1[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }

            return d[s1.Length, s2.Length];
        }

        public class MnemonicValidationResult
        {
            public bool IsValid { get; set; }
            public string OriginalInput { get; set; }
            public int WordCount { get; set; }
            public string ErrorMessage { get; set; }
            public List<string> InvalidWords { get; set; }
            public Dictionary<string, List<string>> Suggestions { get; set; }
        }
    }
}
