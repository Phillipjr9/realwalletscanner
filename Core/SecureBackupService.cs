using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Secure backup service for found wallets
    /// Supports local backup and cloud sync preparation
    /// </summary>
    public static class SecureBackupService
    {
        private static readonly string BackupFolder;
        private const int PBKDF2_ITERATIONS = 100000;

        static SecureBackupService()
        {
            var settings = AppSettings.Load();
            BackupFolder = !string.IsNullOrEmpty(settings.BackupLocation)
                ? settings.BackupLocation
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "WalletRecoveryTool", "Backups");
            
            Directory.CreateDirectory(BackupFolder);
        }

        /// <summary>
        /// Create encrypted backup of found wallets
        /// </summary>
        public static async Task<string> CreateBackupAsync(List<CryptoWalletGenerator.WalletInfo> wallets, string password)
        {
            if (wallets == null || wallets.Count == 0)
                throw new ArgumentException("No wallets to backup");

            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("Password is required for encrypted backup");

            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var backupFile = Path.Combine(BackupFolder, $"wallet_backup_{timestamp}.wrbak");

            // Serialize wallets to JSON
            var json = JsonSerializer.Serialize(wallets, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });

            // Compress
            var compressed = Compress(Encoding.UTF8.GetBytes(json));

            // Encrypt
            var encrypted = Encrypt(compressed, password);

            // Write with header
            await using var fs = new FileStream(backupFile, FileMode.Create);
            await using var bw = new BinaryWriter(fs);
            
            // Header
            bw.Write("WRBAK"); // Magic bytes
            bw.Write((byte)1); // Version
            bw.Write(DateTime.Now.ToBinary()); // Timestamp
            bw.Write(wallets.Count); // Wallet count
            
            // Encrypted data
            bw.Write(encrypted.Length);
            bw.Write(encrypted);

            return backupFile;
        }

        /// <summary>
        /// Restore wallets from encrypted backup
        /// </summary>
        public static async Task<List<CryptoWalletGenerator.WalletInfo>> RestoreBackupAsync(string backupFile, string password)
        {
            if (!File.Exists(backupFile))
                throw new FileNotFoundException("Backup file not found");

            await using var fs = new FileStream(backupFile, FileMode.Open);
            using var br = new BinaryReader(fs);

            // Read header
            var magic = br.ReadString();
            if (magic != "WRBAK")
                throw new InvalidDataException("Invalid backup file format");

            var version = br.ReadByte();
            var timestamp = DateTime.FromBinary(br.ReadInt64());
            var walletCount = br.ReadInt32();

            // Read encrypted data
            var encryptedLength = br.ReadInt32();
            var encrypted = br.ReadBytes(encryptedLength);

            // Decrypt
            var compressed = Decrypt(encrypted, password);

            // Decompress
            var json = Encoding.UTF8.GetString(Decompress(compressed));

            // Deserialize
            var wallets = JsonSerializer.Deserialize<List<CryptoWalletGenerator.WalletInfo>>(json);

            return wallets ?? new List<CryptoWalletGenerator.WalletInfo>();
        }

        /// <summary>
        /// Create unencrypted JSON backup (not recommended, but faster)
        /// </summary>
        public static async Task<string> CreatePlainBackupAsync(List<CryptoWalletGenerator.WalletInfo> wallets)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var backupFile = Path.Combine(BackupFolder, $"wallet_backup_{timestamp}.json");

            var json = JsonSerializer.Serialize(wallets, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });

            await File.WriteAllTextAsync(backupFile, json);
            return backupFile;
        }

        /// <summary>
        /// Auto-backup a single wallet when found
        /// </summary>
        private static readonly object _backupLock = new object();

        public static void AutoBackupWallet(CryptoWalletGenerator.WalletInfo wallet)
        {
            try
            {
                var settings = AppSettings.Load();
                if (!settings.EnableAutoBackup) return;

                lock (_backupLock)
                {
                    var quickBackupFile = Path.Combine(BackupFolder, "found_wallets.json");
                    
                    List<CryptoWalletGenerator.WalletInfo> existing;
                    if (File.Exists(quickBackupFile))
                    {
                        var json = File.ReadAllText(quickBackupFile);
                        existing = JsonSerializer.Deserialize<List<CryptoWalletGenerator.WalletInfo>>(json) 
                            ?? new List<CryptoWalletGenerator.WalletInfo>();
                    }
                    else
                    {
                        existing = new List<CryptoWalletGenerator.WalletInfo>();
                    }

                    existing.Add(wallet);
                    
                    var updatedJson = JsonSerializer.Serialize(existing, new JsonSerializerOptions 
                    { 
                        WriteIndented = true 
                    });
                    
                    File.WriteAllText(quickBackupFile, updatedJson);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Auto-backup failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Get all backup files
        /// </summary>
        public static List<BackupFileInfo> GetBackupFiles()
        {
            var files = new List<BackupFileInfo>();
            
            foreach (var file in Directory.GetFiles(BackupFolder, "wallet_backup_*.*"))
            {
                var info = new FileInfo(file);
                files.Add(new BackupFileInfo
                {
                    FilePath = file,
                    FileName = info.Name,
                    CreatedAt = info.CreationTime,
                    SizeBytes = info.Length,
                    IsEncrypted = file.EndsWith(".wrbak")
                });
            }

            return files;
        }

        /// <summary>
        /// Delete old backups (keep only last N)
        /// </summary>
        public static int CleanupOldBackups(int keepCount = 10)
        {
            var files = GetBackupFiles();
            files.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt)); // Newest first

            int deleted = 0;
            for (int i = keepCount; i < files.Count; i++)
            {
                try
                {
                    File.Delete(files[i].FilePath);
                    deleted++;
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"Failed to delete backup {files[i].FileName}: {ex.Message}");
                }
            }

            return deleted;
        }

        /// <summary>
        /// Prepare backup for cloud upload (OneDrive, Google Drive, Dropbox)
        /// Returns path to the cloud sync folder if available
        /// </summary>
        public static string GetCloudSyncFolder()
        {
            var possibleFolders = new[]
            {
                // OneDrive
                Environment.GetEnvironmentVariable("OneDrive"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive"),
                
                // Google Drive
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Google Drive"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "GoogleDrive"),
                
                // Dropbox
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Dropbox"),
            };

            foreach (var folder in possibleFolders)
            {
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                    return folder;
            }

            return null;
        }

        /// <summary>
        /// Copy backup to cloud sync folder
        /// </summary>
        public static async Task<string> CopyToCloudAsync(string backupFile)
        {
            var cloudFolder = GetCloudSyncFolder();
            if (cloudFolder == null)
                throw new InvalidOperationException("No cloud sync folder found (OneDrive, Google Drive, or Dropbox)");

            var destFolder = Path.Combine(cloudFolder, "WalletRecoveryTool", "Backups");
            Directory.CreateDirectory(destFolder);

            var destFile = Path.Combine(destFolder, Path.GetFileName(backupFile));
            await Task.Run(() => File.Copy(backupFile, destFile, true));

            return destFile;
        }

        #region Encryption Helpers

        private static byte[] Encrypt(byte[] data, string password)
        {
            using var aes = Aes.Create();
            aes.KeySize = 256;
            
            // Generate salt and IV
            var salt = new byte[16];
            var iv = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt);
                rng.GetBytes(iv);
            }

            // Derive key from password
            using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, PBKDF2_ITERATIONS, HashAlgorithmName.SHA256);
            aes.Key = pbkdf2.GetBytes(32);
            aes.IV = iv;

            using var encryptor = aes.CreateEncryptor();
            var encrypted = encryptor.TransformFinalBlock(data, 0, data.Length);

            // Combine: salt + iv + encrypted
            var result = new byte[salt.Length + iv.Length + encrypted.Length];
            Buffer.BlockCopy(salt, 0, result, 0, salt.Length);
            Buffer.BlockCopy(iv, 0, result, salt.Length, iv.Length);
            Buffer.BlockCopy(encrypted, 0, result, salt.Length + iv.Length, encrypted.Length);

            return result;
        }

        private static byte[] Decrypt(byte[] data, string password)
        {
            // Extract salt, iv, encrypted data
            var salt = new byte[16];
            var iv = new byte[16];
            var encrypted = new byte[data.Length - 32];
            
            Buffer.BlockCopy(data, 0, salt, 0, 16);
            Buffer.BlockCopy(data, 16, iv, 0, 16);
            Buffer.BlockCopy(data, 32, encrypted, 0, encrypted.Length);

            using var aes = Aes.Create();
            aes.KeySize = 256;

            // Derive key from password
            using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, PBKDF2_ITERATIONS, HashAlgorithmName.SHA256);
            aes.Key = pbkdf2.GetBytes(32);
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
        }

        private static byte[] Compress(byte[] data)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
            {
                gzip.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }

        private static byte[] Decompress(byte[] data)
        {
            using var input = new MemoryStream(data);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }

        #endregion
    }

    public class BackupFileInfo
    {
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public DateTime CreatedAt { get; set; }
        public long SizeBytes { get; set; }
        public bool IsEncrypted { get; set; }
        
        public string SizeFormatted => SizeBytes < 1024 
            ? $"{SizeBytes} B" 
            : SizeBytes < 1048576 
                ? $"{SizeBytes / 1024.0:F1} KB" 
                : $"{SizeBytes / 1048576.0:F1} MB";
    }
}
