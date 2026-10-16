using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace WalletRecoveryTool.Core
{
    public class AppSettings
    {
        public string EtherscanApiKey { get; set; } = "";
        public string BlockCypherApiKey { get; set; } = "";
        public string QuickNodeApiKey { get; set; } = "";
        public string AlchemyApiKey { get; set; } = "";
        public string TronGridApiKey { get; set; } = "";
        public string PolygonscanApiKey { get; set; } = "";  // For MATIC balance checking - get free key at polygonscan.com
        public string BscscanApiKey { get; set; } = "";      // For BNB balance checking - get free key at bscscan.com
        public string BlockfrostApiKey { get; set; } = "";   // For ADA balance checking - get free key at blockfrost.io

        // Local node / self-hosted RPC settings (used before public providers when configured)
        public bool UseLocalNodeForBalances { get; set; } = false;
        public string LocalBitcoinRpcUrl { get; set; } = "";
        public string LocalBitcoinRpcUser { get; set; } = "";
        public string LocalBitcoinRpcPassword { get; set; } = "";
        public string LocalEthereumRpcUrl { get; set; } = "";
        public string LocalEthereumRpcUser { get; set; } = "";
        public string LocalEthereumRpcPassword { get; set; } = "";
        public string LocalSolanaRpcUrl { get; set; } = "";
public int CheckDelayMs { get; set; } = 0;  // ULTRA SPEED: 0ms API delay - max throughput
        public bool EnableLogging { get; set; } = false;  // SPEED: Disabled logging by default (disk writes are slow)
        public bool AutoExportFoundWallets { get; set; } = true;
public int ThreadCount { get; set; } = Math.Clamp(Environment.ProcessorCount * 12, 8, 64); // High throughput: 12x CPU cores, cap at 64
        public bool EnableBalanceCache { get; set; } = true;
        public int CacheExpiryMinutes { get; set; } = 15; // Extended from 5 to 15 minutes
public int MaxConcurrentApiCalls { get; set; } = 300;  // High throughput: 300 concurrent API calls (I/O bound, minimal memory per call)
        public bool SkipEmptyAddressTypes { get; set; } = false; // Smart optimization: skip BTC SegWit/NativeSegWit if Legacy is empty (2-3x faster) - DISABLED BY DEFAULT
        public bool QuickScanMode { get; set; } = false; // Stop checking wallet immediately if first address is empty (10-100x faster for empty wallets) - DISABLED BY DEFAULT
        public bool StrictScanMode { get; set; } = false; // Coverage-first: disable skip shortcuts that can hide funded wallets - DISABLED BY DEFAULT
        public bool ValidateAddressesBeforeCheck { get; set; } = false; // SPEED: Disabled address validation (adds overhead, not needed)
        public bool SaveFoundWalletsOnly { get; set; } = true; // Save ONLY wallets with balance to JSON files (no empty wallets)
        public bool LogAllWallets { get; set; } = true; // Log all checked wallets to file (if false, only logs wallets with balance) - ENABLED BY DEFAULT

    // Multi-PC cluster sharding (prevents different PCs from scanning the same candidates)
    public bool EnableClusterSharding { get; set; } = false;
    public int ClusterNodeIndex { get; set; } = 0; // 0-based index of this machine in the cluster
    public int ClusterNodeCount { get; set; } = 1; // total participating machines
        
        // 🚀 TURBO MODE - Maximum speed (may hit API rate limits faster)
public bool TurboMode { get; set; } = true; // MAX SPEED: Disable API delays, max concurrency
        
        // ⚡ HASHCAT PIPELINE - Optimized PBKDF2 batch processing (10-50x faster mnemonic generation)
        public bool UseHashcatPipeline { get; set; } = true; // Skip NBitcoin.Mnemonic overhead, batch PBKDF2 directly
        public int HashcatBatchSize { get; set; } = 512; // Mnemonics per batch (higher = better throughput, more memory)

        // Deep scan settings - for thorough recovery
        public bool EnableDeepScan { get; set; } = true; // Check multiple address indices (finds wallets with non-zero indices)
        public int DeepScanMaxAccounts { get; set; } = 5; // Number of accounts to scan (0-4)
        public int DeepScanMaxAddresses { get; set; } = 10; // Number of address indices per account (0-9)
        
        // SMART HUNTING SETTINGS - Dramatically increases success rate
        public bool EnableSmartHunting { get; set; } = false; // Disabled by default - Random mode generates pure random mnemonics
        public bool ScanWeakRngWallets { get; set; } = true; // Target wallets with weak random number generation
        public bool ScanBrainWallets { get; set; } = true; // Scan brain wallet phrases (very high success rate!)
        public bool ScanVulnerablePatterns { get; set; } = true; // Target known vulnerable wallet software
        public bool ScanHumanPatterns { get; set; } = true; // Exploit human-generated mnemonic patterns
        public bool ScanTimestampSeeds { get; set; } = true; // Check timestamp-based seeds (2009-2020)
        public bool ScanDerivationPaths { get; set; } = true; // Exhaustive derivation path scanning (Ledger, Trezor, etc.)
        public bool ScanDictionaryBrainWallets { get; set; } = true; // Extended dictionary-based brain wallet attack
        public bool ScanKnownLeakedKeys { get; set; } = true; // Check publicly known compromised keys
        public bool ScanExchangeHackKeys { get; set; } = true; // Scan known exchange hack keys & weak private keys
        public bool ScanRepeatedWordMnemonics { get; set; } = true; // Target repeated/simple word mnemonics
        public bool ScanIncrementalMnemonics { get; set; } = true; // Sequential BIP39 word index combinations
        public bool PrioritizeHighValuePrefixes { get; set; } = true; // Focus on address prefixes with higher success
        public int SmartHuntingMaxAttempts { get; set; } = 10000000; // Max attempts for smart hunting per session
        
        // ENHANCED SCANNING FEATURES - Better chances of finding funded wallets
        public bool EnableMultiAccountScan { get; set; } = true;    // Check accounts 0-2 and indices 0-4 (finds wallets at non-default paths)
        public bool EnablePassphraseScan { get; set; } = true;      // Try common BIP39 passphrases (same seed + passphrase = different wallet)
        public bool EnableERC20Scan { get; set; } = true;           // Check USDT/USDC/DAI/WBTC tokens (wallet may have 0 ETH but $50K in tokens)
        public bool EnableChangeAddresses { get; set; } = true;     // Check BTC change path m/x'/0'/0'/1/x (funds often sit in change addresses)
        public bool EnableCrossChainEVM { get; set; } = true;       // Check same ETH address on Arbitrum/Optimism/Base/Avalanche L2s
        public bool EnableMultiLanguageBrainWallets { get; set; } = true; // Brain wallets in Japanese, Spanish, Chinese, etc.
        
        // OFFLINE FUNDED ADDRESS FILTER - Pre-filter addresses locally before API calls
        public bool EnableOfflineFilter { get; set; } = true; // Enable bloom filter pre-filtering when a meaningful dataset is available
        
        // Update settings
        public DateTime LastUpdateCheck { get; set; } = DateTime.MinValue; // Last time we checked for updates
        public string SkippedVersion { get; set; } = null; // Version user chose to skip
        public bool CheckForUpdatesOnStartup { get; set; } = true; // Automatically check for updates when app starts
        
        // Theme settings
        public bool UseDarkTheme { get; set; } = false; // Dark theme preference (default: light theme)
        
        // Email alert settings
        public bool EnableEmailAlerts { get; set; } = true;
        public string AlertEmailAddress { get; set; } = "smokie1969@atlanticbb.net";
        public string EmailMethod { get; set; } = "SMTP";  // SMTP, Gmail API, SendGrid, Webhook
        
        // SMTP settings
        public string SmtpServer { get; set; } = "smtp.atlanticbb.net";
        public int SmtpPort { get; set; } = 587;
        public string SmtpUsername { get; set; } = "smokie1969@atlanticbb.net";
        public string SmtpPassword { get; set; } = ""; // Store encrypted via DPAPI / local config
        
        // Alternative email methods (API keys)
        public string GmailApiKey { get; set; } = "";  // Gmail API OAuth token
        public string SendGridApiKey { get; set; } = "";  // SendGrid API key
        public string WebhookUrl { get; set; } = "";  // Custom webhook URL for email notifications
        
        // Export settings
        public string LastExportPath { get; set; } = "";
        public string ExportFormat { get; set; } = "CSV"; // CSV or JSON
        public int AutoExportThreshold { get; set; } = 10; // Auto-export after X wallets found
        
        // Backup settings
        public bool EnableAutoBackup { get; set; } = true; // Automatically backup found wallets
        public string BackupLocation { get; set; } = ""; // Custom backup location (empty = default Documents\WalletRecoveryTool\Backups)
        
        // Notification settings
        public bool EnableSoundNotification { get; set; } = true; // Play sound when wallet found
        public string NotificationSound { get; set; } = "Default"; // Default, Beep, Chime, or custom path
        public bool EnableModeSpecificSounds { get; set; } = false; // Allow unique sounds per recovery mode
        public Dictionary<string, string> ModeNotificationSounds { get; set; } = new Dictionary<string, string>
        {
            { RecoveryType.PartialMnemonic.ToString(), "Default" },
            { RecoveryType.RandomGeneration.ToString(), "Default" },
            { RecoveryType.PrivateKeyBruteForce.ToString(), "Default" },
            { RecoveryType.SmartHunting.ToString(), "Default" }
        };

        // 🆕 TELEGRAM ALERT SETTINGS
        public bool EnableTelegramAlerts { get; set; } = false;
        public string TelegramBotToken { get; set; } = ""; // Get from @BotFather
        public string TelegramChatId { get; set; } = "";   // Get from @userinfobot

        // 🆕 DUPLICATE DETECTION SETTINGS
        public bool EnableDuplicateDetection { get; set; } = true; // Skip already-checked addresses
        public int DuplicateCacheRetentionDays { get; set; } = 30; // How long to remember checked addresses

        // 🆕 LIVE PRICE TICKER SETTINGS
        public bool EnableLivePriceTicker { get; set; } = true;
        public int PriceUpdateIntervalSeconds { get; set; } = 60;

        // 🆕 MULTI-LANGUAGE MNEMONIC SETTINGS
        public string PreferredMnemonicLanguage { get; set; } = "English"; // Default language for new mnemonics

        // 🆕 ADDRESS WATCHLIST SETTINGS
        public bool EnableAddressWatchlist { get; set; } = false;
        public int WatchlistCheckIntervalMinutes { get; set; } = 5;

        // 🆕 CUSTOM DERIVATION PATH SETTINGS  
        public string WalletPreset { get; set; } = "Default"; // Default, LedgerLive, Trezor, etc.
        public List<string> CustomDerivationPaths { get; set; } = new List<string>();

        // 🆕 CUSTOM BRAIN WALLET WORDLIST
        public string CustomBrainWalletListPath { get; set; } = ""; // Path to file with one phrase per line

        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WalletRecoveryTool",
            "settings.json"
        );

        // Cached instance to avoid repeated disk I/O + DPAPI decryption in hot loops
        private static AppSettings _cachedInstance;
        private static DateTime _cacheTimestamp = DateTime.MinValue;
        private static readonly object _cacheLock = new object();
        private static readonly TimeSpan _cacheTTL = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Get cached settings (reloads from disk at most every 30 seconds).
        /// Use this in hot paths (WalletLogger, SmartWalletHunter, etc.)
        /// </summary>
        public static AppSettings Current
        {
            get
            {
                if (_cachedInstance != null && (DateTime.Now - _cacheTimestamp) < _cacheTTL)
                    return _cachedInstance;
                return Load();
            }
        }

        /// <summary>
        /// Invalidate the cache so next access reloads from disk. Call after Save().
        /// </summary>
        public static void InvalidateCache()
        {
            lock (_cacheLock)
            {
                _cachedInstance = null;
                _cacheTimestamp = DateTime.MinValue;
            }
        }

        public static AppSettings Load()
        {
            lock (_cacheLock)
            {
                // Return cached if still valid
                if (_cachedInstance != null && (DateTime.Now - _cacheTimestamp) < _cacheTTL)
                    return _cachedInstance;

                try
                {
                    if (File.Exists(SettingsPath))
                    {
                        var json = File.ReadAllText(SettingsPath);
                        var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                        EnsureModeSoundDefaults(settings);
                        
                        // Decrypt sensitive data (API keys + passwords)
                        settings.EtherscanApiKey = DecryptIfNeeded(settings.EtherscanApiKey);
                        settings.BlockCypherApiKey = DecryptIfNeeded(settings.BlockCypherApiKey);
                        settings.QuickNodeApiKey = DecryptIfNeeded(settings.QuickNodeApiKey);
                        settings.AlchemyApiKey = DecryptIfNeeded(settings.AlchemyApiKey);
                        settings.TronGridApiKey = DecryptIfNeeded(settings.TronGridApiKey);
                        settings.PolygonscanApiKey = DecryptIfNeeded(settings.PolygonscanApiKey);
                        settings.BscscanApiKey = DecryptIfNeeded(settings.BscscanApiKey);
                        settings.BlockfrostApiKey = DecryptIfNeeded(settings.BlockfrostApiKey);
                        // Local RPC credentials (passwords) may be encrypted in settings file
                        settings.LocalBitcoinRpcPassword = DecryptIfNeeded(settings.LocalBitcoinRpcPassword);
                        settings.LocalEthereumRpcPassword = DecryptIfNeeded(settings.LocalEthereumRpcPassword);
                        settings.SmtpPassword = DecryptIfNeeded(settings.SmtpPassword);
                        settings.GmailApiKey = DecryptIfNeeded(settings.GmailApiKey);
                        settings.SendGridApiKey = DecryptIfNeeded(settings.SendGridApiKey);
                        settings.TelegramBotToken = DecryptIfNeeded(settings.TelegramBotToken);
                        ApplyLocalSmtpProfile(settings);
                        
                        _cachedInstance = settings;
                        _cacheTimestamp = DateTime.Now;
                        return settings;
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"Error loading settings: {ex.Message}");
                }

                var defaults = new AppSettings();
                EnsureModeSoundDefaults(defaults);
                ApplyLocalSmtpProfile(defaults);
                _cachedInstance = defaults;
                _cacheTimestamp = DateTime.Now;
                return defaults;
            }
        }

        private static void ApplyLocalSmtpProfile(AppSettings settings)
        {
            if (settings == null) return;
            try
            {
                string[] candidatePaths =
                {
                    Path.Combine(Directory.GetCurrentDirectory(), "smtp.local.json"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "smtp.local.json"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WalletRecoveryTool", "smtp.local.json"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WalletRecoveryTool", "smtp.local.json"),
                    "/home/user/realwalletscanner/smtp.local.json"
                };

                foreach (var path in candidatePaths)
                {
                    if (!File.Exists(path)) continue;
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("enabled", out var en) && (en.ValueKind == JsonValueKind.True || en.ValueKind == JsonValueKind.False))
                        settings.EnableEmailAlerts = en.GetBoolean();
                    if (root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(m.GetString()))
                        settings.EmailMethod = m.GetString();
                    if (root.TryGetProperty("smtpHost", out var h) && h.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(h.GetString()))
                        settings.SmtpServer = h.GetString();
                    if (root.TryGetProperty("smtpPort", out var p) && p.TryGetInt32(out int portVal) && portVal > 0)
                        settings.SmtpPort = portVal;
                    if (root.TryGetProperty("smtpUser", out var u) && u.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(u.GetString()))
                        settings.SmtpUsername = u.GetString();
                    if (root.TryGetProperty("smtpPass", out var pw) && pw.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(pw.GetString()))
                        settings.SmtpPassword = DecryptIfNeeded(pw.GetString());
                    if (root.TryGetProperty("recipientEmail", out var rcpt) && rcpt.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(rcpt.GetString()))
                        settings.AlertEmailAddress = rcpt.GetString();
                    break;
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"ApplyLocalSmtpProfile error: {ex.Message}");
            }
        }

        public void Save()
        {
            try
            {
                EnsureModeSoundDefaults(this);
                var directory = Path.GetDirectoryName(SettingsPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Create a copy for saving with encrypted password
                var settingsToSave = new AppSettings
                {
                    // API Keys (encrypted via DPAPI)
                    EtherscanApiKey = EncryptIfNeeded(this.EtherscanApiKey),
                    BlockCypherApiKey = EncryptIfNeeded(this.BlockCypherApiKey),
                    QuickNodeApiKey = EncryptIfNeeded(this.QuickNodeApiKey),
                    AlchemyApiKey = EncryptIfNeeded(this.AlchemyApiKey),
                    TronGridApiKey = EncryptIfNeeded(this.TronGridApiKey),
                    PolygonscanApiKey = EncryptIfNeeded(this.PolygonscanApiKey),
                    BscscanApiKey = EncryptIfNeeded(this.BscscanApiKey),
                    BlockfrostApiKey = EncryptIfNeeded(this.BlockfrostApiKey),
                    
                    // Performance settings
                    CheckDelayMs = this.CheckDelayMs,
                    // Local node / RPC settings
                    UseLocalNodeForBalances = this.UseLocalNodeForBalances,
                    LocalBitcoinRpcUrl = this.LocalBitcoinRpcUrl,
                    LocalBitcoinRpcUser = this.LocalBitcoinRpcUser,
                    LocalBitcoinRpcPassword = EncryptIfNeeded(this.LocalBitcoinRpcPassword),
                    LocalEthereumRpcUrl = this.LocalEthereumRpcUrl,
                    LocalEthereumRpcUser = this.LocalEthereumRpcUser,
                    LocalEthereumRpcPassword = EncryptIfNeeded(this.LocalEthereumRpcPassword),
                    LocalSolanaRpcUrl = this.LocalSolanaRpcUrl,
                    ThreadCount = this.ThreadCount,
                    MaxConcurrentApiCalls = this.MaxConcurrentApiCalls,
                    EnableBalanceCache = this.EnableBalanceCache,
                    CacheExpiryMinutes = this.CacheExpiryMinutes,
                    SkipEmptyAddressTypes = this.SkipEmptyAddressTypes,
                    QuickScanMode = this.QuickScanMode,
                    StrictScanMode = this.StrictScanMode,
                    ValidateAddressesBeforeCheck = this.ValidateAddressesBeforeCheck,
                    TurboMode = this.TurboMode,
                    UseHashcatPipeline = this.UseHashcatPipeline,
                    HashcatBatchSize = this.HashcatBatchSize,
                    
                    // Logging settings
                    EnableLogging = this.EnableLogging,
                    SaveFoundWalletsOnly = this.SaveFoundWalletsOnly,
                    LogAllWallets = this.LogAllWallets,
                    AutoExportFoundWallets = this.AutoExportFoundWallets,

                    // Multi-PC cluster sharding settings
                    EnableClusterSharding = this.EnableClusterSharding,
                    ClusterNodeIndex = this.ClusterNodeIndex,
                    ClusterNodeCount = this.ClusterNodeCount,
                    
                    // Deep scan settings
                    EnableDeepScan = this.EnableDeepScan,
                    DeepScanMaxAccounts = this.DeepScanMaxAccounts,
                    DeepScanMaxAddresses = this.DeepScanMaxAddresses,
                    
                    // Smart hunting settings
                    EnableSmartHunting = this.EnableSmartHunting,
                    ScanWeakRngWallets = this.ScanWeakRngWallets,
                    ScanBrainWallets = this.ScanBrainWallets,
                    ScanVulnerablePatterns = this.ScanVulnerablePatterns,
                    ScanHumanPatterns = this.ScanHumanPatterns,
                    ScanTimestampSeeds = this.ScanTimestampSeeds,
                    ScanDerivationPaths = this.ScanDerivationPaths,
                    ScanDictionaryBrainWallets = this.ScanDictionaryBrainWallets,
                    ScanKnownLeakedKeys = this.ScanKnownLeakedKeys,
                    ScanExchangeHackKeys = this.ScanExchangeHackKeys,
                    ScanRepeatedWordMnemonics = this.ScanRepeatedWordMnemonics,
                    ScanIncrementalMnemonics = this.ScanIncrementalMnemonics,
                    PrioritizeHighValuePrefixes = this.PrioritizeHighValuePrefixes,
                    SmartHuntingMaxAttempts = this.SmartHuntingMaxAttempts,
                    
                    // Enhanced scanning features & offline filter
                    EnableMultiAccountScan = this.EnableMultiAccountScan,
                    EnablePassphraseScan = this.EnablePassphraseScan,
                    EnableERC20Scan = this.EnableERC20Scan,
                    EnableChangeAddresses = this.EnableChangeAddresses,
                    EnableCrossChainEVM = this.EnableCrossChainEVM,
                    EnableMultiLanguageBrainWallets = this.EnableMultiLanguageBrainWallets,
                    EnableOfflineFilter = this.EnableOfflineFilter,
                    CustomBrainWalletListPath = this.CustomBrainWalletListPath,
                    
                    // Update settings
                    LastUpdateCheck = this.LastUpdateCheck,
                    SkippedVersion = this.SkippedVersion,
                    CheckForUpdatesOnStartup = this.CheckForUpdatesOnStartup,
                    
                    // Theme settings
                    UseDarkTheme = this.UseDarkTheme,
                    
                    // Email alert settings
                    EnableEmailAlerts = this.EnableEmailAlerts,
                    AlertEmailAddress = this.AlertEmailAddress,
                    EmailMethod = this.EmailMethod,
                    SmtpServer = this.SmtpServer,
                    SmtpPort = this.SmtpPort,
                    SmtpUsername = this.SmtpUsername,
                    SmtpPassword = EncryptIfNeeded(this.SmtpPassword),
                    GmailApiKey = EncryptIfNeeded(this.GmailApiKey),
                    SendGridApiKey = EncryptIfNeeded(this.SendGridApiKey),
                    WebhookUrl = this.WebhookUrl,
                    
                    // Export settings
                    LastExportPath = this.LastExportPath,
                    ExportFormat = this.ExportFormat,
                    AutoExportThreshold = this.AutoExportThreshold,
                    
                    // Backup settings
                    EnableAutoBackup = this.EnableAutoBackup,
                    BackupLocation = this.BackupLocation,
                    
                    // Notification settings
                    EnableSoundNotification = this.EnableSoundNotification,
                    NotificationSound = this.NotificationSound,
                    EnableModeSpecificSounds = this.EnableModeSpecificSounds,
                    ModeNotificationSounds = new Dictionary<string, string>(this.ModeNotificationSounds),
                    
                    // 🆕 Telegram alert settings
                    EnableTelegramAlerts = this.EnableTelegramAlerts,
                    TelegramBotToken = EncryptIfNeeded(this.TelegramBotToken),
                    TelegramChatId = this.TelegramChatId,
                    
                    // 🆕 Duplicate detection settings
                    EnableDuplicateDetection = this.EnableDuplicateDetection,
                    DuplicateCacheRetentionDays = this.DuplicateCacheRetentionDays,
                    
                    // 🆕 Live price ticker settings
                    EnableLivePriceTicker = this.EnableLivePriceTicker,
                    PriceUpdateIntervalSeconds = this.PriceUpdateIntervalSeconds,
                    
                    // 🆕 Multi-language mnemonic settings
                    PreferredMnemonicLanguage = this.PreferredMnemonicLanguage,
                    
                    // 🆕 Address watchlist settings
                    EnableAddressWatchlist = this.EnableAddressWatchlist,
                    WatchlistCheckIntervalMinutes = this.WatchlistCheckIntervalMinutes,
                    
                    // 🆕 Custom derivation path settings
                    WalletPreset = this.WalletPreset,
                    CustomDerivationPaths = this.CustomDerivationPaths != null 
                        ? new List<string>(this.CustomDerivationPaths) 
                        : new List<string>()
                };

                var json = JsonSerializer.Serialize(settingsToSave, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
                InvalidateCache(); // Force reload on next access
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Error saving settings: {ex.Message}");
            }
        }

        private static void EnsureModeSoundDefaults(AppSettings settings)
        {
            if (settings.ModeNotificationSounds == null)
            {
                settings.ModeNotificationSounds = new Dictionary<string, string>();
            }

            foreach (RecoveryType type in Enum.GetValues(typeof(RecoveryType)))
            {
                var key = type.ToString();
                if (!settings.ModeNotificationSounds.ContainsKey(key) || string.IsNullOrWhiteSpace(settings.ModeNotificationSounds[key]))
                {
                    settings.ModeNotificationSounds[key] = "Default";
                }
            }
        }

        private static string EncryptIfNeeded(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return EncryptionHelper.Encrypt(value);
        }

        private static string DecryptIfNeeded(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return EncryptionHelper.Decrypt(value);
        }
    }
}