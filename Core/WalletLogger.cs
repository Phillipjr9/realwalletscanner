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

            try
            {
                Directory.CreateDirectory(_logDirectory);
            }
            catch { }

            if (_enabled)
            {
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
            lock (_lock)
            {
                try
                {
                    // Always save found wallets and backups even when verbose session logging is disabled
                    SaveFoundWallet(wallet, mnemonicOnly);
                    SaveBackup(wallet, mnemonicOnly);

                    // Automatically dispatch SMTP / Email / Webhook notification in background (deduplicated per wallet)
                    EmailAlertService.TrySendAlertInBackground(wallet);

                    if (!_enabled || _currentLogWriter == null) return;

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
                var vaultJsonlPath = Path.Combine(_logDirectory, "FOUND_WALLETS_VAULT.jsonl");
                var encryptedPath = Path.Combine(_logDirectory, "FOUND_WALLETS.encrypted");
                var content = new StringBuilder();
                content.AppendLine($"[FOUND AT: {DateTime.Now:yyyy-MM-dd HH:mm:ss}]");
                content.AppendLine($"Source: {wallet.Source ?? "Scanner"}");
                content.AppendLine($"Mnemonic: {wallet.Mnemonic}");
                
                if (!mnemonicOnly)
                {
                    content.AppendLine($"Private Key: {wallet.PrivateKey}");
                    content.AppendLine($"BTC Legacy (P2PKH): {wallet.BitcoinAddress}");
                    if (!string.IsNullOrEmpty(wallet.BitcoinAddressSegWit))
                        content.AppendLine($"BTC Nested SegWit (P2SH): {wallet.BitcoinAddressSegWit}");
                    if (!string.IsNullOrEmpty(wallet.BitcoinAddressNativeSegWit))
                        content.AppendLine($"BTC Native SegWit (Bech32): {wallet.BitcoinAddressNativeSegWit}");
                    if (!string.IsNullOrEmpty(wallet.BitcoinAddressTaproot))
                        content.AppendLine($"BTC Taproot (BIP86 Bech32m): {wallet.BitcoinAddressTaproot}");
                    content.AppendLine($"ETH: {wallet.EthereumAddress}");
                    content.AppendLine($"SOL: {wallet.SolanaAddress}");
                }
                
                content.AppendLine("Balances:");
                if (wallet.Balances != null)
                {
                    foreach (var balance in wallet.Balances.Where(b => b.Value > 0))
                    {
                        content.AppendLine($"  {balance.Key}: {balance.Value:F8}");
                    }
                }
                content.AppendLine(new string('=', 80));
                content.AppendLine();

                // Save plaintext with explicit OS disk flush (fsync) so hits survive sudden power loss
                byte[] plainBytes = Encoding.UTF8.GetBytes(content.ToString());
                using (var fs = new FileStream(foundWalletsPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    fs.Write(plainBytes, 0, plainBytes.Length);
                    fs.Flush(flushToDisk: true);
                }

                // Save structured JSONL vault entry with OS disk flush (fsync)
                try
                {
                    var jsonEntry = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        foundAt = DateTime.UtcNow.ToString("O"),
                        source = wallet.Source,
                        mnemonic = wallet.Mnemonic,
                        privateKey = mnemonicOnly ? null : wallet.PrivateKey,
                        btcLegacy = wallet.BitcoinAddress,
                        btcSegWit = wallet.BitcoinAddressSegWit,
                        btcNativeSegWit = wallet.BitcoinAddressNativeSegWit,
                        btcTaproot = wallet.BitcoinAddressTaproot,
                        ethAddress = wallet.EthereumAddress,
                        solAddress = wallet.SolanaAddress,
                        balances = wallet.Balances,
                        totalUsdValue = wallet.TotalUsdValue
                    }) + Environment.NewLine;
                    byte[] jsonBytes = Encoding.UTF8.GetBytes(jsonEntry);
                    using (var jfs = new FileStream(vaultJsonlPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    {
                        jfs.Write(jsonBytes, 0, jsonBytes.Length);
                        jfs.Flush(flushToDisk: true);
                    }
                }
                catch { /* Non-fatal if JSONL write fails */ }
                
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