using System;
using System.Collections.Generic;
using System.Linq;
using NBitcoin;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Custom derivation path support for different wallet software
    /// Supports Ledger, Trezor, Electrum, and custom paths
    /// </summary>
    public static class CustomDerivationPaths
    {
        /// <summary>
        /// Well-known derivation paths for different wallets/coins
        /// </summary>
        public static class KnownPaths
        {
            // ═══════════════════════════════════════════
            //  BIP44 - Legacy (P2PKH) - "1..." addresses
            // ═══════════════════════════════════════════
            public const string BIP44_Bitcoin = "m/44'/0'/0'/0/0";
            public const string BIP44_Ethereum = "m/44'/60'/0'/0/0";
            public const string BIP44_Litecoin = "m/44'/2'/0'/0/0";
            public const string BIP44_Dogecoin = "m/44'/3'/0'/0/0";
            public const string BIP44_Bitcoin_Cash = "m/44'/145'/0'/0/0";
            public const string BIP44_Dash = "m/44'/5'/0'/0/0";
            public const string BIP44_Zcash = "m/44'/133'/0'/0/0";
            public const string BIP44_Bitcoin_Gold = "m/44'/156'/0'/0/0";
            public const string BIP44_DigiByte = "m/44'/20'/0'/0/0";
            public const string BIP44_Ravencoin = "m/44'/175'/0'/0/0";
            public const string BIP44_Vertcoin = "m/44'/28'/0'/0/0";
            public const string BIP44_Groestlcoin = "m/44'/17'/0'/0/0";

            // ═══════════════════════════════════════════
            //  BIP44 - EVM Chains (same key, different networks)
            // ═══════════════════════════════════════════
            public const string BIP44_Polygon = "m/44'/60'/0'/0/0";
            public const string BIP44_BSC = "m/44'/60'/0'/0/0";
            public const string BIP44_Avalanche = "m/44'/60'/0'/0/0";
            public const string BIP44_Fantom = "m/44'/60'/0'/0/0";
            public const string BIP44_Arbitrum = "m/44'/60'/0'/0/0";
            public const string BIP44_Optimism = "m/44'/60'/0'/0/0";
            public const string BIP44_Cronos = "m/44'/60'/0'/0/0";
            public const string BIP44_Harmony = "m/44'/60'/0'/0/0";

            // ═══════════════════════════════════════════
            //  BIP44 - Non-EVM Alt Chains
            // ═══════════════════════════════════════════
            public const string BIP44_Ripple = "m/44'/144'/0'/0/0";
            public const string BIP44_Stellar = "m/44'/148'/0'";
            public const string BIP44_Tron = "m/44'/195'/0'/0/0";
            public const string BIP44_Cosmos = "m/44'/118'/0'/0/0";
            public const string BIP44_Tezos = "m/44'/1729'/0'/0'";
            public const string BIP44_Cardano_Icarus = "m/44'/1815'/0'/0/0";
            public const string BIP44_Cardano_Shelley = "m/1852'/1815'/0'/0/0";
            public const string BIP44_Polkadot = "m/44'/354'/0'/0'/0'";
            public const string BIP44_Kusama = "m/44'/434'/0'/0'/0'";
            public const string BIP44_Near = "m/44'/397'/0'";
            public const string BIP44_Algorand = "m/44'/283'/0'/0'/0'";
            public const string BIP44_Monero = "m/44'/128'/0'/0/0";
            public const string BIP44_EOS = "m/44'/194'/0'/0/0";
            public const string BIP44_Neo = "m/44'/888'/0'/0/0";
            public const string BIP44_Vechain = "m/44'/818'/0'/0/0";
            public const string BIP44_Theta = "m/44'/500'/0'/0/0";
            public const string BIP44_Zilliqa = "m/44'/313'/0'/0/0";
            public const string BIP44_Ontology = "m/44'/1024'/0'/0/0";
            public const string BIP44_IOTA = "m/44'/4218'/0'/0'/0'";
            public const string BIP44_Hedera = "m/44'/3030'/0'/0'/0'";
            public const string BIP44_Filecoin = "m/44'/461'/0'/0/0";
            public const string BIP44_Aptos = "m/44'/637'/0'/0'/0'";
            public const string BIP44_Sui = "m/44'/784'/0'/0'/0'";

            // ═══════════════════════════════════════════
            //  BIP49 - SegWit (P2SH-P2WPKH) - "3..." addresses
            // ═══════════════════════════════════════════
            public const string BIP49_Bitcoin = "m/49'/0'/0'/0/0";
            public const string BIP49_Litecoin = "m/49'/2'/0'/0/0";
            public const string BIP49_Bitcoin_Cash = "m/49'/145'/0'/0/0";
            public const string BIP49_Dash = "m/49'/5'/0'/0/0";
            public const string BIP49_Groestlcoin = "m/49'/17'/0'/0/0";

            // ═══════════════════════════════════════════
            //  BIP84 - Native SegWit (P2WPKH) - "bc1q..." addresses
            // ═══════════════════════════════════════════
            public const string BIP84_Bitcoin = "m/84'/0'/0'/0/0";
            public const string BIP84_Litecoin = "m/84'/2'/0'/0/0";
            public const string BIP84_Groestlcoin = "m/84'/17'/0'/0/0";

            // ═══════════════════════════════════════════
            //  BIP86 - Taproot (P2TR) - "bc1p..." addresses
            // ═══════════════════════════════════════════
            public const string BIP86_Bitcoin = "m/86'/0'/0'/0/0";

            // ═══════════════════════════════════════════
            //  Solana paths
            // ═══════════════════════════════════════════
            public const string Solana_Default = "m/44'/501'/0'/0'";
            public const string Solana_Ledger = "m/44'/501'/0'";
            public const string Solana_SolFlare = "m/44'/501'/0'/0'";
            public const string Phantom_Solana = "m/44'/501'/0'/0'";

            // ═══════════════════════════════════════════
            //  Hardware Wallets
            // ═══════════════════════════════════════════
            // Ledger Live
            public const string Ledger_Bitcoin_Legacy = "m/44'/0'/0'/0/0";
            public const string Ledger_Bitcoin_SegWit = "m/49'/0'/0'/0/0";
            public const string Ledger_Bitcoin_NativeSegWit = "m/84'/0'/0'/0/0";
            public const string Ledger_Bitcoin_Taproot = "m/86'/0'/0'/0/0";
            public const string Ledger_Ethereum = "m/44'/60'/0'/0/0";
            public const string Ledger_Ethereum_Legacy = "m/44'/60'/0'/0";   // Ledger legacy ETH (no trailing /0)
            public const string Ledger_Litecoin = "m/84'/2'/0'/0/0";
            public const string Ledger_Dogecoin = "m/44'/3'/0'/0/0";
            public const string Ledger_Ripple = "m/44'/144'/0'/0/0";
            public const string Ledger_Solana = "m/44'/501'/0'";
            public const string Ledger_Cardano = "m/1852'/1815'/0'/0/0";
            public const string Ledger_Polkadot = "m/44'/354'/0'/0'/0'";
            public const string Ledger_Cosmos = "m/44'/118'/0'/0/0";
            public const string Ledger_Tezos = "m/44'/1729'/0'/0'";
            public const string Ledger_Tron = "m/44'/195'/0'/0/0";

            // Trezor
            public const string Trezor_Bitcoin_Legacy = "m/44'/0'/0'/0/0";
            public const string Trezor_Bitcoin_SegWit = "m/49'/0'/0'/0/0";
            public const string Trezor_Bitcoin_NativeSegWit = "m/84'/0'/0'/0/0";
            public const string Trezor_Bitcoin_Taproot = "m/86'/0'/0'/0/0";
            public const string Trezor_Ethereum = "m/44'/60'/0'/0/0";
            public const string Trezor_Litecoin = "m/84'/2'/0'/0/0";
            public const string Trezor_Cardano = "m/1852'/1815'/0'/0/0";
            public const string Trezor_Ripple = "m/44'/144'/0'/0/0";

            // KeepKey
            public const string KeepKey_Bitcoin = "m/44'/0'/0'/0/0";
            public const string KeepKey_Ethereum = "m/44'/60'/0'/0/0";
            public const string KeepKey_Litecoin = "m/44'/2'/0'/0/0";
            public const string KeepKey_Bitcoin_Cash = "m/44'/145'/0'/0/0";
            public const string KeepKey_Dogecoin = "m/44'/3'/0'/0/0";
            public const string KeepKey_Dash = "m/44'/5'/0'/0/0";

            // ═══════════════════════════════════════════
            //  Software Wallets
            // ═══════════════════════════════════════════
            // Electrum
            public const string Electrum_Standard = "m/0/0";
            public const string Electrum_SegWit = "m/0'/0/0";
            public const string Electrum_NativeSegWit = "m/84'/0'/0'/0/0";

            // MetaMask / Web3
            public const string MetaMask_Default = "m/44'/60'/0'/0/0";

            // Trust Wallet
            public const string TrustWallet_Bitcoin = "m/84'/0'/0'/0/0";
            public const string TrustWallet_Ethereum = "m/44'/60'/0'/0/0";
            public const string TrustWallet_BNB = "m/44'/714'/0'/0/0";
            public const string TrustWallet_Cosmos = "m/44'/118'/0'/0/0";
            public const string TrustWallet_Tron = "m/44'/195'/0'/0/0";

            // Exodus
            public const string Exodus_Bitcoin = "m/44'/0'/0'/0/0";
            public const string Exodus_Bitcoin_SegWit = "m/84'/0'/0'/0/0";
            public const string Exodus_Ethereum = "m/44'/60'/0'/0/0";
            public const string Exodus_Solana = "m/44'/501'/0'/0'";
            public const string Exodus_Cardano = "m/1852'/1815'/0'/0/0";

            // Coinbase Wallet
            public const string Coinbase_Ethereum = "m/44'/60'/0'/0/0";
            public const string Coinbase_Bitcoin = "m/44'/0'/0'/0/0";
            public const string Coinbase_Solana = "m/44'/501'/0'/0'";

            // Blockchain.com
            public const string BlockchainCom_Legacy = "m/44'/0'/0'/0/0";
            public const string BlockchainCom_SegWit = "m/84'/0'/0'/0/0";
            public const string BlockchainCom_Ethereum = "m/44'/60'/0'/0/0";

            // Mycelium
            public const string Mycelium_Bitcoin = "m/44'/0'/0'/0/0";
            public const string Mycelium_Bitcoin_SegWit = "m/49'/0'/0'/0/0";

            // Samourai Wallet
            public const string Samourai_Deposit = "m/44'/0'/0'/0/0";
            public const string Samourai_SegWit = "m/49'/0'/0'/0/0";
            public const string Samourai_NativeSegWit = "m/84'/0'/0'/0/0";
            public const string Samourai_Whirlpool = "m/84'/0'/2147483646'/0/0"; // Premix account
            public const string Samourai_BadBank = "m/84'/0'/2147483645'/0/0";

            // Wasabi Wallet
            public const string Wasabi_Default = "m/84'/0'/0'/0/0";

            // Blue Wallet
            public const string BlueWallet_Legacy = "m/44'/0'/0'/0/0";
            public const string BlueWallet_SegWit = "m/49'/0'/0'/0/0";
            public const string BlueWallet_NativeSegWit = "m/84'/0'/0'/0/0";

            // Atomic Wallet
            public const string Atomic_Bitcoin = "m/44'/0'/0'/0/0";
            public const string Atomic_Ethereum = "m/44'/60'/0'/0/0";
            public const string Atomic_Litecoin = "m/44'/2'/0'/0/0";
            public const string Atomic_Ripple = "m/44'/144'/0'/0/0";

            // Guarda Wallet
            public const string Guarda_Bitcoin = "m/44'/0'/0'/0/0";
            public const string Guarda_Ethereum = "m/44'/60'/0'/0/0";

            // Edge Wallet (formerly Airbitz)
            public const string Edge_Bitcoin = "m/44'/0'/0'/0/0";
            public const string Edge_Bitcoin_SegWit = "m/49'/0'/0'/0/0";
            public const string Edge_Ethereum = "m/44'/60'/0'/0/0";

            // Coinomi
            public const string Coinomi_Bitcoin = "m/44'/0'/0'/0/0";
            public const string Coinomi_SegWit = "m/49'/0'/0'/0/0";
            public const string Coinomi_NativeSegWit = "m/84'/0'/0'/0/0";
            public const string Coinomi_Ethereum = "m/44'/60'/0'/0/0";
            public const string Coinomi_Litecoin = "m/44'/2'/0'/0/0";

            // ═══════════════════════════════════════════
            //  Non-standard / Historical paths
            // ═══════════════════════════════════════════
            public const string BitcoinCore_Default = "m/0'/0'/0'"; // Bitcoin Core HD
            public const string MultiBit_HD = "m/0'/0"; // MultiBit HD (discontinued)
            public const string Copay_Multisig = "m/48'/0'/0'/0/0"; // BIP48 multisig
            public const string BIP45_Copay = "m/45'/0/0/0"; // Old Copay multisig
            public const string Breadwallet_Default = "m/0'/0"; // BRD / Bread Wallet
            public const string Jaxx_Ethereum = "m/44'/60'/0'/0"; // Jaxx (no trailing index)
            public const string MEW_Default = "m/44'/60'/0'/0/0"; // MyEtherWallet
            public const string Blockchain_Legacy = "m/44'/0'/0'"; // Old Blockchain.com
        }

        /// <summary>
        /// Wallet software presets
        /// </summary>
        public enum WalletPreset
        {
            Default,
            LedgerLive,
            Trezor,
            KeepKey,
            Electrum,
            MetaMask,
            TrustWallet,
            Exodus,
            Coinbase,
            Phantom,
            BlockchainCom,
            Mycelium,
            Samourai,
            Wasabi,
            BlueWallet,
            Atomic,
            Guarda,
            Edge,
            Coinomi,
            AllPaths,
            Custom
        }

        /// <summary>
        /// Get all derivation paths for a wallet preset
        /// </summary>
        public static List<DerivationPathInfo> GetPathsForPreset(WalletPreset preset)
        {
            return preset switch
            {
                WalletPreset.LedgerLive => new List<DerivationPathInfo>
                {
                    new("Ledger BTC Legacy", KnownPaths.Ledger_Bitcoin_Legacy, "BTC", ScriptPubKeyType.Legacy),
                    new("Ledger BTC SegWit", KnownPaths.Ledger_Bitcoin_SegWit, "BTC", ScriptPubKeyType.SegwitP2SH),
                    new("Ledger BTC Native SegWit", KnownPaths.Ledger_Bitcoin_NativeSegWit, "BTC", ScriptPubKeyType.Segwit),
                    new("Ledger BTC Taproot", KnownPaths.Ledger_Bitcoin_Taproot, "BTC", null),
                    new("Ledger ETH", KnownPaths.Ledger_Ethereum, "ETH", null),
                    new("Ledger ETH Legacy", KnownPaths.Ledger_Ethereum_Legacy, "ETH", null),
                    new("Ledger LTC", KnownPaths.Ledger_Litecoin, "LTC", ScriptPubKeyType.Segwit),
                    new("Ledger DOGE", KnownPaths.Ledger_Dogecoin, "DOGE", ScriptPubKeyType.Legacy),
                    new("Ledger XRP", KnownPaths.Ledger_Ripple, "XRP", null),
                    new("Ledger SOL", KnownPaths.Ledger_Solana, "SOL", null),
                    new("Ledger ADA", KnownPaths.Ledger_Cardano, "ADA", null),
                    new("Ledger DOT", KnownPaths.Ledger_Polkadot, "DOT", null),
                    new("Ledger ATOM", KnownPaths.Ledger_Cosmos, "ATOM", null),
                    new("Ledger XTZ", KnownPaths.Ledger_Tezos, "XTZ", null),
                    new("Ledger TRX", KnownPaths.Ledger_Tron, "TRX", null),
                },
                WalletPreset.Trezor => new List<DerivationPathInfo>
                {
                    new("Trezor BTC Legacy", KnownPaths.Trezor_Bitcoin_Legacy, "BTC", ScriptPubKeyType.Legacy),
                    new("Trezor BTC SegWit", KnownPaths.Trezor_Bitcoin_SegWit, "BTC", ScriptPubKeyType.SegwitP2SH),
                    new("Trezor BTC Native SegWit", KnownPaths.Trezor_Bitcoin_NativeSegWit, "BTC", ScriptPubKeyType.Segwit),
                    new("Trezor BTC Taproot", KnownPaths.Trezor_Bitcoin_Taproot, "BTC", null),
                    new("Trezor ETH", KnownPaths.Trezor_Ethereum, "ETH", null),
                    new("Trezor LTC", KnownPaths.Trezor_Litecoin, "LTC", ScriptPubKeyType.Segwit),
                    new("Trezor ADA", KnownPaths.Trezor_Cardano, "ADA", null),
                    new("Trezor XRP", KnownPaths.Trezor_Ripple, "XRP", null),
                },
                WalletPreset.KeepKey => new List<DerivationPathInfo>
                {
                    new("KeepKey BTC", KnownPaths.KeepKey_Bitcoin, "BTC", ScriptPubKeyType.Legacy),
                    new("KeepKey ETH", KnownPaths.KeepKey_Ethereum, "ETH", null),
                    new("KeepKey LTC", KnownPaths.KeepKey_Litecoin, "LTC", ScriptPubKeyType.Legacy),
                    new("KeepKey BCH", KnownPaths.KeepKey_Bitcoin_Cash, "BCH", ScriptPubKeyType.Legacy),
                    new("KeepKey DOGE", KnownPaths.KeepKey_Dogecoin, "DOGE", ScriptPubKeyType.Legacy),
                    new("KeepKey DASH", KnownPaths.KeepKey_Dash, "DASH", ScriptPubKeyType.Legacy),
                },
                WalletPreset.Electrum => new List<DerivationPathInfo>
                {
                    new("Electrum Standard", KnownPaths.Electrum_Standard, "BTC", ScriptPubKeyType.Legacy),
                    new("Electrum SegWit", KnownPaths.Electrum_SegWit, "BTC", ScriptPubKeyType.Segwit),
                    new("Electrum Native SegWit", KnownPaths.Electrum_NativeSegWit, "BTC", ScriptPubKeyType.Segwit),
                },
                WalletPreset.MetaMask => new List<DerivationPathInfo>
                {
                    new("MetaMask ETH", KnownPaths.MetaMask_Default, "ETH", null),
                },
                WalletPreset.TrustWallet => new List<DerivationPathInfo>
                {
                    new("Trust Wallet BTC", KnownPaths.TrustWallet_Bitcoin, "BTC", ScriptPubKeyType.Segwit),
                    new("Trust Wallet ETH", KnownPaths.TrustWallet_Ethereum, "ETH", null),
                    new("Trust Wallet BNB", KnownPaths.TrustWallet_BNB, "BNB", null),
                    new("Trust Wallet ATOM", KnownPaths.TrustWallet_Cosmos, "ATOM", null),
                    new("Trust Wallet TRX", KnownPaths.TrustWallet_Tron, "TRX", null),
                },
                WalletPreset.Exodus => new List<DerivationPathInfo>
                {
                    new("Exodus BTC Legacy", KnownPaths.Exodus_Bitcoin, "BTC", ScriptPubKeyType.Legacy),
                    new("Exodus BTC SegWit", KnownPaths.Exodus_Bitcoin_SegWit, "BTC", ScriptPubKeyType.Segwit),
                    new("Exodus ETH", KnownPaths.Exodus_Ethereum, "ETH", null),
                    new("Exodus SOL", KnownPaths.Exodus_Solana, "SOL", null),
                    new("Exodus ADA", KnownPaths.Exodus_Cardano, "ADA", null),
                },
                WalletPreset.Coinbase => new List<DerivationPathInfo>
                {
                    new("Coinbase ETH", KnownPaths.Coinbase_Ethereum, "ETH", null),
                    new("Coinbase BTC", KnownPaths.Coinbase_Bitcoin, "BTC", ScriptPubKeyType.Legacy),
                    new("Coinbase SOL", KnownPaths.Coinbase_Solana, "SOL", null),
                },
                WalletPreset.Phantom => new List<DerivationPathInfo>
                {
                    new("Phantom SOL", KnownPaths.Phantom_Solana, "SOL", null),
                },
                WalletPreset.BlockchainCom => new List<DerivationPathInfo>
                {
                    new("Blockchain.com Legacy", KnownPaths.BlockchainCom_Legacy, "BTC", ScriptPubKeyType.Legacy),
                    new("Blockchain.com SegWit", KnownPaths.BlockchainCom_SegWit, "BTC", ScriptPubKeyType.Segwit),
                    new("Blockchain.com ETH", KnownPaths.BlockchainCom_Ethereum, "ETH", null),
                },
                WalletPreset.Mycelium => new List<DerivationPathInfo>
                {
                    new("Mycelium BTC", KnownPaths.Mycelium_Bitcoin, "BTC", ScriptPubKeyType.Legacy),
                    new("Mycelium BTC SegWit", KnownPaths.Mycelium_Bitcoin_SegWit, "BTC", ScriptPubKeyType.SegwitP2SH),
                },
                WalletPreset.Samourai => new List<DerivationPathInfo>
                {
                    new("Samourai Deposit", KnownPaths.Samourai_Deposit, "BTC", ScriptPubKeyType.Legacy),
                    new("Samourai SegWit", KnownPaths.Samourai_SegWit, "BTC", ScriptPubKeyType.SegwitP2SH),
                    new("Samourai Native SegWit", KnownPaths.Samourai_NativeSegWit, "BTC", ScriptPubKeyType.Segwit),
                    new("Samourai Whirlpool", KnownPaths.Samourai_Whirlpool, "BTC", ScriptPubKeyType.Segwit),
                    new("Samourai Bad Bank", KnownPaths.Samourai_BadBank, "BTC", ScriptPubKeyType.Segwit),
                },
                WalletPreset.Wasabi => new List<DerivationPathInfo>
                {
                    new("Wasabi Default", KnownPaths.Wasabi_Default, "BTC", ScriptPubKeyType.Segwit),
                },
                WalletPreset.BlueWallet => new List<DerivationPathInfo>
                {
                    new("BlueWallet Legacy", KnownPaths.BlueWallet_Legacy, "BTC", ScriptPubKeyType.Legacy),
                    new("BlueWallet SegWit", KnownPaths.BlueWallet_SegWit, "BTC", ScriptPubKeyType.SegwitP2SH),
                    new("BlueWallet Native SegWit", KnownPaths.BlueWallet_NativeSegWit, "BTC", ScriptPubKeyType.Segwit),
                },
                WalletPreset.Atomic => new List<DerivationPathInfo>
                {
                    new("Atomic BTC", KnownPaths.Atomic_Bitcoin, "BTC", ScriptPubKeyType.Legacy),
                    new("Atomic ETH", KnownPaths.Atomic_Ethereum, "ETH", null),
                    new("Atomic LTC", KnownPaths.Atomic_Litecoin, "LTC", ScriptPubKeyType.Legacy),
                    new("Atomic XRP", KnownPaths.Atomic_Ripple, "XRP", null),
                },
                WalletPreset.Guarda => new List<DerivationPathInfo>
                {
                    new("Guarda BTC", KnownPaths.Guarda_Bitcoin, "BTC", ScriptPubKeyType.Legacy),
                    new("Guarda ETH", KnownPaths.Guarda_Ethereum, "ETH", null),
                },
                WalletPreset.Edge => new List<DerivationPathInfo>
                {
                    new("Edge BTC", KnownPaths.Edge_Bitcoin, "BTC", ScriptPubKeyType.Legacy),
                    new("Edge BTC SegWit", KnownPaths.Edge_Bitcoin_SegWit, "BTC", ScriptPubKeyType.SegwitP2SH),
                    new("Edge ETH", KnownPaths.Edge_Ethereum, "ETH", null),
                },
                WalletPreset.Coinomi => new List<DerivationPathInfo>
                {
                    new("Coinomi BTC", KnownPaths.Coinomi_Bitcoin, "BTC", ScriptPubKeyType.Legacy),
                    new("Coinomi BTC SegWit", KnownPaths.Coinomi_SegWit, "BTC", ScriptPubKeyType.SegwitP2SH),
                    new("Coinomi BTC Native SegWit", KnownPaths.Coinomi_NativeSegWit, "BTC", ScriptPubKeyType.Segwit),
                    new("Coinomi ETH", KnownPaths.Coinomi_Ethereum, "ETH", null),
                    new("Coinomi LTC", KnownPaths.Coinomi_Litecoin, "LTC", ScriptPubKeyType.Legacy),
                },
                WalletPreset.AllPaths => GetAllPaths(),
                _ => GetDefaultPaths()
            };
        }

        /// <summary>
        /// Get default derivation paths
        /// </summary>
        public static List<DerivationPathInfo> GetDefaultPaths()
        {
            return new List<DerivationPathInfo>
            {
                new("BIP44 Bitcoin", KnownPaths.BIP44_Bitcoin, "BTC", ScriptPubKeyType.Legacy),
                new("BIP49 Bitcoin", KnownPaths.BIP49_Bitcoin, "BTC", ScriptPubKeyType.SegwitP2SH),
                new("BIP84 Bitcoin", KnownPaths.BIP84_Bitcoin, "BTC", ScriptPubKeyType.Segwit),
                new("BIP86 Bitcoin Taproot", KnownPaths.BIP86_Bitcoin, "BTC", null),
                new("BIP44 Ethereum", KnownPaths.BIP44_Ethereum, "ETH", null),
                new("BIP44 Litecoin", KnownPaths.BIP44_Litecoin, "LTC", ScriptPubKeyType.Legacy),
                new("BIP84 Litecoin", KnownPaths.BIP84_Litecoin, "LTC", ScriptPubKeyType.Segwit),
                new("BIP44 Dogecoin", KnownPaths.BIP44_Dogecoin, "DOGE", ScriptPubKeyType.Legacy),
                new("BIP44 Bitcoin Cash", KnownPaths.BIP44_Bitcoin_Cash, "BCH", ScriptPubKeyType.Legacy),
                new("Solana Default", KnownPaths.Solana_Default, "SOL", null),
                new("BIP44 Ripple", KnownPaths.BIP44_Ripple, "XRP", null),
                new("BIP44 Tron", KnownPaths.BIP44_Tron, "TRX", null),
                new("Cardano Shelley", KnownPaths.BIP44_Cardano_Shelley, "ADA", null),
            };
        }

        /// <summary>
        /// Get ALL known derivation paths (exhaustive scan)
        /// </summary>
        public static List<DerivationPathInfo> GetAllPaths()
        {
            return new List<DerivationPathInfo>
            {
                // Bitcoin - all address types
                new("BIP44 BTC Legacy", KnownPaths.BIP44_Bitcoin, "BTC", ScriptPubKeyType.Legacy),
                new("BIP49 BTC SegWit", KnownPaths.BIP49_Bitcoin, "BTC", ScriptPubKeyType.SegwitP2SH),
                new("BIP84 BTC Native SegWit", KnownPaths.BIP84_Bitcoin, "BTC", ScriptPubKeyType.Segwit),
                new("BIP86 BTC Taproot", KnownPaths.BIP86_Bitcoin, "BTC", null),
                new("Electrum Standard", KnownPaths.Electrum_Standard, "BTC", ScriptPubKeyType.Legacy),
                new("Electrum SegWit", KnownPaths.Electrum_SegWit, "BTC", ScriptPubKeyType.Segwit),
                new("Bitcoin Core HD", KnownPaths.BitcoinCore_Default, "BTC", ScriptPubKeyType.Legacy),
                new("MultiBit HD", KnownPaths.MultiBit_HD, "BTC", ScriptPubKeyType.Legacy),
                new("Breadwallet", KnownPaths.Breadwallet_Default, "BTC", ScriptPubKeyType.Legacy),
                new("Copay Multisig", KnownPaths.Copay_Multisig, "BTC", ScriptPubKeyType.Legacy),
                new("BIP45 Copay", KnownPaths.BIP45_Copay, "BTC", ScriptPubKeyType.Legacy),
                new("Blockchain.com Legacy", KnownPaths.Blockchain_Legacy, "BTC", ScriptPubKeyType.Legacy),
                new("Samourai Whirlpool", KnownPaths.Samourai_Whirlpool, "BTC", ScriptPubKeyType.Segwit),
                new("Samourai Bad Bank", KnownPaths.Samourai_BadBank, "BTC", ScriptPubKeyType.Segwit),
                // Ethereum + EVM
                new("BIP44 ETH", KnownPaths.BIP44_Ethereum, "ETH", null),
                new("Ledger ETH Legacy", KnownPaths.Ledger_Ethereum_Legacy, "ETH", null),
                new("Jaxx ETH", KnownPaths.Jaxx_Ethereum, "ETH", null),
                // Other coins
                new("BIP44 Litecoin", KnownPaths.BIP44_Litecoin, "LTC", ScriptPubKeyType.Legacy),
                new("BIP49 Litecoin", KnownPaths.BIP49_Litecoin, "LTC", ScriptPubKeyType.SegwitP2SH),
                new("BIP84 Litecoin", KnownPaths.BIP84_Litecoin, "LTC", ScriptPubKeyType.Segwit),
                new("BIP44 Dogecoin", KnownPaths.BIP44_Dogecoin, "DOGE", ScriptPubKeyType.Legacy),
                new("BIP44 Bitcoin Cash", KnownPaths.BIP44_Bitcoin_Cash, "BCH", ScriptPubKeyType.Legacy),
                new("BIP44 Dash", KnownPaths.BIP44_Dash, "DASH", ScriptPubKeyType.Legacy),
                new("BIP44 Zcash", KnownPaths.BIP44_Zcash, "ZEC", ScriptPubKeyType.Legacy),
                new("BIP44 Bitcoin Gold", KnownPaths.BIP44_Bitcoin_Gold, "BTG", ScriptPubKeyType.Legacy),
                new("BIP44 DigiByte", KnownPaths.BIP44_DigiByte, "DGB", ScriptPubKeyType.Legacy),
                new("BIP44 Ravencoin", KnownPaths.BIP44_Ravencoin, "RVN", ScriptPubKeyType.Legacy),
                new("BIP44 Vertcoin", KnownPaths.BIP44_Vertcoin, "VTC", ScriptPubKeyType.Legacy),
                new("BIP44 Groestlcoin", KnownPaths.BIP44_Groestlcoin, "GRS", ScriptPubKeyType.Legacy),
                new("BIP44 Ripple", KnownPaths.BIP44_Ripple, "XRP", null),
                new("BIP44 Stellar", KnownPaths.BIP44_Stellar, "XLM", null),
                new("BIP44 Tron", KnownPaths.BIP44_Tron, "TRX", null),
                new("BIP44 Cosmos", KnownPaths.BIP44_Cosmos, "ATOM", null),
                new("BIP44 Tezos", KnownPaths.BIP44_Tezos, "XTZ", null),
                new("Cardano Icarus", KnownPaths.BIP44_Cardano_Icarus, "ADA", null),
                new("Cardano Shelley", KnownPaths.BIP44_Cardano_Shelley, "ADA", null),
                new("BIP44 Polkadot", KnownPaths.BIP44_Polkadot, "DOT", null),
                new("BIP44 Kusama", KnownPaths.BIP44_Kusama, "KSM", null),
                new("BIP44 Near", KnownPaths.BIP44_Near, "NEAR", null),
                new("BIP44 Algorand", KnownPaths.BIP44_Algorand, "ALGO", null),
                new("BIP44 Monero", KnownPaths.BIP44_Monero, "XMR", null),
                new("BIP44 EOS", KnownPaths.BIP44_EOS, "EOS", null),
                new("BIP44 Neo", KnownPaths.BIP44_Neo, "NEO", null),
                new("BIP44 Vechain", KnownPaths.BIP44_Vechain, "VET", null),
                new("BIP44 Theta", KnownPaths.BIP44_Theta, "THETA", null),
                new("BIP44 Zilliqa", KnownPaths.BIP44_Zilliqa, "ZIL", null),
                new("BIP44 Ontology", KnownPaths.BIP44_Ontology, "ONT", null),
                new("BIP44 IOTA", KnownPaths.BIP44_IOTA, "IOTA", null),
                new("BIP44 Hedera", KnownPaths.BIP44_Hedera, "HBAR", null),
                new("BIP44 Filecoin", KnownPaths.BIP44_Filecoin, "FIL", null),
                new("BIP44 Aptos", KnownPaths.BIP44_Aptos, "APT", null),
                new("BIP44 Sui", KnownPaths.BIP44_Sui, "SUI", null),
                // Solana
                new("Solana Default", KnownPaths.Solana_Default, "SOL", null),
                new("Solana Ledger", KnownPaths.Solana_Ledger, "SOL", null),
                // BNB (native chain)
                new("Trust Wallet BNB", KnownPaths.TrustWallet_BNB, "BNB", null),
            };
        }

        /// <summary>
        /// Parse a derivation path string
        /// </summary>
        public static KeyPath ParsePath(string path)
        {
            return new KeyPath(path);
        }

        /// <summary>
        /// Validate a derivation path string
        /// </summary>
        public static bool IsValidPath(string path)
        {
            try
            {
                new KeyPath(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Generate addresses for multiple paths from a mnemonic
        /// </summary>
        public static List<DerivedAddress> DeriveAddresses(string mnemonic, List<DerivationPathInfo> paths)
        {
            var results = new List<DerivedAddress>();
            var mnemonicObj = new Mnemonic(mnemonic);
            var masterKey = mnemonicObj.DeriveExtKey();

            foreach (var pathInfo in paths)
            {
                try
                {
                    var keyPath = new KeyPath(pathInfo.Path);
                    var derivedKey = masterKey.Derive(keyPath);
                    
                    string address;
                    if (pathInfo.Coin == "ETH")
                    {
                        var ethWallet = new Nethereum.HdWallet.Wallet(mnemonic, "");
                        address = ethWallet.GetAccount(0).Address;
                    }
                    else if (pathInfo.Coin == "SOL")
                    {
                        var solWallet = new Solnet.Wallet.Wallet(mnemonic);
                        address = solWallet.Account.PublicKey.Key;
                    }
                    else if (pathInfo.ScriptType != null)
                    {
                        address = derivedKey.PrivateKey.PubKey
                            .GetAddress(pathInfo.ScriptType.Value, Network.Main)
                            .ToString();
                    }
                    else
                    {
                        address = derivedKey.PrivateKey.PubKey
                            .GetAddress(ScriptPubKeyType.Legacy, Network.Main)
                            .ToString();
                    }

                    results.Add(new DerivedAddress
                    {
                        Name = pathInfo.Name,
                        Path = pathInfo.Path,
                        Coin = pathInfo.Coin,
                        Address = address,
                        PrivateKey = derivedKey.PrivateKey.GetWif(Network.Main).ToString()
                    });
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"Error deriving path {pathInfo.Path}: {ex.Message}");
                }
            }

            return results;
        }

        /// <summary>
        /// Scan multiple account indices for a path
        /// </summary>
        public static List<DerivedAddress> ScanAccountIndices(string mnemonic, string basePath, int maxAccounts = 10, int maxAddresses = 5)
        {
            var results = new List<DerivedAddress>();
            var mnemonicObj = new Mnemonic(mnemonic);
            var masterKey = mnemonicObj.DeriveExtKey();

            for (int account = 0; account < maxAccounts; account++)
            {
                for (int index = 0; index < maxAddresses; index++)
                {
                    // Replace account and index in path template
                    var path = basePath
                        .Replace("{account}", account.ToString())
                        .Replace("{index}", index.ToString());
                    
                    // Also handle standard BIP44 format
                    if (basePath.Contains("'/0'/0"))
                    {
                        path = basePath.Replace("'/0'/0", $"'/{account}'/0/{index}");
                    }

                    try
                    {
                        var keyPath = new KeyPath(path);
                        var derivedKey = masterKey.Derive(keyPath);
                        var address = derivedKey.PrivateKey.PubKey
                            .GetAddress(ScriptPubKeyType.Legacy, Network.Main)
                            .ToString();

                        results.Add(new DerivedAddress
                        {
                            Name = $"Account {account}, Index {index}",
                            Path = path,
                            Coin = "BTC",
                            Address = address,
                            PrivateKey = derivedKey.PrivateKey.GetWif(Network.Main).ToString()
                        });
                    }
                    catch { }
                }
            }

            return results;
        }
    }

    public class DerivationPathInfo
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Coin { get; set; }
        public ScriptPubKeyType? ScriptType { get; set; }

        public DerivationPathInfo(string name, string path, string coin, ScriptPubKeyType? scriptType)
        {
            Name = name;
            Path = path;
            Coin = coin;
            ScriptType = scriptType;
        }
    }

    public class DerivedAddress
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Coin { get; set; }
        public string Address { get; set; }
        public string PrivateKey { get; set; }
    }
}
