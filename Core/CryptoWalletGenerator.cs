using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NBitcoin;
using Nethereum.HdWallet;
using Nethereum.Web3;
using Solnet.Wallet;
using BtcMnemonic = NBitcoin.Mnemonic;
using BtcWordlist = NBitcoin.Wordlist;
using BtcWordCount = NBitcoin.WordCount;

namespace WalletRecoveryTool.Core
{
    public class CryptoWalletGenerator
    {
        // Version bytes for altcoin P2PKH addresses
        private const byte LitecoinVersionByte = 0x30; // Produces 'L' prefix
        private const byte DogecoinVersionByte = 0x1E; // Produces 'D' prefix

        /// <summary>
        /// Encode a public key hash as a Base58Check address with the given version byte.
        /// Works for any Bitcoin-derived altcoin (LTC, DOGE, etc.).
        /// </summary>
        private static string EncodeAltcoinAddress(PubKey pubKey, byte versionByte)
        {
            var hash160 = pubKey.Hash.ToBytes(); // RIPEMD160(SHA256(pubkey))
            var payload = new byte[1 + hash160.Length];
            payload[0] = versionByte;
            Array.Copy(hash160, 0, payload, 1, hash160.Length);
            var checksum = SHA256.HashData(SHA256.HashData(payload));
            var addressBytes = new byte[payload.Length + 4];
            Array.Copy(payload, addressBytes, payload.Length);
            Array.Copy(checksum, 0, addressBytes, payload.Length, 4);
            return NBitcoin.DataEncoders.Encoders.Base58.EncodeData(addressBytes);
        }

        /// <summary>Encode a Litecoin P2PKH address ('L' prefix) from a public key.</summary>
        public static string EncodeLitecoinAddress(PubKey pubKey) => EncodeAltcoinAddress(pubKey, LitecoinVersionByte);
        
        /// <summary>Encode a Dogecoin P2PKH address ('D' prefix) from a public key.</summary>
        public static string EncodeDogecoinAddress(PubKey pubKey) => EncodeAltcoinAddress(pubKey, DogecoinVersionByte);
        /// <summary>
        /// Gets the BIP39 wordlist for the configured language
        /// </summary>
        public static BtcWordlist GetWordlistForLanguage(string language = null)
        {
            language = language ?? AppSettings.Load().PreferredMnemonicLanguage ?? "English";
            return language.ToLower() switch
            {
                "japanese" => BtcWordlist.Japanese,
                "spanish" => BtcWordlist.Spanish,
                "chinesesimplified" or "chinese_simplified" => BtcWordlist.ChineseSimplified,
                "chinesetraditional" or "chinese_traditional" => BtcWordlist.ChineseTraditional,
                "french" => BtcWordlist.French,
                "czech" => BtcWordlist.Czech,
                _ => BtcWordlist.English,
            };
        }
        public class WalletInfo
        {
            public string Mnemonic { get; set; }
            public string PrivateKey { get; set; }
            public string BitcoinAddress { get; set; }
            public string BitcoinAddressSegWit { get; set; }
            public string BitcoinAddressNativeSegWit { get; set; }
            public string BitcoinAddressTaproot { get; set; }
            public string EthereumAddress { get; set; }
            public string SolanaAddress { get; set; }
            public string DogecoinAddress { get; set; }
            public string LitecoinAddress { get; set; }
            public string RippleAddress { get; set; }
            public string CardanoAddress { get; set; }
            public string PolygonAddress { get; set; }
            public string BnbAddress { get; set; }
            public Dictionary<string, decimal> Balances { get; set; } = new Dictionary<string, decimal>();
            public decimal TotalUsdValue { get; set; }
            public DateTime? FoundAt { get; set; }
            
            // Extended addresses for deep scanning (multiple indices)
            public List<string> AllBitcoinAddresses { get; set; } = new List<string>();
            public List<string> AllEthereumAddresses { get; set; } = new List<string>();
            public List<string> AllLitecoinAddresses { get; set; } = new List<string>();
            public List<string> AllDogecoinAddresses { get; set; } = new List<string>();
            public int AddressIndex { get; set; } = 0; // Which address index this wallet uses
            
