using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Recovers BIP39 passphrases using dictionary attacks and pattern matching
    /// Many users add passphrases to mnemonics - this is MUCH easier to crack than full mnemonic
    /// </summary>
    public class PassphraseRecovery
    {
        private readonly BalanceChecker _balanceChecker;

        public event EventHandler<PassphraseProgressEventArgs> ProgressUpdated;
        public event EventHandler<PassphraseFoundEventArgs> PassphraseFound;

        public PassphraseRecovery()
        {
            _balanceChecker = new BalanceChecker();
        }

        /// <summary>
        /// Brute force BIP39 passphrase using dictionary and patterns
        /// </summary>
        public async Task<PassphraseResult> BruteForcePassphrase(
            string mnemonic,
            string knownAddress,
            UserProfile userProfile,
            CancellationToken cancellationToken)
        {
            var candidates = GeneratePassphraseCandidates(userProfile);
            int checkedCount = 0;
            var totalCount = candidates.Count;
            PassphraseResult foundResult = null;

            DebugLogger.Log($"Generated {totalCount} passphrase candidates to test");

            // Parallel passphrase testing — TestPassphrase is pure local crypto, no API calls
            await Parallel.ForEachAsync(candidates,
                new ParallelOptions 
                { 
                    MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount), 
                    CancellationToken = cancellationToken 
                },
                async (passphrase, ct) =>
                {
                    if (ct.IsCancellationRequested || foundResult != null) return;

                    var current = Interlocked.Increment(ref checkedCount);

                    // Report progress every 100 candidates
                    if (current % 100 == 0)
                    {
                        ProgressUpdated?.Invoke(this, new PassphraseProgressEventArgs
                        {
                            CurrentPassphrase = passphrase,
                            CheckedCount = current,
                            TotalCount = totalCount,
                            ProgressPercentage = (current * 100) / totalCount
                        });
                    }

                    // Test passphrase (pure local crypto, no API calls)
                    if (await TestPassphrase(mnemonic, passphrase, knownAddress))
                    {
                        var result = new PassphraseResult
                        {
                            Success = true,
                            Passphrase = passphrase,
                            AttemptsRequired = current
                        };

                        // Thread-safe: only first match wins
                        if (Interlocked.CompareExchange(ref foundResult, result, null) == null)
                        {
                            PassphraseFound?.Invoke(this, new PassphraseFoundEventArgs { Result = result });
                        }
                    }
                });

            return foundResult ?? new PassphraseResult
            {
                Success = false,
                AttemptsRequired = checkedCount
            };
        }

        /// <summary>
        /// Tests if a passphrase is correct by checking against known address
        /// </summary>
        private Task<bool> TestPassphrase(string mnemonic, string passphrase, string knownAddress)
        {
            try
            {
                var mnemonicObj = new Mnemonic(mnemonic);
                var masterKey = mnemonicObj.DeriveExtKey(passphrase);

                // Try common derivation paths
                var paths = new[]
                {
                    "m/44'/0'/0'/0/0",   // BIP44 Legacy
                    "m/49'/0'/0'/0/0",   // BIP49 SegWit
                    "m/84'/0'/0'/0/0",   // BIP84 Native SegWit
                    "m/0/0",              // Electrum
                    "m/44'/60'/0'/0/0"   // Ethereum
                };

                foreach (var path in paths)
                {
                    try
                    {
                        var key = masterKey.Derive(new KeyPath(path));
                        
                        // Bitcoin addresses
                        if (knownAddress.StartsWith("1") || knownAddress.StartsWith("3") || knownAddress.StartsWith("bc1"))
                        {
                            var legacy = key.PrivateKey.PubKey.GetAddress(ScriptPubKeyType.Legacy, Network.Main).ToString();
                            if (legacy == knownAddress) return Task.FromResult(true);

                            var segwit = key.PrivateKey.PubKey.GetAddress(ScriptPubKeyType.SegwitP2SH, Network.Main).ToString();
                            if (segwit == knownAddress) return Task.FromResult(true);

                            var native = key.PrivateKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.Main).ToString();
                            if (native == knownAddress) return Task.FromResult(true);
                        }
                        // Ethereum addresses
                        else if (knownAddress.StartsWith("0x"))
                        {
                            var ethWallet = new Nethereum.HdWallet.Wallet(mnemonic, passphrase);
                            var ethAddress = ethWallet.GetAccount(0).Address;
                            if (ethAddress.ToLower() == knownAddress.ToLower()) return Task.FromResult(true);
                        }
                    }
                    catch (Exception ex) { DebugLogger.Log($"Passphrase check failed: {ex.Message}"); continue; }
                }
            }
            catch (Exception ex) { DebugLogger.LogError("FindRecoveredWallet", ex); }

            return Task.FromResult(false);
        }

        /// <summary>
        /// Generates passphrase candidates based on user profile
        /// </summary>
        public List<string> GeneratePassphraseCandidates(UserProfile profile)
        {
            var candidates = new HashSet<string>();

            // 1. Common passphrases (most people use simple ones)
            candidates.UnionWith(GetCommonPassphrases());

            // 2. User-specific passphrases
            if (profile != null)
            {
                // Names
                if (!string.IsNullOrEmpty(profile.FirstName))
                {
                    candidates.Add(profile.FirstName);
                    candidates.Add(profile.FirstName.ToLower());
                    candidates.Add(Capitalize(profile.FirstName));
                }

                if (!string.IsNullOrEmpty(profile.LastName))
                {
                    candidates.Add(profile.LastName);
                    candidates.Add(profile.LastName.ToLower());
                    candidates.Add(profile.FirstName + profile.LastName);
                }

                // Birthdates
                if (profile.BirthDate.HasValue)
                {
                    var date = profile.BirthDate.Value;
                    candidates.Add(date.ToString("ddMMyyyy"));
                    candidates.Add(date.ToString("MMddyyyy"));
                    candidates.Add(date.ToString("yyyyMMdd"));
                    candidates.Add(date.ToString("dd/MM/yyyy"));
                    candidates.Add(date.ToString("MM/dd/yyyy"));
                    candidates.Add(date.Year.ToString());
                }

                // Pet names
                foreach (var pet in profile.PetNames ?? new List<string>())
                {
                    candidates.Add(pet);
                    candidates.Add(pet.ToLower());
                    candidates.Add(Capitalize(pet));
                }

                // Favorite words
                foreach (var word in profile.FavoriteWords ?? new List<string>())
                {
                    candidates.Add(word);
                    candidates.Add(word.ToLower());
                }

                // Combinations
                if (!string.IsNullOrEmpty(profile.FirstName) && profile.BirthDate.HasValue)
                {
                    candidates.Add(profile.FirstName + profile.BirthDate.Value.Year);
                    candidates.Add(profile.FirstName.ToLower() + profile.BirthDate.Value.Year);
                }
            }

            // 3. Number patterns
            candidates.UnionWith(GetNumberPatterns());

            // 4. Keyboard patterns
            candidates.UnionWith(GetKeyboardPatterns());

            // 5. Crypto-related terms
            candidates.UnionWith(GetCryptoTerms());

            // 6. Empty passphrase (many users don't use one!)
            candidates.Add("");

            return candidates.ToList();
        }

        private List<string> GetCommonPassphrases()
        {
            return new List<string>
            {
                "", "password", "Password", "PASSWORD", "123456", "12345678",
                "password123", "qwerty", "abc123", "letmein", "admin",
                "welcome", "monkey", "dragon", "master", "sunshine",
                "princess", "bitcoin", "crypto", "hodl", "moon",
                "lambo", "satoshi", "wallet", "secret", "money"
            };
        }

        private List<string> GetNumberPatterns()
        {
            var patterns = new HashSet<string>();
            
            // Years (most common passphrases)
            for (int year = 1950; year <= 2025; year++)
                patterns.Add(year.ToString());

            // Common PINs (0000-9999 is 10K entries — use targeted patterns instead)
            // Repeated digits
            for (int d = 0; d <= 9; d++)
                patterns.Add(new string((char)('0' + d), 4));

            // Sequential patterns
            patterns.Add("1234"); patterns.Add("4321");
            patterns.Add("12345"); patterns.Add("54321");
            patterns.Add("123456"); patterns.Add("654321");
            patterns.Add("1234567890");

            // Common PINs
            foreach (var pin in new[] { "0000", "1111", "1234", "4321", "0001", "7777", "2580",
                "1122", "1313", "2002", "6969", "1010", "2020", "1357", "2468", "9876",
                "1212", "6666", "8888", "4444", "5555", "3333", "9999" })
                patterns.Add(pin);

            // Short numbers
            for (int i = 0; i <= 99; i++)
                patterns.Add(i.ToString());

            return patterns.ToList();
        }

        private List<string> GetKeyboardPatterns()
        {
            return new List<string>
            {
                "qwerty", "asdfgh", "zxcvbn", "qwertyuiop",
                "asdfghjkl", "zxcvbnm", "1qaz2wsx", "!QAZ@WSX"
            };
        }

        private List<string> GetCryptoTerms()
        {
            return new List<string>
            {
                "bitcoin", "Bitcoin", "BITCOIN", "btc", "BTC",
                "ethereum", "Ethereum", "ETH", "eth",
                "crypto", "Crypto", "CRYPTO",
                "hodl", "HODL", "Hodl",
                "moon", "Moon", "MOON",
                "lambo", "Lambo", "LAMBO",
                "satoshi", "Satoshi", "SATOSHI",
                "blockchain", "Blockchain",
                "defi", "DeFi", "DEFI",
                "wallet", "Wallet", "WALLET"
            };
        }

        private string Capitalize(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            return char.ToUpper(input[0]) + input.Substring(1).ToLower();
        }

        public class UserProfile
        {
            public string FirstName { get; set; }
            public string LastName { get; set; }
            public DateTime? BirthDate { get; set; }
            public List<string> PetNames { get; set; }
            public List<string> FavoriteWords { get; set; }
            public List<string> CustomPassphrases { get; set; }
        }

        public class PassphraseResult
        {
            public bool Success { get; set; }
            public string Passphrase { get; set; }
            public int AttemptsRequired { get; set; }
        }

        public class PassphraseProgressEventArgs : EventArgs
        {
            public string CurrentPassphrase { get; set; }
            public int CheckedCount { get; set; }
            public int TotalCount { get; set; }
            public int ProgressPercentage { get; set; }
        }

        public class PassphraseFoundEventArgs : EventArgs
        {
            public PassphraseResult Result { get; set; }
        }
    }
}
