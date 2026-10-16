using System;
using System.Collections.Generic;
using System.Linq;
using NBitcoin;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Enhanced typo correction with comprehensive BIP39 error patterns
    /// Helps users fix common mistakes in their mnemonic phrases
    /// </summary>
    public class AdvancedTypoCorrection
    {
        private static readonly List<string> Bip39Words = Wordlist.English.GetWords().ToList();
        private static readonly Dictionary<string, List<string>> CommonTypos = BuildCommonTyposDatabase();

        /// <summary>
        /// Finds all possible corrections for a word
        /// </summary>
        public static List<string> FindAllCorrections(string word, int maxDistance = 2)
        {
            var corrections = new HashSet<string>();
            word = word.ToLower().Trim();

            // 1. Exact match (no typo)
            if (Bip39Words.Contains(word))
            {
                corrections.Add(word);
                return corrections.ToList();
            }

            // 1b. Official BIP39 4-character prefix expansion (Cryptosteel / Billfodl 4-letter tile backups)
            if (word.Length == 4)
            {
                var prefixMatches = Bip39Words.Where(w => w.StartsWith(word, StringComparison.OrdinalIgnoreCase)).ToList();
                if (prefixMatches.Count == 1)
                {
                    corrections.Add(prefixMatches[0]);
                    return corrections.ToList();
                }
                corrections.UnionWith(prefixMatches);
            }

            // 1c. Adjacent character transposition (e.g. "plnaet" -> "planet")
            if (word.Length >= 3)
            {
                var chars = word.ToCharArray();
                for (int i = 0; i < chars.Length - 1; i++)
                {
                    (chars[i], chars[i + 1]) = (chars[i + 1], chars[i]);
                    var swapped = new string(chars);
                    if (Bip39Words.Contains(swapped))
                        corrections.Add(swapped);
                    (chars[i], chars[i + 1]) = (chars[i + 1], chars[i]);
                }
            }

            // 2. Check common typos database
            if (CommonTypos.TryGetValue(word, out var commonMatches))
            {
                corrections.UnionWith(commonMatches);
            }

            // 3. Levenshtein distance matching
            var levenshteinMatches = Bip39Words
                .Where(w => LevenshteinDistance(word, w) <= maxDistance)
                .ToList();
            corrections.UnionWith(levenshteinMatches);

            // 4. Phonetic matching (sounds like)
            var phoneticMatches = FindPhoneticMatches(word);
            corrections.UnionWith(phoneticMatches);

            // 5. Keyboard distance (adjacent keys)
            var keyboardMatches = FindKeyboardTypos(word);
            corrections.UnionWith(keyboardMatches);

            // 6. Missing/extra letters
            var letterMatches = FindLetterErrors(word);
            corrections.UnionWith(letterMatches);

            return corrections
                .OrderBy(w => LevenshteinDistance(word, w))
                .Take(10) // Return top 10 matches
                .ToList();
        }

        /// <summary>
        /// Finds phonetic matches (words that sound similar)
        /// </summary>
        public static List<string> FindPhoneticMatches(string word)
        {
            var matches = new List<string>();
            var soundex = GetSoundex(word);

            foreach (var bip39Word in Bip39Words)
            {
                if (GetSoundex(bip39Word) == soundex)
                {
                    matches.Add(bip39Word);
                }
            }

            return matches;
        }

        /// <summary>
        /// Finds keyboard typos (adjacent key mistakes)
        /// </summary>
        public static List<string> FindKeyboardTypos(string word)
        {
            var matches = new List<string>();
            var keyboard = BuildKeyboardMap();

            foreach (var bip39Word in Bip39Words)
            {
                if (word.Length != bip39Word.Length)
                    continue;

                var differences = 0;
                for (int i = 0; i < word.Length; i++)
                {
                    if (word[i] != bip39Word[i])
                    {
                        // Check if keys are adjacent
                        if (!AreKeysAdjacent(word[i], bip39Word[i], keyboard))
                        {
                            differences++;
                            if (differences > 2) break;
                        }
                    }
                }

                if (differences <= 2)
                {
                    matches.Add(bip39Word);
                }
            }

            return matches;
        }

        /// <summary>
        /// Finds words with missing or extra letters
        /// </summary>
        private static List<string> FindLetterErrors(string word)
        {
            var matches = new List<string>();

            foreach (var bip39Word in Bip39Words)
            {
                // Missing letter (word is shorter)
                if (bip39Word.Length == word.Length + 1)
                {
                    for (int i = 0; i < bip39Word.Length; i++)
                    {
                        var test = bip39Word.Remove(i, 1);
                        if (test == word)
                        {
                            matches.Add(bip39Word);
                            break;
                        }
                    }
                }
                // Extra letter (word is longer)
                else if (bip39Word.Length == word.Length - 1)
                {
                    for (int i = 0; i < word.Length; i++)
                    {
                        var test = word.Remove(i, 1);
                        if (test == bip39Word)
                        {
                            matches.Add(bip39Word);
                            break;
                        }
                    }
                }
            }

            return matches;
        }

        /// <summary>
        /// Calculates Levenshtein distance between two strings
        /// </summary>
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

        /// <summary>
        /// Simplified Soundex algorithm for phonetic matching
        /// </summary>
        private static string GetSoundex(string word)
        {
            if (string.IsNullOrEmpty(word)) return "";

            word = word.ToUpper();
            var result = word[0].ToString();

            var soundexMap = new Dictionary<char, char>
            {
                {'B', '1'}, {'F', '1'}, {'P', '1'}, {'V', '1'},
                {'C', '2'}, {'G', '2'}, {'J', '2'}, {'K', '2'}, {'Q', '2'}, {'S', '2'}, {'X', '2'}, {'Z', '2'},
                {'D', '3'}, {'T', '3'},
                {'L', '4'},
                {'M', '5'}, {'N', '5'},
                {'R', '6'}
            };

            for (int i = 1; i < word.Length && result.Length < 4; i++)
            {
                if (soundexMap.TryGetValue(word[i], out var code))
                {
                    if (result[result.Length - 1] != code)
                    {
                        result += code;
                    }
                }
            }

            return result.PadRight(4, '0');
        }

        /// <summary>
        /// Builds QWERTY keyboard adjacency map
        /// </summary>
        private static Dictionary<char, List<char>> BuildKeyboardMap()
        {
            return new Dictionary<char, List<char>>
            {
                {'q', new List<char> {'w', 'a', 's'}},
                {'w', new List<char> {'q', 'e', 'a', 's', 'd'}},
                {'e', new List<char> {'w', 'r', 's', 'd', 'f'}},
                {'r', new List<char> {'e', 't', 'd', 'f', 'g'}},
                {'t', new List<char> {'r', 'y', 'f', 'g', 'h'}},
                {'y', new List<char> {'t', 'u', 'g', 'h', 'j'}},
                {'u', new List<char> {'y', 'i', 'h', 'j', 'k'}},
                {'i', new List<char> {'u', 'o', 'j', 'k', 'l'}},
                {'o', new List<char> {'i', 'p', 'k', 'l'}},
                {'p', new List<char> {'o', 'l'}},
                {'a', new List<char> {'q', 'w', 's', 'z'}},
                {'s', new List<char> {'a', 'w', 'e', 'd', 'z', 'x'}},
                {'d', new List<char> {'s', 'e', 'r', 'f', 'x', 'c'}},
                {'f', new List<char> {'d', 'r', 't', 'g', 'c', 'v'}},
                {'g', new List<char> {'f', 't', 'y', 'h', 'v', 'b'}},
                {'h', new List<char> {'g', 'y', 'u', 'j', 'b', 'n'}},
                {'j', new List<char> {'h', 'u', 'i', 'k', 'n', 'm'}},
                {'k', new List<char> {'j', 'i', 'o', 'l', 'm'}},
                {'l', new List<char> {'k', 'o', 'p'}},
                {'z', new List<char> {'a', 's', 'x'}},
                {'x', new List<char> {'z', 's', 'd', 'c'}},
                {'c', new List<char> {'x', 'd', 'f', 'v'}},
                {'v', new List<char> {'c', 'f', 'g', 'b'}},
                {'b', new List<char> {'v', 'g', 'h', 'n'}},
                {'n', new List<char> {'b', 'h', 'j', 'm'}},
                {'m', new List<char> {'n', 'j', 'k'}}
            };
        }

        private static bool AreKeysAdjacent(char key1, char key2, Dictionary<char, List<char>> keyboard)
        {
            key1 = char.ToLower(key1);
            key2 = char.ToLower(key2);

            if (keyboard.TryGetValue(key1, out var adjacent))
            {
                return adjacent.Contains(key2);
            }

            return false;
        }

        /// <summary>
        /// Builds database of common typos for BIP39 words
        /// </summary>
        private static Dictionary<string, List<string>> BuildCommonTyposDatabase()
        {
            var database = new Dictionary<string, List<string>>();

            // Common double letter typos
            var commonDoubles = new Dictionary<string, string>
            {
                {"recieve", "receive"}, {"occured", "occurred"},
                {"untill", "until"}, {"successfull", "successful"}
            };

            foreach (var typo in commonDoubles)
            {
                if (Bip39Words.Contains(typo.Value))
                {
                    if (!database.ContainsKey(typo.Key))
                        database[typo.Key] = new List<string>();
                    database[typo.Key].Add(typo.Value);
                }
            }

            // ie/ei confusion
            foreach (var word in Bip39Words)
            {
                if (word.Contains("ie"))
                {
                    var typo = word.Replace("ie", "ei");
                    if (!database.ContainsKey(typo))
                        database[typo] = new List<string>();
                    database[typo].Add(word);
                }
                if (word.Contains("ei"))
                {
                    var typo = word.Replace("ei", "ie");
                    if (!database.ContainsKey(typo))
                        database[typo] = new List<string>();
                    database[typo].Add(word);
                }
            }

            return database;
        }

        /// <summary>
        /// Auto-corrects an entire mnemonic phrase
        /// </summary>
        public static MnemonicCorrectionResult AutoCorrectMnemonic(string mnemonic)
        {
            var result = new MnemonicCorrectionResult
            {
                OriginalMnemonic = mnemonic
            };

            var words = mnemonic.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var correctedWords = new List<string>();
            var corrections = new Dictionary<string, string>();
            var positionCandidates = new Dictionary<int, List<string>>();

            for (int idx = 0; idx < words.Length; idx++)
            {
                var cleanWord = words[idx].ToLower().Trim();
                
                if (Bip39Words.Contains(cleanWord))
                {
                    correctedWords.Add(cleanWord);
                }
                else
                {
                    var suggestions = FindAllCorrections(cleanWord, maxDistance: 2);
                    if (suggestions.Any())
                    {
                        positionCandidates[idx] = suggestions;
                        var bestMatch = suggestions.First();
                        correctedWords.Add(bestMatch);
                        corrections[cleanWord] = bestMatch;
                        result.CorrectionsMade++;
                    }
                    else
                    {
                        correctedWords.Add(cleanWord); // Keep original if no match
                        result.UnfixableWords.Add(cleanWord);
                    }
                }
            }

            result.CorrectedMnemonic = string.Join(" ", correctedWords);
            result.Corrections = corrections;
            result.IsValid = CryptoWalletGenerator.IsValidMnemonic(result.CorrectedMnemonic) &&
                             !MnemonicTracker.IsRestrictedTrivialMnemonic(result.CorrectedMnemonic);

            // Checksum-guided solver: if greedy top-1 pick didn't pass BIP39 checksum, try alternative candidates
            if (!result.IsValid && result.UnfixableWords.Count == 0 && correctedWords.Count >= 12)
            {
                if (positionCandidates.Count > 0)
                {
                    foreach (var kvp in positionCandidates)
                    {
                        int pos = kvp.Key;
                        string orig = correctedWords[pos];
                        foreach (var alt in kvp.Value)
                        {
                            correctedWords[pos] = alt;
                            var candidate = string.Join(" ", correctedWords);
                            if (CryptoWalletGenerator.IsValidMnemonic(candidate) &&
                                !MnemonicTracker.IsRestrictedTrivialMnemonic(candidate) &&
                                !GlobalMnemonicTracker.Instance.WasScanned(candidate))
                            {
                                result.CorrectedMnemonic = candidate;
                                result.Corrections[words[pos].ToLower().Trim()] = alt;
                                result.IsValid = true;
                                return result;
                            }
                        }
                        correctedWords[pos] = orig;
                    }
                }
                else
                {
                    // All words were in BIP39 list, but one word was mistyped into another valid BIP39 word
                    for (int pos = 0; pos < correctedWords.Count; pos++)
                    {
                        string orig = correctedWords[pos];
                        var nearWords = Bip39Words.Where(w => w != orig && LevenshteinDistance(orig, w) == 1);
                        foreach (var alt in nearWords)
                        {
                            correctedWords[pos] = alt;
                            var candidate = string.Join(" ", correctedWords);
                            if (CryptoWalletGenerator.IsValidMnemonic(candidate) &&
                                !MnemonicTracker.IsRestrictedTrivialMnemonic(candidate) &&
                                !GlobalMnemonicTracker.Instance.WasScanned(candidate))
                            {
                                result.CorrectedMnemonic = candidate;
                                result.Corrections[orig] = alt;
                                result.CorrectionsMade++;
                                result.IsValid = true;
                                return result;
                            }
                        }
                        correctedWords[pos] = orig;
                    }
                }
            }

            return result;
        }

        public class MnemonicCorrectionResult
        {
            public string OriginalMnemonic { get; set; }
            public string CorrectedMnemonic { get; set; }
            public Dictionary<string, string> Corrections { get; set; } = new Dictionary<string, string>();
            public List<string> UnfixableWords { get; set; } = new List<string>();
            public int CorrectionsMade { get; set; }
            public bool IsValid { get; set; }
        }
    }
}
