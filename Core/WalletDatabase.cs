using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// JSON file-based wallet storage (no database required)
    /// </summary>
    public class WalletDatabase : IDisposable
    {
        private readonly string _walletsFile;
        private readonly string _sessionStatsFile;
        private readonly string _usdHistoryFile;
        private readonly string _dataFolder;
        private readonly object _fileLock = new object();

        public class WalletRecord
        {
            public int Id { get; set; }
            public string Mnemonic { get; set; }
            public string PrivateKey { get; set; }
            public string BitcoinAddress { get; set; }
            public string BitcoinAddressSegWit { get; set; }
            public string BitcoinAddressNativeSegWit { get; set; }
            public string BitcoinAddressTaproot { get; set; }
            public string EthereumAddress { get; set; }
            public string SolanaAddress { get; set; }
            public string LitecoinAddress { get; set; }
            public string DogecoinAddress { get; set; }
            public string RippleAddress { get; set; }
            public string CardanoAddress { get; set; }
            public decimal BtcBalance { get; set; }
            public decimal BtcSegWitBalance { get; set; }
            public decimal BtcNativeSegWitBalance { get; set; }
            public decimal EthBalance { get; set; }
            public decimal SolBalance { get; set; }
            public decimal UsdtBalance { get; set; }
            public decimal LtcBalance { get; set; }
            public decimal DogeBalance { get; set; }
            public decimal MaticBalance { get; set; }
            public decimal BnbBalance { get; set; }
            public decimal XrpBalance { get; set; }
            public decimal AdaBalance { get; set; }
            public decimal TotalBalanceUSD { get; set; }
            public DateTime FoundDate { get; set; }
            public string RecoveryType { get; set; }
            public string SessionId { get; set; }
        }

        public class SessionStatsRecord
        {
            public string SessionId { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime? EndTime { get; set; }
            public string RecoveryType { get; set; }
            public int TotalChecked { get; set; }
            public int WalletsFound { get; set; }
            public double AvgSpeed { get; set; }
            public string Notes { get; set; }
        }

        public class UsdHistoryEntry
        {
            public DateTime Timestamp { get; set; }
            public decimal UsdValue { get; set; }
            public string RecoveryType { get; set; }
            public string SessionId { get; set; }
            public string Address { get; set; }
        }

        public WalletDatabase()
        {
            _dataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WalletRecoveryTool",
                "Database"
            );
            Directory.CreateDirectory(_dataFolder);
            _walletsFile = Path.Combine(_dataFolder, "wallets.json");
            _sessionStatsFile = Path.Combine(_dataFolder, "session_stats.json");
            _usdHistoryFile = Path.Combine(_dataFolder, "usd_history.json");
        }

        public void SaveWallet(CryptoWalletGenerator.WalletInfo wallet, string sessionId = null, string recoveryType = null)
        {
            try
            {
                lock (_fileLock)
                {
                    var wallets = LoadWalletsFromFile();
                    
                    decimal GetBalance(string symbol)
                    {
                        if (wallet?.Balances == null)
                            return 0;
                        return wallet.Balances.TryGetValue(symbol, out var value) ? value : 0;
                    }

                    var btcBal = GetBalance("BTC");
                    var btcSegWitBal = GetBalance("BTC-SegWit");
                    var btcNativeBal = GetBalance("BTC-NativeSegWit");
                    var ethBal = GetBalance("ETH");
                    var solBal = GetBalance("SOL");
                    // Sum any chain-specific USDT entries (USDT, USDT-Polygon, USDT-BSC, USDT-TRC20, etc.)
                    decimal usdtBal = 0;
                    if (wallet?.Balances != null)
                    {
                        usdtBal = wallet.Balances
                            .Where(kv => kv.Key != null && kv.Key.StartsWith("USDT", StringComparison.OrdinalIgnoreCase))
                            .Sum(kv => kv.Value);
                    }
                    var ltcBal = GetBalance("LTC");
                    var dogeBal = GetBalance("DOGE");
                    var maticBal = GetBalance("MATIC");
                    var bnbBal = GetBalance("BNB");
                    var xrpBal = GetBalance("XRP");
                    var adaBal = GetBalance("ADA");

                    var totalUSD = wallet?.TotalUsdValue > 0
                        ? wallet.TotalUsdValue
                        : CalculateFallbackUsdValue(wallet?.Balances ?? new Dictionary<string, decimal>());

                    var foundDate = wallet?.FoundAt ?? DateTime.Now;

                    // SAFETY: Never save wallets with no real balance
                    var totalBal = btcBal + btcSegWitBal + btcNativeBal + ethBal + solBal + usdtBal + ltcBal + dogeBal + maticBal + bnbBal + xrpBal + adaBal;
                    if (totalBal <= 0.00000001m && totalUSD <= 0.01m)
                    {
                        // Log full balances to help debugging why a wallet was rejected
                        var balancesStr = wallet?.Balances != null
                            ? string.Join(", ", wallet.Balances.Select(kv => $"{kv.Key}={kv.Value}"))
                            : "<no balances checked>";
                        DebugLogger.Log($"WalletDatabase: Rejected save — no real balance (total={totalBal}) | Balances: {balancesStr}");
                        return;
                    }

                    var record = new WalletRecord
                    {
                        Id = wallets.Count > 0 ? wallets.Max(w => w.Id) + 1 : 1,
                        Mnemonic = wallet.Mnemonic ?? "",
                        PrivateKey = wallet.PrivateKey ?? "",
                        BitcoinAddress = wallet.BitcoinAddress ?? "",
                        BitcoinAddressSegWit = wallet.BitcoinAddressSegWit ?? "",
                        BitcoinAddressNativeSegWit = wallet.BitcoinAddressNativeSegWit ?? "",
                        BitcoinAddressTaproot = wallet.BitcoinAddressTaproot ?? "",
                        EthereumAddress = wallet.EthereumAddress ?? "",
                        SolanaAddress = wallet.SolanaAddress ?? "",
                        LitecoinAddress = wallet.LitecoinAddress ?? "",
                        DogecoinAddress = wallet.DogecoinAddress ?? "",
                        RippleAddress = wallet.RippleAddress ?? "",
                        CardanoAddress = wallet.CardanoAddress ?? "",
                        BtcBalance = btcBal,
                        BtcSegWitBalance = btcSegWitBal,
                        BtcNativeSegWitBalance = btcNativeBal,
                        EthBalance = ethBal,
                        SolBalance = solBal,
                        UsdtBalance = usdtBal,
                        LtcBalance = ltcBal,
                        DogeBalance = dogeBal,
                        MaticBalance = maticBal,
                        BnbBalance = bnbBal,
                        XrpBalance = xrpBal,
                        AdaBalance = adaBal,
                        TotalBalanceUSD = totalUSD,
                        FoundDate = foundDate,
                        RecoveryType = recoveryType ?? "",
                        SessionId = sessionId ?? ""
                    };

                    // Skip duplicate wallets (same primary address)
                    var primaryAddr = record.BitcoinAddress.Length > 0 ? record.BitcoinAddress
                        : record.EthereumAddress.Length > 0 ? record.EthereumAddress
                        : record.SolanaAddress;
                    if (!string.IsNullOrEmpty(primaryAddr) && wallets.Any(w =>
                        w.BitcoinAddress == primaryAddr || w.EthereumAddress == primaryAddr || w.SolanaAddress == primaryAddr))
                    {
                        return; // Already saved
                    }

                    wallets.Add(record);
                    SaveWalletsToFile(wallets);

                    if (totalUSD > 0)
                    {
                        AppendUsdHistoryEntry(new UsdHistoryEntry
                        {
                            Timestamp = foundDate,
                            UsdValue = totalUSD,
                            RecoveryType = recoveryType ?? string.Empty,
                            SessionId = sessionId ?? string.Empty,
                            Address = wallet.BitcoinAddress ?? wallet.EthereumAddress ?? wallet.SolanaAddress ?? string.Empty
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Error saving wallet to file: {ex.Message}");
            }
        }

        public List<CryptoWalletGenerator.WalletInfo> SearchByBalance(decimal minBalance)
        {
            var wallets = new List<CryptoWalletGenerator.WalletInfo>();

            try
            {
                lock (_fileLock)
                {
                    var records = LoadWalletsFromFile();
                    var filtered = records
                        .Where(r => (r.BtcBalance + r.BtcSegWitBalance + r.BtcNativeSegWitBalance + 
                                    r.EthBalance + r.SolBalance + r.UsdtBalance) >= minBalance)
                        .OrderByDescending(r => r.TotalBalanceUSD)
                        .ToList();

                    foreach (var record in filtered)
                    {
                        wallets.Add(MapRecordToWallet(record));
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Error searching wallets: {ex.Message}");
            }

            return wallets;
        }

        public List<CryptoWalletGenerator.WalletInfo> GetAllFoundWallets()
        {
            return SearchByBalance(0.00000001m);
        }

        public List<UsdHistoryEntry> GetUsdHistory(int days = 30)
        {
            lock (_fileLock)
            {
                var history = LoadUsdHistoryFromFile();
                if (days > 0)
                {
                    var cutoff = DateTime.Now.AddDays(-days);
                    history = history.Where(entry => entry.Timestamp >= cutoff).ToList();
                }
                return history.OrderBy(entry => entry.Timestamp).ToList();
            }
        }

        public int GetTotalWalletsCount()
        {
            try
            {
                lock (_fileLock)
                {
                    return LoadWalletsFromFile().Count;
                }
            }
            catch
            {
                return 0;
            }
        }

        public void SaveSessionStats(string sessionId, DateTime startTime, DateTime? endTime, 
            string recoveryType, int totalChecked, int walletsFound, double avgSpeed, string notes = "")
        {
            try
            {
                lock (_fileLock)
                {
                    var sessions = LoadSessionStatsFromFile();
                    
                    var existing = sessions.FirstOrDefault(s => s.SessionId == sessionId);
                    if (existing != null)
                    {
                        sessions.Remove(existing);
                    }

                    sessions.Add(new SessionStatsRecord
                    {
                        SessionId = sessionId,
                        StartTime = startTime,
                        EndTime = endTime,
                        RecoveryType = recoveryType,
                        TotalChecked = totalChecked,
                        WalletsFound = walletsFound,
                        AvgSpeed = avgSpeed,
                        Notes = notes
                    });

                    SaveSessionStatsToFile(sessions);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Error saving session stats: {ex.Message}");
            }
        }

        private List<WalletRecord> LoadWalletsFromFile()
        {
            if (!File.Exists(_walletsFile))
                return new List<WalletRecord>();

            try
            {
                var json = File.ReadAllText(_walletsFile);
                return JsonSerializer.Deserialize<List<WalletRecord>>(json) ?? new List<WalletRecord>();
            }
            catch
            {
                return new List<WalletRecord>();
            }
        }

        private void SaveWalletsToFile(List<WalletRecord> wallets)
        {
            var json = JsonSerializer.Serialize(wallets, new JsonSerializerOptions { WriteIndented = true });
            AtomicWrite(_walletsFile, json);
        }

        private List<SessionStatsRecord> LoadSessionStatsFromFile()
        {
            if (!File.Exists(_sessionStatsFile))
                return new List<SessionStatsRecord>();

            try
            {
                var json = File.ReadAllText(_sessionStatsFile);
                return JsonSerializer.Deserialize<List<SessionStatsRecord>>(json) ?? new List<SessionStatsRecord>();
            }
            catch
            {
                return new List<SessionStatsRecord>();
            }
        }

        private void SaveSessionStatsToFile(List<SessionStatsRecord> sessions)
        {
            var json = JsonSerializer.Serialize(sessions, new JsonSerializerOptions { WriteIndented = true });
            AtomicWrite(_sessionStatsFile, json);
        }

        private List<UsdHistoryEntry> LoadUsdHistoryFromFile()
        {
            if (!File.Exists(_usdHistoryFile))
                return new List<UsdHistoryEntry>();

            try
            {
                var json = File.ReadAllText(_usdHistoryFile);
                return JsonSerializer.Deserialize<List<UsdHistoryEntry>>(json) ?? new List<UsdHistoryEntry>();
            }
            catch
            {
                return new List<UsdHistoryEntry>();
            }
        }

        private void SaveUsdHistoryToFile(List<UsdHistoryEntry> history)
        {
            var json = JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true });
            AtomicWrite(_usdHistoryFile, json);
        }

        /// <summary>
        /// Atomic file write: write to temp file, then rename.
        /// Prevents data loss if the process crashes mid-write.
        /// </summary>
        private static void AtomicWrite(string targetPath, string content)
        {
            var tempPath = targetPath + ".tmp";
            File.WriteAllText(tempPath, content);
            File.Move(tempPath, targetPath, overwrite: true);
        }

        private void AppendUsdHistoryEntry(UsdHistoryEntry entry)
        {
            if (entry == null || entry.UsdValue <= 0)
                return;

            var history = LoadUsdHistoryFromFile();
            history.Add(entry);
            SaveUsdHistoryToFile(history);
        }

        private static decimal CalculateFallbackUsdValue(decimal btcBal, decimal ethBal, decimal solBal, decimal usdtBal,
            decimal ltcBal = 0, decimal dogeBal = 0, decimal maticBal = 0, decimal bnbBal = 0,
            decimal xrpBal = 0, decimal adaBal = 0)
        {
            // Use live prices when available, fall back to approximate values
            decimal Price(string sym, decimal fallback)
            {
                var p = LivePriceTicker.GetPrice(sym);
                return p > 0 ? p : fallback;
            }

            return btcBal * Price("BTC", 40000m) + ethBal * Price("ETH", 2000m) + solBal * Price("SOL", 100m) + usdtBal 
                 + ltcBal * Price("LTC", 80m) + dogeBal * Price("DOGE", 0.10m) + maticBal * Price("MATIC", 0.50m) + bnbBal * Price("BNB", 300m)
                 + xrpBal * Price("XRP", 0.50m) + adaBal * Price("ADA", 0.35m);
        }

        /// <summary>
        /// Public overload: estimate USD value from a balances dictionary
        /// </summary>
        public static decimal CalculateFallbackUsdValue(Dictionary<string, decimal> balances)
        {
            if (balances == null || balances.Count == 0) return 0;
            balances.TryGetValue("BTC", out var btc);
            balances.TryGetValue("ETH", out var eth);
            balances.TryGetValue("SOL", out var sol);
            balances.TryGetValue("USDT", out var usdt);
            balances.TryGetValue("LTC", out var ltc);
            balances.TryGetValue("DOGE", out var doge);
            balances.TryGetValue("MATIC", out var matic);
            balances.TryGetValue("BNB", out var bnb);
            balances.TryGetValue("XRP", out var xrp);
            balances.TryGetValue("ADA", out var ada);
            return CalculateFallbackUsdValue(btc, eth, sol, usdt, ltc, doge, matic, bnb, xrp, ada);
        }

        private CryptoWalletGenerator.WalletInfo MapRecordToWallet(WalletRecord record)
        {
            var wallet = new CryptoWalletGenerator.WalletInfo
            {
                Mnemonic = record.Mnemonic,
                PrivateKey = record.PrivateKey,
                BitcoinAddress = record.BitcoinAddress,
                BitcoinAddressSegWit = record.BitcoinAddressSegWit,
                BitcoinAddressNativeSegWit = record.BitcoinAddressNativeSegWit,
                BitcoinAddressTaproot = record.BitcoinAddressTaproot,
                EthereumAddress = record.EthereumAddress,
                SolanaAddress = record.SolanaAddress,
                LitecoinAddress = record.LitecoinAddress,
                DogecoinAddress = record.DogecoinAddress,
                RippleAddress = record.RippleAddress,
                CardanoAddress = record.CardanoAddress,
                Balances = new Dictionary<string, decimal>
                {
                    ["BTC"] = record.BtcBalance,
                    ["BTC-SegWit"] = record.BtcSegWitBalance,
                    ["BTC-NativeSegWit"] = record.BtcNativeSegWitBalance,
                    ["ETH"] = record.EthBalance,
                    ["SOL"] = record.SolBalance,
                    ["USDT"] = record.UsdtBalance,
                    ["LTC"] = record.LtcBalance,
                    ["DOGE"] = record.DogeBalance,
                    ["MATIC"] = record.MaticBalance,
                    ["BNB"] = record.BnbBalance,
                    ["XRP"] = record.XrpBalance,
                    ["ADA"] = record.AdaBalance
                }
            };

            wallet.TotalUsdValue = record.TotalBalanceUSD;
            wallet.FoundAt = record.FoundDate;

            return wallet;
        }

        public void Dispose()
        {
            // No resources to dispose with JSON file storage
        }
    }
}
