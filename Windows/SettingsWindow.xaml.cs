using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Windows;
using WalletRecoveryTool.Core;
using WinFormsMessageBox = System.Windows.Forms.MessageBox;
using WinFormsFolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;
using WinFormsDialogResult = System.Windows.Forms.DialogResult;

namespace WalletRecoveryTool
{
    public partial class SettingsWindow : Window
    {
        private AppSettings _settings;
        
        // Cache control references to avoid slow FindName() calls
        private System.Windows.Controls.CheckBox _enableCacheCheckBox;
        private System.Windows.Controls.Slider _cacheExpirySlider;
        private System.Windows.Controls.Slider _maxConcurrentApiCallsSlider;
        private System.Windows.Controls.CheckBox _skipEmptyCheckBox;
        private System.Windows.Controls.CheckBox _quickScanCheckBox;
        private System.Windows.Controls.CheckBox _strictScanModeCheckBox;
        private System.Windows.Controls.CheckBox _validateAddressesCheckBox;
        private System.Windows.Controls.CheckBox _logAllWalletsCheckBox;
        private System.Windows.Controls.ComboBox _exportFormatComboBox;
        private System.Windows.Controls.Slider _autoExportThresholdSlider;
        private System.Windows.Controls.CheckBox _useDarkThemeCheckBox;
        private System.Windows.Controls.CheckBox _turboModeCheckBox;
        private System.Windows.Controls.Slider _maxApiCallsSlider;
        private System.Windows.Controls.StackPanel _modeSoundPanel;
        private Dictionary<RecoveryType, System.Windows.Controls.ComboBox> _modeSoundCombos = new Dictionary<RecoveryType, System.Windows.Controls.ComboBox>();

        public SettingsWindow()
        {
            InitializeComponent(); // Must load XAML first before accessing controls
            CacheControlReferences();
            LoadSettings();
        }

        // Cache all control references on window load (fast one-time lookup)
        private void CacheControlReferences()
        {
            try
            {
                _enableCacheCheckBox = FindName("EnableCacheCheckBox") as System.Windows.Controls.CheckBox;
                _cacheExpirySlider = FindName("CacheExpirySlider") as System.Windows.Controls.Slider;
                _maxConcurrentApiCallsSlider = FindName("MaxConcurrentApiCallsSlider") as System.Windows.Controls.Slider;
                _skipEmptyCheckBox = FindName("SkipEmptyCheckBox") as System.Windows.Controls.CheckBox;
                _quickScanCheckBox = FindName("QuickScanCheckBox") as System.Windows.Controls.CheckBox;
                _strictScanModeCheckBox = FindName("StrictScanModeCheckBox") as System.Windows.Controls.CheckBox;
                _validateAddressesCheckBox = FindName("ValidateAddressesCheckBox") as System.Windows.Controls.CheckBox;
                _logAllWalletsCheckBox = FindName("LogAllWalletsCheckBox") as System.Windows.Controls.CheckBox;
                _exportFormatComboBox = FindName("ExportFormatComboBox") as System.Windows.Controls.ComboBox;
                _autoExportThresholdSlider = FindName("AutoExportThresholdSlider") as System.Windows.Controls.Slider;
                _useDarkThemeCheckBox = FindName("UseDarkThemeCheckBox") as System.Windows.Controls.CheckBox;
                _turboModeCheckBox = FindName("TurboModeCheckBox") as System.Windows.Controls.CheckBox;
                _maxApiCallsSlider = FindName("MaxApiCallsSlider") as System.Windows.Controls.Slider;
                _modeSoundPanel = FindName("ModeSoundPanel") as System.Windows.Controls.StackPanel;

                _modeSoundCombos = new Dictionary<RecoveryType, System.Windows.Controls.ComboBox>
                {
                    { RecoveryType.PartialMnemonic, FindName("PartialMnemonicSoundCombo") as System.Windows.Controls.ComboBox },
                    { RecoveryType.RandomGeneration, FindName("RandomGenerationSoundCombo") as System.Windows.Controls.ComboBox },
                    { RecoveryType.PrivateKeyBruteForce, FindName("PrivateKeySoundCombo") as System.Windows.Controls.ComboBox },
                    { RecoveryType.SmartHunting, FindName("SmartHuntingSoundCombo") as System.Windows.Controls.ComboBox }
                };
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Error caching control references: {ex.Message}");
            }
        }

