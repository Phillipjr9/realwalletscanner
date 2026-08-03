using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NBitcoin;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Scans disk, browser storage, and memory for cached wallet data
    /// This is often the FASTEST way to recover wallets - users forget about cached data
    /// </summary>
    public class ForensicWalletRecovery
    {
        public class WalletFile
        {
            public string FilePath { get; set; }
            public string FileType { get; set; }
            public long FileSize { get; set; }
            public DateTime ModifiedDate { get; set; }
            public string DetectedWalletType { get; set; }
        }

        public class BrowserWallet
        {
            public string Browser { get; set; }
            public string ExtensionName { get; set; }
            public string DataPath { get; set; }
            public List<string> FoundMnemonics { get; set; } = new List<string>();
            public List<string> FoundAddresses { get; set; } = new List<string>();
        }

        /// <summary>
        /// Scans common wallet file locations
        /// </summary>
        public List<WalletFile> ScanForWalletFiles(string drivePath = null)
        {
            var walletFiles = new List<WalletFile>();
            var searchPaths = GetCommonWalletPaths(drivePath);

            DebugLogger.Log($"Scanning {searchPaths.Count} common wallet locations...");

            foreach (var searchPath in searchPaths)
            {
                try
                {
                    if (!Directory.Exists(searchPath))
                        continue;

                    // Scan for wallet files
                    var files = Directory.GetFiles(searchPath, "*.*", SearchOption.AllDirectories)
                        .Where(f => IsLikelyWalletFile(f))
                        .ToList();

                    foreach (var file in files)
                    {
                        try
                        {
                            var fileInfo = new FileInfo(file);
                            walletFiles.Add(new WalletFile
                            {
                                FilePath = file,
                                FileType = Path.GetExtension(file),
                                FileSize = fileInfo.Length,
                                ModifiedDate = fileInfo.LastWriteTime,
                                DetectedWalletType = DetectWalletType(file)
                            });
                        }
                        catch (Exception ex) { DebugLogger.Log($"File scan error: {ex.Message}"); }
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"Error scanning {searchPath}: {ex.Message}");
                }
            }

            DebugLogger.Log($"Found {walletFiles.Count} potential wallet files");
            return walletFiles;
        }

        /// <summary>
        /// Extracts wallet data from browser storage (Chrome, Firefox, Brave)
        /// </summary>
        public async Task<List<BrowserWallet>> ExtractFromBrowser(BrowserType browser)
        {
            var browserWallets = new List<BrowserWallet>();
            var browserPaths = GetBrowserPaths(browser);

            foreach (var path in browserPaths)
            {
                try
                {
                    if (!Directory.Exists(path))
                        continue;

                    // Scan for extension data
                    var extensionDirs = Directory.GetDirectories(path);

                    foreach (var extDir in extensionDirs)
                    {
                        var wallet = await ScanExtensionDirectory(extDir, browser.ToString());
                        if (wallet != null && (wallet.FoundMnemonics.Any() || wallet.FoundAddresses.Any()))
                        {
                            browserWallets.Add(wallet);
                        }
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"Error scanning browser {browser}: {ex.Message}");
                }
            }

            return browserWallets;
        }

        /// <summary>
        /// Scans memory dump for mnemonics and private keys
        /// </summary>
        public List<string> ScanMemoryDump(string memoryDumpPath)
        {
            var foundMnemonics = new List<string>();

            if (!File.Exists(memoryDumpPath))
                return foundMnemonics;

            DebugLogger.Log($"Scanning memory dump: {memoryDumpPath}");

            try
            {
                // Read file in chunks to avoid memory issues
                const int maxChunks = 1024; // Cap at 1GB total processing (1024 * 1MB)
                int chunksRead = 0;
                using (var fs = new FileStream(memoryDumpPath, FileMode.Open, FileAccess.Read))
                using (var reader = new StreamReader(fs))
                {
                    var buffer = new char[1024 * 1024]; // 1MB chunks
                    int charsRead;

                    while ((charsRead = reader.Read(buffer, 0, buffer.Length)) > 0 && chunksRead < maxChunks)
                    {
                        var chunk = new string(buffer, 0, charsRead);
                        
                        // Look for 12-word patterns
                        var mnemonics = ExtractMnemonicsFromText(chunk);
                        foundMnemonics.AddRange(mnemonics);
                        chunksRead++;
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Error scanning memory dump: {ex.Message}");
            }

            return foundMnemonics.Distinct().ToList();
        }

        /// <summary>
        /// Scans text files for mnemonics (notes, backups, etc.)
        /// </summary>
        public List<string> ScanTextFilesForMnemonics(string directoryPath)
        {
            var foundMnemonics = new List<string>();
            var textExtensions = new[] { ".txt", ".doc", ".docx", ".pdf", ".rtf", ".md" };

            try
            {
                var files = Directory.GetFiles(directoryPath, "*.*", SearchOption.AllDirectories)
                    .Where(f => textExtensions.Contains(Path.GetExtension(f).ToLower()))
                    .ToList();

                foreach (var file in files)
                {
                    try
                    {
                        if (new FileInfo(file).Length > 10 * 1024 * 1024) // Skip files > 10MB
                            continue;

                        var content = File.ReadAllText(file);
                        var mnemonics = ExtractMnemonicsFromText(content);
                        foundMnemonics.AddRange(mnemonics);
                    }
                    catch (Exception ex) { DebugLogger.Log($"Text file parse error: {ex.Message}"); }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogError("ScanTextFiles", ex);
            }

            return foundMnemonics.Distinct().ToList();
        }

        /// <summary>
        /// Extracts potential mnemonics from text
        /// </summary>
        private List<string> ExtractMnemonicsFromText(string text)
        {
            var mnemonics = new List<string>();
            var bip39Words = Wordlist.English.GetWords().ToHashSet();

            // Split into potential word sequences
            var words = Regex.Split(text, @"\s+")
                .Select(w => w.Trim().ToLower())
                .Where(w => !string.IsNullOrWhiteSpace(w))
                .ToList();

            // Look for 12, 15, 18, 21, or 24 consecutive BIP39 words
            var validLengths = new[] { 12, 15, 18, 21, 24 };

            for (int i = 0; i <= words.Count - 12; i++)
            {
                foreach (var length in validLengths)
                {
                    if (i + length > words.Count)
                        continue;

                    var sequence = words.Skip(i).Take(length).ToList();
                    
                    // Check if all words are BIP39 words
                    if (sequence.All(w => bip39Words.Contains(w)))
                    {
                        var mnemonic = string.Join(" ", sequence);
                        
                        // Validate checksum
                        if (CryptoWalletGenerator.IsValidMnemonic(mnemonic))
                        {
                            mnemonics.Add(mnemonic);
                        }
                    }
                }
            }

            return mnemonics;
        }

        private async Task<BrowserWallet> ScanExtensionDirectory(string extDir, string browserName)
        {
            var wallet = new BrowserWallet
            {
                Browser = browserName,
                DataPath = extDir
            };

            try
            {
                // Look for Local Storage files
                var storageFiles = Directory.GetFiles(extDir, "*", SearchOption.AllDirectories)
                    .Where(f => f.Contains("Local Storage") || f.Contains("IndexedDB"))
                    .ToList();

                foreach (var file in storageFiles)
                {
                    try
                    {
                        var content = File.ReadAllText(file);
                        
                        // Extract mnemonics
                        wallet.FoundMnemonics.AddRange(ExtractMnemonicsFromText(content));
                        
                        // Extract addresses
                        wallet.FoundAddresses.AddRange(ExtractAddressesFromText(content));
                    }
                    catch (Exception ex) { DebugLogger.Log($"Extension content parse error: {ex.Message}"); }
                }

                // Detect extension name
                var manifestPath = Path.Combine(extDir, "manifest.json");
                if (File.Exists(manifestPath))
                {
                    var manifest = JsonConvert.DeserializeObject<dynamic>(File.ReadAllText(manifestPath));
                    wallet.ExtensionName = manifest.name;
                }
            }
            catch (Exception ex) { DebugLogger.Log($"Extension analysis error: {ex.Message}"); }

            return wallet;
        }

        private List<string> ExtractAddressesFromText(string text)
        {
            var addresses = new List<string>();

            // Bitcoin addresses
            var btcPattern = @"\b(1|3|bc1)[a-zA-HJ-NP-Z0-9]{25,62}\b";
            var btcMatches = Regex.Matches(text, btcPattern);
            addresses.AddRange(btcMatches.Cast<Match>().Select(m => m.Value));

            // Ethereum addresses
            var ethPattern = @"\b0x[a-fA-F0-9]{40}\b";
            var ethMatches = Regex.Matches(text, ethPattern);
            addresses.AddRange(ethMatches.Cast<Match>().Select(m => m.Value));

            return addresses.Distinct().ToList();
        }

        private List<string> GetCommonWalletPaths(string drivePath)
        {
            var basePath = drivePath ?? "C:";
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            return new List<string>
            {
                Path.Combine(appData, "Bitcoin"),
                Path.Combine(appData, "Ethereum"),
                Path.Combine(appData, "Electrum"),
                Path.Combine(appData, "Exodus"),
                Path.Combine(localAppData, "Coinomi"),
                Path.Combine(localAppData, "atomic"),
                Path.Combine(userProfile, ".bitcoin"),
                Path.Combine(userProfile, ".ethereum"),
                Path.Combine(userProfile, "Documents", "Wallets"),
                Path.Combine(userProfile, "Downloads"),
                Path.Combine(userProfile, "Desktop")
            };
        }

        private List<string> GetBrowserPaths(BrowserType browser)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            return browser switch
            {
                BrowserType.Chrome => new List<string>
                {
                    Path.Combine(localAppData, @"Google\Chrome\User Data\Default\Extensions"),
                    Path.Combine(localAppData, @"Google\Chrome\User Data\Default\Local Extension Settings")
                },
                BrowserType.Firefox => new List<string>
                {
                    Path.Combine(appData, @"Mozilla\Firefox\Profiles")
                },
                BrowserType.Brave => new List<string>
                {
                    Path.Combine(localAppData, @"BraveSoftware\Brave-Browser\User Data\Default\Extensions")
                },
                BrowserType.Edge => new List<string>
                {
                    Path.Combine(localAppData, @"Microsoft\Edge\User Data\Default\Extensions")
                },
                _ => new List<string>()
            };
        }

        private bool IsLikelyWalletFile(string filePath)
        {
            var fileName = Path.GetFileName(filePath).ToLower();
            var extension = Path.GetExtension(filePath).ToLower();

            var walletKeywords = new[] { "wallet", "bitcoin", "ethereum", "crypto", "seed", "mnemonic", "private", "backup" };
            var walletExtensions = new[] { ".dat", ".wallet", ".key", ".json", ".aes", ".backup" };

            return walletKeywords.Any(k => fileName.Contains(k)) || walletExtensions.Contains(extension);
        }

        private string DetectWalletType(string filePath)
        {
            var fileName = Path.GetFileName(filePath).ToLower();

            if (fileName.Contains("electrum")) return "Electrum";
            if (fileName.Contains("exodus")) return "Exodus";
            if (fileName.Contains("coinomi")) return "Coinomi";
            if (fileName.Contains("atomic")) return "Atomic";
            if (fileName == "wallet.dat") return "Bitcoin Core";
            if (fileName.Contains("keystore")) return "Ethereum Keystore";

            return "Unknown";
        }

        public enum BrowserType
        {
            Chrome,
            Firefox,
            Brave,
            Edge,
            Opera
        }
    }
}
