using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using WalletRecoveryTool.Core;

namespace WalletRecoveryTool
{
    /// <summary>
    /// Event handlers for advanced recovery features (Forensic, Fingerprint, Pattern Analysis)
    /// </summary>
    public partial class MainWindow
    {
        // Forensic Scan
        public async void ForensicScan_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ForensicScanButton.IsEnabled = false;
                ForensicResultsText.Text = "🔍 Starting forensic scan...";

                var scanner = new ForensicWalletRecovery();
                var results = new List<string>();
                int foundCount = 0;

                await Task.Run(async () =>
                {
                    // Scan for wallet files
                    var walletFiles = scanner.ScanForWalletFiles();
                    foreach (var file in walletFiles)
                    {
                        results.Add($"✓ Found wallet file: {file.FilePath} ({file.DetectedWalletType})");
                        foundCount++;
                    }

                    // Extract from browsers
                    if (IncludeBrowserCheckBox.IsChecked == true)
                    {
                        var browsers = new[] { 
                            ForensicWalletRecovery.BrowserType.Chrome, 
                            ForensicWalletRecovery.BrowserType.Firefox,
                            ForensicWalletRecovery.BrowserType.Brave,
                            ForensicWalletRecovery.BrowserType.Edge 
                        };
                        foreach (var browser in browsers)
                        {
                            var browserWallets = await scanner.ExtractFromBrowser(browser);
                            foreach (var bw in browserWallets)
                            {
                                foreach (var mnemonic in bw.FoundMnemonics)
                                {
                                    results.Add($"🌐 [{bw.Browser}] Found mnemonic: {mnemonic.Substring(0, Math.Min(50, mnemonic.Length))}...");
                                    foundCount++;
                                }
                            }
                        }
                    }
                });

                Dispatcher.Invoke(() =>
                {
                    if (results.Count > 0)
                    {
                        ForensicResultsText.Text = $"✅ FOUND {foundCount} RESULTS:\n\n" + string.Join("\n", results);
                        ForensicResultsText.Foreground = System.Windows.Media.Brushes.LimeGreen;
                    }
                    else
                    {
                        ForensicResultsText.Text = "❌ No wallet data found on this system.";
                        ForensicResultsText.Foreground = System.Windows.Media.Brushes.Orange;
                    }
                });
            }
            catch (Exception ex)
            {
                ForensicResultsText.Text = $"❌ Error: {ex.Message}";
                ForensicResultsText.Foreground = System.Windows.Media.Brushes.Red;
            }
            finally
            {
                ForensicScanButton.IsEnabled = true;
            }
        }

        // Wallet Fingerprinting
        public async void WalletFingerprint_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var address = FingerprintAddressTextBox.Text.Trim();
                if (string.IsNullOrEmpty(address))
                {
                    MessageBox.Show("Please enter an address to analyze.", "Missing Address", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                FingerprintButton.IsEnabled = false;
                FingerprintResultsText.Text = "🔍 Analyzing address...";

                WalletFingerprinting.WalletPattern result = null;

                await Task.Run(() =>
                {
                    result = WalletFingerprinting.DetectWalletSoftware(address);
                });

                if (result != null)
                {
                    var resultText = $"🔍 WALLET DETECTED\n\n";
                    resultText += $"Software: {result.WalletType} (Confidence: {result.ConfidenceScore}%)\n";
                    resultText += $"Derivation Path: {result.SuggestedDerivationPath}\n";
                    resultText += $"Description: {result.Description}\n";

                    FingerprintResultsText.Text = resultText;
                    FingerprintResultsText.Foreground = System.Windows.Media.Brushes.LimeGreen;

                    if (!string.IsNullOrEmpty(result.SuggestedDerivationPath))
                    {
                        SmartMnemonicAddressTextBox.Text = address;
                    }
                }
                else
                {
                    FingerprintResultsText.Text = "❌ Unable to detect wallet software from this address.";
                    FingerprintResultsText.Foreground = System.Windows.Media.Brushes.Orange;
                }
            }
            catch (Exception ex)
            {
                FingerprintResultsText.Text = $"❌ Error: {ex.Message}";
                FingerprintResultsText.Foreground = System.Windows.Media.Brushes.Red;
            }
            finally
            {
                FingerprintButton.IsEnabled = true;
            }
        }

        // Blockchain Pattern Analysis
        public async void BlockchainPatternAnalysis_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var address = PatternAnalysisAddressTextBox.Text.Trim();
                if (string.IsNullOrEmpty(address))
                {
                    MessageBox.Show("Please enter an address to analyze.", "Missing Address", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                PatternAnalysisButton.IsEnabled = false;
                PatternAnalysisResultsText.Text = "📊 Analyzing blockchain data...";

                var analyzer = new BlockchainPatternAnalyzer();
                BlockchainPatternAnalyzer.WalletMetadata metadata = null;

                await Task.Run(async () =>
                {
                    metadata = await analyzer.AnalyzeWalletBehavior(address);
                });

                if (metadata != null)
                {
                    var resultText = $"📊 BLOCKCHAIN ANALYSIS\n\n";
                    resultText += $"Address: {metadata.Address}\n";
                    resultText += $"Balance: {metadata.CurrentBalance:F8}\n";
                    resultText += $"Total Received: {metadata.TotalReceived:F8}\n";
                    resultText += $"Total Sent: {metadata.TotalSent:F8}\n";
                    resultText += $"Transactions: {metadata.TransactionCount}\n\n";

                    if (metadata.FirstSeen.HasValue)
                        resultText += $"First Seen: {metadata.FirstSeen:yyyy-MM-dd}\n";
                    if (metadata.LastActivity.HasValue)
                        resultText += $"Last Activity: {metadata.LastActivity:yyyy-MM-dd}\n";

                    resultText += $"\nWallet Software: {metadata.LikelyWalletSoftware}\n";
                    resultText += $"Derivation Path: {metadata.DerivationPathHint}\n\n";

                    if (metadata.AddressPattern.Count > 0)
                    {
                        resultText += "Patterns Detected:\n";
                        foreach (var pattern in metadata.AddressPattern)
                        {
                            resultText += $"• {pattern}\n";
                        }
                    }

                    PatternAnalysisResultsText.Text = resultText;
                    PatternAnalysisResultsText.Foreground = System.Windows.Media.Brushes.LimeGreen;
                }
                else
                {
                    PatternAnalysisResultsText.Text = "❌ Unable to analyze this address.";
                    PatternAnalysisResultsText.Foreground = System.Windows.Media.Brushes.Orange;
                }
            }
            catch (Exception ex)
            {
                PatternAnalysisResultsText.Text = $"❌ Error: {ex.Message}";
                PatternAnalysisResultsText.Foreground = System.Windows.Media.Brushes.Red;
            }
            finally
            {
                PatternAnalysisButton.IsEnabled = true;
            }
        }
    }
}
