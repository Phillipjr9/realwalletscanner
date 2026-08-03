using System;
using System.IO;
using System.Text.Json;

#nullable enable

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Manages pause/resume state for recovery sessions
    /// </summary>
    public class RecoverySessionState
    {
        private static readonly string StateFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WalletRecoveryTool",
            "session_state.json"
        );

        public enum RecoveryMode
        {
            MnemonicRecovery,
            RandomGeneration,
            PrivateKeyBruteForce
        }

        public RecoveryMode Mode { get; set; }
        public string PartialMnemonic { get; set; } = "";
        public string LastCheckedMnemonic { get; set; } = "";
        public long AttemptsCompleted { get; set; }
        public long TotalAttempts { get; set; }
        public DateTime PausedAt { get; set; }
        public int WordLength { get; set; } = 12;
        public string SelectedCrypto { get; set; } = "BTC";
        public int ThreadCount { get; set; } = 4;

        /// <summary>
        /// Save current recovery state to disk
        /// </summary>
        public void Save()
        {
            try
            {
                var directory = Path.GetDirectoryName(StateFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                PausedAt = DateTime.Now;
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(StateFilePath, json);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to save session state: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Load saved recovery state from disk
        /// </summary>
        public static RecoverySessionState? Load()
        {
            try
            {
                if (File.Exists(StateFilePath))
                {
                    var json = File.ReadAllText(StateFilePath);
                    return JsonSerializer.Deserialize<RecoverySessionState>(json);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Error loading session state: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Check if a saved session exists
        /// </summary>
        public static bool HasSavedSession()
        {
            return File.Exists(StateFilePath);
        }

        /// <summary>
        /// Delete saved session state
        /// </summary>
        public static void Clear()
        {
            try
            {
                if (File.Exists(StateFilePath))
                {
                    File.Delete(StateFilePath);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Error clearing session state: {ex.Message}");
            }
        }

        /// <summary>
        /// Get a human-readable description of the paused session
        /// </summary>
        public string GetDescription()
        {
            var elapsed = DateTime.Now - PausedAt;
            var timeAgo = elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours} hour(s) ago"
                : $"{(int)elapsed.TotalMinutes} minute(s) ago";

            var modeText = Mode switch
            {
                RecoveryMode.MnemonicRecovery => "Mnemonic Recovery",
                RecoveryMode.RandomGeneration => "Random Generation",
                RecoveryMode.PrivateKeyBruteForce => "Private Key Brute Force",
                _ => "Unknown"
            };

            var progress = TotalAttempts > 0 
                ? $"{AttemptsCompleted:N0} / {TotalAttempts:N0} ({AttemptsCompleted * 100.0 / TotalAttempts:F1}%)"
                : $"{AttemptsCompleted:N0} attempts";

            return $"{modeText} - {SelectedCrypto} - Paused {timeAgo}\n" +
                   $"Progress: {progress}";
        }
    }
}
