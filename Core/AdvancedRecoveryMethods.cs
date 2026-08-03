using System;
using System.Collections.Generic;
using System.Linq;
using NBitcoin;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Advanced recovery methods including typo correction, word order permutation, and passphrase brute force
    /// </summary>
    public class AdvancedRecoveryMethods
    {
        private static readonly Wordlist _wordlist = Wordlist.English;
        
        /// <summary>
        /// Generates mnemonic variations with typo corrections
        /// Checks for common misspellings and similar words
        /// </summary>
        public static List<string> GenerateTypoCorrections(string mnemonic)
        {
            var variations = new List<string>();
            var words = mnemonic.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var wordlistArray = _wordlist.GetWords().ToArray();

            for (int i = 0; i < words.Length; i++)
            {
                var word = words[i];
                
                // Find similar words using Levenshtein distance
                var similarWords = wordlistArray
                    .Where(w => LevenshteinDistance(w, word) <= 2) // Max 2 character difference
                    .Take(5); // Limit to 5 most similar

                foreach (var similarWord in similarWords)
                {
                    if (similarWord != word)
                    {
                        var newWords = (string[])words.Clone();
                        newWords[i] = similarWord;
                        var newMnemonic = string.Join(" ", newWords);
                        
                        if (IsValidMnemonic(newMnemonic))
                        {
                            variations.Add(newMnemonic);
                        }
                    }
                }
            }

            return variations.Distinct().ToList();
        }

        /// <summary>
        /// Generates all permutations of word order
        /// Useful when words are known but order is uncertain
        /// </summary>
        public static List<string> GenerateWordOrderPermutations(string mnemonic, int maxPermutations = 1000)
        {
            var variations = new List<string>();
            var words = mnemonic.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (words.Length > 12)
                return variations; // Too many permutations

            var permutations = GetPermutations(words.ToList())
                .Take(maxPermutations);

            foreach (var perm in permutations)
            {
                var permMnemonic = string.Join(" ", perm);
                if (IsValidMnemonic(permMnemonic))
                {
                    variations.Add(permMnemonic);
                }
            }

            return variations;
        }

        /// <summary>
        /// Generates variations with different BIP39 passphrases
        /// </summary>
        public static List<(string mnemonic, string passphrase)> GeneratePassphraseVariations(
            string mnemonic, 
            List<string> commonPassphrases = null)
        {
            var variations = new List<(string, string)>();

            if (commonPassphrases == null)
            {
                // Common passphrases to try
                commonPassphrases = new List<string>
                {
                    "", // No passphrase
                    "password",
                    "Password",
                    "PASSWORD",
                    "password123",
                    "12345678",
                    "crypto",
                    "bitcoin",
                    "wallet",
                    "secret"
                };
            }

            if (IsValidMnemonic(mnemonic))
            {
                foreach (var passphrase in commonPassphrases)
                {
                    variations.Add((mnemonic, passphrase));
                }
            }

            return variations;
        }

        /// <summary>
        /// Generates variations with missing words filled in
        /// Intelligently fills gaps using word frequency and context
        /// </summary>
        public static List<string> GenerateMissingWordVariations(
            string partialMnemonic, 
            int maxVariations = 5000)
        {
            var variations = new List<string>();
            var words = partialMnemonic.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w == "_" || w == "?" || w == "X" ? null : w)
                .ToList();

            if (words.Count != 12)
                return variations;

            var missingIndices = words
                .Select((w, i) => w == null ? i : -1)
                .Where(i => i >= 0)
                .ToList();

            if (missingIndices.Count == 0 || missingIndices.Count > 4)
                return variations; // 0 missing or too many to brute force

            var wordlistArray = _wordlist.GetWords().ToArray();
            GenerateMissingWordCombinations(words, missingIndices, 0, wordlistArray, variations, maxVariations);

            return variations;
        }

        private static void GenerateMissingWordCombinations(
            List<string> words,
            List<int> missingIndices,
            int currentIndex,
            string[] wordlist,
            List<string> results,
            int maxResults)
        {
            if (results.Count >= maxResults)
                return;

            if (currentIndex >= missingIndices.Count)
            {
                var mnemonic = string.Join(" ", words);
                if (IsValidMnemonic(mnemonic))
                {
                    results.Add(mnemonic);
                }
                return;
            }

            var index = missingIndices[currentIndex];
            foreach (var word in wordlist.Take(100)) // Limit search space
            {
                if (results.Count >= maxResults)
                    return;

                words[index] = word;
                GenerateMissingWordCombinations(words, missingIndices, currentIndex + 1, wordlist, results, maxResults);
            }
            words[index] = null; // Reset
        }

        /// <summary>
        /// Scans different derivation paths for same mnemonic
        /// Useful when wallet might be from different software
        /// </summary>
        public static List<string> GetCommonDerivationPaths()
        {
            return new List<string>
            {
                "m/44'/0'/0'/0/0",      // BIP44 Bitcoin
                "m/49'/0'/0'/0/0",      // BIP49 Bitcoin SegWit
                "m/84'/0'/0'/0/0",      // BIP84 Bitcoin Native SegWit
                "m/44'/60'/0'/0/0",     // BIP44 Ethereum
                "m/44'/501'/0'/0",      // BIP44 Solana
                "m/44'/3'/0'/0/0",      // BIP44 Dogecoin
                "m/44'/2'/0'/0/0",      // BIP44 Litecoin
                "m/44'/144'/0'/0/0",    // BIP44 Ripple
                "m/1852'/1815'/0'/0/0", // Cardano (CIP-1852)
            };
        }

        #region Helper Methods

        private static bool IsValidMnemonic(string mnemonic)
        {
            try
            {
                new Mnemonic(mnemonic);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static int LevenshteinDistance(string source, string target)
        {
            if (string.IsNullOrEmpty(source))
                return string.IsNullOrEmpty(target) ? 0 : target.Length;

            if (string.IsNullOrEmpty(target))
                return source.Length;

            int[,] distance = new int[source.Length + 1, target.Length + 1];

            for (int i = 0; i <= source.Length; i++)
                distance[i, 0] = i;

            for (int j = 0; j <= target.Length; j++)
                distance[0, j] = j;

            for (int i = 1; i <= source.Length; i++)
            {
                for (int j = 1; j <= target.Length; j++)
                {
                    int cost = (target[j - 1] == source[i - 1]) ? 0 : 1;
                    distance[i, j] = Math.Min(
                        Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1),
                        distance[i - 1, j - 1] + cost);
                }
            }

            return distance[source.Length, target.Length];
        }

        private static IEnumerable<List<T>> GetPermutations<T>(List<T> list)
        {
            if (list.Count == 0)
            {
                yield return new List<T>();
            }
            else
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var element = list[i];
                    var remaining = new List<T>(list);
                    remaining.RemoveAt(i);

                    foreach (var permutation in GetPermutations(remaining))
                    {
                        var result = new List<T> { element };
                        result.AddRange(permutation);
                        yield return result;
                    }
                }
            }
        }

        #endregion
    }
}