        private void LoadSettings()
        {
            _settings = AppSettings.Load();
            
            EtherscanApiKeyTextBox.Text = _settings.EtherscanApiKey;
            BlockCypherApiKeyTextBox.Text = _settings.BlockCypherApiKey;
            QuickNodeApiKeyTextBox.Text = _settings.QuickNodeApiKey;
            UseLocalNodeForBalancesCheckBox.IsChecked = _settings.UseLocalNodeForBalances;
            LocalBitcoinRpcUrlTextBox.Text = _settings.LocalBitcoinRpcUrl;
            LocalEthereumRpcUrlTextBox.Text = _settings.LocalEthereumRpcUrl;
            LocalSolanaRpcUrlTextBox.Text = _settings.LocalSolanaRpcUrl;
            AlchemyApiKeyTextBox.Text = _settings.AlchemyApiKey;
            TronGridApiKeyTextBox.Text = _settings.TronGridApiKey;
            PolygonscanApiKeyTextBox.Text = _settings.PolygonscanApiKey;
            BscscanApiKeyTextBox.Text = _settings.BscscanApiKey;
            CheckDelaySlider.Value = _settings.CheckDelayMs;
            ThreadCountSlider.Value = _settings.ThreadCount;
            EnableLoggingCheckBox.IsChecked = _settings.EnableLogging;
            AutoExportCheckBox.IsChecked = _settings.AutoExportFoundWallets;
            SaveToDatabase.IsChecked = _settings.SaveFoundWalletsOnly;
            CheckForUpdatesCheckBox.IsChecked = _settings.CheckForUpdatesOnStartup;
            EmailAlertsCheckBox.IsChecked = _settings.EnableEmailAlerts;
            EmailAddressTextBox.Text = _settings.AlertEmailAddress;
            
            // SMTP & Email notification settings
            if (SmtpServerTextBox != null)
                SmtpServerTextBox.Text = _settings.SmtpServer ?? "";
            if (SmtpPortTextBox != null)
                SmtpPortTextBox.Text = (_settings.SmtpPort > 0 ? _settings.SmtpPort : 587).ToString();
            if (SmtpUsernameTextBox != null)
                SmtpUsernameTextBox.Text = _settings.SmtpUsername ?? "";
            if (SmtpPasswordTextBox != null)
                SmtpPasswordTextBox.Text = _settings.SmtpPassword ?? "";
            SendGridApiKeyTextBox.Text = _settings.SendGridApiKey ?? "";
            if (EmailMethodComboBox != null)
            {
                EmailMethodComboBox.SelectedIndex = (_settings.EmailMethod ?? "SMTP") switch
                {
                    "SendGrid" => 1,
                    "Gmail API" => 2,
                    "Webhook" => 3,
                    _ => 0 // SMTP (Traditional)
                };
            }
            
            // Cache and performance settings (use cached references - much faster than FindName)
            if (_enableCacheCheckBox != null)
                _enableCacheCheckBox.IsChecked = _settings.EnableBalanceCache;
            if (_cacheExpirySlider != null)
                _cacheExpirySlider.Value = _settings.CacheExpiryMinutes;
            if (_maxConcurrentApiCallsSlider != null)
                _maxConcurrentApiCallsSlider.Value = _settings.MaxConcurrentApiCalls;
            
            // Optimization settings
            if (_skipEmptyCheckBox != null)
                _skipEmptyCheckBox.IsChecked = _settings.SkipEmptyAddressTypes;
            if (_quickScanCheckBox != null)
                _quickScanCheckBox.IsChecked = _settings.QuickScanMode;
            if (_strictScanModeCheckBox != null)
                _strictScanModeCheckBox.IsChecked = _settings.StrictScanMode;
            if (_validateAddressesCheckBox != null)
                _validateAddressesCheckBox.IsChecked = _settings.ValidateAddressesBeforeCheck;
            if (_logAllWalletsCheckBox != null)
                _logAllWalletsCheckBox.IsChecked = _settings.LogAllWallets;
            
            // Export settings
            if (_exportFormatComboBox != null)
                _exportFormatComboBox.SelectedIndex = _settings.ExportFormat == "JSON" ? 1 : 0;
            if (_autoExportThresholdSlider != null)
                _autoExportThresholdSlider.Value = _settings.AutoExportThreshold;
            
            // Theme settings
            if (_useDarkThemeCheckBox != null)
                _useDarkThemeCheckBox.IsChecked = _settings.UseDarkTheme;
            
            // Turbo mode and max API calls
            if (_turboModeCheckBox != null)
                _turboModeCheckBox.IsChecked = _settings.TurboMode;
            if (_maxApiCallsSlider != null)
                _maxApiCallsSlider.Value = _settings.MaxConcurrentApiCalls;
            
            // Backup and notification settings
            EnableAutoBackupCheckBox.IsChecked = _settings.EnableAutoBackup;
            BackupLocationTextBox.Text = string.IsNullOrEmpty(_settings.BackupLocation) 
                ? "Default: Documents\\WalletRecoveryTool\\Backups" 
                : _settings.BackupLocation;
            EnableSoundNotificationCheckBox.IsChecked = _settings.EnableSoundNotification;
            EnableModeSpecificSoundsCheckBox.IsChecked = _settings.EnableModeSpecificSounds;
            UpdateModeSoundPanelVisibility();

            foreach (var kvp in _modeSoundCombos)
            {
                SetComboFromSoundName(kvp.Value, _settings.ModeNotificationSounds.GetValueOrDefault(kvp.Key.ToString(), "Default"));
            }
            
            // Set notification sound selection
            NotificationSoundComboBox.SelectedIndex = _settings.NotificationSound switch
            {
                "Beep" => 1,
                "Chime" => 2,
                "Alert" => 3,
                "Custom" => 4,
                _ => 0 // Default
            };
            
            // Show/hide email settings
            EmailSettingsPanel.Visibility = _settings.EnableEmailAlerts ? Visibility.Visible : Visibility.Collapsed;
            
            // Update performance info
            UpdatePerformanceInfo();
            
            // Update funded address filter status
            UpdateFilterStatus();
        }
        
