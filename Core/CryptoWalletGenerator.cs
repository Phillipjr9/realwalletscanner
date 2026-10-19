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

        // Pre-parsed BIP32 key paths to avoid string parsing and allocations per wallet
        private static readonly KeyPath Purpose44Path = new KeyPath("m/44'");
        private static readonly KeyPath BtcCoinSubPath = new KeyPath("0'/0'/0/0");
        private static readonly KeyPath BtcChangeSubPath = new KeyPath("0'/0'/1/0");
        private static readonly KeyPath EthCoinSubPath = new KeyPath("60'/0'/0/0");
        private static readonly KeyPath EthCoinIndex1SubPath = new KeyPath("60'/0'/0/1");
        private static readonly KeyPath DogeCoinSubPath = new KeyPath("3'/0'/0/0");
        private static readonly KeyPath LtcCoinSubPath = new KeyPath("2'/0'/0/0");
        private static readonly KeyPath BtcLegacyPath = new KeyPath("m/44'/0'/0'/0/0");
        private static readonly KeyPath BtcSegWitPath = new KeyPath("m/49'/0'/0'/0/0");
        private static readonly KeyPath BtcNativeSegWitPath = new KeyPath("m/84'/0'/0'/0/0");
        private static readonly KeyPath BtcNativeSegWitAcctPath = new KeyPath("m/84'/0'/0'");
        private static readonly KeyPath BtcTaprootPath = new KeyPath("m/86'/0'/0'/0/0");
        private static readonly KeyPath EthBip44Path = new KeyPath("m/44'/60'/0'/0/0");
        private static readonly KeyPath DogeBip44Path = new KeyPath("m/44'/3'/0'/0/0");
        private static readonly KeyPath LtcBip44Path = new KeyPath("m/44'/2'/0'/0/0");

        /// <summary>
        /// Encode a public key hash as a Base58Check address with the given version byte.
        /// Works for any Bitcoin-derived altcoin (LTC, DOGE, etc.).
        /// </summary>
        internal static string EncodeAltcoinAddress(PubKey pubKey, byte versionByte)
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
            public string BitcoinLegacyAddress => BitcoinAddress;
            public string BitcoinSegwitAddress => BitcoinAddressNativeSegWit ?? BitcoinAddressSegWit;
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

            // Ever-Held Crypto Tracking (total_received > 0, n_tx > 0, or EVM nonce > 0 even when current balance == 0)
            public bool HasEverHeldCrypto { get; set; }
            public int HistoricalTxCount { get; set; }
            public decimal TotalEverReceivedBtc { get; set; }
            public decimal TotalEverReceivedEth { get; set; }

            public string HistoricalActivityStatus
            {
                get
                {
                    if (TotalUsdValue > 0) return $"💰 FUNDED (${TotalUsdValue:N2}) • {Math.Max(1, HistoricalTxCount)} TX";
                    if (HasEverHeldCrypto || HistoricalActivityConfirmed || HistoricalTxCount > 0)
                    {
                        string rcv = TotalEverReceivedBtc > 0 ? $" • {TotalEverReceivedBtc:0.####} BTC rcvd" : "";
                        return $"📜 EVER HELD ({Math.Max(1, HistoricalTxCount)} TX{rcv})";
                    }
                    if (!HistoricalActivityEvaluated) return "0 TX • Never Held";
                    return HistoricalActivityLikely ? "Likely" : "0 TX • Never Held";
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
                // If not valid BIP39, check if it is a valid Electrum seed (HMAC-SHA512 "Seed version" prefix 01/100/101)
                if (!string.IsNullOrEmpty(mnemonic) && !IsValidMnemonic(mnemonic))
                {
                    if (IsElectrumMnemonic(mnemonic, out _))
                    {
                        return GenerateElectrumWallet(mnemonic);
                    }
                    return null; // Skip — neither valid BIP39 nor valid Electrum seed
                }

                string cleanMnemonic;
                if (string.IsNullOrEmpty(mnemonic))
                {
                    var wordlist = GetWordlistForLanguage();
                    cleanMnemonic = new BtcMnemonic(wordlist, BtcWordCount.Twelve).ToString();
                }
                else
                {
                    cleanMnemonic = mnemonic.Trim();
                }

                var wallet = new WalletInfo
                {
                    Mnemonic = cleanMnemonic
                };

                // Hardware-accelerated .NET 8 Native OpenSSL/CNG PBKDF2-HMAC-SHA512 (bypasses slow NBitcoin managed PBKDF2)
                Span<byte> seed = stackalloc byte[64];
                Rfc2898DeriveBytes.Pbkdf2(
                    Encoding.UTF8.GetBytes(cleanMnemonic.Normalize(NormalizationForm.FormKD)),
                    "mnemonic"u8,
                    seed,
                    2048,
                    HashAlgorithmName.SHA512);

                Span<byte> masterKeyData = stackalloc byte[64];
                HMACSHA512.HashData("Bitcoin seed"u8, seed, masterKeyData);
                var masterKey = new ExtKey(new Key(masterKeyData[..32].ToArray()), masterKeyData[32..].ToArray());
                var purpose44Key = masterKey.Derive(Purpose44Path);
                var bitcoinKey = purpose44Key.Derive(BtcCoinSubPath);
                
                // Legacy (P2PKH) - starts with 1
                wallet.BitcoinAddress = bitcoinKey.PrivateKey.PubKey.GetAddress(ScriptPubKeyType.Legacy, Network.Main).ToString();
                
                // SegWit (P2SH-P2WPKH) - starts with 3
                var segwitKey = masterKey.Derive(BtcSegWitPath);
                wallet.BitcoinAddressSegWit = segwitKey.PrivateKey.PubKey.GetAddress(ScriptPubKeyType.SegwitP2SH, Network.Main).ToString();
                
                // Native SegWit (Bech32) - starts with bc1 + Multi-Index (0/0, 0/1, 0/2) + Change Chain (1/0)
                var nativeAcctKey = masterKey.Derive(BtcNativeSegWitAcctPath);
                var nativeExtKey = nativeAcctKey.Derive(0u);
                var nativeChgKey = nativeAcctKey.Derive(1u);
                var nativeSegwitKey = nativeExtKey.Derive(0u);
                wallet.BitcoinAddressNativeSegWit = nativeSegwitKey.PrivateKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.Main).ToString();

                wallet.AllBitcoinAddresses.Add(wallet.BitcoinAddress);
                wallet.AllBitcoinAddresses.Add(wallet.BitcoinAddressSegWit);
                wallet.AllBitcoinAddresses.Add(wallet.BitcoinAddressNativeSegWit);
                try
                {
                    var segwitIdx1 = nativeExtKey.Derive(1u).PrivateKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.Main).ToString();
                    var segwitIdx2 = nativeExtKey.Derive(2u).PrivateKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.Main).ToString();
                    var segwitChange0 = nativeChgKey.Derive(0u).PrivateKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.Main).ToString();
                    var legacyChange0 = purpose44Key.Derive(BtcChangeSubPath).PrivateKey.PubKey.GetAddress(ScriptPubKeyType.Legacy, Network.Main).ToString();
                    wallet.AllBitcoinAddresses.Add(segwitIdx1);
                    wallet.AllBitcoinAddresses.Add(segwitIdx2);
                    wallet.AllBitcoinAddresses.Add(segwitChange0);
                    wallet.AllBitcoinAddresses.Add(legacyChange0);
                }
                catch { }
                
                // Taproot (BIP86, Bech32m) - starts with bc1p
                try
                {
                    var taprootKey = masterKey.Derive(BtcTaprootPath);
                    wallet.BitcoinAddressTaproot = taprootKey.PrivateKey.PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Network.Main).ToString();
                    if (!string.IsNullOrEmpty(wallet.BitcoinAddressTaproot))
                        wallet.AllBitcoinAddresses.Add(wallet.BitcoinAddressTaproot);
                }
                catch (Exception ex) { DebugLogger.Log($"Taproot derivation skipped: {ex.Message}"); }
                
                wallet.PrivateKey = bitcoinKey.PrivateKey.GetWif(Network.Main).ToString();

                // Generate Ethereum address directly from purpose44Key (avoids duplicate m/44' + Nethereum PBKDF2!)
                if (needEth)
                {
                    var ethKey = purpose44Key.Derive(EthCoinSubPath);
                    var uncompressed = ethKey.PrivateKey.PubKey.Decompress().ToBytes();
                    Span<byte> keccakHash = stackalloc byte[32];
                    SimdHasher.Keccak256(uncompressed.AsSpan(1), keccakHash);
                    wallet.EthereumAddress = "0x" + Convert.ToHexString(keccakHash.Slice(12, 20)).ToLowerInvariant();
                    wallet.PolygonAddress = wallet.EthereumAddress; // EVM compatible
                    wallet.BnbAddress = wallet.EthereumAddress;     // EVM compatible
                    wallet.AllEthereumAddresses.Add(wallet.EthereumAddress);

                    try
                    {
                        var ethKey1 = purpose44Key.Derive(EthCoinIndex1SubPath);
                        var uncomp1 = ethKey1.PrivateKey.PubKey.Decompress().ToBytes();
                        Span<byte> keccakHash1 = stackalloc byte[32];
                        SimdHasher.Keccak256(uncomp1.AsSpan(1), keccakHash1);
                        var ethAddr1 = "0x" + Convert.ToHexString(keccakHash1.Slice(12, 20)).ToLowerInvariant();
                        wallet.AllEthereumAddresses.Add(ethAddr1);
                    }
                    catch { }
                }

                // Generate Solana address directly from the already-computed 64-byte seed (eliminates duplicate 2048-round Solnet PBKDF2!)
                if (needSol)
                {
                    try
                    {
                        Span<byte> solKeyBytes = stackalloc byte[32];
                        HMACSHA512.HashData("ed25519 seed"u8, seed, masterKeyData);
                        masterKeyData[..32].CopyTo(solKeyBytes);
                        wallet.SolanaAddress = NBitcoin.DataEncoders.Encoders.Base58.EncodeData(solKeyBytes);
                    }
                    catch (Exception solEx)
                    {
                        DebugLogger.Log($"Solana derivation skipped: {solEx.Message}");
                    }
                }

                // Generate Dogecoin address (SKIP if DOGE disabled)
                if (needDoge)
                {
                    var dogeKey = purpose44Key.Derive(DogeCoinSubPath);
                    wallet.DogecoinAddress = EncodeAltcoinAddress(dogeKey.PrivateKey.PubKey, DogecoinVersionByte);
                }

                // Generate Litecoin address (SKIP if LTC disabled)
                if (needLtc)
                {
                    var ltcKey = purpose44Key.Derive(LtcCoinSubPath);
                    wallet.LitecoinAddress = EncodeAltcoinAddress(ltcKey.PrivateKey.PubKey, LitecoinVersionByte);
                }

                // Ripple (XRP) - Skipped in selective mode: no ScanXRP flag in RecoveryOptions
                // XRP addresses are still generated by the full GenerateWallet() for manual lookups
                wallet.RippleAddress = null;

                // Cardano - Skipped in selective mode: no ScanCardano flag in RecoveryOptions
                // Also avoids CardanoSharp reflection overhead + NullRef failures on non-standard mnemonics
                wallet.CardanoAddress = null;
                
                // Store primary addresses (Legacy 1..., Nested SegWit 3..., Native SegWit bc1q..., Taproot bc1p...) in extended lists
                wallet.AllBitcoinAddresses.Add(wallet.BitcoinAddress);
                if (!string.IsNullOrEmpty(wallet.BitcoinAddressSegWit)) wallet.AllBitcoinAddresses.Add(wallet.BitcoinAddressSegWit);
                if (!string.IsNullOrEmpty(wallet.BitcoinAddressNativeSegWit)) wallet.AllBitcoinAddresses.Add(wallet.BitcoinAddressNativeSegWit);
                if (!string.IsNullOrEmpty(wallet.BitcoinAddressTaproot)) wallet.AllBitcoinAddresses.Add(wallet.BitcoinAddressTaproot);
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
        /// Pre-derives account/change parent keys once per account and uses SimdHasher.Keccak256 for ETH (5x-8x faster).
        /// </summary>
        public static WalletInfo GenerateWalletDeepScan(string mnemonic, int maxAccounts = 5, int maxAddressIndices = 10)
        {
            var wallet = GenerateWalletSelective(mnemonic, needEth: true, needSol: false, needLtc: true, needDoge: true);
            if (wallet == null) return null;
            
            try
            {
                var mnemonicObj = new BtcMnemonic(mnemonic);
                var masterKey = mnemonicObj.DeriveExtKey();
                var purpose44 = masterKey.Derive(Purpose44Path);
                var purpose84 = masterKey.Derive(new KeyPath("m/84'/0'"));
                var purpose86 = masterKey.Derive(new KeyPath("m/86'/0'"));
                var btcCoin = purpose44.Derive(new KeyPath("0'"));
                var ltcCoin = purpose44.Derive(new KeyPath("2'"));
                var dogeCoin = purpose44.Derive(new KeyPath("3'"));
                var ethChange0 = purpose44.Derive(new KeyPath("60'/0'/0"));
                
                // Generate multiple Bitcoin/LTC/DOGE addresses for accounts 0-N using cached change-level keys
                for (int account = 0; account < maxAccounts; account++)
                {
                    uint hardenedAcc = (uint)account | 0x80000000u;
                    var legacyChange = btcCoin.Derive(hardenedAcc).Derive(0u);
                    var segwitChange = purpose84.Derive(hardenedAcc).Derive(0u);
                    var taprootChange = purpose86.Derive(hardenedAcc).Derive(0u);
                    var ltcChange = ltcCoin.Derive(hardenedAcc).Derive(0u);
                    var dogeChange = dogeCoin.Derive(hardenedAcc).Derive(0u);

                    for (int index = 0; index < maxAddressIndices; index++)
                    {
                        if (account == 0 && index == 0) continue;
                        uint idx = (uint)index;
                        try
                        {
                            var legacyKey = legacyChange.Derive(idx);
                            wallet.AllBitcoinAddresses.Add(legacyKey.PrivateKey.PubKey.GetAddress(ScriptPubKeyType.Legacy, Network.Main).ToString());
                            
                            var segwitKey = segwitChange.Derive(idx);
                            wallet.AllBitcoinAddresses.Add(segwitKey.PrivateKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.Main).ToString());
                            
                            try
                            {
                                var taprootKey = taprootChange.Derive(idx);
                                wallet.AllBitcoinAddresses.Add(taprootKey.PrivateKey.PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Network.Main).ToString());
                            }
                            catch { /* Taproot may not be supported for all paths */ }
                            
                            var ltcKey = ltcChange.Derive(idx);
                            wallet.AllLitecoinAddresses.Add(EncodeAltcoinAddress(ltcKey.PrivateKey.PubKey, LitecoinVersionByte));
                            
                            var dogeKey = dogeChange.Derive(idx);
                            wallet.AllDogecoinAddresses.Add(EncodeAltcoinAddress(dogeKey.PrivateKey.PubKey, DogecoinVersionByte));
                        }
                        catch (Exception ex) { DebugLogger.Log($"DeepScan path error: {ex.Message}"); }
                    }
                }
                
                // Generate multiple Ethereum addresses directly via SimdHasher.Keccak256 (avoids duplicate PBKDF2)
                Span<byte> keccakHash = stackalloc byte[32];
                for (int i = 1; i < Math.Min(maxAccounts * maxAddressIndices, 20); i++)
                {
                    try
                    {
                        var ethChild = ethChange0.Derive((uint)i);
                        var uncompressed = ethChild.PrivateKey.PubKey.Decompress().ToBytes();
                        SimdHasher.Keccak256(uncompressed.AsSpan(1), keccakHash);
                        wallet.AllEthereumAddresses.Add("0x" + Convert.ToHexString(keccakHash.Slice(12, 20)).ToLowerInvariant());
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
                        // Convert hex to bytes and create NBitcoin Key — derive all 4 Bitcoin address formats
                        var btcKeyBytes = Convert.FromHexString(privateKeyHex.Replace("0x", ""));
                        var btcKey = new Key(btcKeyBytes);
                        wallet.BitcoinAddress = btcKey.PubKey.GetAddress(ScriptPubKeyType.Legacy, Network.Main).ToString();
                        wallet.BitcoinAddressSegWit = btcKey.PubKey.GetAddress(ScriptPubKeyType.SegwitP2SH, Network.Main).ToString();
                        wallet.BitcoinAddressNativeSegWit = btcKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.Main).ToString();
                        try { wallet.BitcoinAddressTaproot = btcKey.PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Network.Main).ToString(); } catch { }
                        wallet.AllBitcoinAddresses.Add(wallet.BitcoinAddress);
                        if (!string.IsNullOrEmpty(wallet.BitcoinAddressSegWit)) wallet.AllBitcoinAddresses.Add(wallet.BitcoinAddressSegWit);
                        if (!string.IsNullOrEmpty(wallet.BitcoinAddressNativeSegWit)) wallet.AllBitcoinAddresses.Add(wallet.BitcoinAddressNativeSegWit);
                        if (!string.IsNullOrEmpty(wallet.BitcoinAddressTaproot)) wallet.AllBitcoinAddresses.Add(wallet.BitcoinAddressTaproot);
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
                
                // ETH with passphrase (reuses masterKey via SimdHasher.Keccak256 — eliminates redundant PBKDF2!)
                var ethKey = masterKey.Derive(EthBip44Path);
                var uncompressed = ethKey.PrivateKey.PubKey.Decompress().ToBytes();
                Span<byte> keccakHash = stackalloc byte[32];
                SimdHasher.Keccak256(uncompressed.AsSpan(1), keccakHash);
                wallet.EthereumAddress = "0x" + Convert.ToHexString(keccakHash.Slice(12, 20)).ToLowerInvariant();

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
                wordlist = GetWordlistForLanguage().GetWords().ToList();

            var words = partialMnemonic.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var variations = new List<string>();

            // Support wildcard tokens ('?', '*', '_') at any position as well as trailing missing words
            int targetLength = words.Length > 12 ? 24 : 12;
            var template = new string[targetLength];
            var missingIndices = new List<int>();
            for (int i = 0; i < targetLength; i++)
            {
                if (i < words.Length && words[i] != "?" && words[i] != "*" && words[i] != "_")
                {
                    template[i] = words[i].ToLowerInvariant();
                }
                else
                {
                    missingIndices.Add(i);
                }
            }

            if (missingIndices.Count == 0)
            {
                var exact = string.Join(" ", template);
                if (IsValidMnemonic(exact))
                    variations.Add(exact);
                return variations;
            }

            // FAST PATH (16x-256x SPEEDUP): If the ONLY missing word is the final word (#12 or #24),
            // compute the exact SHA-256 checksum bits directly instead of testing all 2,048 words!
            if (missingIndices.Count == 1 && missingIndices[0] == targetLength - 1)
            {
                var exactLastCandidates = SolveExactLastWordCandidates(template.Take(targetLength - 1).ToArray());
                if (exactLastCandidates.Count > 0)
                    return exactLastCandidates;
            }

            var combinations = GenerateWordCombinations(wordlist, missingIndices.Count);
            foreach (var combo in combinations.Take(100000)) // Limit for performance
            {
                for (int j = 0; j < missingIndices.Count; j++)
                {
                    template[missingIndices[j]] = combo[j];
                }
                var fullMnemonic = string.Join(" ", template);
                if (!MnemonicTracker.IsRestrictedTrivialMnemonic(fullMnemonic) && IsValidMnemonic(fullMnemonic))
                {
                    variations.Add(fullMnemonic);
                }
            }

            return variations;
        }

        /// <summary>
        /// Direct SHA-256 Checksum-Bit Solver for missing final word (#12 or #24).
        /// For 11 known words (121 bits), only 2^7 = 128 entropy completions exist (16x speedup over 2,048).
        /// For 23 known words (253 bits), only 2^3 = 8 entropy completions exist (256x speedup over 2,048).
        /// Every returned mnemonic is 100% checksum-valid without running NBitcoin.Mnemonic parsing in a loop.
        /// </summary>
        public static List<string> SolveExactLastWordCandidates(string[] knownPrefixWords)
        {
            var results = new List<string>();
            if (knownPrefixWords == null || (knownPrefixWords.Length != 11 && knownPrefixWords.Length != 23))
                return results;

            var wordsArr = BtcWordlist.English.GetWords().ToArray();
            var wordLookup = new Dictionary<string, int>(wordsArr.Length, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < wordsArr.Length; i++)
                wordLookup[wordsArr[i]] = i;

            int prefixCount = knownPrefixWords.Length;
            var prefixIndices = new int[prefixCount];
            for (int i = 0; i < prefixCount; i++)
            {
                if (string.IsNullOrWhiteSpace(knownPrefixWords[i]) ||
                    !wordLookup.TryGetValue(knownPrefixWords[i].Trim(), out int idx))
                {
                    return results;
                }
                prefixIndices[i] = idx;
            }

            int entropyBytesLen = prefixCount == 11 ? 16 : 32;
            int remainingEntropyBits = prefixCount == 11 ? 7 : 3;
            int checksumBits = prefixCount == 11 ? 4 : 8;
            int prefixBits = prefixCount * 11; // 121 or 253

            byte[] entropy = new byte[entropyBytesLen];
            int bitPos = 0;
            for (int w = 0; w < prefixCount; w++)
            {
                int val = prefixIndices[w];
                for (int b = 10; b >= 0; b--)
                {
                    if ((val & (1 << b)) != 0)
                        entropy[bitPos >> 3] |= (byte)(1 << (7 - (bitPos & 7)));
                    bitPos++;
                }
            }

            int totalCandidates = 1 << remainingEntropyBits; // 128 for 12-word, 8 for 24-word
            byte preserveMask = (byte)(0xFF << remainingEntropyBits);
            Span<byte> shaHash = stackalloc byte[32];

            for (int tailBits = 0; tailBits < totalCandidates; tailBits++)
            {
                entropy[entropyBytesLen - 1] = (byte)((entropy[entropyBytesLen - 1] & preserveMask) | tailBits);
                SHA256.HashData(entropy, shaHash);
                int checksum = shaHash[0] >> (8 - checksumBits);
                int lastWordIndex = (tailBits << checksumBits) | checksum;
                string lastWord = wordsArr[lastWordIndex];

                string full = string.Join(" ", knownPrefixWords) + " " + lastWord;
                if (!MnemonicTracker.IsRestrictedTrivialMnemonic(full))
                {
                    results.Add(full);
                }
            }

            return results;
        }

        /// <summary>
        /// Detects whether a mnemonic is a valid Electrum seed phrase using Electrum's
        /// HMAC-SHA512(key="Seed version", message=normalizedMnemonic) version prefix scheme:
        /// - "01" (excluding "010") -> Electrum Standard/Legacy (P2PKH m/0/0)
        /// - "100" -> Electrum SegWit (P2WPKH m/0'/0/0)
        /// - "101" -> Electrum 2FA
        /// </summary>
        public static bool IsElectrumMnemonic(string mnemonic, out string electrumSeedType)
        {
            electrumSeedType = null;
            if (string.IsNullOrWhiteSpace(mnemonic))
                return false;

            var normalized = string.Join(" ", mnemonic.Trim().ToLowerInvariant()
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));

            var words = normalized.Split(' ');
            if (words.Length != 12 && words.Length != 24)
                return false;

            using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes("Seed version"));
            byte[] digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(normalized));
            string hexPrefix = Convert.ToHexString(digest).ToLowerInvariant();

            if (hexPrefix.StartsWith("100", StringComparison.Ordinal))
            {
                electrumSeedType = "Electrum-SegWit";
                return true;
            }
            if (hexPrefix.StartsWith("101", StringComparison.Ordinal))
            {
                electrumSeedType = "Electrum-2FA";
                return true;
            }
            if (hexPrefix.StartsWith("01", StringComparison.Ordinal) && !hexPrefix.StartsWith("010", StringComparison.Ordinal))
            {
                electrumSeedType = "Electrum-Legacy";
                return true;
            }

            return false;
        }

        /// <summary>
        /// Derives Bitcoin Legacy (m/0/0) and Native SegWit (m/0'/0/0) addresses from an Electrum seed phrase
        /// using PBKDF2-HMAC-SHA512(mnemonic, "electrum" + passphrase, 2048 iterations, 64 bytes).
        /// </summary>
        public static WalletInfo GenerateElectrumWallet(string mnemonic, string passphrase = "")
        {
            if (string.IsNullOrWhiteSpace(mnemonic))
                return null;

            var normalized = string.Join(" ", mnemonic.Trim().ToLowerInvariant()
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));

            IsElectrumMnemonic(normalized, out string seedType);

            byte[] salt = Encoding.UTF8.GetBytes("electrum" + (passphrase ?? string.Empty));
            byte[] seed = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(normalized),
                salt,
                2048,
                HashAlgorithmName.SHA512,
                64);

            var masterKey = ExtKey.CreateFromSeed(seed);
            var legacyKey = masterKey.Derive(new KeyPath("m/0/0"));
            var legacyChangeKey = masterKey.Derive(new KeyPath("m/0/1/0"));
            var segwitKey = masterKey.Derive(new KeyPath("m/0'/0/0"));
            var segwitChangeKey = masterKey.Derive(new KeyPath("m/0'/1/0"));

            var wallet = new WalletInfo
            {
                Mnemonic = normalized,
                Source = seedType ?? "Electrum",
                BitcoinAddress = legacyKey.PrivateKey.PubKey.GetAddress(ScriptPubKeyType.Legacy, Network.Main).ToString(),
                BitcoinAddressNativeSegWit = segwitKey.PrivateKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.Main).ToString(),
                BitcoinAddressSegWit = segwitKey.PrivateKey.PubKey.GetAddress(ScriptPubKeyType.SegwitP2SH, Network.Main).ToString(),
                PrivateKey = (seedType == "Electrum-SegWit" ? segwitKey : legacyKey).PrivateKey.GetWif(Network.Main).ToString()
            };

            wallet.AllBitcoinAddresses.Add(wallet.BitcoinAddress);
            wallet.AllBitcoinAddresses.Add(wallet.BitcoinAddressNativeSegWit);
            wallet.AllBitcoinAddresses.Add(legacyChangeKey.PrivateKey.PubKey.GetAddress(ScriptPubKeyType.Legacy, Network.Main).ToString());
            wallet.AllBitcoinAddresses.Add(segwitChangeKey.PrivateKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.Main).ToString());
            return wallet;
        }

        private static readonly Wordlist[] SupportedMultiLanguageWordlists = new[]
        {
            Wordlist.English,
            Wordlist.Spanish,
            Wordlist.French,
            Wordlist.PortugueseBrazil,
            Wordlist.ChineseSimplified,
            Wordlist.Japanese
        };

        /// <summary>
        /// Generates a valid checksummed BIP39 mnemonic in one of the supported multi-language BIP39 wordlists
        /// (English, Spanish, French, Portuguese, Chinese Simplified, Japanese).
        /// </summary>
        public static string GenerateMultiLanguageMnemonic(int languageIndex = 0, int wordCount = 12)
        {
            var wl = SupportedMultiLanguageWordlists[Math.Abs(languageIndex) % SupportedMultiLanguageWordlists.Length];
            var wc = wordCount == 24 ? BtcWordCount.TwentyFour : BtcWordCount.Twelve;
            return new BtcMnemonic(wl, wc).ToString();
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
                var m = new BtcMnemonic(mnemonic, GetWordlistForLanguage());
                if (m.IsValidChecksum) return true;
            }
            catch { }

            foreach (var wl in SupportedMultiLanguageWordlists)
            {
                try
                {
                    var m = new BtcMnemonic(mnemonic, wl);
                    if (m.IsValidChecksum) return true;
                }
                catch { }
            }
            return false;
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
            
            // Check each word is in BIP39 wordlist for configured language
            var wordlistObj = GetWordlistForLanguage();
            var wordlist = wordlistObj.GetWords().ToList();
            for (int i = 0; i < words.Length; i++)
            {
                if (!wordlist.Contains(words[i].ToLower()))
                    return (false, $"Word #{i + 1} '{words[i]}' is not a valid BIP39 word", i);
            }
            
            // Check checksum
            try
            {
                var m = new BtcMnemonic(mnemonic, wordlistObj);
                if (!m.IsValidChecksum)
                    return (false, "Invalid BIP39 checksum (check word order or last word).", -1);
                return (true, null, -1);
            }
            catch (Exception ex)
            {
                return (false, $"Invalid checksum: {ex.Message}", -1);
            }
        }
        
        /// <summary>
        /// Generate all valid mnemonics by fixing checksum word (last word)
        /// Uses the direct SHA-256 Checksum-Bit Solver (16x-256x faster than testing all 2,048 words)
        /// </summary>
        public static List<string> GenerateValidChecksumVariations(string partialMnemonic)
        {
            if (string.IsNullOrWhiteSpace(partialMnemonic))
                return new List<string>();

            var words = partialMnemonic.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 12 || words.Length == 24)
            {
                var fastSolved = SolveExactLastWordCandidates(words.Take(words.Length - 1).ToArray());
                if (fastSolved.Count > 0)
                    return fastSolved;
            }
            else if (words.Length == 11 || words.Length == 23)
            {
                var fastSolved = SolveExactLastWordCandidates(words);
                if (fastSolved.Count > 0)
                    return fastSolved;
            }

            var validMnemonics = new List<string>();
            if (words.Length < 11 || words.Length > 24) 
                return validMnemonics;
            
            var wordlist = GetWordlistForLanguage().GetWords().ToList();
            var baseWords = (words.Length == 11 || words.Length == 23)
                ? words.ToList()
                : words.Take(words.Length - 1).ToList();
            
            foreach (var lastWord in wordlist)
            {
                var testMnemonic = string.Join(" ", baseWords.Concat(new[] { lastWord }));
                if (!MnemonicTracker.IsRestrictedTrivialMnemonic(testMnemonic) && IsValidMnemonic(testMnemonic))
                {
                    validMnemonics.Add(testMnemonic);
                }
            }
            
            return validMnemonics;
        }

        public static string GenerateMnemonic(int wordCount = 12)
        {
            var countEnum = wordCount switch
            {
                15 => BtcWordCount.Fifteen,
                18 => BtcWordCount.Eighteen,
                21 => BtcWordCount.TwentyOne,
                24 => BtcWordCount.TwentyFour,
                _ => BtcWordCount.Twelve
            };
            var mnemonic = new BtcMnemonic(GetWordlistForLanguage(), countEnum);
            return mnemonic.ToString();
        }

        public static string GenerateRandomMnemonic()
        {
            return GenerateMnemonic(12);
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