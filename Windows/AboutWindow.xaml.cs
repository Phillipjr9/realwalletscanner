using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using WalletRecoveryTool.Core;

namespace WalletRecoveryTool
{
    public partial class AboutWindow : Window
    {
        private string _supportInfo = string.Empty;

        public AboutWindow()
        {
            InitializeComponent();
            LoadSystemInfo();
            
            // Set version number
            var version = UpdateChecker.GetCurrentVersion();
            VersionTextBlock.Text = $"Wallet Recovery Tool v{version}";
        }

        private void LoadSystemInfo()
        {
            try
            {
                var info = $"Application Information:\n";
                info += $"  Version: {UpdateChecker.GetCurrentVersion()}\n";
                info += $"  Build Date: {GetBuildDate():yyyy-MM-dd HH:mm:ss}\n";
                info += $"  .NET Version: {RuntimeInformation.FrameworkDescription}\n";
                info += $"  Owner: {AppSignature.GetOwner()}\n";
                info += $"  Signature: {AppSignature.GetSignatureCode()}\n";
                info += $"  Build Fingerprint: {AppSignature.GetBuildFingerprint()}\n";
                info += $"  {AppSignature.GetCopyright()}\n\n";
                
                info += $"System Information:\n";
                info += $"  OS: {Environment.OSVersion}\n";
                info += $"  Architecture: {RuntimeInformation.OSArchitecture}\n";
                info += $"  Machine Name: {Environment.MachineName}\n";
                info += $"  User Name: {Environment.UserName}\n";
                info += $"  CPU Cores: {Environment.ProcessorCount}\n";
                info += $"  System RAM: {GetTotalMemoryMB():N0} MB\n";
                info += $"  Working Set: {Environment.WorkingSet / 1024 / 1024:N0} MB\n\n";
                
                info += $"Application Paths:\n";
                info += $"  Executable: {Environment.ProcessPath}\n";
                info += $"  Install Directory: {GetInstallDirectory()}\n";
                info += $"  Settings: {Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}\\WalletRecoveryTool\n";
                info += $"  Database: {Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}\\WalletRecoveryTool\\Database\n";
                info += $"  Logs: {Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)}\\WalletRecoveryTool\\Logs\n\n";

                var (clusterNodeIndex, clusterNodeCount, clusterSource, clusterDeviceId) = GetClusterInfo();
                info += $"Cluster Assignment:\n";
                info += $"  Node: {clusterNodeIndex + 1}/{clusterNodeCount}\n";
                info += $"  Source: {clusterSource}\n";
                info += $"  Device ID: {clusterDeviceId}\n\n";
                
                var settings = AppSettings.Load();
                info += $"Configuration:\n";
                info += $"  Thread Count: {settings.ThreadCount}\n";
                info += $"  API Delay: {settings.CheckDelayMs}ms\n";
                info += $"  Max Concurrent APIs: {settings.MaxConcurrentApiCalls}\n";
                info += $"  Cache Enabled: {settings.EnableBalanceCache}\n";
                info += $"  Quick Scan: {settings.QuickScanMode}\n";
                info += $"  Save Found Wallets: {settings.SaveFoundWalletsOnly}\n";
                info += $"  Auto-Update: {settings.CheckForUpdatesOnStartup}\n";
                info += $"  Dark Theme: {settings.UseDarkTheme}\n";
                info += $"  Email Alerts: {settings.EnableEmailAlerts}\n";

                _supportInfo = info;
                
                SystemInfoTextBlock.Text = info;
            }
            catch (Exception ex)
            {
                SystemInfoTextBlock.Text = $"Failed to load system info:\n{ex.Message}";
                _supportInfo = SystemInfoTextBlock.Text;
            }
        }

        private static string GetInstallDirectory()
        {
            try
            {
                var processPath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(processPath))
                {
                    var dir = Path.GetDirectoryName(processPath);
                    if (!string.IsNullOrWhiteSpace(dir))
                        return dir;
                }
            }
            catch { }

