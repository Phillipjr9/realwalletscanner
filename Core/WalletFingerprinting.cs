using System;
using System.Collections.Generic;
using System.Linq;
using NBitcoin;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Identifies wallet software type from address patterns and generates addresses accordingly
    /// This MASSIVELY reduces search space by only checking relevant derivation paths
    /// </summary>
    public class WalletFingerprinting
    {
        public enum WalletType
        {
            Unknown,
            Electrum,           // m/0/0
            ElectrumSegWit,     // m/0'/0/0
            MetaMask,           // m/44'/60'/0'/0/0 (Ethereum)
            TrustWallet,        // m/44'/60'/0'/0/0 (multi-chain)
            Exodus,             // m/44'/0'/0'/0/0 (Bitcoin)
            Ledger,             // m/44'/0'/0'/0 (no hardened last)
            Trezor,             // m/44'/0'/0'/0/0
            Blockchain_com,     // m/44'/0'/account'/0/address_index
            MyCelium,           // m/44'/0'/0'/0/0
            Coinomi,            // m/44'/coin'/0'/0/0
            BitcoinCore,        // m/0'/0'/0' (legacy)
            BRD,                // m/0'/0 (simplified)
            Samourai,           // m/84'/0'/0' (native segwit)
            Wasabi,             // m/84'/0'/0' (coinjoin)
            BlueWallet,         // m/84'/0'/0'/0/0
            Atomic,             // m/44'/60'/0'/0/0
            Edge               // m/44'/0'/0'/0/0
        }

        public class WalletPattern
        {
            public WalletType Type { get; set; }
            public List<string> DerivationPaths { get; set; }
            public List<ScriptPubKeyType> AddressTypes { get; set; }
            public string Description { get; set; }
            private int _confidence;
            public int Confidence
            {
                get => _confidence;
                set { _confidence = value; ConfidenceScore = value; }
            }
            public int ConfidenceScore { get; set; } // Alias for Confidence
            private string _walletType;
            public string WalletType
            {
                get => !string.IsNullOrEmpty(_walletType) ? _walletType : (!string.IsNullOrEmpty(Description) ? Description : Type.ToString());
                set => _walletType = value;
            }
            private string _suggestedDerivationPath;
            public string SuggestedDerivationPath
            {
                get => !string.IsNullOrEmpty(_suggestedDerivationPath) ? _suggestedDerivationPath : (DerivationPaths?.FirstOrDefault() ?? "m/84'/0'/0'/0/0");
                set => _suggestedDerivationPath = value;
            }
        }

        /// <summary>
        /// Detects wallet software from a known address
        /// </summary>
        public static WalletPattern DetectWalletSoftware(string knownAddress, DateTime? createdDate = null)
        {
            var patterns = new List<WalletPattern>();

            // Analyze address format
            if (knownAddress.StartsWith("1"))
            {
                // Legacy P2PKH - could be many wallets
                patterns.Add(new WalletPattern
                {
                    Type = WalletType.Electrum,
                    DerivationPaths = new List<string> { "m/0/0", "m/0/1", "m/0/2" },
                    AddressTypes = new List<ScriptPubKeyType> { ScriptPubKeyType.Legacy },
                    Description = "Electrum Legacy",
                    Confidence = 40
                });

                patterns.Add(new WalletPattern
                {
                    Type = WalletType.Exodus,
                    DerivationPaths = new List<string> { "m/44'/0'/0'/0/0", "m/44'/0'/0'/0/1" },
                    AddressTypes = new List<ScriptPubKeyType> { ScriptPubKeyType.Legacy },
                    Description = "Exodus/Trezor/MyCelium",
                    Confidence = 50
                });

                patterns.Add(new WalletPattern
                {
                    Type = WalletType.Ledger,
                    DerivationPaths = new List<string> { "m/44'/0'/0'/0", "m/44'/0'/1'/0", "m/44'/0'/2'/0" },
                    AddressTypes = new List<ScriptPubKeyType> { ScriptPubKeyType.Legacy },
                    Description = "Ledger Legacy",
                    Confidence = 45
                });
            }
            else if (knownAddress.StartsWith("3"))
            {
                // P2SH (SegWit wrapped)
                patterns.Add(new WalletPattern
                {
                    Type = WalletType.Ledger,
                    DerivationPaths = new List<string> { "m/49'/0'/0'/0/0", "m/49'/0'/0'/0/1" },
                    AddressTypes = new List<ScriptPubKeyType> { ScriptPubKeyType.SegwitP2SH },
                    Description = "Ledger SegWit",
                    Confidence = 70
                });

                patterns.Add(new WalletPattern
                {
                    Type = WalletType.Trezor,
                    DerivationPaths = new List<string> { "m/49'/0'/0'/0/0", "m/49'/0'/0'/0/1" },
                    AddressTypes = new List<ScriptPubKeyType> { ScriptPubKeyType.SegwitP2SH },
                    Description = "Trezor SegWit",
                    Confidence = 70
                });
            }
            else if (knownAddress.StartsWith("bc1q"))
            {
                // Native SegWit (Bech32)
                patterns.Add(new WalletPattern
                {
                    Type = WalletType.Samourai,
                    DerivationPaths = new List<string> { "m/84'/0'/0'/0/0", "m/84'/0'/0'/0/1" },
                    AddressTypes = new List<ScriptPubKeyType> { ScriptPubKeyType.Segwit },
                    Description = "Samourai/Wasabi Native SegWit",
                    Confidence = 80
                });

                patterns.Add(new WalletPattern
                {
                    Type = WalletType.BlueWallet,
                    DerivationPaths = new List<string> { "m/84'/0'/0'/0/0", "m/84'/0'/0'/0/1" },
                    AddressTypes = new List<ScriptPubKeyType> { ScriptPubKeyType.Segwit },
                    Description = "BlueWallet Native SegWit",
                    Confidence = 80
                });

                patterns.Add(new WalletPattern
                {
                    Type = WalletType.Ledger,
                    DerivationPaths = new List<string> { "m/84'/0'/0'/0/0", "m/84'/0'/0'/0/1" },
                    AddressTypes = new List<ScriptPubKeyType> { ScriptPubKeyType.Segwit },
                    Description = "Ledger Native SegWit",
                    Confidence = 75
                });
            }
            else if (knownAddress.StartsWith("0x"))
            {
                // Ethereum address
                patterns.Add(new WalletPattern
                {
                    Type = WalletType.MetaMask,
                    DerivationPaths = new List<string> { "m/44'/60'/0'/0/0", "m/44'/60'/0'/0/1" },
                    AddressTypes = new List<ScriptPubKeyType>(),
                    Description = "MetaMask/TrustWallet Ethereum",
                    Confidence = 85
                });

                patterns.Add(new WalletPattern
                {
                    Type = WalletType.Ledger,
                    DerivationPaths = new List<string> { "m/44'/60'/0'/0", "m/44'/60'/1'/0" },
                    AddressTypes = new List<ScriptPubKeyType>(),
                    Description = "Ledger Ethereum",
                    Confidence = 60
                });
            }

            // Date-based heuristics
            if (createdDate.HasValue)
            {
                // Adjust confidence based on wallet release dates
                foreach (var pattern in patterns)
                {
                    if (createdDate.Value.Year < 2017 && pattern.Type == WalletType.Samourai)
                        pattern.Confidence = 0; // Samourai released 2017

                    if (createdDate.Value.Year < 2016 && pattern.Type == WalletType.Ledger)
                        pattern.Confidence = 20; // Less likely

                    if (createdDate.Value.Year >= 2020 && pattern.AddressTypes.Contains(ScriptPubKeyType.Segwit))
                        pattern.Confidence += 20; // SegWit became popular after 2020
                }
            }

            return patterns.OrderByDescending(p => p.Confidence).FirstOrDefault() ?? new WalletPattern
            {
                Type = WalletType.Unknown,
                DerivationPaths = GetCommonPaths(),
                AddressTypes = new List<ScriptPubKeyType> { ScriptPubKeyType.Legacy, ScriptPubKeyType.SegwitP2SH, ScriptPubKeyType.Segwit },
                Description = "Unknown - will try all common paths",
                Confidence = 0
            };
        }

        /// <summary>
        /// Generates addresses for specific wallet type
        /// </summary>
        public static Dictionary<string, string> GenerateByWalletType(string mnemonic, WalletType walletType, int addressCount = 10)
        {
            var addresses = new Dictionary<string, string>();
            var mnemonicObj = new Mnemonic(mnemonic);
            var masterKey = mnemonicObj.DeriveExtKey();

            var paths = GetDerivationPaths(walletType);

            foreach (var pathTemplate in paths)
            {
                for (int i = 0; i < addressCount; i++)
                {
                    try
                    {
                        var path = pathTemplate.Replace("{index}", i.ToString());
                        var key = masterKey.Derive(new KeyPath(path));
                        
                        // Determine address type from path
                        ScriptPubKeyType addressType = ScriptPubKeyType.Legacy;
                        if (path.Contains("49'")) addressType = ScriptPubKeyType.SegwitP2SH;
                        else if (path.Contains("84'")) addressType = ScriptPubKeyType.Segwit;

                        var address = key.PrivateKey.PubKey.GetAddress(addressType, Network.Main).ToString();
                        addresses[$"{walletType} [{path}]"] = address;
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Log($"Error generating address for {walletType} at {pathTemplate}: {ex.Message}");
                    }
                }
            }

            return addresses;
        }

        /// <summary>
        /// Gets derivation paths for specific wallet type
        /// </summary>
        private static List<string> GetDerivationPaths(WalletType walletType)
        {
            return walletType switch
            {
                WalletType.Electrum => new List<string> { "m/0/{index}" },
                WalletType.ElectrumSegWit => new List<string> { "m/0'/0/{index}" },
                WalletType.MetaMask => new List<string> { "m/44'/60'/0'/0/{index}" },
                WalletType.TrustWallet => new List<string> { "m/44'/60'/0'/0/{index}", "m/44'/0'/0'/0/{index}" },
                WalletType.Exodus => new List<string> { "m/44'/0'/0'/0/{index}" },
                WalletType.Ledger => new List<string> { "m/44'/0'/0'/{index}", "m/49'/0'/0'/0/{index}", "m/84'/0'/0'/0/{index}" },
                WalletType.Trezor => new List<string> { "m/44'/0'/0'/0/{index}", "m/49'/0'/0'/0/{index}", "m/84'/0'/0'/0/{index}" },
                WalletType.Blockchain_com => new List<string> { "m/44'/0'/0'/0/{index}" },
                WalletType.MyCelium => new List<string> { "m/44'/0'/0'/0/{index}" },
                WalletType.Coinomi => new List<string> { "m/44'/0'/0'/0/{index}", "m/44'/2'/0'/0/{index}" },
                WalletType.BitcoinCore => new List<string> { "m/0'/0'/{index}'" },
                WalletType.BRD => new List<string> { "m/0'/{index}" },
                WalletType.Samourai => new List<string> { "m/84'/0'/0'/0/{index}" },
                WalletType.Wasabi => new List<string> { "m/84'/0'/0'/0/{index}" },
                WalletType.BlueWallet => new List<string> { "m/84'/0'/0'/0/{index}" },
                WalletType.Atomic => new List<string> { "m/44'/60'/0'/0/{index}" },
                WalletType.Edge => new List<string> { "m/44'/0'/0'/0/{index}" },
                _ => GetCommonPaths()
            };
        }

        /// <summary>
        /// Gets most common derivation paths (fallback)
        /// </summary>
        private static List<string> GetCommonPaths()
        {
            return new List<string>
            {
                "m/44'/0'/0'/0/{index}",   // BIP44 Legacy (most common)
                "m/49'/0'/0'/0/{index}",   // BIP49 SegWit
                "m/84'/0'/0'/0/{index}",   // BIP84 Native SegWit
                "m/0/{index}",              // Electrum Legacy
                "m/44'/60'/0'/0/{index}"   // Ethereum
            };
        }

        /// <summary>
        /// Generate addresses for ALL wallet types (comprehensive scan)
        /// </summary>
        public static Dictionary<string, string> GenerateAllWalletTypes(string mnemonic, int addressCount = 5)
        {
            var allAddresses = new Dictionary<string, string>();

            foreach (WalletType walletType in Enum.GetValues(typeof(WalletType)))
            {
                if (walletType == WalletType.Unknown) continue;

                var addresses = GenerateByWalletType(mnemonic, walletType, addressCount);
                foreach (var kvp in addresses)
                {
                    allAddresses[kvp.Key] = kvp.Value;
                }
            }

            return allAddresses;
        }
    }
}