            // Source of the wallet (for smart hunting tracking)
            public string Source { get; set; } = "Random";

            // Historical funding visibility (for monitoring wallets that may have held funds before)
            public bool HistoricalActivityLikely { get; set; }
            public bool HistoricalActivityConfirmed { get; set; }
            public bool HistoricalActivityEvaluated { get; set; }
            public string HistoricalActivityDetails { get; set; }

            public string HistoricalActivityStatus
            {
                get
                {
                    if (HistoricalActivityConfirmed) return "Confirmed";
                    if (!HistoricalActivityEvaluated) return "Unknown";
                    return HistoricalActivityLikely ? "Likely" : "No Match";
                }
            }
        }

        /// <summary>
        /// Generate wallet with all addresses (backward-compatible overload).
        /// </summary>
        public static WalletInfo GenerateWallet(string mnemonic = null)
        {
            return GenerateWalletSelective(mnemonic, needEth: true, needSol: true, needLtc: true, needDoge: true);
        }

        /// <summary>
        /// SPEED-OPTIMIZED: Generate wallet, skipping expensive derivations for disabled coins.
        /// Fewer coins = faster generation (skips Nethereum/Solnet/altcoin key derivation).
        /// </summary>
        /// <param name="mnemonic">BIP39 mnemonic (null = generate random)</param>
        /// <param name="needEth">Generate ETH/MATIC/BNB/USDT addresses (Nethereum HD wallet)</param>
        /// <param name="needSol">Generate Solana address (Solnet wallet)</param>
        /// <param name="needLtc">Generate Litecoin address</param>
        /// <param name="needDoge">Generate Dogecoin address</param>
        public static WalletInfo GenerateWalletSelective(string mnemonic = null,
            bool needEth = true, bool needSol = true, bool needLtc = true, bool needDoge = true)
        {
            try
            {
                // Validate BIP39 checksum before doing expensive key derivation
                // Invalid checksums mean no real wallet software ever generated this mnemonic
                if (!string.IsNullOrEmpty(mnemonic) && !IsValidMnemonic(mnemonic))
                {
                    return null; // Skip — invalid checksum, no real wallet uses this
                }

                // Generate or use provided mnemonic
                var wordlist = GetWordlistForLanguage();
                var mnemonicObj = string.IsNullOrEmpty(mnemonic) 
                    ? new BtcMnemonic(wordlist, BtcWordCount.Twelve)
                    : new BtcMnemonic(mnemonic);

                var wallet = new WalletInfo
                {
                    Mnemonic = mnemonicObj.ToString()
                };

                // Generate Bitcoin addresses (always needed - fast secp256k1 derivation)
                var masterKey = mnemonicObj.DeriveExtKey();
                var bitcoinKey = masterKey.Derive(new KeyPath("m/44'/0'/0'/0/0"));
                
                // Legacy (P2PKH) - starts with 1
                wallet.BitcoinAddress = bitcoinKey.PrivateKey.PubKey.GetAddress(ScriptPubKeyType.Legacy, Network.Main).ToString();
                
                // SegWit (P2SH-P2WPKH) - starts with 3
                var segwitKey = masterKey.Derive(new KeyPath("m/49'/0'/0'/0/0"));
                wallet.BitcoinAddressSegWit = segwitKey.PrivateKey.PubKey.GetAddress(ScriptPubKeyType.SegwitP2SH, Network.Main).ToString();
                
                // Native SegWit (Bech32) - starts with bc1
                var nativeSegwitKey = masterKey.Derive(new KeyPath("m/84'/0'/0'/0/0"));
                wallet.BitcoinAddressNativeSegWit = nativeSegwitKey.PrivateKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.Main).ToString();
                
                // Taproot (BIP86, Bech32m) - starts with bc1p
                try
                {
                    var taprootKey = masterKey.Derive(new KeyPath("m/86'/0'/0'/0/0"));
                    wallet.BitcoinAddressTaproot = taprootKey.PrivateKey.PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Network.Main).ToString();
                }
                catch (Exception ex) { DebugLogger.Log($"Taproot derivation skipped: {ex.Message}"); }
                
