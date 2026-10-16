using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
            public bool IsEncrypted { get; set; }
        }

        public class BrowserWallet
        {
            public string Browser { get; set; }
            public string ExtensionName { get; set; }
            public string DataPath { get; set; }
            public List<string> FoundMnemonics { get; set; } = new List<string>();
            public List<string> FoundAddresses { get; set; } = new List<string>();
            public List<string> FoundPrivateKeys { get; set; } = new List<string>();
            public List<string> FoundEncryptedVaults { get; set; } = new List<string>();
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
                            var ext = Path.GetExtension(file).ToLowerInvariant();
                            walletFiles.Add(new WalletFile
                            {
                                FilePath = file,
                                FileType = ext,
                                FileSize = fileInfo.Length,
                                ModifiedDate = fileInfo.LastWriteTime,
                                DetectedWalletType = DetectWalletType(file),
                                IsEncrypted = ext == ".aes" || ext == ".dat" || file.Contains("keystore", StringComparison.OrdinalIgnoreCase)
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
        public List<string> ExtractMnemonicsFromText(string text)
        {
            var mnemonics = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
                return mnemonics;

            var bip39Words = Wordlist.English.GetWords().ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Extract clean alphabetic tokens so JSON quotes/brackets/commas don't hide seed words
            var words = Regex.Matches(text, @"\b[a-zA-Z]{3,8}\b")
                .Cast<Match>()
                .Select(m => m.Value.ToLowerInvariant())
                .ToList();

            var validLengths = new[] { 24, 21, 18, 15, 12 };

            for (int i = 0; i <= words.Count - 12; i++)
            {
                if (!bip39Words.Contains(words[i]))
                    continue;

                foreach (var length in validLengths)
                {
                    if (i + length > words.Count)
                        continue;

                    bool allBip39 = true;
                    for (int k = 0; k < length; k++)
                    {
                        if (!bip39Words.Contains(words[i + k]))
                        {
                            allBip39 = false;
                            break;
                        }
                    }

                    if (allBip39)
                    {
                        var mnemonic = string.Join(" ", words.Skip(i).Take(length));
                        
                        // Validate checksum AND reject trivial test vectors ("abandon abandon...") or already-empty seeds
                        if (!MnemonicTracker.IsRestrictedTrivialMnemonic(mnemonic) &&
                            !GlobalMnemonicTracker.Instance.WasScanned(mnemonic) &&
                            CryptoWalletGenerator.IsValidMnemonic(mnemonic))
                        {
                            mnemonics.Add(mnemonic);
                            i += length - 1; // Advance past matched seed
                            break;
                        }
                    }
                }
            }

            return mnemonics.Distinct().ToList();
        }

        /// <summary>
        /// Extracts checksum-verified Bitcoin WIF private keys (5..., K..., L...), BIP38 encrypted keys (6P...),
        /// and MetaMask/Phantom encrypted PBKDF2 vaults from raw text or LevelDB storage.
        /// </summary>
        public List<string> ExtractPrivateKeysAndVaultsFromText(string text)
        {
            var findings = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
                return findings;

            // 1. Bitcoin WIF Private Keys (Uncompressed '5' 51 chars, Compressed 'K'/'L' 52 chars)
            var wifMatches = Regex.Matches(text, @"\b([5KL][1-9A-HJ-NP-Za-km-z]{50,51})\b");
            foreach (Match m in wifMatches)
            {
                try
                {
                    var secret = new BitcoinSecret(m.Value, Network.Main);
                    var hex = secret.PrivateKey.ToHex().ToLowerInvariant();
                    if (!MnemonicTracker.IsRestrictedTrivialPrivateKey(hex) &&
                        !GlobalMnemonicTracker.Instance.WasScanned("pk:" + hex))
                    {
                        findings.Add($"WIF:{m.Value}");
                    }
                }
                catch { /* Invalid Base58Check checksum - ignore */ }
            }

            // 2. BIP38 Encrypted Bitcoin Keys (6P..., 58 chars)
            var bip38Matches = Regex.Matches(text, @"\b(6P[1-9A-HJ-NP-Za-km-z]{56})\b");
            foreach (Match m in bip38Matches)
            {
                findings.Add($"BIP38:{m.Value}");
            }

            // 3. MetaMask / Rabby / Extension Encrypted Vault JSON blobs
            var vaultMatches = Regex.Matches(text, @"\{[^{}]*""data""\s*:\s*""[^""]+""[^{}]*""iv""\s*:\s*""[^""]+""[^{}]*""salt""\s*:\s*""[^""]+""[^{}]*\}");
            foreach (Match m in vaultMatches)
            {
                findings.Add($"VAULT:{m.Value}");
            }

            return findings.Distinct().ToList();
        }

        public List<string> ExtractPrivateKeysFromText(string text) =>
            ExtractPrivateKeysAndVaultsFromText(text);

        /// <summary>
        /// Decrypts an encrypted wallet file or vault (MetaMask AES-GCM vault, Ethereum Web3 Keystore V3, or BIP38 key).
        /// Returns the recovered mnemonic or hex private key if the password is valid, or null otherwise.
        /// </summary>
        public string DecryptWalletFile(string filePathOrContent, string password)
        {
            if (string.IsNullOrWhiteSpace(filePathOrContent) || password == null)
                return null;

            try
            {
                string content = File.Exists(filePathOrContent)
                    ? File.ReadAllText(filePathOrContent)
                    : filePathOrContent.Trim();

                // 1. Check if content is a BIP38 encrypted key (6P...)
                if (content.StartsWith("6P", StringComparison.Ordinal) && content.Length == 58)
                {
                    return DecryptBip38Key(content, password);
                }

                // 2. Check if content is a MetaMask / Browser Extension AES-GCM Vault JSON
                if (content.Contains("\"data\"") && content.Contains("\"iv\"") && content.Contains("\"salt\""))
                {
                    var vaultPlaintext = DecryptMetaMaskVault(content, password);
                    if (!string.IsNullOrEmpty(vaultPlaintext))
                    {
                        var mnemonics = ExtractMnemonicsFromText(vaultPlaintext);
                        if (mnemonics.Count > 0)
                            return mnemonics[0];
                        return vaultPlaintext;
                    }
                }

                // 3. Check if content is an Ethereum Web3 Keystore V3 JSON
                if (content.Contains("\"crypto\"", StringComparison.OrdinalIgnoreCase) &&
                    content.Contains("\"ciphertext\"", StringComparison.OrdinalIgnoreCase))
                {
                    return DecryptEthereumKeystoreV3(content, password);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[ForensicWalletRecovery] DecryptWalletFile failed: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Decrypts a MetaMask / Rabby / Phantom PBKDF2 + AES-GCM encrypted vault JSON
        /// ({"data":"base64","iv":"base64","salt":"base64"}).
        /// </summary>
        public string DecryptMetaMaskVault(string vaultJson, string password)
        {
            if (string.IsNullOrWhiteSpace(vaultJson) || password == null)
                return null;

            try
            {
                var obj = JObject.Parse(vaultJson);
                string dataB64 = obj["data"]?.ToString();
                string ivB64 = obj["iv"]?.ToString();
                string saltB64 = obj["salt"]?.ToString();
                if (string.IsNullOrEmpty(dataB64) || string.IsNullOrEmpty(ivB64) || string.IsNullOrEmpty(saltB64))
                    return null;

                int iterations = obj["keyMetadata"]?["params"]?["iterations"]?.Value<int>() ?? 10000;
                byte[] cipherWithTag = Convert.FromBase64String(dataB64);
                byte[] iv = Convert.FromBase64String(ivB64);
                byte[] salt = Convert.FromBase64String(saltB64);

                if (cipherWithTag.Length <= 16)
                    return null;

                byte[] key = Rfc2898DeriveBytes.Pbkdf2(
                    Encoding.UTF8.GetBytes(password),
                    salt,
                    iterations,
                    HashAlgorithmName.SHA256,
                    32);

                int cipherLen = cipherWithTag.Length - 16;
                byte[] ciphertext = cipherWithTag.AsSpan(0, cipherLen).ToArray();
                byte[] tag = cipherWithTag.AsSpan(cipherLen, 16).ToArray();
                byte[] plaintext = new byte[cipherLen];

                using var aesGcm = new AesGcm(key, 16);
                aesGcm.Decrypt(iv, ciphertext, tag, plaintext);
                return Encoding.UTF8.GetString(plaintext);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Decrypts an Ethereum Web3 Secret Storage Definition (Keystore V3) JSON using PBKDF2-SHA256 + AES-128-CTR.
        /// Returns the 64-character lowercase hex private key on success, or null if MAC verification fails.
        /// </summary>
        public string DecryptEthereumKeystoreV3(string keystoreJson, string password)
        {
            if (string.IsNullOrWhiteSpace(keystoreJson) || password == null)
                return null;

            try
            {
                var root = JObject.Parse(keystoreJson);
                var cryptoNode = (root["crypto"] ?? root["Crypto"]) as JObject;
                if (cryptoNode == null)
                    return null;

                string cipherTextHex = cryptoNode["ciphertext"]?.ToString();
                string ivHex = cryptoNode["cipherparams"]?["iv"]?.ToString();
                string kdf = cryptoNode["kdf"]?.ToString()?.ToLowerInvariant();
                string macHex = cryptoNode["mac"]?.ToString()?.ToLowerInvariant();
                var kdfParams = cryptoNode["kdfparams"] as JObject;

                if (string.IsNullOrEmpty(cipherTextHex) || string.IsNullOrEmpty(ivHex) || kdfParams == null)
                    return null;

                byte[] ciphertext = Convert.FromHexString(cipherTextHex);
                byte[] iv = Convert.FromHexString(ivHex);
                byte[] salt = Convert.FromHexString(kdfParams["salt"]?.ToString() ?? string.Empty);
                int dklen = kdfParams["dklen"]?.Value<int>() ?? 32;

                byte[] derivedKey;
                if (kdf == "pbkdf2")
                {
                    int c = kdfParams["c"]?.Value<int>() ?? 262144;
                    derivedKey = Rfc2898DeriveBytes.Pbkdf2(
                        Encoding.UTF8.GetBytes(password),
                        salt,
                        c,
                        HashAlgorithmName.SHA256,
                        dklen);
                }
                else
                {
                    return null;
                }

                // Verify MAC (Keccak-256 or SHA-256 of derivedKey[16..32] || ciphertext)
                byte[] macInput = new byte[16 + ciphertext.Length];
                Buffer.BlockCopy(derivedKey, 16, macInput, 0, 16);
                Buffer.BlockCopy(ciphertext, 0, macInput, 16, ciphertext.Length);

                var keccak = new Nethereum.Util.Sha3Keccack();
                string computedKeccakMac = Convert.ToHexString(keccak.CalculateHash(macInput)).ToLowerInvariant();
                string computedSha256Mac = Convert.ToHexString(SHA256.HashData(macInput)).ToLowerInvariant();

                if (!string.IsNullOrEmpty(macHex) &&
                    !string.Equals(macHex, computedKeccakMac, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(macHex, computedSha256Mac, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                // AES-128-CTR decryption using first 16 bytes of derivedKey
                byte[] encKey = derivedKey.AsSpan(0, 16).ToArray();
                byte[] plainKey = Aes128CtrTransform(encKey, iv, ciphertext);
                string hexKey = Convert.ToHexString(plainKey).ToLowerInvariant();

                return MnemonicTracker.IsRestrictedTrivialPrivateKey(hexKey) ? null : hexKey;
            }
            catch
            {
                return null;
            }
        }

        private static byte[] Aes128CtrTransform(byte[] key16, byte[] iv16, byte[] input)
        {
            using var aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.Key = key16;

            using var encryptor = aes.CreateEncryptor();
            byte[] counter = (byte[])iv16.Clone();
            byte[] keystreamBlock = new byte[16];
            byte[] output = new byte[input.Length];

            for (int offset = 0; offset < input.Length; offset += 16)
            {
                encryptor.TransformBlock(counter, 0, 16, keystreamBlock, 0);
                int chunk = Math.Min(16, input.Length - offset);
                for (int i = 0; i < chunk; i++)
                {
                    output[offset + i] = (byte)(input[offset + i] ^ keystreamBlock[i]);
                }

                // Increment 128-bit big-endian counter
                for (int c = 15; c >= 0; c--)
                {
                    if (++counter[c] != 0)
                        break;
                }
            }

            return output;
        }

        /// <summary>
        /// Decrypts a BIP38 password-protected Bitcoin private key (6P...) using NBitcoin.
        /// Returns the 64-character hex private key if the passphrase is valid, or null otherwise.
        /// </summary>
        public string DecryptBip38Key(string bip38EncryptedKey, string passphrase)
        {
            if (string.IsNullOrWhiteSpace(bip38EncryptedKey) || passphrase == null)
                return null;

            try
            {
                var encryptedSecret = BitcoinEncryptedSecret.Create(bip38EncryptedKey.Trim(), Network.Main);
                var key = encryptedSecret.GetKey(passphrase);
                return key.ToHex().ToLowerInvariant();
            }
            catch
            {
                return null;
            }
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

                        // Extract WIF private keys, BIP38 keys, and encrypted extension vaults
                        foreach (var item in ExtractPrivateKeysAndVaultsFromText(content))
                        {
                            if (item.StartsWith("VAULT:"))
                                wallet.FoundEncryptedVaults.Add(item.Substring(6));
                            else
                                wallet.FoundPrivateKeys.Add(item);
                        }
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
