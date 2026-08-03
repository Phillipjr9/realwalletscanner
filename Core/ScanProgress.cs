using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Tracks last completed index/offset per scan phase so that on restart,
    /// phases resume where they left off instead of re-scanning from the beginning.
    /// Persists to AppData/Roaming/WalletRecoveryTool/Database/scan_progress.json
    /// </summary>
    public static class ScanProgress
    {
        private static readonly string _filePath;
        private static readonly object _lock = new object();
        private static Dictionary<string, long> _progress;
        private static DateTime _lastSave = DateTime.MinValue;
        private static bool _dirty;

        static ScanProgress()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WalletRecoveryTool", "Database");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "scan_progress.json");
            Load();
        }

        /// <summary>
        /// Reload progress from disk (call before starting phases to pick up latest state).
        /// Also called automatically from the static constructor.
        /// </summary>
        public static void Load()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(_filePath))
                    {
                        var json = File.ReadAllText(_filePath);
                        _progress = JsonConvert.DeserializeObject<Dictionary<string, long>>(json)
                                    ?? new Dictionary<string, long>();
                    }
                    else
                    {
                        _progress = new Dictionary<string, long>();
                    }
                }
                catch
                {
                    _progress = new Dictionary<string, long>();
                }
            }
        }

        /// <summary>
        /// Get the last completed index for a phase. Returns 0 if no progress saved.
        /// </summary>
        public static long GetProgress(string phaseKey)
        {
            lock (_lock)
            {
                return _progress.TryGetValue(phaseKey, out var val) ? val : 0;
            }
        }

        /// <summary>
        /// Update progress for a phase. Throttled: only writes to disk every 10 seconds.
        /// </summary>
        public static void SetProgress(string phaseKey, long index)
        {
            lock (_lock)
            {
                _progress[phaseKey] = index;
                _dirty = true;

                // Throttle disk writes to every 10 seconds
                if ((DateTime.UtcNow - _lastSave).TotalSeconds >= 10)
                {
                    SaveToDisk();
                }
            }
        }

        /// <summary>
        /// Force save to disk (call on app shutdown or stop).
        /// </summary>
        public static void Flush()
        {
            lock (_lock)
            {
                if (_dirty)
                    SaveToDisk();
            }
        }

        /// <summary>
        /// Reset progress for all phases (start fresh).
        /// </summary>
        public static void Reset()
        {
            lock (_lock)
            {
                _progress.Clear();
                SaveToDisk();
            }
        }

        private static void SaveToDisk()
        {
            try
            {
                var json = JsonConvert.SerializeObject(_progress, Formatting.Indented);
                File.WriteAllText(_filePath, json);
                _lastSave = DateTime.UtcNow;
                _dirty = false;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[ScanProgress] Save failed: {ex.Message}");
            }
        }
    }
}
