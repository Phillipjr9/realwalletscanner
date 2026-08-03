using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Manages recovery session persistence - save/resume progress
    /// </summary>
    public class SessionManager
    {
        private static readonly string SessionPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WalletRecoveryTool",
            "sessions"
        );

        private static readonly object _fileLock = new object();

        public class RecoverySession
        {
            public string SessionId { get; set; } = Guid.NewGuid().ToString();
            public DateTime StartTime { get; set; } = DateTime.Now;
            public DateTime LastSaved { get; set; } = DateTime.Now;
            public RecoveryType RecoveryType { get; set; }
            public long ProcessedCount { get; set; }
            public long TotalAttempts { get; set; }
            public int WalletsFound { get; set; }
            public long TotalChecked { get; set; }
            public double RuntimeSeconds { get; set; }
            public decimal TotalUsdFound { get; set; }
            public string PartialMnemonic { get; set; } = "";
            public string LastCheckedMnemonic { get; set; } = "";
            public string LastPrivateKey { get; set; } = "";
            public List<string> FoundWalletAddresses { get; set; } = new List<string>();
            public bool IsCompleted { get; set; }
            public Dictionary<string, object> CustomData { get; set; } = new Dictionary<string, object>();
        }

        public static void SaveSession(RecoverySession session)
        {
            try
            {
                session.LastSaved = DateTime.Now;
                var json = JsonSerializer.Serialize(session, new JsonSerializerOptions 
                { 
                    WriteIndented = true 
                });
                
                // Atomic write with file-level lock to prevent concurrent conflicts
                lock (_fileLock)
                {
                    if (!Directory.Exists(SessionPath))
                    {
                        Directory.CreateDirectory(SessionPath);
                    }

                    var filename = Path.Combine(SessionPath, $"session_{session.SessionId}.json");
                    var tempFile = filename + ".tmp";
                    File.WriteAllText(tempFile, json);
                    File.Move(tempFile, filename, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Error saving session: {ex.Message}");
            }
        }

        public static RecoverySession LoadSession(string sessionId)
        {
            try
            {
                var filename = Path.Combine(SessionPath, $"session_{sessionId}.json");
                if (File.Exists(filename))
                {
                    var json = File.ReadAllText(filename);
                    return JsonSerializer.Deserialize<RecoverySession>(json);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Error loading session: {ex.Message}");
            }

            return null;
        }

        public static List<RecoverySession> GetAllSessions()
        {
            var sessions = new List<RecoverySession>();
            
            try
            {
                if (!Directory.Exists(SessionPath))
                {
                    return sessions;
                }

                foreach (var file in Directory.GetFiles(SessionPath, "session_*.json"))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var session = JsonSerializer.Deserialize<RecoverySession>(json);
                        if (session != null)
                        {
                            sessions.Add(session);
                        }
                    }
                    catch
                    {
                        // Skip corrupted files
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Error loading sessions: {ex.Message}");
            }

            return sessions;
        }

        public static void DeleteSession(string sessionId)
        {
            try
            {
                var filename = Path.Combine(SessionPath, $"session_{sessionId}.json");
                if (File.Exists(filename))
                {
                    File.Delete(filename);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Error deleting session: {ex.Message}");
            }
        }

        public static void MarkSessionCompleted(string sessionId)
        {
            var session = LoadSession(sessionId);
            if (session != null)
            {
                session.IsCompleted = true;
                SaveSession(session);
            }
        }
    }
}