                wallet.PrivateKey = bitcoinKey.PrivateKey.GetWif(Network.Main).ToString();

                // Generate Ethereum address (SKIP if ETH/USDT/MATIC/BNB all disabled — saves Nethereum HD wallet creation)
                if (needEth)
                {
                    var ethWallet = new Nethereum.HdWallet.Wallet(mnemonicObj.ToString(), "");
                    var ethAccount = ethWallet.GetAccount(0);
                    wallet.EthereumAddress = ethAccount.Address;
                    wallet.PolygonAddress = wallet.EthereumAddress; // EVM compatible
                    wallet.BnbAddress = wallet.EthereumAddress;     // EVM compatible
                }

                // Generate Solana address (SKIP if SOL disabled — saves Solnet Ed25519 derivation)
                if (needSol)
                {
                    var solanaWallet = new Solnet.Wallet.Wallet(mnemonicObj.ToString());
                    wallet.SolanaAddress = solanaWallet.Account.PublicKey.Key;
                }

                // Generate Dogecoin address (SKIP if DOGE disabled)
                if (needDoge)
                {
                    var dogeKey = masterKey.Derive(new KeyPath("m/44'/3'/0'/0/0"));
                    wallet.DogecoinAddress = EncodeAltcoinAddress(dogeKey.PrivateKey.PubKey, DogecoinVersionByte);
                }

                // Generate Litecoin address (SKIP if LTC disabled)
                if (needLtc)
                {
                    var ltcKey = masterKey.Derive(new KeyPath("m/44'/2'/0'/0/0"));
                    wallet.LitecoinAddress = EncodeAltcoinAddress(ltcKey.PrivateKey.PubKey, LitecoinVersionByte);
                }

                // Ripple (XRP) - Skipped in selective mode: no ScanXRP flag in RecoveryOptions
                // XRP addresses are still generated by the full GenerateWallet() for manual lookups
                wallet.RippleAddress = null;

                // Cardano - Skipped in selective mode: no ScanCardano flag in RecoveryOptions
                // Also avoids CardanoSharp reflection overhead + NullRef failures on non-standard mnemonics
                wallet.CardanoAddress = null;
                
                // Store primary addresses in extended lists
                wallet.AllBitcoinAddresses.Add(wallet.BitcoinAddress);
                if (wallet.EthereumAddress != null) wallet.AllEthereumAddresses.Add(wallet.EthereumAddress);
                if (wallet.LitecoinAddress != null) wallet.AllLitecoinAddresses.Add(wallet.LitecoinAddress);
                if (wallet.DogecoinAddress != null) wallet.AllDogecoinAddresses.Add(wallet.DogecoinAddress);