        private void UpdatePerformanceInfo()
        {
            var cacheSize = BalanceChecker.GetCacheSize();
            var cpuCores = Environment.ProcessorCount;
            
            // Show cache info if cache stats textblock exists
            try
            {
                var cacheInfo = $"Balance Cache: {cacheSize} addresses cached\n" +
                               $"CPU Cores: {cpuCores} (Recommended: {Math.Max(4, cpuCores - 1)} threads)\n" +
                               $"Current Threads: {_settings.ThreadCount}";
                               
                if (FindName("PerformanceInfoTextBlock") is System.Windows.Controls.TextBlock textBlock)
                {
                    textBlock.Text = cacheInfo;
                }
            }
            catch { /* Ignore if UI element doesn't exist */ }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            _settings.EtherscanApiKey = EtherscanApiKeyTextBox.Text.Trim();
            _settings.BlockCypherApiKey = BlockCypherApiKeyTextBox.Text.Trim();
            _settings.QuickNodeApiKey = QuickNodeApiKeyTextBox.Text.Trim();
            _settings.UseLocalNodeForBalances = UseLocalNodeForBalancesCheckBox.IsChecked == true;
            _settings.LocalBitcoinRpcUrl = LocalBitcoinRpcUrlTextBox.Text.Trim();
            _settings.LocalEthereumRpcUrl = LocalEthereumRpcUrlTextBox.Text.Trim();
            _settings.LocalSolanaRpcUrl = LocalSolanaRpcUrlTextBox.Text.Trim();
            _settings.AlchemyApiKey = AlchemyApiKeyTextBox.Text.Trim();
            _settings.TronGridApiKey = TronGridApiKeyTextBox.Text.Trim();
            _settings.PolygonscanApiKey = PolygonscanApiKeyTextBox.Text.Trim();
            _settings.BscscanApiKey = BscscanApiKeyTextBox.Text.Trim();
            _settings.CheckDelayMs = (int)CheckDelaySlider.Value;
            _settings.ThreadCount = (int)ThreadCountSlider.Value;
            _settings.EnableLogging = EnableLoggingCheckBox.IsChecked == true;
            _settings.AutoExportFoundWallets = AutoExportCheckBox.IsChecked == true;
            _settings.SaveFoundWalletsOnly = SaveToDatabase.IsChecked == true;
            _settings.CheckForUpdatesOnStartup = CheckForUpdatesCheckBox.IsChecked == true;
            _settings.EnableEmailAlerts = EmailAlertsCheckBox.IsChecked == true;
            _settings.AlertEmailAddress = EmailAddressTextBox.Text.Trim();
            
            // SMTP, SendGrid & Webhook settings
            if (SmtpServerTextBox != null)
                _settings.SmtpServer = SmtpServerTextBox.Text.Trim();
            if (SmtpPortTextBox != null && int.TryParse(SmtpPortTextBox.Text.Trim(), out int smtpPort) && smtpPort > 0)
                _settings.SmtpPort = smtpPort;
            else
                _settings.SmtpPort = 587;
            if (SmtpUsernameTextBox != null)
                _settings.SmtpUsername = SmtpUsernameTextBox.Text.Trim();
            if (SmtpPasswordTextBox != null)
                _settings.SmtpPassword = SmtpPasswordTextBox.Text.Trim();
            _settings.SendGridApiKey = SendGridApiKeyTextBox.Text.Trim();
            _settings.EmailMethod = (EmailMethodComboBox?.SelectedIndex ?? 0) switch
            {
                1 => "SendGrid",
                2 => "Gmail API",
                3 => "Webhook",
                _ => "SMTP"
            };
            
            // Cache and performance settings (use cached references - much faster)
            if (_enableCacheCheckBox != null)
                _settings.EnableBalanceCache = _enableCacheCheckBox.IsChecked == true;
            if (_cacheExpirySlider != null)
                _settings.CacheExpiryMinutes = (int)_cacheExpirySlider.Value;
            if (_maxConcurrentApiCallsSlider != null)
                _settings.MaxConcurrentApiCalls = (int)_maxConcurrentApiCallsSlider.Value;
            
            // Optimization settings
            if (_skipEmptyCheckBox != null)
                _settings.SkipEmptyAddressTypes = _skipEmptyCheckBox.IsChecked == true;
            if (_quickScanCheckBox != null)
                _settings.QuickScanMode = _quickScanCheckBox.IsChecked == true;
            if (_strictScanModeCheckBox != null)
                _settings.StrictScanMode = _strictScanModeCheckBox.IsChecked == true;
            if (_validateAddressesCheckBox != null)
                _settings.ValidateAddressesBeforeCheck = _validateAddressesCheckBox.IsChecked == true;
            if (_logAllWalletsCheckBox != null)
                _settings.LogAllWallets = _logAllWalletsCheckBox.IsChecked == true;
            
            // Export settings
            if (_exportFormatComboBox != null)
                _settings.ExportFormat = _exportFormatComboBox.SelectedIndex == 1 ? "JSON" : "CSV";
            if (_autoExportThresholdSlider != null)
                _settings.AutoExportThreshold = (int)_autoExportThresholdSlider.Value;
            
            // Theme settings
            if (_useDarkThemeCheckBox != null)
                _settings.UseDarkTheme = _useDarkThemeCheckBox.IsChecked == true;
            
            // Turbo mode and max API calls
            if (_turboModeCheckBox != null)
                _settings.TurboMode = _turboModeCheckBox.IsChecked == true;
            if (_maxApiCallsSlider != null)
                _settings.MaxConcurrentApiCalls = (int)_maxApiCallsSlider.Value;
            
            // Backup and notification settings
            _settings.EnableAutoBackup = EnableAutoBackupCheckBox.IsChecked == true;
            if (!string.IsNullOrEmpty(BackupLocationTextBox.Text) && 
                !BackupLocationTextBox.Text.Contains("Default:"))
            {
                _settings.BackupLocation = BackupLocationTextBox.Text.Trim();
            }
            else
            {
                _settings.BackupLocation = ""; // Use default
            }
            
            _settings.EnableSoundNotification = EnableSoundNotificationCheckBox.IsChecked == true;
            _settings.NotificationSound = NotificationSoundComboBox.SelectedIndex switch
            {
                1 => "Beep",
                2 => "Chime",
                3 => "Alert",
                4 => "Custom",
                _ => "Default"
            };
            _settings.EnableModeSpecificSounds = EnableModeSpecificSoundsCheckBox.IsChecked == true;
            if (_settings.ModeNotificationSounds == null)
                _settings.ModeNotificationSounds = new Dictionary<string, string>();

            foreach (var kvp in _modeSoundCombos)
            {
                var modeKey = kvp.Key.ToString();
                _settings.ModeNotificationSounds[modeKey] = GetSoundNameFromCombo(kvp.Value);
            }
            
            _settings.Save();
            
            MessageBox.Show(
                "Settings saved successfully!\n\n" +
                "⚡ Performance Tips:\n" +
                $"• Using {_settings.ThreadCount} threads (max: {Environment.ProcessorCount})\n" +
                $"• API delay: {_settings.CheckDelayMs}ms\n" +
                "• Enable Turbo Mode to skip balance checks for 10-100x speed\n" +
                "• Balance cache automatically speeds up duplicate address checks\n" +
                $"• Save found wallets: {(SaveToDatabase.IsChecked == true ? "Enabled" : "Disabled")}",
                "Settings Saved",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            
            DialogResult = true;
            Close();
        }
        
        private void ClearCache_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Clear the balance cache?\n\n" +
                "This will force all addresses to be re-checked on the next scan.\n" +
                "The cache helps avoid redundant API calls and speeds up scanning.",
                "Clear Cache",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            
            if (result == MessageBoxResult.Yes)
            {
                BalanceChecker.ClearCache();
                MessageBox.Show("Balance cache cleared!", "Cache Cleared", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
                UpdatePerformanceInfo();
            }
        }
        
        private void OptimizeSettings_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Apply recommended performance settings?\n\n" +
                $"• Threads: {Math.Max(4, Environment.ProcessorCount - 1)} (optimal for your CPU)\n" +
                "• API Delay: 50ms (balanced speed/reliability)\n" +
                "• Enable caching and logging\n\n" +
                "This will overwrite your current settings.",
                "Optimize Settings",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            
            if (result == MessageBoxResult.Yes)
            {
                _settings.ThreadCount = Math.Max(4, Environment.ProcessorCount - 1);
                _settings.CheckDelayMs = 50;
                _settings.EnableBalanceCache = true;
                _settings.EnableLogging = true;
                _settings.MaxConcurrentApiCalls = 50;
                _settings.TurboMode = false;
                
                LoadSettings(); // Refresh UI
                
                MessageBox.Show(
                    "Settings optimized!\n\n" +
                    "Expected Performance:\n" +
                    "• Random Generation (no balance): 5,000-20,000 wallets/sec\n" +
                    "• Random Generation (with balance): 50-200 wallets/sec\n" +
                    "• Mnemonic Recovery: 50-500 variations/sec",
                    "Optimization Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        
        private void MaxSpeed_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "🚀 ENABLE MAXIMUM SPEED MODE?\n\n" +
                "This will set:\n" +
                $"• Threads: {Environment.ProcessorCount * 2} (2x your CPU cores)\n" +
                "• API Delay: 1ms (minimum possible)\n" +
                "• Max API Calls: 200 concurrent\n" +
                "• TURBO MODE: ENABLED\n\n" +
                "⚠️ WARNING: You may hit API rate limits faster!\n" +
                "Use at your own risk.",
                "🚀 MAX SPEED",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            
            if (result == MessageBoxResult.Yes)
            {
                _settings.ThreadCount = Environment.ProcessorCount * 2;
                _settings.CheckDelayMs = 1;
                _settings.EnableBalanceCache = true;
                _settings.EnableLogging = false; // Disable logging for speed
                _settings.MaxConcurrentApiCalls = 200;
                _settings.TurboMode = true;
                _settings.StrictScanMode = false;
                _settings.QuickScanMode = true;
                _settings.SkipEmptyAddressTypes = true;
                
                LoadSettings(); // Refresh UI
                
                MessageBox.Show(
                    "🚀 MAXIMUM SPEED ENABLED!\n\n" +
                    "Expected Performance:\n" +
                    "• Wallet Generation: 50,000-100,000/sec\n" +
                    "• Balance Checking: Up to 500/sec\n" +
                    "• Smart Hunting: 5-10x faster\n\n" +
                    "⚠️ Monitor for API rate limit errors.\n" +
                    "If you see errors, reduce speed in Settings.",
                    "🚀 TURBO ACTIVATED",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        private async void TestApiKeys_Click(object sender, RoutedEventArgs e)
        {
            var testButton = sender as System.Windows.Controls.Button;
            if (testButton != null)
            {
                testButton.IsEnabled = false;
                testButton.Content = "⏳ TESTING CONNECTIONS...";
            }

            // Capture UI values on UI thread to avoid cross-thread violations
            var ethKey = EtherscanApiKeyTextBox.Text?.Trim() ?? string.Empty;
            var bcKey = BlockCypherApiKeyTextBox.Text?.Trim() ?? string.Empty;
            var quickNodeKey = QuickNodeApiKeyTextBox.Text?.Trim() ?? string.Empty;
            var alchemyKey = AlchemyApiKeyTextBox.Text?.Trim() ?? string.Empty;
            var tronKey = TronGridApiKeyTextBox.Text?.Trim() ?? string.Empty;
            var polygonKey = PolygonscanApiKeyTextBox?.Text?.Trim() ?? string.Empty;
            var bscKey = BscscanApiKeyTextBox?.Text?.Trim() ?? string.Empty;

            await System.Threading.Tasks.Task.Run(async () =>
            {
                var results = new System.Text.StringBuilder();
                results.AppendLine("API Key & Zero-Waste Quota Protection Test:\n");

                // Create temporary settings with the provided keys for testing
                var testSettings = new Core.AppSettings
                {
                    EtherscanApiKey = ethKey,
                    BlockCypherApiKey = bcKey,
                    QuickNodeApiKey = quickNodeKey,
                    AlchemyApiKey = alchemyKey,
                    TronGridApiKey = tronKey,
                    PolygonscanApiKey = polygonKey,
                    BscscanApiKey = bscKey,
                    CheckDelayMs = 0
                };
                var testChecker = new Core.BalanceChecker(testSettings);
                var validation = await testChecker.ValidateAllApiKeys();
                foreach (var kvp in validation)
                {
                    results.AppendLine($"{(kvp.Value.IsValid ? "✅" : "⚠️")} {kvp.Key}: {kvp.Value.Message}");
                }

                results.AppendLine("\n🛡️ ZERO-WASTE API QUOTA PROTECTION:");
                results.AppendLine("• Bulk empty wallet scans use Local Offline Filter + Free Multi-Address Batch RPCs FIRST (0 API key credits used).");
                results.AppendLine("• Your API keys (Etherscan, BlockCypher, Alchemy, QuickNode, Polygonscan, BscScan, TronGrid) are reserved for positive hit verification & protected fallback.");
                results.AppendLine($"• API Calls Saved So Far: {Core.BalanceChecker.ApiQuotaSavedCount:N0}");

                Dispatcher.Invoke(() =>
                {
                    if (testButton != null)
                    {
                        testButton.Content = "TEST ALL API KEYS & NODES";
                        testButton.IsEnabled = true;
                    }
                    MessageBox.Show(results.ToString(), "API Key Live Test Results",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                });
            });
        }

        private void ChangePassword_Click(object sender, RoutedEventArgs e)
        {
            if (!SecurityManager.IsPasswordProtectionEnabled())
            {
                var result = MessageBox.Show(
                    "Password protection is not currently enabled.\n\nWould you like to enable it now?",
                    "Enable Password Protection",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    var passwordWindow = new PasswordWindow(setupMode: true)
                    {
                        Owner = this
                    };
                    passwordWindow.ShowDialog();
                }
            }
            else
            {
                var result = MessageBox.Show(
                    "To change your password, you must first verify your current password.\n\n" +
                    "⚠️ Warning: If you forget your new password, you will be locked out!\n\n" +
                    "Continue?",
                    "Change Password",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    var verifyWindow = new PasswordWindow(setupMode: false)
                    {
                        Owner = this
                    };

                    if (verifyWindow.ShowDialog() == true)
                    {
                        var newPasswordWindow = new PasswordWindow(setupMode: true)
                        {
                            Owner = this
                        };
                        newPasswordWindow.ShowDialog();
                    }
                }
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "WalletRecoveryTool",
                "Logs"
            );
            
            try
            {
                Directory.CreateDirectory(logPath);
                Process.Start("explorer.exe", logPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Could not open log folder:\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        
        private void EmailAlertsCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (EmailSettingsPanel != null)
            {
                EmailSettingsPanel.Visibility = EmailAlertsCheckBox.IsChecked == true 
                    ? Visibility.Visible 
                    : Visibility.Collapsed;
            }
        }
        
        private void UpdateFilterStatus()
        {
            try
            {
                if (FindName("BTCFilterStatusText") is System.Windows.Controls.TextBlock btcStatus)
                {
                    btcStatus.Text = FundedAddressFilter.BTC.IsLoaded
                        ? $"{FundedAddressFilter.BTC.AddressCount:N0} addresses loaded"
                        : "No filter loaded";
                }
                if (FindName("ETHFilterStatusText") is System.Windows.Controls.TextBlock ethStatus)
                {
                    ethStatus.Text = FundedAddressFilter.ETH.IsLoaded
                        ? $"{FundedAddressFilter.ETH.AddressCount:N0} addresses loaded"
                        : "No filter loaded";
                }
            }
            catch { /* UI elements may not exist in older layouts */ }
        }

        private void ImportBTCAddresses_Click(object sender, RoutedEventArgs e)
        {
            ImportAddressFile("btc", "BTC");
        }

        private void ImportETHAddresses_Click(object sender, RoutedEventArgs e)
        {
            ImportAddressFile("eth", "ETH");
        }

        private void ImportAddressFile(string crypto, string displayName)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = $"Select {displayName} funded addresses file(s) — you can select multiple",
                Filter = "Text Files (*.txt)|*.txt|TSV Files (*.tsv)|*.tsv|CSV Files (*.csv)|*.csv|All Files (*.*)|*.*",
                DefaultExt = ".txt",
                Multiselect = true
            };

            if (dialog.ShowDialog() == true && dialog.FileNames.Length > 0)
            {
                try
                {
                    var importedFiles = new List<string>();
                    foreach (var file in dialog.FileNames)
                    {
                        var importedPath = FundedAddressFilter.ImportAddressFile(file, crypto);
                        importedFiles.Add(Path.GetFileName(file));
                    }

                    // Rebuild the bloom filter with all files combined
                    if (crypto == "btc")
                        FundedAddressFilter.RebuildBTC();
                    else
                        FundedAddressFilter.RebuildETH();

                    UpdateFilterStatus();

                    var count = crypto == "btc"
                        ? FundedAddressFilter.BTC.AddressCount
                        : FundedAddressFilter.ETH.AddressCount;

                    var fileList = string.Join("\n  ", importedFiles);
                    MessageBox.Show(
                        $"{displayName} addresses imported successfully!\n\n" +
                        $"Files ({importedFiles.Count}):\n  {fileList}\n\n" +
                        $"Total loaded: {count:N0} addresses\n\n" +
                        $"Offline pre-filtering is now enabled for {displayName}.\n" +
                        "The scanner will check addresses locally at 50,000+/sec before making API calls.",
                        "Import Success",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Failed to import {displayName} addresses:\n{ex.Message}",
                        "Import Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }

        private void OpenFilterFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dir = FundedAddressFilter.GetDataDirectory();
                Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", dir);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open folder:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RebuildFilters_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                FundedAddressFilter.RebuildBTC();
                FundedAddressFilter.RebuildETH();
                UpdateFilterStatus();

                var btcCount = FundedAddressFilter.BTC.IsLoaded ? FundedAddressFilter.BTC.AddressCount : 0;
                var ethCount = FundedAddressFilter.ETH.IsLoaded ? FundedAddressFilter.ETH.AddressCount : 0;

                MessageBox.Show(
                    $"Bloom filters rebuilt.\n\nBTC: {btcCount:N0} addresses\nETH: {ethCount:N0} addresses",
                    "Rebuild Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to rebuild filters:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BrowseBackupLocation_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new WinFormsFolderBrowserDialog())
            {
                dialog.Description = "Select backup location for found wallets";
                dialog.ShowNewFolderButton = true;
                
                var result = dialog.ShowDialog();
                if (result == WinFormsDialogResult.OK)
                {
                    BackupLocationTextBox.Text = dialog.SelectedPath;
                }
            }
        }
        
        private void TestSound_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var soundType = GetSoundNameFromCombo(NotificationSoundComboBox);
                PlayNotificationSound(soundType);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to play sound:\\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void TestModeSound_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button && button.Tag is string modeKey)
            {
                if (Enum.TryParse<RecoveryType>(modeKey, out var type) && _modeSoundCombos.TryGetValue(type, out var combo))
                {
                    PlayNotificationSound(GetSoundNameFromCombo(combo));
                }
            }
        }

        private void ModeSoundToggleChanged(object sender, RoutedEventArgs e)
        {
            UpdateModeSoundPanelVisibility();
        }

        private void UpdateModeSoundPanelVisibility()
        {
            if (_modeSoundPanel == null)
                return;

            _modeSoundPanel.Visibility = EnableModeSpecificSoundsCheckBox.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private string GetSoundNameFromCombo(System.Windows.Controls.ComboBox combo)
        {
            if (combo == null)
                return "Default";

            return combo.SelectedIndex switch
            {
                1 => "Beep",
                2 => "Chime",
                3 => "Alert",
                4 => "Custom",
                _ => "Default"
            };
        }

        private void SetComboFromSoundName(System.Windows.Controls.ComboBox combo, string soundName)
        {
            if (combo == null)
                return;

            combo.SelectedIndex = soundName switch
            {
                "Beep" => 1,
                "Chime" => 2,
                "Alert" => 3,
                "Custom" => 4,
                _ => 0
            };
        }

        private void PlayNotificationSound(string soundType)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                switch (soundType)
                {
                    case "Beep":
                        Console.Beep(800, 500);
                        System.Threading.Thread.Sleep(100);
                        Console.Beep(1000, 500);
                        break;

                    case "Chime":
                        Console.Beep(523, 200);
                        Console.Beep(659, 200);
                        Console.Beep(784, 400);
                        break;

                    case "Alert":
                        for (int i = 0; i < 3; i++)
                        {
                            Console.Beep(1000, 200);
                            System.Threading.Thread.Sleep(100);
                        }
                        break;

                    case "Custom":
                        var customSoundPath = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "WalletRecoveryTool",
                            "custom_sound.wav"
                        );
                        if (File.Exists(customSoundPath))
                        {
                            using (var player = new SoundPlayer(customSoundPath))
                            {
                                player.PlaySync();
                            }
                        }
                        else
                        {
                            Dispatcher.Invoke(() =>
                            {
                                MessageBox.Show(
                                    $"Place a WAV file named 'custom_sound.wav' in:\\n{customSoundPath}",
                                    "Custom Sound Not Found",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Information);
                            });
                            SystemSounds.Exclamation.Play();
                        }
                        break;

                    default:
                        SystemSounds.Exclamation.Play();
                        break;
                }
            });
        }
        
        private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
        {
            var backupPath = string.IsNullOrEmpty(_settings.BackupLocation)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WalletRecoveryTool", "Backups")
                : _settings.BackupLocation;
            
            try
            {
                Directory.CreateDirectory(backupPath);
                Process.Start("explorer.exe", backupPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Could not open backup folder:\\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        
        private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var button = sender as System.Windows.Controls.Button;
                button.IsEnabled = false;
                button.Content = "⏳ CHECKING...";
                
                var updateInfo = await UpdateChecker.CheckForUpdatesAsync();
                
                button.IsEnabled = true;
                button.Content = "🔄 CHECK FOR UPDATES";
                
                if (updateInfo.UpdateAvailable)
                {
                    var updateWindow = new UpdateWindow(updateInfo)
                    {
                        Owner = this
                    };
                    updateWindow.ShowDialog();
                }
                else
                {
                    MessageBox.Show(
                        $"You are running the latest version ({updateInfo.CurrentVersion}).\n\n" +
                        "No updates available at this time.",
                        "Up to Date",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to check for updates:\n{ex.Message}\n\n" +
                    "Please check your internet connection and try again.",
                    "Update Check Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        
        private async void TestEmailConfiguration_Click(object sender, RoutedEventArgs e)
        {
            string logFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "email_test.log");
            
            void LogMessage(string msg)
            {
                DebugLogger.Log(msg);
                Debug.WriteLine(msg);
                File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n");
            }
            
            try
            {
                File.WriteAllText(logFile, $"=== EMAIL TEST LOG ===\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Starting SendGrid test...\n");
                
                // Validate email address
                string emailAddress = EmailAddressTextBox.Text?.Trim();
                LogMessage($"Email address: {emailAddress}");
                
                if (string.IsNullOrEmpty(emailAddress))
                {
                    LogMessage("ERROR: Email address is empty");
                    MessageBox.Show("Please enter an email address first.", "Missing Email", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                
                // Validate email format
                try
                {
                    var addr = new System.Net.Mail.MailAddress(emailAddress);
                }
                catch (Exception ex)
                {
                    LogMessage($"ERROR: Invalid email format: {ex.Message}");
                    MessageBox.Show("Please enter a valid email address.", "Invalid Email", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                
                // Show progress
                var button = sender as System.Windows.Controls.Button;
                var originalContent = button.Content;
                button.IsEnabled = false;
                button.Content = "⏳ SENDING TEST EMAIL...";
                
                try
                {
                    LogMessage("Validating SMTP / Email configuration...");
                    string smtpHost = SmtpServerTextBox?.Text?.Trim() ?? "";
                    int smtpPort = 587;
                    if (SmtpPortTextBox != null && int.TryParse(SmtpPortTextBox.Text.Trim(), out int parsedPort) && parsedPort > 0)
                        smtpPort = parsedPort;
                    string smtpUser = SmtpUsernameTextBox?.Text?.Trim() ?? "";
                    string smtpPass = SmtpPasswordTextBox?.Text?.Trim() ?? "";
                    string apiKey = SendGridApiKeyTextBox?.Text?.Trim() ?? "";
                    string emailMethod = (EmailMethodComboBox?.SelectedIndex ?? 0) switch
                    {
                        1 => "SendGrid",
                        2 => "Gmail API",
                        3 => "Webhook",
                        _ => "SMTP"
                    };

                    var testSettings = new AppSettings
                    {
                        EnableEmailAlerts = true,
                        AlertEmailAddress = emailAddress,
                        EmailMethod = emailMethod,
                        SmtpServer = smtpHost,
                        SmtpPort = smtpPort,
                        SmtpUsername = smtpUser,
                        SmtpPassword = smtpPass,
                        SendGridApiKey = apiKey,
                        WebhookUrl = emailAddress.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? emailAddress : ""
                    };
                    
                    // Create test wallet using a non-restricted valid BIP39 mnemonic
                    LogMessage("Creating test wallet with sample data...");
                    var testWallet = new CryptoWalletGenerator.WalletInfo
                    {
                        Mnemonic = "planet crystal ocean velvet silver canyon harvest timber shadow marble winter ankle",
                        BitcoinAddress = "1FbeLoQ9s428xK2E9fR4fV2cR2X6n3L2sB",
                        BitcoinAddressSegWit = "3J98t1WpEZ73CNmYviecrnyiWrnqRhWNLy",
                        BitcoinAddressNativeSegWit = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4",
                        EthereumAddress = "0x91D6E6B770e9F98e40C47D0156F20B4358989060",
                        SolanaAddress = "7xKXtg2CW87d97TXJSDpbD5jBkheTqA83TZRuJosgAsU",
                        Balances = new System.Collections.Generic.Dictionary<string, decimal>
                        {
                            { "BTC", 0.00001m },
                            { "ETH", 0.0001m },
                            { "SOL", 0.001m },
                            { "USDT", 0.01m }
                        }
                    };
                    
                    // Send test email via SMTP / configured channel
                    LogMessage($"Sending test email to {emailAddress} via {emailMethod} ({(string.IsNullOrEmpty(smtpHost) ? "auto-inferred host" : $"{smtpHost}:{smtpPort}")})...");
                    await EmailAlertService.SendWalletFoundAlertAsync(testWallet, testSettings);
                    
                    LogMessage("✅ Test email sent successfully!");
                    button.IsEnabled = true;
                    button.Content = originalContent;
                    
                    MessageBox.Show(
                        $"✅ TEST EMAIL SENT SUCCESSFULLY!\n\n" +
                        $"Email sent to: {emailAddress}\n\n" +
                        $"📧 Check your inbox (and spam/junk folder) for the test email.\n" +
                        $"This is a sample email with realistic wallet data.\n\n" +
                        $"💚 Your SendGrid integration is working!\n\n" +
                        $"You can now enable email alerts in the checkbox above, and alerts will automatically send when wallets are found.",
                        "Test Email Sent",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    LogMessage($"❌ ERROR: {ex.GetType().Name}: {ex.Message}");
                    if (ex.InnerException != null)
                        LogMessage($"Inner Exception: {ex.InnerException.Message}");
                    
                    button.IsEnabled = true;
                    button.Content = originalContent;
                    
                    string debugInfo = ex.Message;
                    if (ex.InnerException != null)
                        debugInfo += $"\n\nDetails: {ex.InnerException.Message}";
                    
                    MessageBox.Show(
                        $"❌ FAILED TO SEND TEST EMAIL\n\n" +
                        $"Error: {debugInfo}\n\n" +
                        $"📋 Full debug log saved to: Desktop/email_test.log\n\n" +
                        $"Please check:\n" +
                        $"• Email address is correct\n" +
                        $"• Internet connection is active\n" +
                        $"• SendGrid API key is valid (should start with SG.)\n" +
                        $"• You've activated a sender email in SendGrid dashboard\n\n" +
                        $"Check email_test.log on your Desktop for detailed error info.",
                        "Test Email Failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Critical error: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}