using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography;

namespace WalletRecoveryTool.Core
{
    public class WalletLogger
    {
        private readonly string _logDirectory;
        private readonly bool _enabled;
        private StreamWriter _currentLogWriter;
        private readonly object _lock = new object();

        public WalletLogger(bool enabled = true)
        {
            _enabled = enabled;
            _logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "WalletRecoveryTool",
                "Logs"
            );

            if (_enabled)
            {
                Directory.CreateDirectory(_logDirectory);
                InitializeLogFile();
            }
        }

        private void InitializeLogFile()
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var logFilePath = Path.Combine(_logDirectory, $"recovery_log_{timestamp}.txt");
            var fs = new FileStream(logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _currentLogWriter = new StreamWriter(fs, System.Text.Encoding.UTF8) { AutoFlush = true };
            
            _currentLogWriter.WriteLine("=".PadRight(80, '='));
            _currentLogWriter.WriteLine($"Wallet Recovery Session Started: {DateTime.Now}");
            _currentLogWriter.WriteLine("=".PadRight(80, '='));
            _currentLogWriter.WriteLine();
        }

        public void LogWalletChecked(CryptoWalletGenerator.WalletInfo wallet)
        {
            // Check if logging all wallets is enabled (default: false, only log wallets with balance)
            var settings = AppSettings.Current;
            if (!settings.LogAllWallets) return;
            
            if (!_enabled || _currentLogWriter == null) return;

            lock (_lock)
            {
                try
                {
                    _currentLogWriter.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] CHECKED");
                    _currentLogWriter.WriteLine($"  Mnemonic: {wallet.Mnemonic}");
                    if (!string.IsNullOrEmpty(wallet.BitcoinAddress))
                        _currentLogWriter.WriteLine($"  BTC: {wallet.BitcoinAddress}");
                    if (!string.IsNullOrEmpty(wallet.EthereumAddress))
                        _currentLogWriter.WriteLine($"  ETH: {wallet.EthereumAddress}");
                    if (!string.IsNullOrEmpty(wallet.SolanaAddress))
                        _currentLogWriter.WriteLine($"  SOL: {wallet.SolanaAddress}");
                    
                    // Log balances if available
                    if (wallet.Balances != null && wallet.Balances.Count > 0)
                    {
                        _currentLogWriter.WriteLine("  Balances:");
                        foreach (var balance in wallet.Balances)
                        {
                            _currentLogWriter.WriteLine($"    {balance.Key}: {balance.Value:F8}");
                        }
                    }
                    else
                    {
                        _currentLogWriter.WriteLine("  Balances: NOT CHECKED YET");
                    }
                    
                    _currentLogWriter.WriteLine();
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"Error logging wallet: {ex.Message}");
                }
            }
        }

        public void LogWalletFound(CryptoWalletGenerator.WalletInfo wallet, bool mnemonicOnly = false)
        {
            if (!_enabled || _currentLogWriter == null) return;

            lock (_lock)
            {
                try
                {
                    _currentLogWriter.WriteLine("!".PadRight(80, '!'));
                    _currentLogWriter.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] *** WALLET WITH BALANCE FOUND ***");
                    _currentLogWriter.WriteLine("!".PadRight(80, '!'));
                    _currentLogWriter.WriteLine($"  Mnemonic: {wallet.Mnemonic}");
                    
                    // Only include wallet addresses if NOT mnemonic-only mode
                    if (!mnemonicOnly)
                    {
                        _currentLogWriter.WriteLine($"  Private Key: {wallet.PrivateKey}");
                        if (!string.IsNullOrEmpty(wallet.BitcoinAddress))
                            _currentLogWriter.WriteLine($"  BTC Address: {wallet.BitcoinAddress}");
                        if (!string.IsNullOrEmpty(wallet.EthereumAddress))
                            _currentLogWriter.WriteLine($"  ETH Address: {wallet.EthereumAddress}");
                        if (!string.IsNullOrEmpty(wallet.SolanaAddress))
                            _currentLogWriter.WriteLine($"  SOL Address: {wallet.SolanaAddress}");
                    }
                    
                    _currentLogWriter.WriteLine("  Balances:");
                    if (wallet.Balances != null)
                    {
                        foreach (var balance in wallet.Balances.Where(b => b.Value > 0))
                        {
                            _currentLogWriter.WriteLine($"    {balance.Key}: {balance.Value:F8}");
                        }
                    }
                    _currentLogWriter.WriteLine("!".PadRight(80, '!'));
                    _currentLogWriter.WriteLine();

                    // Also save to a separate "found wallets" file
                    SaveFoundWallet(wallet, mnemonicOnly);
                    
                    // Save encrypted backup if enabled
                    SaveBackup(wallet, mnemonicOnly);
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"Error logging found wallet: {ex.Message}");
                }
            }
        }

        private void SaveFoundWallet(CryptoWalletGenerator.WalletInfo wallet, bool mnemonicOnly = false)
        {
            try
            {
                var foundWalletsPath = Path.Combine(_logDirectory, "FOUND_WALLETS.txt");
                var encryptedPath = Path.Combine(_logDirectory, "FOUND_WALLETS.encrypted");
                var content = new StringBuilder();
                content.AppendLine($"[FOUND AT: {DateTime.Now:yyyy-MM-dd HH:mm:ss}]");
                content.AppendLine($"Mnemonic: {wallet.Mnemonic}");
                
                if (!mnemonicOnly)
                {
                    content.AppendLine($"Private Key: {wallet.PrivateKey}");
                    content.AppendLine($"BTC: {wallet.BitcoinAddress}");
                    content.AppendLine($"ETH: {wallet.EthereumAddress}");
                    content.AppendLine($"SOL: {wallet.SolanaAddress}");
                }
                
                content.AppendLine("Balances:");
                foreach (var balance in wallet.Balances.Where(b => b.Value > 0))
                {
                    content.AppendLine($"  {balance.Key}: {balance.Value:F8}");
                }
                content.AppendLine(new string('=', 80));
                content.AppendLine();

                // Save plaintext (for user convenience)
                File.AppendAllText(foundWalletsPath, content.ToString());
                
                // Also save DPAPI-encrypted copy (only current Windows user can decrypt)
                try
                {
                    var encrypted = EncryptionHelper.Encrypt(content.ToString());
                    File.AppendAllText(encryptedPath, encrypted + Environment.NewLine);
                }
                catch { /* Encryption is best-effort, plaintext already saved */ }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Error saving found wallet: {ex.Message}");
            }
        }

        private void SaveBackup(CryptoWalletGenerator.WalletInfo wallet, bool mnemonicOnly = false)
        {
            try
            {
                var settings = AppSettings.Load();
                if (!settings.EnableAutoBackup) return;

                // Determine backup location
                var backupDir = string.IsNullOrEmpty(settings.BackupLocation)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WalletRecoveryTool", "Backups")
                    : settings.BackupLocation;

                Directory.CreateDirectory(backupDir);

                // Create timestamped backup file
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff");
                var backupFile = Path.Combine(backupDir, $"WALLET_BACKUP_{timestamp}.txt");

                var sb = new StringBuilder();
                sb.AppendLine("=== WALLET BACKUP ===");
                sb.AppendLine($"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"Mnemonic: {wallet.Mnemonic}");

                if (!mnemonicOnly)
                {
                    if (!string.IsNullOrEmpty(wallet.PrivateKey))
                        sb.AppendLine($"Private Key: {wallet.PrivateKey}");
                    if (!string.IsNullOrEmpty(wallet.BitcoinAddress))
                        sb.AppendLine($"BTC: {wallet.BitcoinAddress}");
                    if (!string.IsNullOrEmpty(wallet.EthereumAddress))
                        sb.AppendLine($"ETH: {wallet.EthereumAddress}");
                    if (!string.IsNullOrEmpty(wallet.SolanaAddress))
                        sb.AppendLine($"SOL: {wallet.SolanaAddress}");
                }

                if (wallet.Balances != null && wallet.Balances.Any(b => b.Value > 0))
                {
                    sb.AppendLine("\nBalances:");
                    foreach (var balance in wallet.Balances.Where(b => b.Value > 0))
                    {
                        sb.AppendLine($"  {balance.Key}: {balance.Value:F8}");
                    }
                }

                File.WriteAllText(backupFile, sb.ToString());

                // Also create encrypted backup
                var encryptedFile = Path.Combine(backupDir, $"WALLET_BACKUP_{timestamp}.encrypted");
                var encrypted = EncryptData(sb.ToString());
                File.WriteAllText(encryptedFile, encrypted);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Error saving backup: {ex.Message}");
            }
        }

        private string EncryptData(string plainText)
        {
            try
            {
                // Use DPAPI (Windows Data Protection) for encryption tied to current user
                // No key file needed - Windows manages the key securely
                return EncryptionHelper.Encrypt(plainText);
            }
            catch
            {
                return plainText; // Fallback to unencrypted if encryption fails
            }
        }

        public void LogError(string message, Exception ex = null)
        {
            if (!_enabled || _currentLogWriter == null) return;

            lock (_lock)
            {
                try
                {
                    _currentLogWriter.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR: {message}");
                    if (ex != null)
                    {
                        _currentLogWriter.WriteLine($"  Exception: {ex.Message}");
                        _currentLogWriter.WriteLine($"  Stack: {ex.StackTrace}");
                    }
                    _currentLogWriter.WriteLine();
                }
                catch { }
            }
        }

        public void Close()
        {
            lock (_lock)
            {
                if (_currentLogWriter != null)
                {
                    _currentLogWriter.WriteLine("=".PadRight(80, '='));
                    _currentLogWriter.WriteLine($"Session Ended: {DateTime.Now}");
                    _currentLogWriter.WriteLine("=".PadRight(80, '='));
                    _currentLogWriter.Close();
                    _currentLogWriter.Dispose();
                    _currentLogWriter = null;
                }
            }
        }

        public string GetLogDirectory() => _logDirectory;
    }
}