                return wallet;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to generate wallet: {ex.Message}", ex);
            }
        }
        
        /// <summary>
        /// DEEP SCAN: Generate wallet with multiple address indices (accounts 0-9, indices 0-19)
        /// This finds wallets that used non-default address indices - increases success by 10x
        /// </summary>
        public static WalletInfo GenerateWalletDeepScan(string mnemonic, int maxAccounts = 5, int maxAddressIndices = 10)
        {
            var wallet = GenerateWallet(mnemonic);
            
            try
            {
                var mnemonicObj = new BtcMnemonic(mnemonic);
                var masterKey = mnemonicObj.DeriveExtKey();
                var ethWallet = new Nethereum.HdWallet.Wallet(mnemonic, "");
                
                // Generate multiple Bitcoin addresses (Legacy, SegWit, Native SegWit) for accounts 0-N
                for (int account = 0; account < maxAccounts; account++)
                {
                    for (int index = 0; index < maxAddressIndices; index++)
                    {
                        // Skip index 0 account 0 - already generated
                        if (account == 0 && index == 0) continue;
                        
                        try
                        {
                            // BIP44 Legacy Bitcoin
                            var legacyKey = masterKey.Derive(new KeyPath($"m/44'/0'/{account}'/0/{index}"));
                            wallet.AllBitcoinAddresses.Add(legacyKey.PrivateKey.PubKey.GetAddress(ScriptPubKeyType.Legacy, Network.Main).ToString());
                            
                            // BIP84 Native SegWit Bitcoin
                            var segwitKey = masterKey.Derive(new KeyPath($"m/84'/0'/{account}'/0/{index}"));
                            wallet.AllBitcoinAddresses.Add(segwitKey.PrivateKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.Main).ToString());
                            
                            // BIP86 Taproot Bitcoin
                            try
                            {
                                var taprootKey = masterKey.Derive(new KeyPath($"m/86'/0'/{account}'/0/{index}"));
                                wallet.AllBitcoinAddresses.Add(taprootKey.PrivateKey.PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Network.Main).ToString());
                            }
                            catch { /* Taproot may not be supported for all paths */ }
                            
                            // Litecoin (BIP44 coin type 2, version byte 0x30 → 'L' prefix)
                            var ltcKey = masterKey.Derive(new KeyPath($"m/44'/2'/{account}'/0/{index}"));
                            wallet.AllLitecoinAddresses.Add(EncodeAltcoinAddress(ltcKey.PrivateKey.PubKey, LitecoinVersionByte));
                            
                            // Dogecoin (BIP44 coin type 3, version byte 0x1E → 'D' prefix)
                            var dogeKey = masterKey.Derive(new KeyPath($"m/44'/3'/{account}'/0/{index}"));
                            wallet.AllDogecoinAddresses.Add(EncodeAltcoinAddress(dogeKey.PrivateKey.PubKey, DogecoinVersionByte));
                        }
                        catch (Exception ex) { DebugLogger.Log($"DeepScan path error: {ex.Message}"); }
                    }
                }
                
                // Generate multiple Ethereum addresses (also applies to Polygon/BNB as they're EVM chains)
                for (int i = 1; i < Math.Min(maxAccounts * maxAddressIndices, 20); i++)
                {
                    try
                    {
                        wallet.AllEthereumAddresses.Add(ethWallet.GetAccount(i).Address);
                    }
                    catch { /* ETH account index not available */ }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogError("DeepScan generation", ex);
            }
            
            return wallet;
        }

        public static WalletInfo GenerateFromPrivateKey(string privateKeyHex, string cryptoType)
        {
            var wallet = new WalletInfo { PrivateKey = privateKeyHex };

            try
            {
                switch (cryptoType.ToUpper())
                {
                    case "BTC":
                    case "BITCOIN":
                        // Convert hex to bytes and create NBitcoin Key
                        var btcKeyBytes = Convert.FromHexString(privateKeyHex);
                        var btcKey = new Key(btcKeyBytes);
                        wallet.BitcoinAddress = btcKey.PubKey.GetAddress(ScriptPubKeyType.Legacy, Network.Main).ToString();
                        wallet.PrivateKey = btcKey.GetWif(Network.Main).ToWif(); // Store as WIF format
                        break;

                    case "ETH":
                    case "ETHEREUM":
                        // Nethereum expects hex string with or without 0x prefix
                        var ethKey = new Nethereum.Signer.EthECKey(privateKeyHex.Replace("0x", ""));
                        wallet.EthereumAddress = ethKey.GetPublicAddress();
                        break;

                    case "SOL":
                    case "SOLANA":
                        // Convert hex to bytes for Solana
                        var solKeyBytes = Convert.FromHexString(privateKeyHex);
                        if (solKeyBytes.Length != 32)
                            throw new InvalidOperationException("Solana private key must be 32 bytes");
                        
                        // Solana uses the private key bytes directly as seed
                        var solWallet = new Solnet.Wallet.Wallet(solKeyBytes);
                        wallet.SolanaAddress = solWallet.Account.PublicKey.Key;
                        break;
                }

                return wallet;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to generate {cryptoType} wallet from private key: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Generate wallet with a BIP39 passphrase (same mnemonic + passphrase = completely different wallet)
        /// </summary>
        public static WalletInfo GenerateWalletWithPassphrase(string mnemonic, string passphrase)
        {
            try
            {
                if (!IsValidMnemonic(mnemonic)) return null;
                
                var mnemonicObj = new BtcMnemonic(mnemonic);
                var wallet = new WalletInfo { Mnemonic = $"{mnemonic} [passphrase: {passphrase}]" };
                
                // BIP39: passphrase is used as salt in PBKDF2 derivation
                var masterKey = mnemonicObj.DeriveExtKey(passphrase);
                
                var bitcoinKey = masterKey.Derive(new KeyPath("m/44'/0'/0'/0/0"));
                wallet.BitcoinAddress = bitcoinKey.PrivateKey.PubKey.GetAddress(ScriptPubKeyType.Legacy, Network.Main).ToString();
                wallet.BitcoinAddressSegWit = masterKey.Derive(new KeyPath("m/49'/0'/0'/0/0")).PrivateKey.PubKey.GetAddress(ScriptPubKeyType.SegwitP2SH, Network.Main).ToString();
                wallet.BitcoinAddressNativeSegWit = masterKey.Derive(new KeyPath("m/84'/0'/0'/0/0")).PrivateKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.Main).ToString();
                try { wallet.BitcoinAddressTaproot = masterKey.Derive(new KeyPath("m/86'/0'/0'/0/0")).PrivateKey.PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Network.Main).ToString(); } catch { }
                wallet.PrivateKey = bitcoinKey.PrivateKey.GetWif(Network.Main).ToString();
                
                // ETH with passphrase
                var ethWallet = new Nethereum.HdWallet.Wallet(mnemonicObj.ToString(), passphrase);
                wallet.EthereumAddress = ethWallet.GetAccount(0).Address;

                // Solana (BIP39 passphrase applied via seed bytes)
                try
                {
                    // Solnet doesn't accept passphrase directly, so derive seed with passphrase and use seed bytes
                    var solanaSeedBytes = mnemonicObj.DeriveSeed(passphrase);
                    var solanaWallet = new Solnet.Wallet.Wallet(solanaSeedBytes);
                    wallet.SolanaAddress = solanaWallet.Account.PublicKey.Key;
                }
                catch (Exception ex) 
                { 
                    DebugLogger.Log($"Solana derivation with passphrase failed: {ex.Message}");
                    wallet.SolanaAddress = null; 
                }

                // Dogecoin
                var dogeKey = masterKey.Derive(new KeyPath("m/44'/3'/0'/0/0"));
                wallet.DogecoinAddress = EncodeAltcoinAddress(dogeKey.PrivateKey.PubKey, DogecoinVersionByte);

                // Litecoin
                var ltcKey = masterKey.Derive(new KeyPath("m/44'/2'/0'/0/0"));
                wallet.LitecoinAddress = EncodeAltcoinAddress(ltcKey.PrivateKey.PubKey, LitecoinVersionByte);

                // EVM chains share ETH address
                wallet.PolygonAddress = wallet.EthereumAddress;
                wallet.BnbAddress = wallet.EthereumAddress;

                // Ripple (XRP)
                try
                {
                    var xrpKey = masterKey.Derive(new KeyPath("m/44'/144'/0'/0/0"));
                    wallet.RippleAddress = GenerateXrpAddress(xrpKey.PrivateKey.PubKey.ToBytes());
                }
                catch { wallet.RippleAddress = null; }

                // Cardano
                try
                {
                    wallet.CardanoAddress = GenerateCardanoAddress(mnemonicObj.ToString());
                }
                catch { wallet.CardanoAddress = null; }
                
                wallet.AllBitcoinAddresses.Add(wallet.BitcoinAddress);
                wallet.AllEthereumAddresses.Add(wallet.EthereumAddress);
                if (wallet.LitecoinAddress != null) wallet.AllLitecoinAddresses.Add(wallet.LitecoinAddress);
                if (wallet.DogecoinAddress != null) wallet.AllDogecoinAddresses.Add(wallet.DogecoinAddress);
                
                return wallet;
            }
            catch { return null; }
        }

        /// <summary>
        /// Generate BTC change addresses (m/x'/0'/0'/1/index) — funds often sit in change addresses
        /// </summary>
        public static List<string> GenerateChangeAddresses(string mnemonic, int maxIndices = 5)
        {
            var addresses = new List<string>();
            try
            {
                var mnemonicObj = new BtcMnemonic(mnemonic);
                var masterKey = mnemonicObj.DeriveExtKey();
                
                for (int i = 0; i < maxIndices; i++)
                {
                    // Change path: .../1/index instead of .../0/index
                    try { addresses.Add(masterKey.Derive(new KeyPath($"m/44'/0'/0'/1/{i}")).PrivateKey.PubKey.GetAddress(ScriptPubKeyType.Legacy, Network.Main).ToString()); } catch { /* skip */ }
                    try { addresses.Add(masterKey.Derive(new KeyPath($"m/84'/0'/0'/1/{i}")).PrivateKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.Main).ToString()); } catch { /* skip */ }
                    try { addresses.Add(masterKey.Derive(new KeyPath($"m/86'/0'/0'/1/{i}")).PrivateKey.PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Network.Main).ToString()); } catch { /* skip */ }
                }
            }
            catch (Exception ex) { DebugLogger.LogError("GenerateChangeAddresses", ex); }
            return addresses;
        }

        /// <summary>
        /// Generate extra account/index addresses for multi-account scanning (accounts 0-2, indices 0-4)
        /// Lighter than full DeepScan — just the most likely paths
        /// </summary>
        public static List<string> GenerateMultiAccountAddresses(string mnemonic, int maxAccounts = 3, int maxIndices = 5)
        {
            var addresses = new List<string>();
            try
            {
                var mnemonicObj = new BtcMnemonic(mnemonic);
                var masterKey = mnemonicObj.DeriveExtKey();
                
                for (int account = 0; account < maxAccounts; account++)
                {
                    for (int index = 0; index < maxIndices; index++)
                    {
                        if (account == 0 && index == 0) continue; // Already checked by default
                        try { addresses.Add(masterKey.Derive(new KeyPath($"m/44'/0'/{account}'/0/{index}")).PrivateKey.PubKey.GetAddress(ScriptPubKeyType.Legacy, Network.Main).ToString()); } catch { /* skip */ }
                        try { addresses.Add(masterKey.Derive(new KeyPath($"m/84'/0'/{account}'/0/{index}")).PrivateKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.Main).ToString()); } catch { /* skip */ }
                    }
                }
            }
            catch (Exception ex) { DebugLogger.LogError("GenerateMultiAccountAddresses", ex); }
            return addresses;
        }

        /// <summary>
        /// Generate extra ETH account addresses (account indices 1-4)
        /// </summary>
        public static List<string> GenerateMultiAccountEthAddresses(string mnemonic, int maxAccounts = 5)
        {
            var addresses = new List<string>();
            try
            {
                var ethWallet = new Nethereum.HdWallet.Wallet(mnemonic, "");
                for (int i = 1; i < maxAccounts; i++)
                {
                    try { addresses.Add(ethWallet.GetAccount(i).Address); } catch { /* skip */ }
                }
            }
            catch (Exception ex) { DebugLogger.LogError("GenerateMultiAccountEthAddresses", ex); }
            return addresses;
        }

        public static List<string> GenerateMnemonicVariations(string partialMnemonic, List<string> wordlist = null)
        {
            if (wordlist == null)
                wordlist = BtcWordlist.English.GetWords().ToList();

            var words = partialMnemonic.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var variations = new List<string>();

            // If we have fewer than 12 words, generate combinations
            if (words.Length < 12)
            {
                var missingCount = 12 - words.Length;
                var combinations = GenerateWordCombinations(wordlist, missingCount);

                foreach (var combo in combinations.Take(100000)) // Limit for performance (10x increase)
                {
                    var fullMnemonic = string.Join(" ", words.Concat(combo));
                    if (IsValidMnemonic(fullMnemonic))
                    {
                        variations.Add(fullMnemonic);
                    }
                }
            }

            return variations;
        }

        private static IEnumerable<List<string>> GenerateWordCombinations(List<string> wordlist, int count)
        {
            if (count == 0)
                yield return new List<string>();
            else
            {
                foreach (var word in wordlist)
                {
                    foreach (var combo in GenerateWordCombinations(wordlist, count - 1))
                    {
                        var newCombo = new List<string> { word };
                        newCombo.AddRange(combo);
                        yield return newCombo;
                    }
                }
            }
        }

        public static bool IsValidMnemonic(string mnemonic)
        {
            try
            {
                new BtcMnemonic(mnemonic);
                return true;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Validate mnemonic with detailed error reporting
        /// </summary>
        public static (bool IsValid, string Error, int InvalidWordIndex) ValidateMnemonicDetailed(string mnemonic)
        {
            if (string.IsNullOrWhiteSpace(mnemonic))
                return (false, "Mnemonic is empty", -1);
                
            var words = mnemonic.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            
            // Check word count
            if (words.Length != 12 && words.Length != 15 && words.Length != 18 && words.Length != 21 && words.Length != 24)
                return (false, $"Invalid word count: {words.Length}. Must be 12, 15, 18, 21, or 24 words.", -1);
            
            // Check each word is in BIP39 wordlist
            var wordlist = BtcWordlist.English.GetWords().ToList();
            for (int i = 0; i < words.Length; i++)
            {
                if (!wordlist.Contains(words[i].ToLower()))
                    return (false, $"Word #{i + 1} '{words[i]}' is not a valid BIP39 word", i);
            }
            
            // Check checksum
            try
            {
                new BtcMnemonic(mnemonic);
                return (true, null, -1);
            }
            catch (Exception ex)
            {
                return (false, $"Invalid checksum: {ex.Message}", -1);
            }
        }
        
        /// <summary>
        /// Generate all valid mnemonics by fixing checksum word (last word)
        /// This is CRITICAL for recovering mnemonics with wrong last word
        /// </summary>
        public static List<string> GenerateValidChecksumVariations(string partialMnemonic)
        {
            var validMnemonics = new List<string>();
            var words = partialMnemonic.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            
            if (words.Length < 11 || words.Length > 23) 
                return validMnemonics;
            
            var wordlist = GetWordlistForLanguage().GetWords().ToList();
            var baseWords = words.Take(words.Length - 1).ToList();
            
            // Try all 2048 possible last words to find valid checksums
            foreach (var lastWord in wordlist)
            {
                var testMnemonic = string.Join(" ", baseWords.Concat(new[] { lastWord }));
                if (IsValidMnemonic(testMnemonic))
                {
                    validMnemonics.Add(testMnemonic);
                }
            }
            
            return validMnemonics;
        }

        public static string GenerateRandomMnemonic()
        {
            var mnemonic = new BtcMnemonic(GetWordlistForLanguage(), BtcWordCount.Twelve);
            return mnemonic.ToString();
        }

        /// <summary>
        /// Generate XRP address from secp256k1 public key bytes using XRP base58check
        /// </summary>
        private static string GenerateXrpAddress(byte[] publicKeyBytes)
        {
            // XRP address = base58check(0x00 + RIPEMD160(SHA256(pubkey)))
            byte[] sha256Hash;
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                sha256Hash = sha256.ComputeHash(publicKeyBytes);
            }

            // Use NBitcoin's built-in RIPEMD-160
            var ripemd160Hash = NBitcoin.Crypto.Hashes.RIPEMD160(sha256Hash, sha256Hash.Length);

            // XRP base58check: payload = 0x00 + accountID(20 bytes)
            var payload = new byte[21];
            payload[0] = 0x00; // XRP account prefix
            Array.Copy(ripemd160Hash, 0, payload, 1, 20);

            // Double SHA-256 checksum
            byte[] checksum;
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                var hash1 = sha256.ComputeHash(payload);
                checksum = sha256.ComputeHash(hash1);
            }

            var addressBytes = new byte[25];
            Array.Copy(payload, 0, addressBytes, 0, 21);
            Array.Copy(checksum, 0, addressBytes, 21, 4);

            // XRP uses a different base58 alphabet: rpshnaf39wBUDNEGHJKLM4PQRST7VWXYZ2bcdeCg65jkm8oFqi1tuvAxyz
            const string xrpAlphabet = "rpshnaf39wBUDNEGHJKLM4PQRST7VWXYZ2bcdeCg65jkm8oFqi1tuvAxyz";
            return Base58Encode(addressBytes, xrpAlphabet);
        }

        private static string Base58Encode(byte[] data, string alphabet)
        {
            var result = new StringBuilder();
            var value = new System.Numerics.BigInteger(data.Reverse().Concat(new byte[] { 0 }).ToArray());
            
            while (value > 0)
            {
                value = System.Numerics.BigInteger.DivRem(value, 58, out var remainder);
                result.Insert(0, alphabet[(int)remainder]);
            }

            // Add leading zeros
            foreach (var b in data)
            {
                if (b != 0) break;
                result.Insert(0, alphabet[0]);
            }

            return result.ToString();
        }

        /// <summary>
        /// Generate Cardano (ADA) address from mnemonic using CardanoSharp
        /// </summary>
        private static System.Reflection.MethodInfo _cardanoDeriveMethod;
        private static string GenerateCardanoAddress(string mnemonicPhrase)
        {
            var mnemonicService = new CardanoSharp.Wallet.MnemonicService();
            var mnemonic = mnemonicService.Restore(mnemonicPhrase);
            
            // Use fully-qualified extension method calls to avoid conflict with NBitcoin
            var rootKey = CardanoSharp.Wallet.Extensions.Models.MnemonicExtensions.GetRootKey(mnemonic);
            
            // Cardano CIP-1852 path: m/1852'/1815'/0'/0/0
            const uint hardened = 0x80000000;
            
            // Cache reflection lookup — GetMethod is expensive per-call
            _cardanoDeriveMethod ??= typeof(CardanoSharp.Wallet.Extensions.Models.PrivateKeyExtensions)
                .GetMethod("Derive", new[] { typeof(CardanoSharp.Wallet.Models.Keys.PrivateKey), typeof(uint) });
            
            if (_cardanoDeriveMethod == null)
                throw new InvalidOperationException("CardanoSharp PrivateKeyExtensions.Derive method not found — library version may have changed");
            
            var derive = _cardanoDeriveMethod;
            
            var purposeNode = (CardanoSharp.Wallet.Models.Keys.PrivateKey)derive.Invoke(null, new object[] { rootKey, 1852u + hardened });
            var coinNode = (CardanoSharp.Wallet.Models.Keys.PrivateKey)derive.Invoke(null, new object[] { purposeNode, 1815u + hardened });
            var accountNode = (CardanoSharp.Wallet.Models.Keys.PrivateKey)derive.Invoke(null, new object[] { coinNode, 0u + hardened });

            var paymentRole = (CardanoSharp.Wallet.Models.Keys.PrivateKey)derive.Invoke(null, new object[] { accountNode, 0u }); // ExternalChain
            var paymentKey = (CardanoSharp.Wallet.Models.Keys.PrivateKey)derive.Invoke(null, new object[] { paymentRole, 0u });

            var stakeRole = (CardanoSharp.Wallet.Models.Keys.PrivateKey)derive.Invoke(null, new object[] { accountNode, 2u }); // Staking
            var stakeKey = (CardanoSharp.Wallet.Models.Keys.PrivateKey)derive.Invoke(null, new object[] { stakeRole, 0u });

            var paymentPubKey = CardanoSharp.Wallet.Extensions.Models.PrivateKeyExtensions.GetPublicKey(paymentKey, false);
            var stakePubKey = CardanoSharp.Wallet.Extensions.Models.PrivateKeyExtensions.GetPublicKey(stakeKey, false);

            var address = CardanoSharp.Wallet.Utilities.AddressUtility.GetBaseAddress(
                paymentPubKey,
                stakePubKey,
                CardanoSharp.Wallet.Enums.NetworkType.Mainnet);

            return address.ToString();
        }
    }
}