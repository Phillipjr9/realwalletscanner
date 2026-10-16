// CliRunner.cs — Terminal-mode wallet scanner
// Usage: WalletRecoveryTool.exe --cli [--mode smart|random|mnemonic] [--coins btc,eth,sol] [--threads N] [--limit N]
// Example: WalletRecoveryTool.exe --cli --mode smart --coins btc,eth --threads 8

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WalletRecoveryTool.Core;

namespace WalletRecoveryTool
{
    public static class CliRunner
    {
        public static async Task<int> RunAsync(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            PrintBanner();

            if (HasFlag(args, "--self-test") || HasFlag(args, "--selftest"))
            {
                return await RunSelfTestAsync();
            }

            var runInteractive = args.Length <= 1 || HasFlag(args, "--interactive");

            // Parse args (or ask interactively)
            string mode;
            string coinsArg;
            int threads;
            int limit;      // 0 = unlimited
            string mnemonic; // partial mnemonic for recovery mode

            if (runInteractive)
            {
                var selected = PromptForOptions();
                mode = selected.Mode;
                coinsArg = selected.CoinsArg;
                threads = selected.Threads;
                limit = selected.Limit;
                mnemonic = selected.Mnemonic;
            }
            else
            {
                mode = GetArg(args, "--mode", "smart");
                coinsArg = GetArg(args, "--coins", "btc,eth");
                threads = ParseIntOrDefault(GetArg(args, "--threads", "12"), 12);
                limit = ParseIntOrDefault(GetArg(args, "--limit", "0"), 0);
                mnemonic = GetArg(args, "--mnemonic", "");
            }

            var coins = coinsArg.Split(',').Select(c => c.Trim().ToLower()).ToHashSet();

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"  Mode    : {mode.ToUpper()}");
            Console.WriteLine($"  Coins   : {coinsArg.ToUpper()}");
            Console.WriteLine($"  Threads : {threads}");
            Console.WriteLine($"  Limit   : {(limit == 0 ? "unlimited" : limit.ToString())}");
            Console.ResetColor();
            Console.WriteLine();

            var engine = new WalletRecoveryEngine();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("\n\n[!] Stopping scan (Ctrl+C)...");
                cts.Cancel();
                engine.StopRecovery();
            };

            long totalChecked = 0;
            long totalFound   = 0;
            var startTime     = DateTime.Now;

            // Wire events
            engine.WalletFound += (s, e) =>
            {
                Interlocked.Increment(ref totalFound);
                var w = e.Wallet;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine();
                Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
                Console.WriteLine("║  💰  WALLET WITH BALANCE FOUND!                              ║");
                Console.WriteLine("╠══════════════════════════════════════════════════════════════╣");
                Console.WriteLine($"║  Source   : {(w.Source ?? "Unknown"),-48}  ║");
                Console.WriteLine($"║  Mnemonic : {Truncate(w.Mnemonic ?? w.PrivateKey ?? "N/A", 48),-48}  ║");
                Console.WriteLine($"║  BTC Addr : {(w.BitcoinAddress ?? "N/A"),-48}  ║");
                Console.WriteLine($"║  ETH Addr : {(w.EthereumAddress ?? "N/A"),-48}  ║");
                if (w.Balances != null)
                {
                    foreach (var kv in w.Balances.Where(b => b.Value > 0))
                        Console.WriteLine($"║  {kv.Key,-9}: {kv.Value:F8}                                         ║");
                }
                Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
                Console.ResetColor();
            };

            engine.WalletChecking += (s, e) =>
            {
                Interlocked.Exchange(ref totalChecked, e.CheckNumber);
            };

            engine.ProgressUpdated += (s, e) =>
            {
                // Reprint status line in-place
                var elapsed = DateTime.Now - startTime;
                var rate    = elapsed.TotalSeconds > 0 ? totalChecked / elapsed.TotalSeconds : 0;
                Console.Write($"\r  [{elapsed:hh\\:mm\\:ss}]  Checked: {totalChecked,10:N0}  |  Speed: {rate,7:N0}/s  |  Found: {totalFound}   ");
            };

