using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using MaterialDesignThemes.Wpf;
using WalletRecoveryTool.Core;

namespace WalletRecoveryTool
{
    public partial class App : Application
    {
        [DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

        private static string GetStartupLogPath()
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WalletRecoveryTool", "Logs");
            try { Directory.CreateDirectory(logDir); } catch { }
            return Path.Combine(logDir, "app_startup.log");
        }

        // Constructor runs very early in app lifecycle. Handle --cli here to avoid
        // any WPF window initialization that may occur before OnStartup.
        public App()
        {
            try
            {
                var cmd = Environment.GetCommandLineArgs();
                if (cmd != null && cmd.Length >= 2 && cmd[1] == "--cli")
                {
                    // Attach or allocate console for terminal mode
                    if (!AttachConsole(-1)) AllocConsole();
                    Console.SetOut(new System.IO.StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                    Console.SetError(new System.IO.StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
                    Console.SetIn(new System.IO.StreamReader(Console.OpenStandardInput()));

                    // Build args array excluding exe path
                    var args = new string[cmd.Length - 1];
                    Array.Copy(cmd, 1, args, 0, args.Length);

                    // Run CLI runner synchronously and exit immediately to prevent WPF startup
                    var exitCode = CliRunner.RunAsync(args).GetAwaiter().GetResult();
                    Environment.Exit(exitCode);
                }
            }
            catch
            {
                // If anything goes wrong here, fall back to normal startup so user still gets UI
            }
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Anti-tamper: start background debugger watchdog (Release builds only)
            #if !DEBUG
            Core.AntiTamper.StartWatchdog();
            #endif

            // Handle --cli flag: run in terminal mode instead of launching WPF
            if (e.Args.Length >= 1 && e.Args[0] == "--cli")
            {
                if (!AttachConsole(-1)) AllocConsole(); // attach to parent terminal, or create new
                // WinExe apps must reopen std streams after attaching
                Console.SetOut(new System.IO.StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                Console.SetError(new System.IO.StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
                Console.SetIn(new System.IO.StreamReader(Console.OpenStandardInput()));
                var exitCode = CliRunner.RunAsync(e.Args).GetAwaiter().GetResult();
                Shutdown(exitCode);
                return;
            }

            // Handle --generate-keys admin command
            if (e.Args.Length >= 2 && e.Args[0] == "--generate-keys" && int.TryParse(e.Args[1], out int keyCount))
            {
                var keys = LicenseManager.GenerateKeys(Math.Max(keyCount, 1));
                var keysText = $"CodeHaven License Keys ({keys.Length})\nGenerated: {DateTime.Now}\n\n" +
                               string.Join("\n", keys);
                var outputPath = Path.Combine(
                    Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory) ?? ".",
                    "license_keys.txt");
                File.WriteAllText(outputPath, keysText);
                MessageBox.Show(
                    $"{keys.Length} license keys generated!\n\nSaved to:\n{outputPath}",
                    "CodeHaven - Key Generator",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(0);
                return;
            }
            
            // Verify application integrity (CodeHaven signature)
            var (isAuthentic, integrityMsg) = Core.AppSignature.VerifyIntegrity();
            if (!isAuthentic)
            {
                MessageBox.Show(
                    $"⚠️ Application Integrity Check Failed\n\n{integrityMsg}\n\n" +
                    "This copy may have been tampered with.\n" +
                    "Please obtain an authentic copy from CodeHaven.",
                    "CodeHaven - Security Warning",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // License check — admin machine skips, others need a valid license
            if (!LicenseManager.IsAdminMachine() && !LicenseManager.IsLicensed())
            {
                var licenseWindow = new LicenseWindow();
                var result = licenseWindow.ShowDialog();
                if (result != true || !licenseWindow.IsActivated)
                {
                    Shutdown(0);
                    return;
                }
            }

            // Show splash screen while loading
            var splash = new SplashWindow();
            splash.Show();
            
            var logPath = GetStartupLogPath();
            
            try
            {
                // Log startup
                try { File.WriteAllText(logPath, $"[{DateTime.Now}] App starting...\n"); } catch { }

                splash.UpdateStatus("Loading theme...");
                // Set up Material Design theme (simplified)
                try { File.AppendAllText(logPath, $"[{DateTime.Now}] Setting up theme...\n"); } catch { }
                var paletteHelper = new PaletteHelper();
                var theme = paletteHelper.GetTheme();
                paletteHelper.SetTheme(theme);

                splash.UpdateStatus("Preparing main window...");
                // Create and show the main window directly
                try { File.AppendAllText(logPath, $"[{DateTime.Now}] Creating MainWindow...\n"); } catch { }
                var mainWindow = new MainWindow();
                try { File.AppendAllText(logPath, $"[{DateTime.Now}] Showing MainWindow...\n"); } catch { }
                mainWindow.Show();
                splash.Close();
                try { File.AppendAllText(logPath, $"[{DateTime.Now}] MainWindow shown successfully\n"); } catch { }

                // First-run style install health check (non-fatal)
                PerformInstallHealthCheck(logPath);

                // Log cluster assignment for multi-PC no-overlap verification
                LogClusterAssignment(logPath);

                // Check for updates asynchronously (non-blocking)
                CheckForUpdatesAsync();
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(logPath, $"[{DateTime.Now}] ERROR: {ex.GetType().Name}\n{ex.Message}\n{ex.StackTrace}\n"); } catch { }
                
                MessageBox.Show(
                    $"STARTUP ERROR:\n\n{ex.GetType().Name}\n{ex.Message}\n\nCheck app_startup.log in AppData",
                    "App Failed to Start",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                this.Shutdown(1);
            }
        }

        private void PerformInstallHealthCheck(string logPath)
        {
            try
            {
                var issues = new List<string>();

                var appDataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "WalletRecoveryTool");
                var docsLogDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "WalletRecoveryTool",
                    "Logs");
                var installDir = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? AppContext.BaseDirectory;

                ValidateDirectoryWrite("AppData", appDataDir, issues);
                ValidateDirectoryWrite("Logs", docsLogDir, issues);
                ValidateDirectoryWrite("InstallDir", installDir, issues, requireWrite: false);

                if (issues.Count == 0)
                {
                    try { File.AppendAllText(logPath, $"[{DateTime.Now}] Install health check: OK\n"); } catch { }
                    return;
                }

                var msg = "Install health check found issues:\n\n- " + string.Join("\n- ", issues) +
                          "\n\nThe app can still run, but some features may fail.\n" +
                          "Please adjust folder permissions or install location.";

                try { File.AppendAllText(logPath, $"[{DateTime.Now}] Install health check issues: {string.Join(" | ", issues)}\n"); } catch { }
                MessageBox.Show(msg, "Install Health Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(logPath, $"[{DateTime.Now}] Install health check failed: {ex.Message}\n"); } catch { }
            }
        }

        private static void ValidateDirectoryWrite(string name, string path, List<string> issues, bool requireWrite = true)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    issues.Add($"{name}: Path is empty");
                    return;
                }

                Directory.CreateDirectory(path);

                if (!requireWrite)
                    return;

                var probeFile = Path.Combine(path, $".wrt_probe_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(probeFile, "ok");
                File.Delete(probeFile);
            }
            catch (Exception ex)
            {
                issues.Add($"{name}: {ex.Message}");
            }
        }

        private async void CheckForUpdatesAsync()
        {
            try
            {
                var settings = AppSettings.Load();
                
                // Only check if enabled and hasn't been checked recently (within 24 hours)
                if (!settings.CheckForUpdatesOnStartup || 
                    (DateTime.Now - settings.LastUpdateCheck).TotalHours < 24)
                {
                    return;
                }

                var updateInfo = await UpdateChecker.CheckForUpdatesAsync();
                
                if (updateInfo.UpdateAvailable)
                {
                    // Don't show if user skipped this version
                    if (updateInfo.LatestVersion == settings.SkippedVersion)
                    {
                        return;
                    }

                    // Show update notification on UI thread
                    Dispatcher.Invoke(() =>
                    {
                        var updateWindow = new UpdateWindow(updateInfo)
                        {
                            Owner = MainWindow
                        };
                        updateWindow.ShowDialog();
                    });
                }

                // Update last check time
                settings.LastUpdateCheck = DateTime.Now;
                settings.Save();
            }
            catch (Exception ex)
            {
                // Silently fail - don't interrupt app startup
                DebugLogger.Log($"Update check failed: {ex.Message}");
            }
        }

        private void LogClusterAssignment(string logPath)
        {
            try
            {
                var settings = AppSettings.Current;

                int nodeIndex = settings.ClusterNodeIndex;
                int nodeCount = settings.ClusterNodeCount;
                string source = "settings";
                string deviceId = "n/a";

                // Cookie fallback written by oneclick-install.bat
                var cookiePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "WalletRecoveryTool",
                    "cluster_cookie.json");

                if (File.Exists(cookiePath))
                {
                    var json = File.ReadAllText(cookiePath);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("nodeIndex", out var ni) && ni.TryGetInt32(out var niValue))
                    {
                        nodeIndex = niValue;
                        source = "cookie";
                    }

                    if (root.TryGetProperty("nodeCount", out var nc) && nc.TryGetInt32(out var ncValue) && ncValue > 0)
                    {
                        nodeCount = ncValue;
                        source = "cookie";
                    }

                    if (root.TryGetProperty("deviceId", out var did) && did.ValueKind == JsonValueKind.String)
                    {
                        deviceId = did.GetString() ?? "n/a";
                    }
                }

                // Environment overrides have highest priority
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

                var enabled = settings.EnableClusterSharding || nodeCount > 1;
                var msg = $"[{DateTime.Now}] Cluster: enabled={enabled}, node={nodeIndex + 1}/{nodeCount}, source={source}, deviceId={deviceId}";

                try { File.AppendAllText(logPath, msg + "\n"); } catch { }
                DebugLogger.Log(msg);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Cluster status logging failed: {ex.Message}");
            }
        }
    }
}