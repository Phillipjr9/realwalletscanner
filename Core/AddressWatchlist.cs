using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Monitor specific addresses for balance changes
    /// Alerts when watched addresses receive funds
    /// </summary>
    public class AddressWatchlist
    {
        private readonly List<WatchedAddress> _watchedAddresses = new();
        private readonly BalanceChecker _balanceChecker;
        private Timer _monitorTimer;
        private readonly object _lock = new();
        
        private static readonly string _watchlistFile;
        
        // Events
        public event EventHandler<BalanceChangeEventArgs> BalanceChanged;
        public event EventHandler<string> StatusUpdated;

        static AddressWatchlist()
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WalletRecoveryTool", "Watchlist");
            Directory.CreateDirectory(folder);
            _watchlistFile = Path.Combine(folder, "watched_addresses.json");
        }

        public AddressWatchlist()
        {
            var settings = AppSettings.Load();
            _balanceChecker = new BalanceChecker(settings);
            LoadWatchlist();
        }

        /// <summary>
        /// Add address to watchlist
        /// </summary>
        public void AddAddress(string address, string label, string coin = "BTC")
        {
            lock (_lock)
            {
                if (_watchedAddresses.Any(w => w.Address.Equals(address, StringComparison.OrdinalIgnoreCase)))
                    return; // Already watching

                _watchedAddresses.Add(new WatchedAddress
                {
                    Address = address,
                    Label = label,
                    Coin = coin.ToUpper(),
                    AddedAt = DateTime.Now,
                    LastBalance = 0,
                    LastChecked = DateTime.MinValue
                });
                
                SaveWatchlist();
            }
        }

        /// <summary>
        /// Remove address from watchlist
        /// </summary>
        public bool RemoveAddress(string address)
        {
            lock (_lock)
            {
                var removed = _watchedAddresses.RemoveAll(w => 
                    w.Address.Equals(address, StringComparison.OrdinalIgnoreCase)) > 0;
                
                if (removed)
                    SaveWatchlist();
                
                return removed;
            }
        }

        /// <summary>
        /// Get all watched addresses
        /// </summary>
        public List<WatchedAddress> GetAllAddresses()
        {
            lock (_lock)
            {
                return _watchedAddresses.ToList();
            }
        }

        /// <summary>
        /// Start monitoring addresses
        /// </summary>
        public void StartMonitoring(int intervalMinutes = 5)
        {
            _monitorTimer?.Dispose();
            
            // Check immediately
            Task.Run(async () => await CheckAllAddressesAsync());
            
            // Then periodically
            _monitorTimer = new Timer(
                async _ => await CheckAllAddressesAsync(),
                null,
                TimeSpan.FromMinutes(intervalMinutes),
                TimeSpan.FromMinutes(intervalMinutes));
            
            StatusUpdated?.Invoke(this, $"Monitoring {_watchedAddresses.Count} addresses every {intervalMinutes} minutes");
        }

        /// <summary>
        /// Stop monitoring
        /// </summary>
        public void StopMonitoring()
        {
            _monitorTimer?.Dispose();
            _monitorTimer = null;
            StatusUpdated?.Invoke(this, "Monitoring stopped");
        }

        /// <summary>
        /// Check all watched addresses for balance changes
        /// </summary>
        public async Task CheckAllAddressesAsync()
        {
            List<WatchedAddress> addressesToCheck;
            lock (_lock)
            {
                addressesToCheck = _watchedAddresses.ToList();
            }

            foreach (var watched in addressesToCheck)
            {
                try
                {
                    decimal newBalance = watched.Coin switch
                    {
                        "BTC" => await _balanceChecker.GetBitcoinBalance(watched.Address),
                        "ETH" => await _balanceChecker.GetEthereumBalance(watched.Address),
                        "SOL" => await _balanceChecker.GetSolanaBalance(watched.Address),
                        "LTC" => await _balanceChecker.GetLitecoinBalance(watched.Address),
                        "DOGE" => await _balanceChecker.GetDogecoinBalance(watched.Address),
                        _ => 0
                    };

                    var oldBalance = watched.LastBalance;
                    watched.LastBalance = newBalance;
                    watched.LastChecked = DateTime.Now;

                    // Check for balance increase
                    if (newBalance > oldBalance && oldBalance >= 0)
                    {
                        var increase = newBalance - oldBalance;
                        BalanceChanged?.Invoke(this, new BalanceChangeEventArgs
                        {
                            Address = watched.Address,
                            Label = watched.Label,
                            Coin = watched.Coin,
                            OldBalance = oldBalance,
                            NewBalance = newBalance,
                            Change = increase,
                            IsIncrease = true
                        });
                    }
                    // Check for balance decrease (outgoing transaction)
                    else if (newBalance < oldBalance && oldBalance > 0)
                    {
                        var decrease = oldBalance - newBalance;
                        BalanceChanged?.Invoke(this, new BalanceChangeEventArgs
                        {
                            Address = watched.Address,
                            Label = watched.Label,
                            Coin = watched.Coin,
                            OldBalance = oldBalance,
                            NewBalance = newBalance,
                            Change = -decrease,
                            IsIncrease = false
                        });
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"Error checking {watched.Address}: {ex.Message}");
                }
                
                // Small delay between checks to avoid rate limits
                await Task.Delay(500);
            }

            SaveWatchlist();
        }

        /// <summary>
        /// Check a single address immediately
        /// </summary>
        public async Task<decimal> CheckAddressAsync(string address, string coin = "BTC")
        {
            return coin.ToUpper() switch
            {
                "BTC" => await _balanceChecker.GetBitcoinBalance(address),
                "ETH" => await _balanceChecker.GetEthereumBalance(address),
                "SOL" => await _balanceChecker.GetSolanaBalance(address),
                "LTC" => await _balanceChecker.GetLitecoinBalance(address),
                "DOGE" => await _balanceChecker.GetDogecoinBalance(address),
                _ => 0
            };
        }

        /// <summary>
        /// Import addresses from file
        /// </summary>
        public int ImportFromFile(string filePath)
        {
            var lines = File.ReadAllLines(filePath);
            int imported = 0;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                    continue;

                var parts = trimmed.Split(',', '\t', '|');
                var address = parts[0].Trim();
                var label = parts.Length > 1 ? parts[1].Trim() : $"Imported {imported + 1}";
                var coin = parts.Length > 2 ? parts[2].Trim().ToUpper() : DetectCoin(address);

                AddAddress(address, label, coin);
                imported++;
            }

            return imported;
        }

        /// <summary>
        /// Detect cryptocurrency from address format
        /// </summary>
        private string DetectCoin(string address)
        {
            if (address.StartsWith("1") || address.StartsWith("3") || address.StartsWith("bc1"))
                return "BTC";
            if (address.StartsWith("0x") && address.Length == 42)
                return "ETH";
            if (address.Length >= 32 && address.Length <= 44 && !address.StartsWith("0x"))
                return "SOL";
            if (address.StartsWith("L") || address.StartsWith("M") || address.StartsWith("ltc1"))
                return "LTC";
            if (address.StartsWith("D"))
                return "DOGE";
            
            return "BTC"; // Default
        }

        /// <summary>
        /// Save watchlist to disk
        /// </summary>
        private void SaveWatchlist()
        {
            try
            {
                var json = JsonSerializer.Serialize(_watchedAddresses, new JsonSerializerOptions 
                { 
                    WriteIndented = true 
                });
                File.WriteAllText(_watchlistFile, json);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Error saving watchlist: {ex.Message}");
            }
        }

        /// <summary>
        /// Load watchlist from disk
        /// </summary>
        private void LoadWatchlist()
        {
            try
            {
                if (File.Exists(_watchlistFile))
                {
                    var json = File.ReadAllText(_watchlistFile);
                    var loaded = JsonSerializer.Deserialize<List<WatchedAddress>>(json);
                    if (loaded != null)
                    {
                        _watchedAddresses.Clear();
                        _watchedAddresses.AddRange(loaded);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Error loading watchlist: {ex.Message}");
            }
        }
    }

    public class WatchedAddress
    {
        public string Address { get; set; }
        public string Label { get; set; }
        public string Coin { get; set; }
        public decimal LastBalance { get; set; }
        public DateTime LastChecked { get; set; }
        public DateTime AddedAt { get; set; }
    }

    public class BalanceChangeEventArgs : EventArgs
    {
        public string Address { get; set; }
        public string Label { get; set; }
        public string Coin { get; set; }
        public decimal OldBalance { get; set; }
        public decimal NewBalance { get; set; }
        public decimal Change { get; set; }
        public bool IsIncrease { get; set; }
    }
}
