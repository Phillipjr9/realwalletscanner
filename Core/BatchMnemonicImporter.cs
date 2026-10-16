using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Import and process mnemonics from files for bulk checking
    /// Supports various formats: one per line, JSON, CSV, mixed text
    /// </summary>
    public class BatchMnemonicImporter
    {
        private readonly BalanceChecker _balanceChecker;
        private readonly WalletLogger _logger;
        
        // Events for progress tracking
        public event EventHandler<BatchProgressEventArgs> ProgressUpdated;
        public event EventHandler<WalletFoundEventArgs> WalletFound;
        public event EventHandler<string> StatusChanged;

        public BatchMnemonicImporter()
        {
            var settings = AppSettings.Load();
            _balanceChecker = new BalanceChecker(settings);
            _logger = new WalletLogger(settings.EnableLogging);
        }

        /// <summary>
        /// Import mnemonics from a file
        /// </summary>
        public List<string> ImportFromFile(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"File not found: {filePath}");

            var extension = Path.GetExtension(filePath).ToLower();
            var content = File.ReadAllText(filePath);

            return extension switch
            {
                ".json" => ImportFromJson(content),
                ".csv" => ImportFromCsv(content),
                _ => ImportFromText(content)
            };
        }

        /// <summary>
        /// Import from plain text (one mnemonic per line or mixed text)
        /// </summary>
        private List<string> ImportFromText(string content)
        {
            var mnemonics = new List<string>();
            var lines = content.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var cleaned = line.Trim();
                
                // Skip comments and empty lines
                if (string.IsNullOrEmpty(cleaned) || cleaned.StartsWith("#") || cleaned.StartsWith("//"))
                    continue;

                // Try to extract mnemonic from line
                var extracted = ExtractMnemonic(cleaned);
                if (extracted != null)
                    mnemonics.Add(extracted);
            }

            return mnemonics.Distinct().ToList();
        }

        /// <summary>
        /// Import from JSON file
        /// </summary>
        private List<string> ImportFromJson(string content)
        {
            var mnemonics = new List<string>();
            
            try
            {
                // Try as array of strings
                var array = System.Text.Json.JsonSerializer.Deserialize<string[]>(content);
                if (array != null)
                    mnemonics.AddRange(array.Where(m => IsValidMnemonic(m)));
            }
            catch
            {
                // Try as array of objects with "mnemonic" field
                try
                {
                    var objects = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, object>>>(content);
                    if (objects != null)
                    {
                        foreach (var obj in objects)
                        {
                            if (obj.TryGetValue("mnemonic", out var m) || obj.TryGetValue("seed", out m) || obj.TryGetValue("phrase", out m))
                            {
                                var mnemonic = m?.ToString();
                                if (!string.IsNullOrWhiteSpace(mnemonic) && IsValidMnemonic(mnemonic))
                                    mnemonics.Add(mnemonic);
                            }
                        }
                    }
                }
                catch
                {
                    // Fall back to text extraction
                    mnemonics = ImportFromText(content);
                }
            }

            return mnemonics.Distinct().ToList();
        }

        /// <summary>
        /// Import from CSV file
        /// </summary>
        private List<string> ImportFromCsv(string content)
        {
            var mnemonics = new List<string>();
            var lines = content.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines.Skip(1)) // Skip header
            {
                var columns = line.Split(',');
                foreach (var col in columns)
                {
                    var cleaned = col.Trim().Trim('"');
                    if (IsValidMnemonic(cleaned))
                    {
                        mnemonics.Add(cleaned);
                        break; // Only take first valid mnemonic per row
                    }
                }
            }

            return mnemonics.Distinct().ToList();
        }

        /// <summary>
        /// Extract mnemonic from text that might contain other data
        /// </summary>
        private string ExtractMnemonic(string text)
        {
            // Remove common prefixes
            text = Regex.Replace(text, @"^(mnemonic|seed|phrase|recovery)[:\s=]+", "", RegexOptions.IgnoreCase);
            text = text.Trim().Trim('"').Trim('\'');

            if (IsValidMnemonic(text))
                return text;

            // Try to find 12/24 consecutive BIP39 words
            var words = text.Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            
            // Check for 12-word sequence
            for (int i = 0; i <= words.Length - 12; i++)
            {
                var candidate = string.Join(" ", words.Skip(i).Take(12));
                if (IsValidMnemonic(candidate))
                    return candidate;
            }

            // Check for 24-word sequence
            for (int i = 0; i <= words.Length - 24; i++)
            {
                var candidate = string.Join(" ", words.Skip(i).Take(24));
                if (IsValidMnemonic(candidate))
                    return candidate;
            }

            return null;
        }

        /// <summary>
        /// Validate if string is a valid BIP39 mnemonic
        /// </summary>
        public static bool IsValidMnemonic(string mnemonic)
        {
            if (string.IsNullOrWhiteSpace(mnemonic))
                return false;

            var words = mnemonic.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            
            // Must be 12, 15, 18, 21, or 24 words
            if (words.Length != 12 && words.Length != 15 && words.Length != 18 && words.Length != 21 && words.Length != 24)
                return false;

            return CryptoWalletGenerator.IsValidMnemonic(string.Join(" ", words));
        }

        /// <summary>
        /// Process imported mnemonics with balance checking
        /// </summary>
        public async Task ProcessMnemonicsAsync(
            List<string> mnemonics, 
            bool scanBtc, bool scanEth, bool scanSol, bool scanUsdt,
            CancellationToken token)
        {
            var total = mnemonics.Count;
            var processed = 0;
            var found = 0;

            StatusChanged?.Invoke(this, $"Processing {total} mnemonics...");

            foreach (var mnemonic in mnemonics)
            {
                if (token.IsCancellationRequested)
                    break;

                try
                {
                    var tracker = GlobalMnemonicTracker.Instance;

                    // STRICT NON-REPETITION RULE: Skip if already scanned empty in any section
                    if (DuplicateDetector.CheckAndMarkMnemonic(mnemonic) || !tracker.TryMarkAsScanned(mnemonic))
                    {
                        processed++;
                        continue;
                    }

                    // Generate wallet
                    var wallet = CryptoWalletGenerator.GenerateWallet(mnemonic);
                    if (wallet == null)
                    {
                        processed++;
                        continue; // Invalid BIP39 checksum — skip
                    }

                    if (tracker.IsAddressOrKeyScannedEmpty(wallet.BitcoinAddress) &&
                        tracker.IsAddressOrKeyScannedEmpty(wallet.EthereumAddress))
                    {
                        processed++;
                        continue;
                    }

                    // Check balances
                    var balances = await _balanceChecker.CheckAllBalances(wallet, scanBtc, scanEth, scanSol, scanUsdt);
                    wallet.Balances = balances;

                    // Check if found
                    if (balances.Any(b => b.Value > 0.00000001m))
                    {
                        tracker.MarkWalletConfirmedFunded(wallet);
                        found++;
                        wallet.FoundAt = DateTime.Now;
                        wallet.Source = "Batch Import";
                        _logger.LogWalletFound(wallet);
                        WalletFound?.Invoke(this, new WalletFoundEventArgs(wallet));
                    }
                    else
                    {
                        tracker.MarkWalletConfirmedEmpty(wallet);
                    }

                    processed++;
                    ProgressUpdated?.Invoke(this, new BatchProgressEventArgs
                    {
                        Current = processed,
                        Total = total,
                        Found = found,
                        CurrentMnemonic = mnemonic.Substring(0, Math.Min(30, mnemonic.Length)) + "...",
                        Percentage = (double)processed / total * 100
                    });
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"Error processing mnemonic: {ex.Message}");
                    processed++;
                }
            }

            StatusChanged?.Invoke(this, $"Completed! Processed {processed}/{total}, Found {found} with balance.");
        }
    }

    public class BatchProgressEventArgs : EventArgs
    {
        public int Current { get; set; }
        public int Total { get; set; }
        public int Found { get; set; }
        public string CurrentMnemonic { get; set; }
        public double Percentage { get; set; }
    }
}
