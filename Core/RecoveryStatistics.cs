using System;
using System.Diagnostics;
using System.Threading;

namespace WalletRecoveryTool.Core
{
    public class RecoveryStatistics
    {
        private readonly Stopwatch _stopwatch;
        private long _totalAttempts;
        private long _walletsFound;
        private long _skippedDuplicates;

        public RecoveryStatistics()
        {
            _stopwatch = new Stopwatch();
        }

        public void Start()
        {
            _stopwatch.Start();
        }

        public void Stop()
        {
            _stopwatch.Stop();
        }

        public void Reset()
        {
            _stopwatch.Reset();
            Interlocked.Exchange(ref _totalAttempts, 0);
            Interlocked.Exchange(ref _walletsFound, 0);
            Interlocked.Exchange(ref _skippedDuplicates, 0);
        }

        public void IncrementAttempts()
        {
            Interlocked.Increment(ref _totalAttempts);
        }

        public void SetTotalChecked(long value)
        {
            Interlocked.Exchange(ref _totalAttempts, value);
        }

        public void IncrementWalletsFound()
        {
            Interlocked.Increment(ref _walletsFound);
        }

        public void IncrementSkipped()
        {
            Interlocked.Increment(ref _skippedDuplicates);
        }

        public long GetSkippedCount()
        {
            return Volatile.Read(ref _skippedDuplicates);
        }

        public double GetAttemptsPerSecond()
        {
            var totalSeconds = _stopwatch.Elapsed.TotalSeconds;
            if (totalSeconds == 0) return 0;
            return Volatile.Read(ref _totalAttempts) / totalSeconds;
        }

        public TimeSpan GetElapsedTime()
        {
            return _stopwatch.Elapsed;
        }

        public TimeSpan GetEstimatedTimeRemaining(int totalTarget)
        {
            var attempts = Volatile.Read(ref _totalAttempts);
            if (attempts == 0) return TimeSpan.Zero;
                
            var rate = GetAttemptsPerSecond();
            if (rate == 0) return TimeSpan.Zero;
                
            var remaining = totalTarget - attempts;
            if (remaining <= 0) return TimeSpan.Zero;
                
            return TimeSpan.FromSeconds(remaining / rate);
        }

        public long GetTotalAttempts()
        {
            return Volatile.Read(ref _totalAttempts);
        }

        public long GetWalletsFound()
        {
            return Volatile.Read(ref _walletsFound);
        }

        // Public properties for easy access
        public double WalletsPerSecond => GetAttemptsPerSecond();
        public long TotalChecked => GetTotalAttempts();
        public long WalletsFound => GetWalletsFound();
        public double SuccessRate => _totalAttempts > 0 ? (double)Volatile.Read(ref _walletsFound) / Volatile.Read(ref _totalAttempts) : 0;
        public TimeSpan? EstimatedTimeRemaining => null; // Calculated on demand

        public string GetFormattedStats(int totalTarget = 0)
        {
            var elapsed = GetElapsedTime();
            var rate = GetAttemptsPerSecond();
            var attempts = Volatile.Read(ref _totalAttempts);
            var found = Volatile.Read(ref _walletsFound);
            var skipped = Volatile.Read(ref _skippedDuplicates);
                
            var stats = $"Attempts: {attempts:N0} | ";
            stats += $"Found: {found} | ";
                
            // Show skipped duplicates (already scanned in previous sessions)
            if (skipped > 0)
            {
                stats += $"Skipped: {skipped:N0} | ";
            }
                
            stats += $"Rate: {rate:F2}/sec | ";
            stats += $"Time: {elapsed:hh\\:mm\\:ss}";
                
            if (totalTarget > 0 && attempts < totalTarget)
            {
                var eta = GetEstimatedTimeRemaining(totalTarget);
                stats += $" | ETA: {eta:hh\\:mm\\:ss}";
            }
                
            return stats;
        }
    }
}