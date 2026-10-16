using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Statistics dashboard for tracking recovery performance over time
    /// </summary>
    public class RecoveryDashboard
    {
        private readonly string _statsFile;
        private List<SessionStats> _sessions = new();
        private readonly object _lock = new();

        public RecoveryDashboard()
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WalletRecoveryTool", "Analytics");
            
            Directory.CreateDirectory(folder);
            _statsFile = Path.Combine(folder, "session_history.json");
            
            LoadStats();
        }

        /// <summary>
        /// Record a completed session
        /// </summary>
        public void RecordSession(SessionStats stats)
        {
            lock (_lock)
            {
                stats.Id = Guid.NewGuid().ToString();
                stats.EndTime = DateTime.Now;
                _sessions.Add(stats);
                SaveStats();
            }
        }

        /// <summary>
        /// Get all-time statistics
        /// </summary>
        public DashboardSummary GetAllTimeSummary()
        {
            lock (_lock)
            {
                var brain = CognitiveBrainEngine.Instance;
                var topArm = brain.GetStrategyArmRankings().FirstOrDefault();

                if (_sessions.Count == 0)
                {
                    return new DashboardSummary
                    {
                        BrainVersion = brain.VersionTag,
                        BrainCognitiveIq = brain.CognitiveIqScore,
                        BrainTopStrategyArm = topArm?.Name ?? "SemanticClusterBip39",
                        BrainTotalSynthesized = brain.TotalCandidatesSynthesized,
                        BrainSubspaceRepulsions = brain.SubspaceRepulsionsTriggered,
                        GlobalEmptyWalletsTracked = GlobalMnemonicTracker.Instance.Count
                    };
                }

                return new DashboardSummary
                {
                    TotalSessions = _sessions.Count,
                    TotalWalletsChecked = _sessions.Sum(s => s.WalletsChecked),
                    TotalWalletsFound = _sessions.Sum(s => s.WalletsFound),
                    TotalUsdFound = _sessions.Sum(s => s.TotalUsdFound),
                    TotalRunTimeMinutes = _sessions.Sum(s => s.RunTimeMinutes),
                    AverageWalletsPerMinute = _sessions.Average(s => s.WalletsPerMinute),
                    BestSession = _sessions.OrderByDescending(s => s.TotalUsdFound).FirstOrDefault(),
                    MostSuccessfulMethod = GetMostSuccessfulMethod(),
                    FirstSessionDate = _sessions.Min(s => s.StartTime),
                    LastSessionDate = _sessions.Max(s => s.EndTime),
                    BrainVersion = brain.VersionTag,
                    BrainCognitiveIq = brain.CognitiveIqScore,
                    BrainTopStrategyArm = topArm?.Name ?? "SemanticClusterBip39",
                    BrainTotalSynthesized = brain.TotalCandidatesSynthesized,
                    BrainSubspaceRepulsions = brain.SubspaceRepulsionsTriggered,
                    GlobalEmptyWalletsTracked = GlobalMnemonicTracker.Instance.Count
                };
            }
        }

        /// <summary>
        /// Get statistics for a specific time period
        /// </summary>
        public DashboardSummary GetPeriodSummary(DateTime from, DateTime to)
        {
            lock (_lock)
            {
                var filtered = _sessions.Where(s => s.StartTime >= from && s.EndTime <= to).ToList();
                
                if (filtered.Count == 0)
                    return new DashboardSummary();

                return new DashboardSummary
                {
                    TotalSessions = filtered.Count,
                    TotalWalletsChecked = filtered.Sum(s => s.WalletsChecked),
                    TotalWalletsFound = filtered.Sum(s => s.WalletsFound),
                    TotalUsdFound = filtered.Sum(s => s.TotalUsdFound),
                    TotalRunTimeMinutes = filtered.Sum(s => s.RunTimeMinutes),
                    AverageWalletsPerMinute = filtered.Average(s => s.WalletsPerMinute)
                };
            }
        }

        /// <summary>
        /// Get statistics grouped by day
        /// </summary>
        public List<DailyStats> GetDailyStats(int lastDays = 30)
        {
            lock (_lock)
            {
                var cutoff = DateTime.Now.AddDays(-lastDays);
                var filtered = _sessions.Where(s => s.StartTime >= cutoff).ToList();

                var grouped = filtered
                    .GroupBy(s => s.StartTime.Date)
                    .Select(g => new DailyStats
                    {
                        Date = g.Key,
                        Sessions = g.Count(),
                        WalletsChecked = g.Sum(s => s.WalletsChecked),
                        WalletsFound = g.Sum(s => s.WalletsFound),
                        UsdFound = g.Sum(s => s.TotalUsdFound),
                        RunTimeMinutes = g.Sum(s => s.RunTimeMinutes)
                    })
                    .OrderBy(d => d.Date)
                    .ToList();

                return grouped;
            }
        }

        /// <summary>
        /// Get statistics grouped by recovery method
        /// </summary>
        public Dictionary<string, MethodStats> GetMethodStats()
        {
            lock (_lock)
            {
                var grouped = _sessions
                    .GroupBy(s => s.RecoveryMethod)
                    .ToDictionary(
                        g => g.Key,
                        g => new MethodStats
                        {
                            Method = g.Key,
                            Sessions = g.Count(),
                            TotalChecked = g.Sum(s => s.WalletsChecked),
                            TotalFound = g.Sum(s => s.WalletsFound),
                            SuccessRate = g.Sum(s => s.WalletsChecked) > 0 
                                ? (double)g.Sum(s => s.WalletsFound) / g.Sum(s => s.WalletsChecked) * 100 
                                : 0,
                            AverageSpeed = g.Average(s => s.WalletsPerMinute),
                            TotalUsd = g.Sum(s => s.TotalUsdFound)
                        });

                return grouped;
            }
        }

        /// <summary>
        /// Get the most successful recovery method
        /// </summary>
        private string GetMostSuccessfulMethod()
        {
            var methodStats = GetMethodStats();
            return methodStats
                .OrderByDescending(m => m.Value.TotalFound)
                .FirstOrDefault()
                .Key ?? "None";
        }

        /// <summary>
        /// Get recent sessions
        /// </summary>
        public List<SessionStats> GetRecentSessions(int count = 10)
        {
            lock (_lock)
            {
                return _sessions
                    .OrderByDescending(s => s.EndTime)
                    .Take(count)
                    .ToList();
            }
        }

        /// <summary>
        /// Generate formatted report
        /// </summary>
        public string GenerateReport()
        {
            var summary = GetAllTimeSummary();
            var methodStats = GetMethodStats();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("═══════════════════════════════════════════════════════════");
            sb.AppendLine("           WALLET RECOVERY TOOL - STATISTICS REPORT         ");
            sb.AppendLine("═══════════════════════════════════════════════════════════");
            sb.AppendLine();
            sb.AppendLine($"📅 Report Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"📊 Data Range: {summary.FirstSessionDate:yyyy-MM-dd} to {summary.LastSessionDate:yyyy-MM-dd}");
            sb.AppendLine();
            sb.AppendLine("─────────────────── ALL-TIME SUMMARY ───────────────────────");
            sb.AppendLine($"  Total Sessions:          {summary.TotalSessions:N0}");
            sb.AppendLine($"  Total Wallets Checked:   {summary.TotalWalletsChecked:N0}");
            sb.AppendLine($"  Total Wallets Found:     {summary.TotalWalletsFound:N0}");
            sb.AppendLine($"  Total USD Value Found:   ${summary.TotalUsdFound:N2}");
            sb.AppendLine($"  Total Runtime:           {summary.TotalRunTimeMinutes:N0} minutes ({summary.TotalRunTimeMinutes / 60:N1} hours)");
            sb.AppendLine($"  Avg Speed:               {summary.AverageWalletsPerMinute:N0} wallets/min");
            sb.AppendLine($"  Empty Wallets Blocked:   {summary.GlobalEmptyWalletsTracked:N0} (0% repeat guarantee)");
            sb.AppendLine();
            sb.AppendLine("───────────── COGNITIVE SELF-LEARNING BRAIN ────────────────");
            sb.AppendLine($"  Brain Version:           {summary.BrainVersion} (IQ {summary.BrainCognitiveIq:F1})");
            sb.AppendLine($"  Dominant Neural Arm:     {summary.BrainTopStrategyArm}");
            sb.AppendLine($"  Candidates Synthesized:  {summary.BrainTotalSynthesized:N0}");
            sb.AppendLine($"  Subspace Repulsions:     {summary.BrainSubspaceRepulsions:N0}");
            sb.AppendLine();
            sb.AppendLine("─────────────────── BY RECOVERY METHOD ─────────────────────");
            
            foreach (var method in methodStats.OrderByDescending(m => m.Value.TotalFound))
            {
                sb.AppendLine($"  [{method.Key}]");
                sb.AppendLine($"    Sessions: {method.Value.Sessions}  |  Checked: {method.Value.TotalChecked:N0}  |  Found: {method.Value.TotalFound}");
                sb.AppendLine($"    Success Rate: {method.Value.SuccessRate:F6}%  |  USD: ${method.Value.TotalUsd:N2}");
            }

            sb.AppendLine();
            sb.AppendLine("═══════════════════════════════════════════════════════════");

            return sb.ToString();
        }

        /// <summary>
        /// Export stats to CSV
        /// </summary>
        public void ExportToCsv(string filePath)
        {
            List<SessionStats> snapshot;
            lock (_lock)
            {
                snapshot = new List<SessionStats>(_sessions);
            }

            using var writer = new StreamWriter(filePath);
            writer.WriteLine("SessionId,StartTime,EndTime,RecoveryMethod,WalletsChecked,WalletsFound,TotalUsdFound,RunTimeMinutes,WalletsPerMinute");
            
            foreach (var session in snapshot)
            {
                writer.WriteLine($"{session.Id},{session.StartTime:o},{session.EndTime:o},{session.RecoveryMethod},{session.WalletsChecked},{session.WalletsFound},{session.TotalUsdFound:F2},{session.RunTimeMinutes:F2},{session.WalletsPerMinute:F2}");
            }
        }

        /// <summary>
        /// Clear all statistics
        /// </summary>
        public void ClearAll()
        {
            lock (_lock)
            {
                _sessions.Clear();
                SaveStats();
            }
        }

        private void SaveStats()
        {
            try
            {
                var json = JsonSerializer.Serialize(_sessions, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_statsFile, json);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Error saving stats: {ex.Message}");
            }
        }

        private void LoadStats()
        {
            try
            {
                if (File.Exists(_statsFile))
                {
                    var json = File.ReadAllText(_statsFile);
                    _sessions = JsonSerializer.Deserialize<List<SessionStats>>(json) ?? new List<SessionStats>();
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Error loading stats: {ex.Message}");
                _sessions = new List<SessionStats>();
            }
        }
    }

    public class SessionStats
    {
        public string Id { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public string RecoveryMethod { get; set; }
        public long WalletsChecked { get; set; }
        public int WalletsFound { get; set; }
        public decimal TotalUsdFound { get; set; }
        public double RunTimeMinutes { get; set; }
        public double WalletsPerMinute { get; set; }
    }

    public class DashboardSummary
    {
        public int TotalSessions { get; set; }
        public long TotalWalletsChecked { get; set; }
        public int TotalWalletsFound { get; set; }
        public decimal TotalUsdFound { get; set; }
        public double TotalRunTimeMinutes { get; set; }
        public double AverageWalletsPerMinute { get; set; }
        public SessionStats BestSession { get; set; }
        public string MostSuccessfulMethod { get; set; }
        public DateTime FirstSessionDate { get; set; }
        public DateTime LastSessionDate { get; set; }
        public string BrainVersion { get; set; } = "v1.0";
        public double BrainCognitiveIq { get; set; } = 112.0;
        public string BrainTopStrategyArm { get; set; } = "SemanticClusterBip39";
        public long BrainTotalSynthesized { get; set; }
        public long BrainSubspaceRepulsions { get; set; }
        public int GlobalEmptyWalletsTracked { get; set; }
    }

    public class DailyStats
    {
        public DateTime Date { get; set; }
        public int Sessions { get; set; }
        public long WalletsChecked { get; set; }
        public int WalletsFound { get; set; }
        public decimal UsdFound { get; set; }
        public double RunTimeMinutes { get; set; }
    }

    public class MethodStats
    {
        public string Method { get; set; }
        public int Sessions { get; set; }
        public long TotalChecked { get; set; }
        public int TotalFound { get; set; }
        public double SuccessRate { get; set; }
        public double AverageSpeed { get; set; }
        public decimal TotalUsd { get; set; }
    }
}
