using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WalletRecoveryTool.Core
{
    public class WalletRecoveryEngine
    {
        private readonly BalanceChecker _balanceChecker;
        private CancellationTokenSource _cancellationTokenSource;
        private readonly WalletLogger _logger;
        private readonly RecoveryStatistics _statistics;
        private readonly HashSet<string> _checkedAddresses = new HashSet<string>();  // SPEED BOOST: Deduplication
        private readonly MnemonicTracker _mnemonicTracker;  // PERSISTENT: Skip already-scanned mnemonics
        
        public event EventHandler<WalletFoundEventArgs> WalletFound;
        public event EventHandler<ProgressEventArgs> ProgressUpdated;
        public event EventHandler<StatisticsEventArgs> StatisticsUpdated;
        public event EventHandler<ErrorEventArgs> ErrorOccurred;
        public event EventHandler<WalletCheckingEventArgs> WalletChecking;
        
        // Public properties for analytics
        public RecoveryStatistics Statistics => _statistics;
        public SessionManager.RecoverySession CurrentSession { get; private set; }
        
        // Live counter from active SmartWalletHunter (bypasses throttle)
        private SmartWalletHunter _activeHunter;
        public long LiveTotalChecked => _activeHunter?.TotalChecked ?? Statistics.TotalChecked;
        public WalletCheckingEventArgs LatestWalletChecking => _activeHunter?.LatestWalletChecking;
        public bool TryDequeueWalletChecking(out WalletCheckingEventArgs args)
        {
            args = null;
            return _activeHunter != null && _activeHunter.TryDequeueWalletChecking(out args);
        }
        
        public WalletRecoveryEngine()
        {
            _balanceChecker = new BalanceChecker();
            var settings = AppSettings.Current;
            _logger = new WalletLogger(settings.EnableLogging);
            _statistics = new RecoveryStatistics();
            _mnemonicTracker = GlobalMnemonicTracker.Instance;  // Use global singleton for persistence
            
            // AUTO-ALERT: Send Telegram notification whenever a wallet with balance is found
            WalletFound += async (sender, e) =>
            {
                try
                {
                    var alertSettings = AppSettings.Current;
                    if (alertSettings.EnableTelegramAlerts &&
                        !string.IsNullOrEmpty(alertSettings.TelegramChatId))
                    {
                        await TelegramAlertService.SendWalletFoundAlertAsync(e.Wallet, alertSettings);
                        DebugLogger.Log("📱 Telegram alert sent!");
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"⚠️ Telegram alert failed: {ex.Message}");
                }
            };
        }

        /// <summary>
        /// Extended derivation path scan: checks all known wallet software paths
        /// This catches wallets using non-standard derivation paths (Electrum, Coinomi, etc.)
        /// Only runs when DeepScan is enabled to avoid excessive API calls
        /// </summary>
        private async Task<bool> CheckExtendedDerivationPaths(CryptoWalletGenerator.WalletInfo wallet, string mnemonic, RecoveryOptions options)
        {
            try
            {
                var settings = AppSettings.Current;
                if (!settings.EnableDeepScan) return false;
                
                // Use the DerivationPathScanner to check all known paths
                var cryptoFilter = new List<string>();
                if (options.ScanBitcoin) cryptoFilter.Add("BTC");
                if (options.ScanEthereum) cryptoFilter.Add("ETH");
                
                if (cryptoFilter.Count == 0) return false;
                
                var results = await DerivationPathScanner.FindAddressesWithBalance(
                    mnemonic, _balanceChecker, maxAddressIndex: 5, cryptoTypesFilter: cryptoFilter);
                
                if (results.Count > 0)
                {
                    // Merge found balances into the wallet
                    foreach (var (path, address, balance) in results)
                    {
                        var key = $"DeepScan-{path}";
                        wallet.Balances[key] = balance;
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Extended derivation path scan error: {ex.Message}");
            }
            return false;
        }

        public async Task StartBruteForceRecovery(RecoveryOptions options)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;
            BalanceChecker.SetGlobalCancellationToken(token); // Enable fast API cancellation
            BalanceChecker.ResetAllProviderCooldowns(); // Reset stale backoff from previous sessions
            
            // DYNAMIC SPEED SCALING: Scale API throttle based on enabled coins
            double speedMult = CalculateSpeedMultiplier(options);
            int baseApiCalls = AppSettings.Current.MaxConcurrentApiCalls;
            int scaledApiCalls = (int)(baseApiCalls * speedMult);
            BalanceChecker.ReconfigureApiThrottle(scaledApiCalls);
            DebugLogger.Log($"[ENGINE] Speed multiplier: {speedMult:F1}x (enabled coins → {scaledApiCalls} API slots, base was {baseApiCalls})");
            
            _statistics.Reset();
            _statistics.Start();

            // Create recovery session
            CurrentSession = new SessionManager.RecoverySession
            {
                RecoveryType = options.RecoveryType,
                TotalAttempts = options.MaxAttempts,
                StartTime = DateTime.Now
            };
            SessionManager.SaveSession(CurrentSession);
            
            // Reset address deduplication cache for new recovery and ensure persistent empty filters are active
            _checkedAddresses.Clear();
            try
            {
                FundedAddressFilter.InitializeBTCEmpty();
                FundedAddressFilter.InitializeETHEmpty();
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[ENGINE] Empty filter init warning: {ex.Message}");
            }

            try
            {
                if (options.RecoveryType == RecoveryType.PartialMnemonic)
                {
                    DebugLogger.Log("[ENGINE] Starting PartialMnemonic recovery");
                    DebugLogger.Log("[ENGINE] Starting PartialMnemonic recovery");
                    await RecoverFromPartialMnemonic(options, token);
                }
                else if (options.RecoveryType == RecoveryType.RandomGeneration)
                {
                    // Check if Smart Hunting is enabled for dramatically better results
                    var settings = AppSettings.Current;
                    if (settings.EnableSmartHunting)
                    {
                        DebugLogger.Log("[ENGINE] Starting SmartHunting recovery (EnableSmartHunting=true)");
                        await RecoverWithSmartHunting(options, token);
                    }
                    else if (settings.UseHashcatPipeline && !options.ScanSolana)
                    {
                        DebugLogger.Log("[ENGINE] Starting HASHCAT PIPELINE (UseHashcatPipeline=true) — optimized PBKDF2 batch processing");
                        await RecoverWithHashcatPipeline(options, token);
                    }
                    else
                    {
                        await RecoverFromRandomGeneration(options, token);
                    }
                }
                else if (options.RecoveryType == RecoveryType.PrivateKeyBruteForce)
                {
                    await RecoverFromPrivateKeyBruteForce(options, token);
                }
                else if (options.RecoveryType == RecoveryType.SmartHunting)
                {
                    await RecoverWithSmartHunting(options, token);
                }
            }
            catch (OperationCanceledException)
            {
                // Recovery was cancelled by user
                DebugLogger.Log("Recovery operation cancelled by user");
            }
            catch (HttpRequestException ex)
            {
                var errorMsg = $"Network error - check your internet connection: {ex.Message}";
                DebugLogger.Log($"Network error in recovery: {ex.Message}");
                ErrorOccurred?.Invoke(this, new ErrorEventArgs(errorMsg));
            }
            catch (UnauthorizedAccessException ex)
            {
                var errorMsg = $"API authentication failed - check your API keys in Settings: {ex.Message}";
                DebugLogger.Log($"API auth error: {ex.Message}");
                ErrorOccurred?.Invoke(this, new ErrorEventArgs(errorMsg));
            }
            catch (ArgumentException ex)
            {
                var errorMsg = $"Invalid input - please check your settings: {ex.Message}";
                DebugLogger.Log($"Invalid argument: {ex.Message}");
                ErrorOccurred?.Invoke(this, new ErrorEventArgs(errorMsg));
            }
            catch (InvalidOperationException ex)
            {
                var errorMsg = $"Invalid operation: {ex.Message}";
                DebugLogger.Log($"Invalid operation: {ex.Message}");
                ErrorOccurred?.Invoke(this, new ErrorEventArgs(errorMsg));
            }
            catch (Exception ex)
            {
                var errorMsg = $"Unexpected error: {ex.Message}\n\nType: {ex.GetType().Name}";
                DebugLogger.Log($"Unexpected error in recovery: {ex.GetType().Name} - {ex.Message}");
                ErrorOccurred?.Invoke(this, new ErrorEventArgs(errorMsg));
            }
            finally
            {
                _statistics.Stop();
                try { _mnemonicTracker.Flush(); } catch { }
                try
                {
                    var upgrade = CognitiveBrainEngine.Instance.ExecuteCognitivePulse();
                    DebugLogger.Log($"[BRAIN ENGINE] Self-upgraded to {upgrade.VersionTag} (IQ {upgrade.CognitiveIqScore:F1} | Top Arm: {upgrade.TopStrategyArm})");
                }
                catch { }
            }
        }

        /// <summary>
        /// Calculates speed multiplier based on enabled coin types.
        /// Fewer coins → less work per wallet → more wallets processed in parallel.
        /// Weights reflect approximate API calls per coin type.
        /// </summary>
        private static double CalculateSpeedMultiplier(RecoveryOptions options)
        {
            int weight = 0;
            if (options.ScanBitcoin) weight += 4;   // Legacy + SegWit + NativeSegWit + Taproot
            if (options.ScanEthereum) weight += 1;
            if (options.ScanUSDT) weight += 1;
            if (options.ScanSolana) weight += 1;
            if (options.ScanLitecoin) weight += 1;
            if (options.ScanDogecoin) weight += 1;
            if (options.ScanPolygon) weight += 1;
            if (options.ScanBnb) weight += 1;

            if (weight == 0) weight = 1;

            const int maxWeight = 11; // All coins enabled
            double multiplier = (double)maxWeight / weight;
            return Math.Min(multiplier, 8.0); // Cap at 8x to allow more parallelism with fewer coins
        }

        /// <summary>
        /// Calculate effective thread count scaled by speed multiplier.
        /// More threads when fewer coins are selected (each wallet uses fewer API slots).
        /// </summary>
        private static int CalculateEffectiveThreadCount(RecoveryOptions options, int baseThreadCount)
        {
            double multiplier = CalculateSpeedMultiplier(options);
            int effectiveThreads = (int)(baseThreadCount * multiplier);
            return Math.Clamp(effectiveThreads, baseThreadCount, 128);
        }

        /// <summary>
        /// Whether ETH address generation is needed (any EVM chain or USDT selected).
        /// </summary>
        private static bool NeedEthAddress(RecoveryOptions options)
        {
            return options.ScanEthereum || options.ScanUSDT || options.ScanPolygon || options.ScanBnb;
        }

        private async Task RecoverFromPartialMnemonic(RecoveryOptions options, CancellationToken token)
        {
            var variations = CryptoWalletGenerator.GenerateMnemonicVariations(options.PartialMnemonic);
            int processed = 0;
            int totalVariations = variations.Count;
            
            var settings = AppSettings.Current;
            int threadCount = CalculateEffectiveThreadCount(options, settings.ThreadCount);
            DebugLogger.Log($"[ENGINE] PartialMnemonic threads: {threadCount} (base={settings.ThreadCount}, multiplier={CalculateSpeedMultiplier(options):F1}x)");
            long lastProgressTicks = 0;
            long lastStatsTicks = 0;

            // Async-safe parallel processing (avoids .GetAwaiter().GetResult() deadlock risk)
            await Parallel.ForEachAsync(variations, 
                new ParallelOptions { MaxDegreeOfParallelism = threadCount, CancellationToken = token },
                async (mnemonic, ct) =>
                {
                    if (ct.IsCancellationRequested) return;

                    // STRICT NON-REPETITION RULE: Never repeat a mnemonic already scanned as empty across any section
                    if (!_mnemonicTracker.TryMarkAsScanned(mnemonic))
                    {
                        _statistics.IncrementSkipped();
                        return; // Skip - already scanned in a previous session or another section
                    }

                    try
                    {
                        var wallet = CryptoWalletGenerator.GenerateWalletSelective(mnemonic,
                            needEth: NeedEthAddress(options), needSol: options.ScanSolana,
                            needLtc: options.ScanLitecoin, needDoge: options.ScanDogecoin);
                        if (wallet == null)
                        {
                            _statistics.IncrementSkipped();
                            return; // Invalid BIP39 checksum — skip
                        }

                        // Also check if the derived primary addresses were already confirmed empty in another section
                        if (_mnemonicTracker.IsAddressOrKeyScannedEmpty(wallet.BitcoinAddress) ||
                            _mnemonicTracker.IsAddressOrKeyScannedEmpty(wallet.EthereumAddress))
                        {
                            _statistics.IncrementSkipped();
                            return;
                        }

                        _statistics.IncrementAttempts();
                        _logger.LogWalletChecked(wallet);
                        
                        // SPEED BOOST: Skip balance check if disabled
                        if (options.CheckBalancesRealTime)
                        {
                            try
                            {
                                var balances = await _balanceChecker.CheckAllBalances(wallet, 
                                    options.ScanBitcoin, options.ScanEthereum, 
                                    options.ScanSolana, options.ScanUSDT,
                                    options.ScanLitecoin, options.ScanDogecoin,
                                    options.ScanPolygon, options.ScanBnb);
                                wallet.Balances = balances;

                                // If standard paths found nothing, try extended derivation paths (DeepScan)
                                if (!balances.Any(b => b.Value > 0.00000001m))
                                {
                                    var deepFound = await CheckExtendedDerivationPaths(wallet, mnemonic, options);
                                    if (deepFound)
                                    {
                                        // Re-check balances dict after deep scan added entries
                                        balances = wallet.Balances;
                                    }
                                }

                                // Only fire WalletFound if ACTUAL balance > minimum threshold
                                if (balances.Any(b => b.Value > 0.00000001m))
                                {
                                    _mnemonicTracker.MarkWalletConfirmedFunded(wallet);
                                    _statistics.IncrementWalletsFound();
                                    _logger.LogWalletFound(wallet);
                                    WalletFound?.Invoke(this, new WalletFoundEventArgs(wallet));
                                }
                                else
                                {
                                    // Permanently record as confirmed empty across all identifiers
                                    _mnemonicTracker.MarkWalletConfirmedEmpty(wallet);
                                }
                            }
                            catch (Exception ex)
                            {
                                DebugLogger.Log($"Balance check error: {ex.Message}");
                            }
                            }

                            // SPEED BOOST: Mark all addresses as checked for deduplication
                            MarkAddressesAsChecked(wallet, options);

                            var currentProcessed = Interlocked.Increment(ref processed);
                            
                            // Thread-safe time-throttled progress updates (20ms = smooth counter)
                            var nowTicks = DateTime.UtcNow.Ticks;
                            var prevProgress = Interlocked.Read(ref lastProgressTicks);
                            if ((nowTicks - prevProgress) > TimeSpan.TicksPerMillisecond * 20 || currentProcessed == totalVariations)
                            {
                                if (Interlocked.CompareExchange(ref lastProgressTicks, nowTicks, prevProgress) == prevProgress)
                                {
                                    var progress = (double)currentProcessed / totalVariations * 100;
                                    ProgressUpdated?.Invoke(this, new ProgressEventArgs(progress, currentProcessed, totalVariations, wallet));
                                }
                            }
                            
                            // Update stats less frequently for performance
                            var prevStats = Interlocked.Read(ref lastStatsTicks);
                            if ((nowTicks - prevStats) > TimeSpan.TicksPerMillisecond * 200 || currentProcessed == totalVariations)
                            {
                                if (Interlocked.CompareExchange(ref lastStatsTicks, nowTicks, prevStats) == prevStats)
                                {
                                    StatisticsUpdated?.Invoke(this, new StatisticsEventArgs(_statistics.GetFormattedStats(totalVariations)));
                                }
                            }
                        }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Error processing mnemonic {mnemonic}", ex);
                    }
                });
        }

        // Batch buffer for random scan — accumulates wallets for batch ETH+BTC checking
        private readonly ConcurrentQueue<(CryptoWalletGenerator.WalletInfo wallet, int checkNumber)> _randomBatchBuffer = new();
        private const int RandomBatchSize = 100; // Larger batches = fewer API calls (BTC batch supports 50, ETH supports 20)
        private int _batchProcessorRunning = 0;
        private long _lastBatchFlushTicks = 0; // For timeout-based batch flushing

        /// <summary>
        /// Process accumulated wallets in batches: ETH via batch API (20 at a time), BTC individually.
        /// Uses 5-20x fewer API calls than checking one wallet at a time.
        /// </summary>
        private async Task ProcessRandomBatchAsync(RecoveryOptions options, CancellationToken token)
        {
            var batch = new List<(CryptoWalletGenerator.WalletInfo wallet, int checkNumber)>();
            
            while (_randomBatchBuffer.TryDequeue(out var item) && batch.Count < RandomBatchSize)
            {
                batch.Add(item);
            }
            
            if (batch.Count == 0) return;

            Interlocked.Exchange(ref _lastBatchFlushTicks, DateTime.UtcNow.Ticks);
            bool useOfflineFilter = AppSettings.Current.EnableOfflineFilter;

            // Collect ETH addresses for batch checking
            var ethAddresses = new List<string>();
            var ethToWallet = new Dictionary<string, (CryptoWalletGenerator.WalletInfo wallet, int checkNumber)>();
            
            foreach (var (wallet, checkNumber) in batch)
            {
                if (!string.IsNullOrEmpty(wallet.EthereumAddress) && options.ScanEthereum)
                {
                    var ethAddr = wallet.EthereumAddress.ToLowerInvariant();
                    if (FundedAddressFilter.ETHEmpty.IsKnownEmpty(ethAddr) || _mnemonicTracker.IsAddressOrKeyScannedEmpty(ethAddr))
                        continue;
                    if (useOfflineFilter && FundedAddressFilter.ETH.IsLoaded && !FundedAddressFilter.ETH.MightBeFunded(ethAddr))
                        continue;
                    ethAddresses.Add(ethAddr);
                    ethToWallet[ethAddr] = (wallet, checkNumber);
                }
            }

            // BATCH CHECK ETH: 20 addresses in 1 API call instead of 20 separate calls
            Dictionary<string, decimal> ethBalances = null;
            if (options.ScanEthereum)
            {
                if (ethAddresses.Count > 0)
                {
                    try
                    {
                        ethBalances = await _balanceChecker.GetEthereumBalancesBatch(ethAddresses);
                        DebugLogger.Log($"[BATCH] ETH batch checked {ethAddresses.Count} addresses in 1 API call");
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Log($"[BATCH] ETH batch error, falling back to individual: {ex.Message}");
                    }
                }
                else
                {
                    ethBalances = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                }
            }

            // BATCH CHECK BTC: collect all address variants (legacy+segwit+native+taproot) and call once
            // 4 address types × 50 wallets = 200 addresses → 4 multiaddr calls vs 200 individual calls
            Dictionary<string, decimal> btcBalances = null;
            if (options.ScanBitcoin)
            {
                var allBtcAddrs = new List<string>();
                foreach (var (w, _) in batch)
                {
                    foreach (var a in new[] { w.BitcoinAddress, w.BitcoinAddressSegWit, w.BitcoinAddressNativeSegWit, w.BitcoinAddressTaproot })
                        if (!string.IsNullOrEmpty(a) && !_mnemonicTracker.IsAddressOrKeyScannedEmpty(a)) allBtcAddrs.Add(a);
                }
                allBtcAddrs = FundedAddressFilter.BTCEmpty.FilterOutKnownEmpty(allBtcAddrs);
                if (useOfflineFilter && FundedAddressFilter.BTC.IsLoaded)
                {
                    allBtcAddrs = FundedAddressFilter.BTC.FilterPotentiallyFunded(allBtcAddrs);
                }
                if (allBtcAddrs.Count > 0)
                {
                    try
                    {
                        btcBalances = await _balanceChecker.GetBitcoinBalancesBatch(allBtcAddrs);
                        DebugLogger.Log($"[BATCH] BTC batch checked {allBtcAddrs.Count} addresses in {(allBtcAddrs.Count + 49) / 50} API calls");
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Log($"[BATCH] BTC batch error, falling back to individual: {ex.Message}");
                    }
                }
                else
                {
                    btcBalances = new Dictionary<string, decimal>();
                }
            }

            // BATCH CHECK SOL, MATIC, BNB
            Dictionary<string, decimal> solBalances = null;
            if (options.ScanSolana)
            {
                var solAddrs = batch.Select(b => b.wallet.SolanaAddress).Where(a => !string.IsNullOrEmpty(a)).Distinct().ToList();
                if (solAddrs.Count > 0)
                {
                    try { solBalances = await _balanceChecker.GetSolanaBalancesBatch(solAddrs); }
                    catch (Exception ex) { DebugLogger.Log($"[BATCH] SOL batch error: {ex.Message}"); }
                }
            }

            Dictionary<string, decimal> maticBalances = null;
            if (options.ScanPolygon)
            {
                var maticAddrs = batch.Select(b => b.wallet.PolygonAddress).Where(a => !string.IsNullOrEmpty(a)).Distinct().ToList();
                if (maticAddrs.Count > 0)
                {
                    try { maticBalances = await _balanceChecker.GetPolygonBalancesBatch(maticAddrs); }
                    catch (Exception ex) { DebugLogger.Log($"[BATCH] MATIC batch error: {ex.Message}"); }
                }
            }

            Dictionary<string, decimal> bnbBalances = null;
            if (options.ScanBnb)
            {
                var bnbAddrs = batch.Select(b => b.wallet.BnbAddress).Where(a => !string.IsNullOrEmpty(a)).Distinct().ToList();
                if (bnbAddrs.Count > 0)
                {
                    try { bnbBalances = await _balanceChecker.GetBnbBalancesBatch(bnbAddrs); }
                    catch (Exception ex) { DebugLogger.Log($"[BATCH] BNB batch error: {ex.Message}"); }
                }
            }

            // Process each wallet: apply batch ETH results + check BTC individually
            // NOTE: Do NOT break on cancellation here - finish the entire dequeued batch
            // to avoid losing wallets with potential balance
            foreach (var (wallet, checkNumber) in batch)
            {
                
                try
                {
                    wallet.Balances ??= new Dictionary<string, decimal>();
                    bool hasBalance = false;

                    // Apply batch ETH result
                    if (ethBalances != null && !string.IsNullOrEmpty(wallet.EthereumAddress))
                    {
                        var ethAddr = wallet.EthereumAddress.ToLowerInvariant();
                        if (ethBalances.TryGetValue(ethAddr, out var ethBal))
                        {
                            wallet.Balances["ETH"] = ethBal;
                            if (ethBal > 0.00000001m) hasBalance = true;
                            else FundedAddressFilter.ETHEmpty.MarkAsEmpty(ethAddr);
                        }
                    }
                    else if (options.ScanEthereum && !string.IsNullOrEmpty(wallet.EthereumAddress))
                    {
                        // Fallback: individual ETH check
                        try
                        {
                            var ethBal = await _balanceChecker.GetEthereumBalance(wallet.EthereumAddress);
                            wallet.Balances["ETH"] = ethBal;
                            if (ethBal > 0.00000001m) hasBalance = true;
                            else FundedAddressFilter.ETHEmpty.MarkAsEmpty(wallet.EthereumAddress.ToLowerInvariant());
                        }
                        catch (Exception ex) { DebugLogger.Log($"[ETH] balance check failed for {wallet.EthereumAddress}: {ex.Message}"); }
                    }

                    // Apply batch BTC results (covers legacy + segwit + native segwit + taproot)
                    if (options.ScanBitcoin && btcBalances != null)
                    {
                        foreach (var a in new[] { wallet.BitcoinAddress, wallet.BitcoinAddressSegWit, wallet.BitcoinAddressNativeSegWit, wallet.BitcoinAddressTaproot })
                        {
                            if (!string.IsNullOrEmpty(a) && btcBalances.TryGetValue(a, out var bal))
                            {
                                if (bal > 0.00000001m)
                                {
                                    wallet.Balances["BTC"] = bal;
                                    hasBalance = true;
                                    break;
                                }
                                else
                                {
                                    FundedAddressFilter.BTCEmpty.MarkAsEmpty(a);
                                }
                            }
                        }
                        if (!wallet.Balances.ContainsKey("BTC") && !string.IsNullOrEmpty(wallet.BitcoinAddress))
                            wallet.Balances["BTC"] = btcBalances.TryGetValue(wallet.BitcoinAddress, out var lb) ? lb : 0m;
                    }
                    else if (options.ScanBitcoin && !string.IsNullOrEmpty(wallet.BitcoinAddress))
                    {
                        // Fallback: individual BTC check when batch failed
                        try
                        {
                            var btcBal = await _balanceChecker.GetBitcoinBalance(wallet.BitcoinAddress);
                            wallet.Balances["BTC"] = btcBal;
                            if (btcBal > 0.00000001m) hasBalance = true;
                            else FundedAddressFilter.BTCEmpty.MarkAsEmpty(wallet.BitcoinAddress);
                        }
                        catch (Exception ex) { DebugLogger.Log($"[BTC] balance check failed for {wallet.BitcoinAddress}: {ex.Message}"); }
                    }

                    // Check SOL (batch result only — never block on sequential per-wallet fallback)
                    if (options.ScanSolana && !string.IsNullOrEmpty(wallet.SolanaAddress))
                    {
                        if (solBalances != null && solBalances.TryGetValue(wallet.SolanaAddress, out var solBal))
                        {
                            wallet.Balances["SOL"] = solBal;
                            if (solBal > 0.00000001m) hasBalance = true;
                        }
                    }

                    // Check USDT (ERC-20) only when ETH batch reported activity or offline filter matched
                    if (options.ScanUSDT && !string.IsNullOrEmpty(wallet.EthereumAddress))
                    {
                        var ethAddrLower = wallet.EthereumAddress.ToLowerInvariant();
                        bool ethHasActivity = ethBalances != null && ethBalances.TryGetValue(ethAddrLower, out var ethVal) && ethVal > 0m;
                        if (ethHasActivity && !FundedAddressFilter.ETHEmpty.IsKnownEmpty(ethAddrLower))
                        {
                            try
                            {
                                var usdtBal = await _balanceChecker.GetUSDTBalance(wallet.EthereumAddress);
                                wallet.Balances["USDT"] = usdtBal;
                                if (usdtBal > 0.01m) hasBalance = true;
                            }
                            catch (Exception ex) { DebugLogger.Log($"[USDT] balance check failed for {wallet.EthereumAddress}: {ex.Message}"); }
                        }
                    }

                    // Check MATIC (batch result only)
                    if (options.ScanPolygon && !string.IsNullOrEmpty(wallet.PolygonAddress))
                    {
                        if (maticBalances != null && maticBalances.TryGetValue(wallet.PolygonAddress, out var maticBal))
                        {
                            wallet.Balances["MATIC"] = maticBal;
                            if (maticBal > 0.00000001m) hasBalance = true;
                        }
                    }

                    // Check BNB (batch result only)
                    if (options.ScanBnb && !string.IsNullOrEmpty(wallet.BnbAddress))
                    {
                        if (bnbBalances != null && bnbBalances.TryGetValue(wallet.BnbAddress, out var bnbBal))
                        {
                            wallet.Balances["BNB"] = bnbBal;
                            if (bnbBal > 0.00000001m) hasBalance = true;
                        }
                    }

                    // Update live preview with actual balances
                    WalletChecking?.Invoke(this, new WalletCheckingEventArgs
                    {
                        Source = "Random Mnemonic (Batch)",
                        Mnemonic = wallet.Mnemonic,
                        Address = wallet.BitcoinAddress,
                        EthAddress = wallet.EthereumAddress,
                        CheckNumber = checkNumber,
                        TotalToCheck = options.MaxAttempts,
                        BtcBalance = wallet.Balances.ContainsKey("BTC") ? wallet.Balances["BTC"] : 0,
                        EthBalance = wallet.Balances.ContainsKey("ETH") ? wallet.Balances["ETH"] : 0
                    });

                    bool everHeld = BalanceChecker.EvaluateWalletEverHeldHistory(wallet);

                    // FOUND WALLET WITH BALANCE!
                    if (hasBalance)
                    {
                        _mnemonicTracker.MarkWalletConfirmedFunded(wallet);
                        CognitiveBrainEngine.Instance.RecordScanFeedback(
                            wallet.Mnemonic,
                            wallet.Source ?? "DeepEntropyCsprng",
                            passedOfflineFilter: true,
                            hasTransactionsOrBalance: true,
                            balanceUsd: 100m);
                        DebugLogger.Log($"[BATCH] 🔥🔥🔥 WALLET FOUND! #{checkNumber} Mnemonic: {wallet.Mnemonic}");
                        _statistics.IncrementWalletsFound();
                        _logger.LogWalletFound(wallet, mnemonicOnly: true);
                        WalletFound?.Invoke(this, new WalletFoundEventArgs(wallet));
                    }
                    else if (everHeld)
                    {
                        CognitiveBrainEngine.Instance.RecordScanFeedback(
                            wallet.Mnemonic,
                            wallet.Source ?? "DeepEntropyCsprng",
                            passedOfflineFilter: true,
                            hasTransactionsOrBalance: true,
                            balanceUsd: 0m);
                        _logger.LogWalletFound(wallet, mnemonicOnly: false);
                        WalletFound?.Invoke(this, new WalletFoundEventArgs(wallet));
                    }
                    else
                    {
                        // STRICT NON-REPETITION RULE: Permanently record confirmed-empty wallet across all sections
                        _mnemonicTracker.MarkWalletConfirmedEmpty(wallet);
                        CognitiveBrainEngine.Instance.RecordScanFeedback(
                            wallet.Mnemonic,
                            wallet.Source ?? "DeepEntropyCsprng",
                            passedOfflineFilter: false,
                            hasTransactionsOrBalance: false);
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"[BATCH] Wallet #{checkNumber} error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// HASHCAT PIPELINE: High-speed mnemonic generation using optimized PBKDF2 batch processing.
        /// Bypasses NBitcoin.Mnemonic overhead and goes directly: entropy → PBKDF2 → master key → addresses.
        /// Targets 10-50x faster throughput than standard RecoverFromRandomGeneration.
        /// </summary>
        private async Task RecoverWithHashcatPipeline(RecoveryOptions options, CancellationToken token)
        {
            var settings = AppSettings.Current;
            int processed = 0;
            long lastStatsTicks = 0;
            long lastUITicks = 0;

            StatisticsUpdated?.Invoke(this, new StatisticsEventArgs(
                $"⚡ HASHCAT Pipeline ({settings.ThreadCount} threads, batch={settings.HashcatBatchSize})..."));

            // Initialize bloom filters
            // Works in both normal and StrictScanMode — bloom filter is always a pre-filter
            if (settings.EnableOfflineFilter)
            {
                try
                {
                    FundedAddressFilter.InitializeBTC();
                    FundedAddressFilter.InitializeETH();
                    FundedAddressFilter.InitializeBTCEmpty();
                    FundedAddressFilter.InitializeETHEmpty();
                    DebugLogger.Log($"[HASHCAT] Bloom filters loaded: BTC={FundedAddressFilter.BTC.AddressCount:N0}, ETH={FundedAddressFilter.ETH.AddressCount:N0}");
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"[HASHCAT] Bloom filter init failed: {ex.Message}");
                }
            }

            using var hashcat = new HashcatAccelerator(
                batchSize: settings.HashcatBatchSize,
                threadCount: settings.ThreadCount);

            CryptoWalletGenerator.WalletInfo lastSampleWallet = null;
            long lastPreviewTicks = 0;

            // Callback: wallet passed bloom filter — needs API verification
            void OnWalletPassedBloom(CryptoWalletGenerator.WalletInfo wallet)
            {
                if (token.IsCancellationRequested) return;

                // STRICT NON-REPETITION RULE: Skip if already scanned in any section (persistent deduplication)
                if (!_mnemonicTracker.TryMarkWalletCandidate(wallet))
                {
                    _statistics.IncrementSkipped();
                    return;
                }

                var checkNumber = Interlocked.Increment(ref processed);
                _statistics.IncrementAttempts();
                Volatile.Write(ref lastSampleWallet, wallet);

                // Update Live Preview with actual mnemonic + BTC/ETH addresses
                var nowPreviewTicks = DateTime.UtcNow.Ticks;
                var prevPreview = Interlocked.Read(ref lastPreviewTicks);
                if (checkNumber <= 5 || (nowPreviewTicks - prevPreview) > TimeSpan.TicksPerMillisecond * 80)
                {
                    if (checkNumber <= 5 || Interlocked.CompareExchange(ref lastPreviewTicks, nowPreviewTicks, prevPreview) == prevPreview)
                    {
                        WalletChecking?.Invoke(this, new WalletCheckingEventArgs
                        {
                            Source = "Hashcat Pipeline",
                            Mnemonic = wallet.Mnemonic,
                            Address = wallet.BitcoinAddress,
                            EthAddress = wallet.EthereumAddress,
                            CheckNumber = checkNumber,
                            TotalToCheck = options.MaxAttempts
                        });
                    }
                }

                // Enqueue for batch API checking without blocking ThreadPool worker threads
                if (options.CheckBalancesRealTime)
                {
                    while (_randomBatchBuffer.Count > RandomBatchSize * 4 && !token.IsCancellationRequested)
                    {
                        Thread.Sleep(5);
                    }

                    _randomBatchBuffer.Enqueue((wallet, checkNumber));

                    var nowFlushTicks = DateTime.UtcNow.Ticks;
                    bool bloomFilterActive = settings.EnableOfflineFilter &&
                        (FundedAddressFilter.BTC.IsLoaded || FundedAddressFilter.ETH.IsLoaded);
                    bool shouldFlush = _randomBatchBuffer.Count >= RandomBatchSize ||
                        bloomFilterActive ||
                        (nowFlushTicks - Interlocked.Read(ref _lastBatchFlushTicks) > TimeSpan.TicksPerSecond * 1);

                    if (shouldFlush &&
                        Interlocked.CompareExchange(ref _batchProcessorRunning, 1, 0) == 0)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                while (_randomBatchBuffer.Count >= RandomBatchSize && !token.IsCancellationRequested)
                                {
                                    await ProcessRandomBatchAsync(options, token).ConfigureAwait(false);
                                }
                                if (!_randomBatchBuffer.IsEmpty && !token.IsCancellationRequested)
                                {
                                    await ProcessRandomBatchAsync(options, token).ConfigureAwait(false);
                                }
                            }
                            catch (Exception ex)
                            {
                                DebugLogger.Log($"[HASHCAT] Batch flush error: {ex.Message}");
                            }
                            finally
                            {
                                Interlocked.Exchange(ref _batchProcessorRunning, 0);
                            }
                        });
                    }
                }
            }

            // Callback: progress update
            void OnProgress(long totalProcessed, double walletsPerSec)
            {
                if (token.IsCancellationRequested) return;

                var sample = Volatile.Read(ref lastSampleWallet);
                if (sample != null)
                {
                    WalletChecking?.Invoke(this, new WalletCheckingEventArgs
                    {
                        Source = "Hashcat Pipeline",
                        Mnemonic = sample.Mnemonic,
                        Address = sample.BitcoinAddress,
                        EthAddress = sample.EthereumAddress,
                        CheckNumber = (int)Math.Min(totalProcessed, int.MaxValue),
                        TotalToCheck = options.MaxAttempts
                    });
                }

                var nowTicks = DateTime.UtcNow.Ticks;

                // UI update (throttled to ~20fps)
                var prevUI = Interlocked.Read(ref lastUITicks);
                if ((nowTicks - prevUI) > TimeSpan.TicksPerMillisecond * 50)
                {
                    if (Interlocked.CompareExchange(ref lastUITicks, nowTicks, prevUI) == prevUI)
                    {
                        var stats = hashcat.GetStats();
                        var progress = options.MaxAttempts > 0
                            ? (double)totalProcessed / options.MaxAttempts * 100 : 0;

                        ProgressUpdated?.Invoke(this, new ProgressEventArgs(
                            progress, (int)totalProcessed, options.MaxAttempts, null,
                            $"⚡ Hashcat: {walletsPerSec:N0}/sec"));
                    }
                }

                // Stats update (every 200ms)
                var prevStats = Interlocked.Read(ref lastStatsTicks);
                if ((nowTicks - prevStats) > TimeSpan.TicksPerMillisecond * 200)
                {
                    if (Interlocked.CompareExchange(ref lastStatsTicks, nowTicks, prevStats) == prevStats)
                    {
                        StatisticsUpdated?.Invoke(this, new StatisticsEventArgs(
                            $"⚡ HASHCAT | {hashcat.GetFormattedStats()} | Found: {_statistics.WalletsFound}"));
                    }
                }
            }

            // Run continuous pipeline until cancelled
            DebugLogger.Log($"[HASHCAT] Starting continuous pipeline: threads={settings.ThreadCount}, batch={settings.HashcatBatchSize}");

            bool isUnlimited = options.MaxAttempts <= 0 || options.MaxAttempts == int.MaxValue;
            bool needEth = NeedEthAddress(options);
            if (isUnlimited)
            {
                // Use continuous mode: each worker thread loops forever until cancellation
                await hashcat.RunContinuousAsync(
                    options.ScanBitcoin,
                    needEth,
                    OnWalletPassedBloom,
                    OnProgress,
                    token,
                    options.ScanLitecoin,
                    options.ScanDogecoin);
            }
            else
            {
                await hashcat.RunPipelineAsync(
                    options.MaxAttempts,
                    options.ScanBitcoin,
                    needEth,
                    OnWalletPassedBloom,
                    OnProgress,
                    token,
                    options.ScanLitecoin,
                    options.ScanDogecoin);

                DebugLogger.Log($"[HASHCAT] Batch completed ({options.MaxAttempts:N0}).");
            }

            // Flush remaining batch buffer (always flush, even on cancel - don't lose potential finds)
            while (!_randomBatchBuffer.IsEmpty)
            {
                await ProcessRandomBatchAsync(options, CancellationToken.None);
            }
        }

        private async Task RecoverFromRandomGeneration(RecoveryOptions options, CancellationToken token)
        {
            var settings = AppSettings.Current;
            int threadCount = CalculateEffectiveThreadCount(options, settings.ThreadCount);
            double speedMult = CalculateSpeedMultiplier(options);
            int processed = 0;
            var lastUIUpdate = DateTime.Now;
            long lastProgressTicks = 0;
            long lastStatsTicks = 0;
            long lastUITicks = 0;
            CryptoWalletGenerator.WalletInfo lastWallet = null;
            
            // Fire initial event
            DebugLogger.Log($"[ENGINE] RecoverFromRandomGeneration started: threads={threadCount} ({speedMult:F1}x boost), maxAttempts={options.MaxAttempts}, checkBalances={options.CheckBalancesRealTime}");
            StatisticsUpdated?.Invoke(this, new StatisticsEventArgs(
                speedMult > 1.1 ? $"🚀 Random Mnemonic Generation ({threadCount} threads, {speedMult:F1}x speed boost!)..."
                                : $"🚀 Random Mnemonic Generation ({threadCount} threads)..."));

            // Initialize bloom filters for pre-check (skip API calls for definitely-not-funded addresses)
            // Works in both normal and StrictScanMode — bloom filter is always a pre-filter
            if (settings.EnableOfflineFilter)
            {
                try
                {
                    FundedAddressFilter.InitializeBTC();
                    FundedAddressFilter.InitializeETH();
                    FundedAddressFilter.InitializeBTCEmpty();
                    FundedAddressFilter.InitializeETHEmpty();
                    DebugLogger.Log($"[ENGINE] Bloom filters loaded: BTC={FundedAddressFilter.BTC.AddressCount:N0} funded, ETH={FundedAddressFilter.ETH.AddressCount:N0} funded");
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"[ENGINE] Bloom filter init failed (will check all via API): {ex.Message}");
                }
            }

            bool isUnlimited = options.MaxAttempts <= 0 || options.MaxAttempts == int.MaxValue;
            int totalTarget = isUnlimited ? int.MaxValue : options.MaxAttempts;

            // Scan in batches until MaxAttempts is reached or user clicks Stop
            while (!token.IsCancellationRequested && (isUnlimited || processed < totalTarget))
            {
            int remaining = isUnlimited ? 100_000 : Math.Max(1, totalTarget - processed);
            int currentBatchCount = Math.Min(remaining, 100_000);
            await Parallel.ForEachAsync(Enumerable.Range(0, currentBatchCount),
                new ParallelOptions { MaxDegreeOfParallelism = threadCount, CancellationToken = token },
                async (i, ct) =>
                {
                    if (ct.IsCancellationRequested) return;

                        try
                        {
                            // 1. GENERATE CANDIDATE (Interleave Cognitive Brain Engine self-learning synthesis with CSPRNG)
                            bool useCognitiveBrain = (i % 2 == 0);
                            string candidateMnemonic = null;
                            string strategyArm = "DeepEntropyCsprng";

                            if (useCognitiveBrain)
                            {
                                var brainCand = CognitiveBrainEngine.Instance.SynthesizeNextCandidate(markAsScannedImmediately: false);
                                candidateMnemonic = brainCand.Mnemonic;
                                strategyArm = brainCand.StrategyArm;
                            }

                            var wallet = CryptoWalletGenerator.GenerateWalletSelective(candidateMnemonic,
                                needEth: NeedEthAddress(options), needSol: options.ScanSolana,
                                needLtc: options.ScanLitecoin, needDoge: options.ScanDogecoin);
                            if (wallet == null)
                            {
                                _statistics.IncrementSkipped();
                                return;
                            }
                            wallet.Source = useCognitiveBrain
                                ? $"Cognitive Brain [{strategyArm}]"
                                : "Random Mnemonic";
                            
                            // STRICT NON-REPETITION RULE: Check if this wallet (mnemonic, key, or address) was scanned before in any section
                            if (!_mnemonicTracker.TryMarkWalletCandidate(wallet))
                            {
                                _statistics.IncrementSkipped();
                                return; // Skip - already scanned in a previous session or another section
                            }
                            
                            _statistics.IncrementAttempts();
                            
                            var checkNumber = Interlocked.Increment(ref processed);
                            lastWallet = wallet;
                            
                            // 2. UPDATE UI every 50ms to prevent flooding (thread-safe)
                            var uiNowTicks = DateTime.UtcNow.Ticks;
                            var prevUI = Interlocked.Read(ref lastUITicks);
                            if ((uiNowTicks - prevUI) > TimeSpan.TicksPerMillisecond * 50)
                            {
                                if (Interlocked.CompareExchange(ref lastUITicks, uiNowTicks, prevUI) == prevUI)
                                {
                                    WalletChecking?.Invoke(this, new WalletCheckingEventArgs
                                    {
                                        Source = "Random Mnemonic",
                                        Mnemonic = wallet.Mnemonic,
                                        Address = wallet.BitcoinAddress,
                                        EthAddress = wallet.EthereumAddress,
                                        CheckNumber = checkNumber,
                                        TotalToCheck = options.MaxAttempts,
                                        BtcBalance = 0,
                                        EthBalance = 0
                                    });
                                }
                            }
                            
                            // 3. UPDATE PROGRESS - thread-safe time-throttled (20ms = ~50 updates/sec for smooth counting)
                            var nowTicks = DateTime.UtcNow.Ticks;
                            var prevProgress = Interlocked.Read(ref lastProgressTicks);
                            if ((nowTicks - prevProgress) > TimeSpan.TicksPerMillisecond * 20)
                            {
                                if (Interlocked.CompareExchange(ref lastProgressTicks, nowTicks, prevProgress) == prevProgress)
                                {
                                    var progress = (double)checkNumber / options.MaxAttempts * 100;
                                    DebugLogger.Log($"[ENGINE] ProgressUpdated fired: checked={checkNumber}, progress={progress:F1}%, statsTotal={_statistics.TotalChecked}");
                                    DebugLogger.Log($"[ENGINE] ProgressUpdated fired: checked={checkNumber}, progress={progress:F1}%, statsTotal={_statistics.TotalChecked}");
                                    ProgressUpdated?.Invoke(this, new ProgressEventArgs(progress, checkNumber, options.MaxAttempts, wallet));
                                }
                            }
                            var prevStats = Interlocked.Read(ref lastStatsTicks);
                            if ((nowTicks - prevStats) > TimeSpan.TicksPerMillisecond * 200)
                            {
                                if (Interlocked.CompareExchange(ref lastStatsTicks, nowTicks, prevStats) == prevStats)
                                {
                                    StatisticsUpdated?.Invoke(this, new StatisticsEventArgs(_statistics.GetFormattedStats(options.MaxAttempts)));
                                }
                            }
                            
                            // 4. CHECK BALANCE (batch mode for efficiency!)
                            if (options.CheckBalancesRealTime)
                            {
                                try
                                {
                                    // BLOOM FILTER PRE-CHECK: Skip API call if address is definitely NOT funded
                                    // Works in StrictScanMode too — bloom is a pre-filter, StrictScanMode only affects verification
                                    bool skipApiCheck = false;
                                    bool hasNonFilterableChain = options.ScanSolana || options.ScanLitecoin || options.ScanDogecoin;
                                    if (AppSettings.Current.EnableOfflineFilter && !hasNonFilterableChain &&
                                        (FundedAddressFilter.BTC.IsLoaded || FundedAddressFilter.ETH.IsLoaded))
                                    {
                                        bool needBtc = options.ScanBitcoin;
                                        bool needEth = NeedEthAddress(options);

                                        bool mightBeFundedBtc = needBtc && (!FundedAddressFilter.BTC.IsLoaded ||
                                            FundedAddressFilter.BTC.MightBeFunded(wallet.BitcoinAddress) ||
                                            (!string.IsNullOrEmpty(wallet.BitcoinAddressNativeSegWit) && FundedAddressFilter.BTC.MightBeFunded(wallet.BitcoinAddressNativeSegWit)) ||
                                            (!string.IsNullOrEmpty(wallet.BitcoinAddressSegWit) && FundedAddressFilter.BTC.MightBeFunded(wallet.BitcoinAddressSegWit)) ||
                                            (!string.IsNullOrEmpty(wallet.BitcoinAddressTaproot) && FundedAddressFilter.BTC.MightBeFunded(wallet.BitcoinAddressTaproot)));
                                        bool mightBeFundedEth = needEth && (!FundedAddressFilter.ETH.IsLoaded ||
                                            (!string.IsNullOrEmpty(wallet.EthereumAddress) && FundedAddressFilter.ETH.MightBeFunded(wallet.EthereumAddress)));
                                        
                                        if (!mightBeFundedBtc && !mightBeFundedEth)
                                        {
                                            skipApiCheck = true;
                                            _statistics.IncrementSkipped();
                                            _mnemonicTracker.MarkWalletConfirmedEmpty(wallet);
                                            CognitiveBrainEngine.Instance.RecordScanFeedback(
                                                wallet.Mnemonic,
                                                strategyArm,
                                                passedOfflineFilter: false,
                                                hasTransactionsOrBalance: false);
                                        }
                                    }
                                    
                                    if (!skipApiCheck)
                                    {
                                        // Add to batch buffer for efficient batch API checking
                                        _randomBatchBuffer.Enqueue((wallet, checkNumber));
                                        
                                        var nowFlushTicks = DateTime.UtcNow.Ticks;
                                        bool bloomFilterActive = AppSettings.Current.EnableOfflineFilter &&
                                            (FundedAddressFilter.BTC.IsLoaded || FundedAddressFilter.ETH.IsLoaded);
                                        bool shouldFlush = _randomBatchBuffer.Count >= RandomBatchSize ||
                                            bloomFilterActive ||
                                            (nowFlushTicks - Interlocked.Read(ref _lastBatchFlushTicks) > TimeSpan.TicksPerSecond * 2);

                                        // When buffer reaches batch size or timeout, flush it
                                        if (shouldFlush && 
                                            Interlocked.CompareExchange(ref _batchProcessorRunning, 1, 0) == 0)
                                        {
                                            try
                                            {
                                                await ProcessRandomBatchAsync(options, ct);
                                            }
                                            finally
                                            {
                                                Interlocked.Exchange(ref _batchProcessorRunning, 0);
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    DebugLogger.Log($"[SCAN] #{checkNumber} Balance check ERROR: {ex.Message}");
                                }
                            }
                            else
                            {
                                // LOG: balance checking disabled
                                if (checkNumber <= 3)
                                    DebugLogger.Log($"[SCAN] #{checkNumber} ⚠️ Balance checking DISABLED (CheckBalancesRealTime=false) - wallets generated but NOT checked!");
                            }
                        }
                        catch (Exception ex)
                        {
                            DebugLogger.Log($"Wallet generation error: {ex.Message}");
                        }
                    });

            // Final flush of remaining wallets in batch buffer (use CancellationToken.None to ensure no wallets are lost)
            while (!_randomBatchBuffer.IsEmpty)
            {
                await ProcessRandomBatchAsync(options, CancellationToken.None);
            }
            
            if (!isUnlimited && processed >= totalTarget)
            {
                DebugLogger.Log($"[ENGINE] Random scan completed ({processed:N0}/{totalTarget:N0} attempts).");
                break;
            }
            DebugLogger.Log($"[ENGINE] Continuous scan batch completed ({processed:N0} attempts). Restarting...");
            } // end continuous while loop
        }

        private async Task RecoverFromPrivateKeyBruteForce(RecoveryOptions options, CancellationToken token)
        {
            int processed = 0;
            var settings = AppSettings.Current;
            int threadCount = CalculateEffectiveThreadCount(options, settings.ThreadCount);
            DebugLogger.Log($"[ENGINE] PrivateKeyBruteForce threads: {threadCount} (base={settings.ThreadCount}, multiplier={CalculateSpeedMultiplier(options):F1}x)");
            long lastProgressTicks = 0;
            long lastStatsTicks = 0;

            // Async-safe parallel processing
            await Parallel.ForEachAsync(Enumerable.Range(0, options.MaxAttempts),
                new ParallelOptions { MaxDegreeOfParallelism = threadCount, CancellationToken = token },
                async (i, ct) =>
                {
                    if (ct.IsCancellationRequested) return;

                    try
                    {
                        // Thread-safe cryptographically secure random generation
                        var keyBytes = new byte[32];
                        System.Security.Cryptography.RandomNumberGenerator.Fill(keyBytes);
                        var privateKey = Convert.ToHexString(keyBytes).ToLower();

                        // STRICT NON-REPETITION RULE: Never repeat a private key already scanned as empty across any section
                        if (!_mnemonicTracker.TryMarkAsScanned("pk:" + privateKey))
                        {
                            _statistics.IncrementSkipped();
                            return;
                        }

                        var wallet = CryptoWalletGenerator.GenerateFromPrivateKey(privateKey, options.CryptoType);
                        if (_mnemonicTracker.IsAddressOrKeyScannedEmpty(wallet.BitcoinAddress) ||
                            _mnemonicTracker.IsAddressOrKeyScannedEmpty(wallet.EthereumAddress))
                        {
                            _statistics.IncrementSkipped();
                            return;
                        }

                        _statistics.IncrementAttempts();
                        _logger.LogWalletChecked(wallet);
                        
                        // SPEED BOOST: Skip balance check if disabled
                        if (options.CheckBalancesRealTime)
                        {
                            // SPEED BOOST: Skip if all addresses already checked (deduplication)
                            if (AreAllAddressesChecked(wallet, options))
                            {
                                _statistics.IncrementSkipped();
                                return; // All addresses already scanned - skip balance check
                            }

                            try
                            {
                                var balances = await _balanceChecker.CheckAllBalances(wallet, 
                                    options.ScanBitcoin, options.ScanEthereum, 
                                    options.ScanSolana, options.ScanUSDT,
                                    options.ScanLitecoin, options.ScanDogecoin,
                                    options.ScanPolygon, options.ScanBnb);
                                wallet.Balances = balances;

                                // Only fire WalletFound if ACTUAL balance > minimum threshold
                                if (balances.Any(b => b.Value > 0.00000001m))
                                {
                                    _mnemonicTracker.MarkWalletConfirmedFunded(wallet, "pk:" + privateKey);
                                    _statistics.IncrementWalletsFound();
                                    _logger.LogWalletFound(wallet);
                                    WalletFound?.Invoke(this, new WalletFoundEventArgs(wallet));
                                }
                                else
                                {
                                    _mnemonicTracker.MarkWalletConfirmedEmpty(wallet, "pk:" + privateKey);
                                }
                            }
                            catch (Exception ex)
                            {
                                DebugLogger.Log($"Balance check error: {ex.Message}");
                            }

                            // SPEED BOOST: Mark all addresses as checked for deduplication
                            MarkAddressesAsChecked(wallet, options);
                        }

                            var currentProcessed = Interlocked.Increment(ref processed);
                            
                            // Thread-safe time-throttled progress updates (20ms = smooth counter)
                            var nowTicks = DateTime.UtcNow.Ticks;
                            var prevProgress = Interlocked.Read(ref lastProgressTicks);
                            if ((nowTicks - prevProgress) > TimeSpan.TicksPerMillisecond * 20 || currentProcessed == options.MaxAttempts)
                            {
                                if (Interlocked.CompareExchange(ref lastProgressTicks, nowTicks, prevProgress) == prevProgress)
                                {
                                    var progress = (double)currentProcessed / options.MaxAttempts * 100;
                                    ProgressUpdated?.Invoke(this, new ProgressEventArgs(progress, currentProcessed, options.MaxAttempts, wallet));
                                }
                            }
                            
                            // Update stats less frequently for performance
                            var prevStats = Interlocked.Read(ref lastStatsTicks);
                            if ((nowTicks - prevStats) > TimeSpan.TicksPerMillisecond * 200 || currentProcessed == options.MaxAttempts)
                            {
                                if (Interlocked.CompareExchange(ref lastStatsTicks, nowTicks, prevStats) == prevStats)
                                {
                                    StatisticsUpdated?.Invoke(this, new StatisticsEventArgs(_statistics.GetFormattedStats(options.MaxAttempts)));
                                }
                            }
                        }
                    catch (Exception ex)
                    {
                        _logger.LogError("Error generating private key wallet", ex);
                    }
                });
        }

        /// <summary>
        /// SMART HUNTING: Uses intelligent patterns to find wallets 10,000x more effectively
        /// Instead of pure random, targets:
        /// - Weak RNG wallets
        /// - Brain wallets
        /// - Human-created patterns
        /// - Vulnerable wallet software
        /// - Timestamp-based seeds
        /// </summary>
        private async Task RecoverWithSmartHunting(RecoveryOptions options, CancellationToken token)
        {
            var settings = AppSettings.Current;
            using var hunter = new SmartWalletHunter();
            _activeHunter = hunter; // Expose for live counter polling
            
            // Pass crypto scanning options from RecoveryOptions
            hunter.ScanBitcoin = options.ScanBitcoin;
            hunter.ScanEthereum = options.ScanEthereum;
            hunter.ScanSolana = options.ScanSolana;
            hunter.ScanUSDT = options.ScanUSDT;
            
            // Pass extended crypto options (altcoins)
            hunter.ScanLitecoin = options.ScanLitecoin;
            hunter.ScanDogecoin = options.ScanDogecoin;
            hunter.ScanPolygon = options.ScanPolygon;
            hunter.ScanBnb = options.ScanBnb;
            
            // Subscribe to hunter events
            hunter.WalletFound += (s, e) =>
            {
                _statistics.IncrementWalletsFound();
                WalletFound?.Invoke(this, e);
            };
            
            hunter.ProgressUpdated += (s, e) =>
            {
                var progress = (double)e.TotalChecked / settings.SmartHuntingMaxAttempts * 100;
                DebugLogger.Log($"[SMART] ProgressUpdated: checked={e.TotalChecked}, speed={e.ChecksPerSecond:N0}/s, dedupeDrop={e.DroppedBySystemDedupe:N0}, phase={e.CurrentPhase}");

                // Sync engine statistics with hunter progress
                _statistics.SetTotalChecked(e.TotalChecked);

                // Fire both statistics and progress events
                StatisticsUpdated?.Invoke(this, new StatisticsEventArgs(
                    $"Smart Hunting | Checked: {e.TotalChecked:N0} | Found: {e.WalletsFound} | DedupeDrop: {e.DroppedBySystemDedupe:N0} | Speed: {e.ChecksPerSecond:N0}/sec | Time: {e.ElapsedTime:hh\\:mm\\:ss}"));
                
                // Fire progress event for UI updates
                ProgressUpdated?.Invoke(this, new ProgressEventArgs(
                    progress, 
                    (int)e.TotalChecked, 
                    settings.SmartHuntingMaxAttempts, 
                    null, 
                    e.CurrentPhase ?? "Scanning...",
                    e.DroppedBySystemDedupe,
                    e.PhaseStats,
                    e.Rolling30sRate));
            };
            
            // Forward wallet checking events to show live preview
            hunter.WalletChecking += (s, e) =>
            {
                WalletChecking?.Invoke(this, e);
            };

            // Load saved progress from previous runs
            ScanProgress.Load();
            
            DebugLogger.Log("🎯 Starting Smart Wallet Hunting (10,000x better odds than random!)");
            DebugLogger.Log("   This targets weak RNG, brain wallets, and human patterns...");
            DebugLogger.Log("   ♻️ Previously scanned items will be auto-skipped (session memory active)");
            DebugLogger.Log("   ⚡ Running all enabled phases in PARALLEL for maximum speed!\n");

            int round = 0;
            while (!token.IsCancellationRequested)
            {
                round++;
                // Run all enabled phases in parallel on thread pool threads.
                // CRITICAL: Task.Run ensures phases don't capture the WPF SynchronizationContext,
                // preventing async continuations from flooding the UI thread.
                var phases = new List<Task>();

                // Phase 0: Autonomous Cognitive Self-Learning Brain Engine (runs when Human Patterns / Cognitive Brain is enabled)
                if (settings.ScanHumanPatterns)
                {
                    DebugLogger.Log($"🧠 Phase 0 (Round {round}): Cognitive Self-Learning Brain Engine {CognitiveBrainEngine.Instance.VersionTag} (IQ {CognitiveBrainEngine.Instance.CognitiveIqScore:F1})...");
                    phases.Add(Task.Run(() => hunter.ScanCognitiveBrainEngine(token, Math.Max(25000, settings.SmartHuntingMaxAttempts / 4)), token));
                }

                if (settings.ScanBrainWallets)
                {
                    DebugLogger.Log($"📖 Phase 1 (Round {round}): Scanning Brain Wallets...");
                    phases.Add(Task.Run(() => hunter.ScanBrainWalletsEnhanced(token), token));
                }

                if (settings.ScanWeakRngWallets)
                {
                    DebugLogger.Log($"🎲 Phase 2 (Round {round}): Exploiting Weak RNG Wallets...");
                    phases.Add(Task.Run(() => hunter.ScanWeakRngWallets(token, settings.SmartHuntingMaxAttempts / 4), token));
                }

                if (settings.ScanTimestampSeeds && !settings.ScanWeakRngWallets)
                {
                    DebugLogger.Log($"⏰ Phase (Round {round}): Scanning Timestamp-Based Seeds (2009-2021)...");
                    phases.Add(Task.Run(() => hunter.ScanTimestampSeeds(token, settings.SmartHuntingMaxAttempts / 4), token));
                }

                if (settings.ScanHumanPatterns)
                {
                    DebugLogger.Log($"🧠 Phase 3 (Round {round}): Scanning Human-Created Patterns...");
                    phases.Add(Task.Run(() => hunter.ScanHumanPatterns(token), token));
                }

                if (settings.ScanVulnerablePatterns)
                {
                    DebugLogger.Log($"⚠️ Phase 4 (Round {round}): Targeting Vulnerable Wallet Software...");
                    phases.Add(Task.Run(() => hunter.ScanVulnerableWalletPatterns(token), token));
                    
                    // Phase 5: Compromised seed patterns (Android SecureRandom, BitcoinJS, degenerate entropy)
                    DebugLogger.Log($"🔓 Phase 5 (Round {round}): Scanning Compromised Seed Patterns...");
                    phases.Add(Task.Run(() => hunter.ScanCompromisedSeedPatterns(token), token));
                }

                if (settings.ScanDerivationPaths)
                {
                    DebugLogger.Log($"🔀 Phase 6 (Round {round}): Derivation Path Exhaustion...");
                    phases.Add(Task.Run(() => hunter.ScanDerivationPathExhaustion(token, settings.SmartHuntingMaxAttempts / 6), token));
                }

                if (settings.ScanDictionaryBrainWallets)
                {
                    DebugLogger.Log($"📚 Phase 7 (Round {round}): Dictionary Brain Wallet Attack...");
                    phases.Add(Task.Run(() => hunter.ScanDictionaryBrainWallets(token), token));
                }

                if (settings.ScanKnownLeakedKeys && round == 1)
                {
                    DebugLogger.Log("🔑 Phase 8: Known Leaked / Compromised Keys...");
                    phases.Add(Task.Run(() => hunter.ScanKnownLeakedKeys(token), token));

                    if (settings.ScanExchangeHackKeys)
                    {
                        DebugLogger.Log("🔑 Phase 11: Exchange Hack Keys (HIGH PRIORITY)...");
                        phases.Add(Task.Run(() => hunter.ScanExchangeHackKeys(token), token));
                    }
                }

                if (settings.ScanRepeatedWordMnemonics)
                {
                    DebugLogger.Log($"🔁 Phase 9 (Round {round}): Repeated Word Mnemonics...");
                    phases.Add(Task.Run(() => hunter.ScanRepeatedWordMnemonics(token), token));
                }

                if (settings.ScanIncrementalMnemonics)
                {
                    DebugLogger.Log($"🔢 Phase 10 (Round {round}): Incremental Mnemonic Scan...");
                    phases.Add(Task.Run(() => hunter.ScanIncrementalMnemonics(token, settings.SmartHuntingMaxAttempts / 6), token));
                }

                bool anyVectorConfigured = settings.ScanHumanPatterns || settings.ScanBrainWallets || settings.ScanWeakRngWallets ||
                                           settings.ScanTimestampSeeds || settings.ScanVulnerablePatterns || settings.ScanDerivationPaths ||
                                           settings.ScanDictionaryBrainWallets || settings.ScanKnownLeakedKeys || settings.ScanRepeatedWordMnemonics ||
                                           settings.ScanIncrementalMnemonics;
                if (!anyVectorConfigured)
                    break;

                // Always keep Autonomous Cognitive Self-Learning Brain Engine running across rounds so scanning never stalls or exits early
                if (!settings.ScanHumanPatterns || phases.Count == 0 || round > 1)
                {
                    phases.Add(Task.Run(() => hunter.ScanCognitiveBrainEngine(token, Math.Max(30000, settings.SmartHuntingMaxAttempts / 3)), token));
                }

                try
                {
                    await Task.WhenAll(phases).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    DebugLogger.Log("Smart Hunting phases cancelled by user.");
                    break;
                }
                catch (AggregateException ae)
                {
                    foreach (var inner in ae.Flatten().InnerExceptions)
                    {
                        if (inner is not OperationCanceledException)
                            DebugLogger.Log($"⚠️ Phase error (non-fatal): {inner.GetType().Name} - {inner.Message}");
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"⚠️ Phase error (non-fatal): {ex.GetType().Name} - {ex.Message}");
                }

                ScanProgress.Flush();
            }

            // Flush remaining batch queue items before finishing
            await hunter.FlushBatchQueue();
            _activeHunter = null;

            // Flush progress to disk so next run resumes where we left off
            ScanProgress.Flush();
            DebugLogger.Log("\n✅ Smart Hunting Complete!");
        }

        public void StopRecovery()
        {
            DebugLogger.Log($"[ENGINE] StopRecovery called. CTS null={_cancellationTokenSource == null}");
            DebugLogger.Log($"[ENGINE] StopRecovery called. CTS null={_cancellationTokenSource == null}");
            _cancellationTokenSource?.Cancel();
            BalanceChecker.SetGlobalCancellationToken(CancellationToken.None); // Clear so next run starts clean
        }

        public void Dispose()
        {
            _cancellationTokenSource?.Dispose();
            _balanceChecker?.Dispose();
            _logger?.Close();
        }

        public string GetLogDirectory()
        {
            return _logger?.GetLogDirectory();
        }

        /// <summary>
        /// SPEED BOOST: Check if all addresses in this wallet have already been checked (deduplication)
        /// Returns true if ALL addresses are already in _checkedAddresses, meaning we can skip the API call
        /// </summary>
        private bool AreAllAddressesChecked(CryptoWalletGenerator.WalletInfo wallet, RecoveryOptions options)
        {
            if (AppSettings.Current.StrictScanMode)
                return false;

            var addressesToCheck = new List<string>();

            if (options.ScanBitcoin)
            {
                if (!string.IsNullOrEmpty(wallet.BitcoinAddress)) addressesToCheck.Add(wallet.BitcoinAddress);
                if (!string.IsNullOrEmpty(wallet.BitcoinAddressSegWit)) addressesToCheck.Add(wallet.BitcoinAddressSegWit);
                if (!string.IsNullOrEmpty(wallet.BitcoinAddressNativeSegWit)) addressesToCheck.Add(wallet.BitcoinAddressNativeSegWit);
                if (!string.IsNullOrEmpty(wallet.BitcoinAddressTaproot)) addressesToCheck.Add(wallet.BitcoinAddressTaproot);
            }

            if (options.ScanEthereum && !string.IsNullOrEmpty(wallet.EthereumAddress))
                addressesToCheck.Add(wallet.EthereumAddress);

            if (options.ScanSolana && !string.IsNullOrEmpty(wallet.SolanaAddress))
                addressesToCheck.Add(wallet.SolanaAddress);

            if (options.ScanLitecoin && !string.IsNullOrEmpty(wallet.LitecoinAddress))
                addressesToCheck.Add(wallet.LitecoinAddress);

            if (options.ScanDogecoin && !string.IsNullOrEmpty(wallet.DogecoinAddress))
                addressesToCheck.Add(wallet.DogecoinAddress);

            if (options.ScanPolygon && !string.IsNullOrEmpty(wallet.PolygonAddress))
                addressesToCheck.Add(wallet.PolygonAddress);

            if (options.ScanBnb && !string.IsNullOrEmpty(wallet.BnbAddress))
                addressesToCheck.Add(wallet.BnbAddress);

            // If no addresses to check, consider it "already checked"
            if (addressesToCheck.Count == 0)
                return true;

            // Check if ALL addresses have been checked before
            lock (_checkedAddresses)
            {
                foreach (var address in addressesToCheck)
                {
                    if (!_checkedAddresses.Contains(address))
                        return false; // At least one address not checked yet
                }
                return true; // All addresses already checked
            }
        }

        /// <summary>
        /// SPEED BOOST: Mark all addresses in this wallet as checked for deduplication
        /// </summary>
        private void MarkAddressesAsChecked(CryptoWalletGenerator.WalletInfo wallet, RecoveryOptions options)
        {
            var addressesToMark = new List<string>();

            if (options.ScanBitcoin)
            {
                if (!string.IsNullOrEmpty(wallet.BitcoinAddress)) addressesToMark.Add(wallet.BitcoinAddress);
                if (!string.IsNullOrEmpty(wallet.BitcoinAddressSegWit)) addressesToMark.Add(wallet.BitcoinAddressSegWit);
                if (!string.IsNullOrEmpty(wallet.BitcoinAddressNativeSegWit)) addressesToMark.Add(wallet.BitcoinAddressNativeSegWit);
                if (!string.IsNullOrEmpty(wallet.BitcoinAddressTaproot)) addressesToMark.Add(wallet.BitcoinAddressTaproot);
            }

            if (options.ScanEthereum && !string.IsNullOrEmpty(wallet.EthereumAddress))
                addressesToMark.Add(wallet.EthereumAddress);

            if (options.ScanSolana && !string.IsNullOrEmpty(wallet.SolanaAddress))
                addressesToMark.Add(wallet.SolanaAddress);

            if (options.ScanLitecoin && !string.IsNullOrEmpty(wallet.LitecoinAddress))
                addressesToMark.Add(wallet.LitecoinAddress);

            if (options.ScanDogecoin && !string.IsNullOrEmpty(wallet.DogecoinAddress))
                addressesToMark.Add(wallet.DogecoinAddress);

            if (options.ScanPolygon && !string.IsNullOrEmpty(wallet.PolygonAddress))
                addressesToMark.Add(wallet.PolygonAddress);

            if (options.ScanBnb && !string.IsNullOrEmpty(wallet.BnbAddress))
                addressesToMark.Add(wallet.BnbAddress);

            // Mark all addresses as checked
            lock (_checkedAddresses)
            {
                foreach (var address in addressesToMark)
                {
                    _checkedAddresses.Add(address);
                }
            }
        }
    }

    public class RecoveryOptions
    {
        public RecoveryType RecoveryType { get; set; }
        public string PartialMnemonic { get; set; }
        public string CryptoType { get; set; } = "BTC";
        public int MaxAttempts { get; set; } = 100000;
        public bool CheckBalancesRealTime { get; set; } = true;
        public bool QuickScanMode { get; set; } = false;  // SPEED BOOST: Only check BTC first
        
        // SPEED BOOST: Crypto selection - scan only selected cryptos
        public bool ScanBitcoin { get; set; } = true;
        public bool ScanEthereum { get; set; } = true;
        public bool ScanSolana { get; set; } = true;
        public bool ScanUSDT { get; set; } = true;
        
        // Extended crypto scanning - altcoins
        public bool ScanLitecoin { get; set; } = false;   // LTC scanning (BlockCypher API)
        public bool ScanDogecoin { get; set; } = false;   // DOGE scanning (BlockCypher API)
        public bool ScanPolygon { get; set; } = false;    // MATIC scanning (Polygonscan API)
        public bool ScanBnb { get; set; } = false;        // BNB scanning (BscScan API)
    }

    public enum RecoveryType
    {
        PartialMnemonic,
        RandomGeneration,
        PrivateKeyBruteForce,
        SmartHunting  // NEW: Intelligent pattern-based hunting (10,000x better odds!)
    }

    public class WalletFoundEventArgs : EventArgs
    {
        public CryptoWalletGenerator.WalletInfo Wallet { get; }

        public WalletFoundEventArgs(CryptoWalletGenerator.WalletInfo wallet)
        {
            Wallet = wallet;
        }
    }

    public class ProgressEventArgs : EventArgs
    {
        public double ProgressPercentage { get; }
        public int ProcessedCount { get; }
        public int TotalCount { get; }
        public CryptoWalletGenerator.WalletInfo CurrentWallet { get; }
        public string Message { get; }
        public long DroppedBySystemDedupe { get; }
        public Dictionary<string, PhaseStats> PhaseStats { get; }
        public double Rolling30sRate { get; }
        public int TotalChecked => ProcessedCount;

        public ProgressEventArgs(double progressPercentage, int processedCount, int totalCount, CryptoWalletGenerator.WalletInfo currentWallet, string message = null, long droppedBySystemDedupe = 0, Dictionary<string, PhaseStats> phaseStats = null, double rolling30sRate = 0)
        {
            ProgressPercentage = progressPercentage;
            ProcessedCount = processedCount;
            TotalCount = totalCount;
            CurrentWallet = currentWallet;
            Message = message;
            DroppedBySystemDedupe = droppedBySystemDedupe;
            PhaseStats = phaseStats;
            Rolling30sRate = rolling30sRate;
        }
    }

    public class StatisticsEventArgs : EventArgs
    {
        public string FormattedStats { get; }

        public StatisticsEventArgs(string formattedStats)
        {
            FormattedStats = formattedStats;
        }
    }

    public class ErrorEventArgs : EventArgs
    {
        public string ErrorMessage { get; }

        public ErrorEventArgs(string errorMessage)
        {
            ErrorMessage = errorMessage;
        }
    }
}