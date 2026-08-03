using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Debug logger for diagnostics - captures detailed app activity for troubleshooting
    /// Log file: Documents\WalletRecoveryTool\Logs\debug_log_[date].txt
    /// </summary>
    public static class DebugLogger
    {
        private static readonly string LogFolder;
        private static readonly string LogFile;
        private static readonly object _lock = new object();
        private static readonly ConcurrentQueue<string> _logQueue = new ConcurrentQueue<string>();
        private static readonly Timer _flushTimer;
        private static bool _enabled = true;
        
        // Performance tracking
        private static long _totalApiCalls = 0;
        private static long _totalApiErrors = 0;
        private static long _totalWalletsGenerated = 0;
        private static long _totalBalanceChecks = 0;
        private static DateTime _sessionStart = DateTime.Now;
        private static readonly ConcurrentDictionary<string, long> _apiCallCounts = new();
        private static readonly ConcurrentDictionary<string, double> _apiResponseTimes = new();

        static DebugLogger()
        {
            LogFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "WalletRecoveryTool", "Logs");
            Directory.CreateDirectory(LogFolder);
            
            LogFile = Path.Combine(LogFolder, $"debug_log_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");
            
            // Flush log every 2 seconds
            _flushTimer = new Timer(_ => FlushLog(), null, 2000, 2000);
            
            // Write header
            Log("═══════════════════════════════════════════════════════════════════════════");
            Log($"DEBUG LOG STARTED: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Log($"Machine: {Environment.MachineName} | Cores: {Environment.ProcessorCount}");
            Log($"OS: {Environment.OSVersion}");
            Log("═══════════════════════════════════════════════════════════════════════════");
        }

        public static void Enable() => _enabled = true;
        public static void Disable() => _enabled = false;

        public static void Log(string message)
        {
            if (!_enabled) return;
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            _logQueue.Enqueue($"[{timestamp}] {message}");
        }

        public static void LogApi(string apiName, string endpoint, bool success, double responseTimeMs, string details = "")
        {
            Interlocked.Increment(ref _totalApiCalls);
            if (!success) Interlocked.Increment(ref _totalApiErrors);
            
            _apiCallCounts.AddOrUpdate(apiName, 1, (_, count) => count + 1);
            _apiResponseTimes.AddOrUpdate(apiName, responseTimeMs, (_, avg) => (avg + responseTimeMs) / 2);
            
            var status = success ? "✓" : "✗";
            Log($"[API] {status} {apiName} | {endpoint} | {responseTimeMs:F0}ms | {details}");
        }

        public static void LogWalletGenerated(string source, string address)
        {
            Interlocked.Increment(ref _totalWalletsGenerated);
            Log($"[WALLET] Generated | Source: {source} | Addr: {address?.Substring(0, Math.Min(20, address?.Length ?? 0))}...");
        }

        public static void LogBalanceCheck(string address, string chain, decimal balance, double responseTimeMs)
        {
            Interlocked.Increment(ref _totalBalanceChecks);
            var balanceStr = balance > 0 ? $"💰 {balance:F8}" : "0";
            Log($"[BALANCE] {chain} | {address?.Substring(0, Math.Min(20, address?.Length ?? 0))}... | {balanceStr} | {responseTimeMs:F0}ms");
        }

        public static void LogError(string context, Exception ex)
        {
            Log($"[ERROR] {context} | {ex.GetType().Name}: {ex.Message}");
        }

        public static void LogPerformance(string metric, double value, string unit = "")
        {
            Log($"[PERF] {metric}: {value:F2} {unit}");
        }

        public static void LogEvent(string eventName, string details = "")
        {
            Log($"[EVENT] {eventName} | {details}");
        }

        public static void LogSettings(AppSettings settings)
        {
            Log("─────────────────────────────────────────────────────────────────────────────");
            Log("[SETTINGS] Current Configuration:");
            Log($"  ThreadCount: {settings.ThreadCount}");
            Log($"  CheckDelayMs: {settings.CheckDelayMs}");
            Log($"  MaxConcurrentApiCalls: {settings.MaxConcurrentApiCalls}");
            Log($"  TurboMode: {settings.TurboMode}");
            Log($"  QuickScanMode: {settings.QuickScanMode}");
            Log($"  EnableBalanceCache: {settings.EnableBalanceCache}");
            Log($"  EtherscanApiKey: {(string.IsNullOrEmpty(settings.EtherscanApiKey) ? "NOT SET" : "SET")}");
            Log($"  BlockCypherApiKey: {(string.IsNullOrEmpty(settings.BlockCypherApiKey) ? "NOT SET" : "SET")}");
            Log($"  QuickNodeApiKey: {(string.IsNullOrEmpty(settings.QuickNodeApiKey) ? "NOT SET" : "SET")}");
            Log($"  UseLocalNodeForBalances: {settings.UseLocalNodeForBalances}");
            if (settings.UseLocalNodeForBalances)
            {
                Log($"  LocalBitcoinRpcUrl: {(string.IsNullOrWhiteSpace(settings.LocalBitcoinRpcUrl) ? "NOT SET" : settings.LocalBitcoinRpcUrl)}");
                Log($"  LocalEthereumRpcUrl: {(string.IsNullOrWhiteSpace(settings.LocalEthereumRpcUrl) ? "NOT SET" : settings.LocalEthereumRpcUrl)}");
                Log($"  LocalSolanaRpcUrl: {(string.IsNullOrWhiteSpace(settings.LocalSolanaRpcUrl) ? "NOT SET" : settings.LocalSolanaRpcUrl)}");
            }
            Log("─────────────────────────────────────────────────────────────────────────────");
        }

        public static string GetStats()
        {
            var elapsed = DateTime.Now - _sessionStart;
            var sb = new StringBuilder();
            sb.AppendLine("═══════════════════════════════════════════════════════════════════════════");
            sb.AppendLine($"SESSION STATS (Elapsed: {elapsed:hh\\:mm\\:ss})");
            sb.AppendLine("─────────────────────────────────────────────────────────────────────────────");
            sb.AppendLine($"Total Wallets Generated: {_totalWalletsGenerated:N0}");
            sb.AppendLine($"Total Balance Checks: {_totalBalanceChecks:N0}");
            sb.AppendLine($"Total API Calls: {_totalApiCalls:N0}");
            sb.AppendLine($"API Errors: {_totalApiErrors:N0} ({(_totalApiCalls > 0 ? (double)_totalApiErrors / _totalApiCalls * 100 : 0):F1}%)");
            sb.AppendLine($"Wallets/sec: {(_totalWalletsGenerated / Math.Max(1, elapsed.TotalSeconds)):F1}");
            sb.AppendLine($"Checks/sec: {(_totalBalanceChecks / Math.Max(1, elapsed.TotalSeconds)):F1}");
            sb.AppendLine();
            sb.AppendLine("API Performance:");
            foreach (var api in _apiCallCounts)
            {
                var avgTime = _apiResponseTimes.TryGetValue(api.Key, out var t) ? t : 0;
                sb.AppendLine($"  {api.Key}: {api.Value:N0} calls, avg {avgTime:F0}ms");
            }
            sb.AppendLine("═══════════════════════════════════════════════════════════════════════════");
            return sb.ToString();
        }

        public static void LogStats()
        {
            var stats = GetStats();
            foreach (var line in stats.Split('\n'))
            {
                Log(line);
            }
        }

        private static void FlushLog()
        {
            if (_logQueue.IsEmpty) return;
            
            var sb = new StringBuilder();
            while (_logQueue.TryDequeue(out var message))
            {
                sb.AppendLine(message);
            }
            
            if (sb.Length > 0)
            {
                lock (_lock)
                {
                    try
                    {
                        File.AppendAllText(LogFile, sb.ToString());
                    }
                    catch { /* Ignore write errors */ }
                }
            }
        }

        public static void Shutdown()
        {
            LogStats();
            Log("═══════════════════════════════════════════════════════════════════════════");
            Log($"DEBUG LOG ENDED: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Log("═══════════════════════════════════════════════════════════════════════════");
            FlushLog();
            _flushTimer?.Dispose();
        }

        public static string GetLogFilePath() => LogFile;
    }
}