            return AppContext.BaseDirectory;
        }

        private static (int nodeIndex, int nodeCount, string source, string deviceId) GetClusterInfo()
        {
            int nodeIndex = 0;
            int nodeCount = 1;
            string source = "settings";
            string deviceId = "n/a";

            try
            {
                var settings = AppSettings.Current;
                nodeIndex = settings.ClusterNodeIndex;
                nodeCount = settings.ClusterNodeCount;

                var cookiePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "WalletRecoveryTool",
                    "cluster_cookie.json");

                if (File.Exists(cookiePath))
                {
                    var json = File.ReadAllText(cookiePath);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("nodeIndex", out var ni) && ni.TryGetInt32(out var niVal))
                    {
                        nodeIndex = niVal;
                        source = "cookie";
                    }

                    if (root.TryGetProperty("nodeCount", out var nc) && nc.TryGetInt32(out var ncVal) && ncVal > 0)
                    {
                        nodeCount = ncVal;
                        source = "cookie";
                    }

                    if (root.TryGetProperty("deviceId", out var did) && did.ValueKind == JsonValueKind.String)
                    {
                        deviceId = did.GetString() ?? "n/a";
                    }
                }

                var envIndex = Environment.GetEnvironmentVariable("WRT_CLUSTER_NODE_INDEX");
                var envCount = Environment.GetEnvironmentVariable("WRT_CLUSTER_NODE_COUNT");

                if (int.TryParse(envIndex, out var envIndexValue) && envIndexValue >= 0)
                {
                    nodeIndex = envIndexValue;
                    source = "env";
                }

                if (int.TryParse(envCount, out var envCountValue) && envCountValue > 0)
                {
                    nodeCount = envCountValue;
                    source = "env";
                }

                if (nodeCount < 1) nodeCount = 1;
                if (nodeIndex < 0) nodeIndex = 0;
                if (nodeIndex >= nodeCount) nodeIndex = nodeCount - 1;
            }
            catch { }

            return (nodeIndex, nodeCount, source, deviceId);
        }

        private DateTime GetBuildDate()
        {
            try
            {
                // Single-file publish safe: ProcessPath points to the bundled executable.
                if (!string.IsNullOrWhiteSpace(Environment.ProcessPath) && System.IO.File.Exists(Environment.ProcessPath))
                {
                    return new System.IO.FileInfo(Environment.ProcessPath).LastWriteTime;
                }

                // Fallback for non-bundled runs or unusual hosts.
                var baseDirectory = AppContext.BaseDirectory;
                if (!string.IsNullOrWhiteSpace(baseDirectory) && System.IO.Directory.Exists(baseDirectory))
                {
                    return new System.IO.DirectoryInfo(baseDirectory).LastWriteTime;
                }
            }
            catch
            {
            }

            return DateTime.Now;
        }

        private long GetTotalMemoryMB()
        {
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher("SELECT Capacity FROM Win32_PhysicalMemory"))
                {
                    long total = 0;
                    foreach (var obj in searcher.Get())
                    {
                        total += Convert.ToInt64(obj["Capacity"]);
                        if (obj is IDisposable disposable) disposable.Dispose();
                    }
                    return total / 1024 / 1024;
                }
            }
            catch
            {
                return 0; // If can't determine, return 0
            }
        }

        private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as System.Windows.Controls.Button;
            button.IsEnabled = false;
            button.Content = "⏳ CHECKING...";

            try
            {
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
                button.IsEnabled = true;
                button.Content = "🔄 CHECK FOR UPDATES";
                
                MessageBox.Show(
                    $"Failed to check for updates:\n{ex.Message}\n\n" +
                    "Please check your internet connection and try again.",
                    "Update Check Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void CopySupportInfo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_supportInfo))
                {
                    LoadSystemInfo();
                }

                Clipboard.SetText(_supportInfo ?? string.Empty);
                MessageBox.Show(
                    "Support info copied to clipboard.",
                    "Copied",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to copy support info:\n{ex.Message}",
                    "Copy Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