            engine.ErrorOccurred += (s, e) =>
            {
                // Only print non-cooldown errors
                if (!e.ErrorMessage.Contains("cooldown") && !e.ErrorMessage.Contains("rate limit"))
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"\n  [!] {e.ErrorMessage}");
                    Console.ResetColor();
                }
            };

            var options = BuildOptions(mode, coins, limit, mnemonic, threads);

            Console.WriteLine("  Press Ctrl+C to stop.\n");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  Time       | Checked      | Speed    | Found");
            Console.WriteLine("  -----------+--------------+----------+------");
            Console.ResetColor();

            try
            {
                await engine.StartBruteForceRecovery(options);
            }
            catch (OperationCanceledException)
            {
                // normal stop
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n  [ERROR] {ex.Message}");
                Console.ResetColor();
                return 1;
            }

            var totalElapsed = DateTime.Now - startTime;
            Console.WriteLine($"\n\n  Done. Checked {totalChecked:N0} wallets in {totalElapsed:hh\\:mm\\:ss}. Found: {totalFound}");
            return 0;
        }

        private static async Task<int> RunSelfTestAsync()
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  Running Core Cryptographic & Cognitive Brain Self-Test Suite...");
            Console.ResetColor();

            var coreSuite = SelfTestRunner.RunAllCoreTests();
            foreach (var r in coreSuite.Results)
            {
                Console.ForegroundColor = r.Passed ? ConsoleColor.Green : ConsoleColor.Red;
                Console.WriteLine($"  {(r.Passed ? "✓" : "✗")} {r.Name} ({r.ElapsedMs:F1}ms)");
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine($"      {r.Details}");
            }
            Console.ResetColor();
            Console.WriteLine($"  Core Suite: {coreSuite.PassedCount}/{coreSuite.Results.Count} passed in {coreSuite.TotalElapsedMs:F1}ms\n");

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  Running funded-address detection self-test...");
            Console.ResetColor();

            // Public, well-known addresses used only for endpoint health validation.
            const string btcAddress = "1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNa";
            const string ethAddress = "0xd8dA6BF26964aF9D7eEd9e03E53415D37aA96045";

            decimal btcBalance = 0;
            decimal ethBalance = 0;
            string btcError = null;
            string ethError = null;

            try
            {
                Console.WriteLine("  • Checking BTC endpoint...");
                btcBalance = await GetBtcBalanceFromMempoolAsync(btcAddress);
            }
            catch (Exception ex)
            {
                btcError = ex.Message;
            }

            try
            {
                Console.WriteLine("  • Checking ETH endpoint...");
                ethBalance = await GetEthBalanceFromPublicNodeAsync(ethAddress);
            }
            catch (Exception ex)
            {
                ethError = ex.Message;
            }

            Console.WriteLine();
            Console.WriteLine("  Self-test results:");
            Console.WriteLine($"    BTC {btcAddress} => {(btcError == null ? $"{btcBalance:F0} sat" : $"ERROR: {btcError}")}");
            Console.WriteLine($"    ETH {ethAddress} => {(ethError == null ? $"{ethBalance:F8} ETH" : $"ERROR: {ethError}")}");

            var btcOk = btcError == null && btcBalance > 0;
            var ethOk = ethError == null && ethBalance > 0;

            Console.WriteLine();
            if (btcOk || ethOk)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  ✅ Self-test PASS: funded-address detection path is working.");
                Console.ResetColor();
                return 0;
            }

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  ❌ Self-test FAIL: could not confirm funded-address detection.");
            Console.WriteLine("     Check API connectivity/rate limits before running long scans.");
            Console.ResetColor();
            return 2;
        }

        private static async Task<decimal> GetBtcBalanceFromMempoolAsync(string address)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var json = await http.GetStringAsync($"https://mempool.space/api/address/{address}")
                .WaitAsync(TimeSpan.FromSeconds(20));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var chain = root.GetProperty("chain_stats");
            var funded = chain.GetProperty("funded_txo_sum").GetDecimal();
            var spent = chain.GetProperty("spent_txo_sum").GetDecimal();
            return funded - spent; // satoshis
        }

        private static async Task<decimal> GetEthBalanceFromPublicNodeAsync(string address)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var body = new
            {
                jsonrpc = "2.0",
                method = "eth_getBalance",
                @params = new[] { address, "latest" },
                id = 1
            };

            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var resp = await http.PostAsync("https://ethereum-rpc.publicnode.com", content)
                .WaitAsync(TimeSpan.FromSeconds(20));
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(20));

            using var doc = JsonDocument.Parse(json);
            var hex = doc.RootElement.GetProperty("result").GetString();
            if (string.IsNullOrWhiteSpace(hex) || !hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("ETH RPC returned invalid balance payload");

            var wei = BigInteger.Parse(hex.Substring(2), System.Globalization.NumberStyles.AllowHexSpecifier);
            return (decimal)wei / 1000000000000000000m;
        }

        private static RecoveryOptions BuildOptions(string mode, HashSet<string> coins, int limit, string mnemonic, int threads)
        {
            // Smart hunting flags live in AppSettings
            var settings = AppSettings.Load();
            // Only enable SmartHunting for --mode smart; random/hashcat use faster pipeline
            bool useSmartHunting = mode.ToLower() == "smart";
            settings.EnableSmartHunting         = useSmartHunting;
            settings.UseHashcatPipeline         = !useSmartHunting; // Use optimized pipeline for random mode
            settings.ScanBrainWallets           = useSmartHunting;
            settings.ScanWeakRngWallets         = useSmartHunting;
            settings.ScanHumanPatterns          = useSmartHunting;
            settings.ScanVulnerablePatterns     = useSmartHunting;
            settings.ScanDerivationPaths        = useSmartHunting;
            settings.ScanDictionaryBrainWallets = useSmartHunting;
            settings.ScanKnownLeakedKeys        = useSmartHunting;
            settings.ThreadCount                = Math.Max(threads, 8);
            settings.MaxConcurrentApiCalls      = Math.Max(300, Math.Max(threads * 24, settings.MaxConcurrentApiCalls));
            settings.CheckDelayMs               = 0;
            settings.TurboMode                  = true;
            settings.Save();
            AppSettings.InvalidateCache();

            var opts = new RecoveryOptions
            {
                CheckBalancesRealTime = true,
                QuickScanMode         = true,
                MaxAttempts           = limit > 0 ? limit : int.MaxValue,
                ScanBitcoin           = coins.Contains("btc"),
                ScanEthereum          = coins.Contains("eth"),
                ScanSolana            = coins.Contains("sol"),
                ScanUSDT              = coins.Contains("usdt"),
                ScanLitecoin          = coins.Contains("ltc"),
                ScanDogecoin          = coins.Contains("doge"),
                ScanPolygon           = coins.Contains("matic"),
                ScanBnb               = coins.Contains("bnb"),
            };

            switch (mode.ToLower())
            {
                case "smart":
                    opts.RecoveryType = RecoveryType.SmartHunting;
                    break;

                case "random":
                    opts.RecoveryType = RecoveryType.RandomGeneration;
                    break;

                case "mnemonic":
                    opts.RecoveryType  = RecoveryType.PartialMnemonic;
                    opts.PartialMnemonic = mnemonic;
                    break;

                default:
                    Console.WriteLine($"  Unknown mode '{mode}', using smart.");
                    opts.RecoveryType = RecoveryType.SmartHunting;
                    break;
            }

            return opts;
        }

        private static void PrintBanner()
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine();
            Console.WriteLine("  ╔═══════════════════════════════════════════╗");
            Console.WriteLine("  ║   Wallet Recovery Tool — Terminal Mode    ║");
            Console.WriteLine("  ╚═══════════════════════════════════════════╝");
            Console.ResetColor();
            Console.WriteLine();
        }

        private static (string Mode, string CoinsArg, int Threads, int Limit, string Mnemonic) PromptForOptions()
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  Interactive setup");
            Console.ResetColor();
            Console.WriteLine("  Use ↑/↓ to move, Enter to select, and Space to toggle coin choices.\n");

            var mode = PromptWithArrowMenu(
                "  Select mode",
                new[]
                {
                    ("smart", "Smart hunting"),
                    ("random", "Random generation"),
                    ("mnemonic", "Partial mnemonic")
                },
                0);

            var coinsArg = PromptWithCoinMenu();

            var defaultThreads = Math.Max(1, AppSettings.Current.ThreadCount);
            var threads = ParseIntOrDefault(Prompt("  Threads", defaultThreads.ToString()), defaultThreads);
            if (threads < 1) threads = defaultThreads;

            var limit = ParseIntOrDefault(Prompt("  Max attempts (0=unlimited)", "0"), 0);
            if (limit < 0) limit = 0;

            var mnemonic = string.Empty;
            if (mode == "mnemonic")
            {
                mnemonic = Prompt("  Partial mnemonic", "");
            }

            Console.WriteLine();
            return (mode, coinsArg, threads, limit, mnemonic);
        }

        private static string Prompt(string label, string defaultValue)
        {
            if (string.IsNullOrEmpty(defaultValue))
            {
                Console.Write($"{label}: ");
                return Console.ReadLine() ?? string.Empty;
            }

            Console.Write($"{label} [{defaultValue}]: ");
            var input = Console.ReadLine();
            return string.IsNullOrWhiteSpace(input) ? defaultValue : input.Trim();
        }

        private static string PromptWithArrowMenu(string title, IReadOnlyList<(string Value, string Label)> options, int defaultIndex)
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
            {
                Console.WriteLine(title + ":");
                for (var i = 0; i < options.Count; i++)
                {
                    var marker = i == defaultIndex ? "[default]" : string.Empty;
                    Console.WriteLine($"    {i + 1}) {options[i].Label} {marker}".TrimEnd());
                }

                var choice = Prompt("  Choice", (defaultIndex + 1).ToString());
                var selectedIndex = Math.Clamp(ParseIntOrDefault(choice, defaultIndex + 1) - 1, 0, options.Count - 1);
                return options[selectedIndex].Value;
            }

            var selected = defaultIndex;
            var top = Console.CursorTop;

            while (true)
            {
                RenderArrowMenu(title, options, selected, top);
                var key = Console.ReadKey(intercept: true);

                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                        selected = selected == 0 ? options.Count - 1 : selected - 1;
                        break;
                    case ConsoleKey.DownArrow:
                        selected = selected == options.Count - 1 ? 0 : selected + 1;
                        break;
                    case ConsoleKey.Enter:
                        Console.SetCursorPosition(0, top + options.Count + 3);
                        Console.WriteLine();
                        return options[selected].Value;
                }

                if (key.KeyChar >= '1' && key.KeyChar <= '9')
                {
                    var numericIndex = key.KeyChar - '1';
                    if (numericIndex >= 0 && numericIndex < options.Count)
                    {
                        selected = numericIndex;
                    }
                }
            }
        }

        private static string PromptWithCoinMenu()
        {
            var coinOptions = new[]
            {
                ("btc", "Bitcoin (BTC)"),
                ("eth", "Ethereum (ETH)"),
                ("sol", "Solana (SOL)"),
                ("usdt", "USDT"),
                ("ltc", "Litecoin (LTC)"),
                ("doge", "Dogecoin (DOGE)"),
                ("matic", "Polygon (MATIC)"),
                ("bnb", "BNB")
            };

            var selectedCoins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "btc", "eth" };

            if (Console.IsInputRedirected || Console.IsOutputRedirected)
            {
                Console.WriteLine("  Coins examples: btc,eth  or  btc,eth,sol,usdt,ltc,doge,matic,bnb");
                return Prompt("  Coins", "btc,eth").ToLower();
            }

            var current = 0;
            var top = Console.CursorTop;

            while (true)
            {
                RenderCoinMenu(coinOptions, selectedCoins, current, top);
                var key = Console.ReadKey(intercept: true);

                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                        current = current == 0 ? coinOptions.Length - 1 : current - 1;
                        break;
                    case ConsoleKey.DownArrow:
                        current = current == coinOptions.Length - 1 ? 0 : current + 1;
                        break;
                    case ConsoleKey.Spacebar:
                        ToggleSelectedCoin(selectedCoins, coinOptions[current].Item1);
                        if (selectedCoins.Count == 0)
                            selectedCoins.Add("btc");
                        break;
                    case ConsoleKey.A:
                        selectedCoins = coinOptions.Select(c => c.Item1).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        break;
                    case ConsoleKey.D:
                        selectedCoins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "btc", "eth" };
                        break;
                    case ConsoleKey.Enter:
                        Console.SetCursorPosition(0, top + coinOptions.Length + 5);
                        Console.WriteLine();
                        return string.Join(",", coinOptions.Where(c => selectedCoins.Contains(c.Item1)).Select(c => c.Item1));
                }
            }
        }

        private static void RenderArrowMenu(string title, IReadOnlyList<(string Value, string Label)> options, int selected, int top)
        {
            Console.SetCursorPosition(0, top);
            WriteMenuLine(title.PadRight(GetMenuWidth()));
            WriteMenuLine("  Use arrow keys or 1-9, then press Enter.".PadRight(GetMenuWidth()));

            for (var i = 0; i < options.Count; i++)
            {
                var prefix = i == selected ? "  ❯ " : "    ";
                var line = $"{prefix}{i + 1}) {options[i].Label}";
                if (i == selected)
                {
                    Console.ForegroundColor = ConsoleColor.Black;
                    Console.BackgroundColor = ConsoleColor.Cyan;
                    WriteMenuLine(line.PadRight(GetMenuWidth()));
                    Console.ResetColor();
                }
                else
                {
                    WriteMenuLine(line.PadRight(GetMenuWidth()));
                }
            }

            WriteMenuLine(new string(' ', GetMenuWidth()));
        }

        private static void RenderCoinMenu((string, string)[] coinOptions, HashSet<string> selectedCoins, int current, int top)
        {
            Console.SetCursorPosition(0, top);
            WriteMenuLine("  Select coins".PadRight(GetMenuWidth()));
            WriteMenuLine("  ↑/↓ move  Space toggle  Enter confirm  A all  D defaults".PadRight(GetMenuWidth()));

            for (var i = 0; i < coinOptions.Length; i++)
            {
                var isSelected = selectedCoins.Contains(coinOptions[i].Item1);
                var prefix = i == current ? "  ❯ " : "    ";
                var checkbox = isSelected ? "[x]" : "[ ]";
                var line = $"{prefix}{checkbox} {coinOptions[i].Item2}";

                if (i == current)
                {
                    Console.ForegroundColor = ConsoleColor.Black;
                    Console.BackgroundColor = ConsoleColor.DarkYellow;
                    WriteMenuLine(line.PadRight(GetMenuWidth()));
                    Console.ResetColor();
                }
                else
                {
                    WriteMenuLine(line.PadRight(GetMenuWidth()));
                }
            }

            var summary = "  Selected: " + string.Join(", ", coinOptions.Where(c => selectedCoins.Contains(c.Item1)).Select(c => c.Item2.Split('(')[0].Trim()));
            WriteMenuLine(summary.PadRight(GetMenuWidth()));
            WriteMenuLine(new string(' ', GetMenuWidth()));
        }

        private static void ToggleSelectedCoin(HashSet<string> selectedCoins, string coin)
        {
            if (!selectedCoins.Add(coin))
            {
                selectedCoins.Remove(coin);
            }
        }

        private static void WriteMenuLine(string text)
        {
            Console.Write(text);
            Console.WriteLine();
        }

        private static int GetMenuWidth()
        {
            try
            {
                return Math.Max(60, Console.BufferWidth - 1);
            }
            catch
            {
                return 80;
            }
        }

        private static bool HasFlag(string[] args, string name) =>
            args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        private static int ParseIntOrDefault(string input, int fallback) =>
            int.TryParse(input, out var value) ? value : fallback;

        private static string GetArg(string[] args, string name, string defaultVal)
        {
            var idx = Array.IndexOf(args, name);
            if (idx >= 0 && idx + 1 < args.Length)
                return args[idx + 1];
            return defaultVal;
        }

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s.Substring(0, max - 3) + "...";
    }
}
