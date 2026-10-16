using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Intelligent batch processing system for wallet recovery
    /// Manages multiple recovery methods in parallel with priority queue
    /// </summary>
    public class BatchRecoveryProcessor
    {
        private readonly ConcurrentQueue<RecoveryJob> _jobQueue = new ConcurrentQueue<RecoveryJob>();
        private readonly ConcurrentDictionary<string, RecoveryResult> _resultCache = new ConcurrentDictionary<string, RecoveryResult>();
        private readonly SemaphoreSlim _semaphore;
        private readonly int _maxConcurrentJobs;
        private CancellationTokenSource _cancellationSource;
        
        public event EventHandler<RecoveryProgressEventArgs> ProgressUpdated;
        public event EventHandler<RecoveryResult> WalletFound;

        public class RecoveryJob
        {
            public string JobId { get; set; } = Guid.NewGuid().ToString();
            public RecoveryMethod Method { get; set; }
            public RecoveryPriority Priority { get; set; }
            public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
            public DateTime QueuedAt { get; set; } = DateTime.Now;
            public int RetryCount { get; set; } = 0;
            public int MaxRetries { get; set; } = 3;
        }

        public enum RecoveryMethod
        {
            ForensicScan,           // Fastest (5 min), 20-40% success
            WalletFingerprinting,   // Instant, 95% search space reduction
            PassphraseRecovery,     // Medium (30 min - 2 hrs), 40-60% success
            SmartMnemonicRecovery,  // Medium (15 min - 1 hr), 60-80% success
            BlockchainAnalysis,     // Slow (5-10 min), intelligence gathering
            TypoCorrection,         // Instant, 80-95% success
            GPUBruteForce,          // Very slow (hours-days), last resort
            RandomGeneration        // Futile (0.00000001% success), not recommended
        }

        public enum RecoveryPriority
        {
            Critical = 0,   // Forensic scan, typo correction (run first)
            High = 1,       // Wallet fingerprinting, smart mnemonic (high success rate)
            Medium = 2,     // Passphrase recovery, blockchain analysis
            Low = 3,        // GPU brute force (last resort)
            Disabled = 99   // Random generation (disabled by default)
        }

        public class RecoveryResult
        {
            public string JobId { get; set; }
            public RecoveryMethod Method { get; set; }
            public bool Success { get; set; }
            public string FoundMnemonic { get; set; }
            public string FoundPassphrase { get; set; }
            public string FoundPrivateKey { get; set; }
            public List<string> FoundAddresses { get; set; } = new List<string>();
            public Dictionary<string, decimal> Balances { get; set; } = new Dictionary<string, decimal>();
            public TimeSpan ElapsedTime { get; set; }
            public int AttemptsCount { get; set; }
            public string ErrorMessage { get; set; }
            public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        }

        public class RecoveryProgressEventArgs : EventArgs
        {
            public string JobId { get; set; }
            public RecoveryMethod Method { get; set; }
            public int Progress { get; set; }
            public string StatusMessage { get; set; }
        }

        public BatchRecoveryProcessor(int maxConcurrentJobs = 3)
        {
            _maxConcurrentJobs = maxConcurrentJobs;
            _semaphore = new SemaphoreSlim(maxConcurrentJobs, maxConcurrentJobs);
        }

        /// <summary>
        /// Adds a recovery job to the queue
        /// </summary>
        public string QueueJob(RecoveryMethod method, RecoveryPriority priority, Dictionary<string, object> parameters)
        {
            var job = new RecoveryJob
            {
                Method = method,
                Priority = priority,
                Parameters = parameters
            };

            _jobQueue.Enqueue(job);
            DebugLogger.Log($"✓ Queued {method} job with {priority} priority (ID: {job.JobId})");

            return job.JobId;
        }

        /// <summary>
        /// Processes all queued jobs with intelligent prioritization
        /// </summary>
        public async Task<List<RecoveryResult>> ProcessAllJobs(CancellationToken cancellationToken = default)
        {
            _cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var results = new ConcurrentBag<RecoveryResult>();

            try
            {
                // Sort jobs by priority
                var sortedJobs = _jobQueue.OrderBy(j => (int)j.Priority).ToList();
                DebugLogger.Log($"🚀 Processing {sortedJobs.Count} recovery jobs...\n");

                // Process jobs in parallel (respecting max concurrency)
                var tasks = sortedJobs.Select(async job =>
                {
                    await _semaphore.WaitAsync(_cancellationSource.Token);
                    
                    try
                    {
                        var result = await ExecuteJob(job, _cancellationSource.Token);
                        results.Add(result);

                        if (result.Success)
                        {
                            WalletFound?.Invoke(this, result);
                            
                            // Stop all jobs if wallet found unless strict scan mode is enabled.
                            if (!AppSettings.Current.StrictScanMode)
                            {
                                DebugLogger.Log("🎉 WALLET FOUND! Stopping all other jobs.");
                                _cancellationSource.Cancel();
                            }
                        }

                        return result;
                    }
                    finally
                    {
                        _semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
                DebugLogger.Log("⚠️ Recovery process cancelled.");
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"❌ Batch processing error: {ex.Message}");
            }

            return results.ToList();
        }

        /// <summary>
        /// Executes a single recovery job
        /// </summary>
        private async Task<RecoveryResult> ExecuteJob(RecoveryJob job, CancellationToken cancellationToken)
        {
            var result = new RecoveryResult
            {
                JobId = job.JobId,
                Method = job.Method
            };

            var startTime = DateTime.Now;

            try
            {
                DebugLogger.Log($"⏳ Starting {job.Method} (Priority: {job.Priority})...");
                
                // Report progress
                ProgressUpdated?.Invoke(this, new RecoveryProgressEventArgs
                {
                    JobId = job.JobId,
                    Method = job.Method,
                    Progress = 0,
                    StatusMessage = $"Starting {job.Method}..."
                });

                switch (job.Method)
                {
                    case RecoveryMethod.ForensicScan:
                        result = await ExecuteForensicScan(job, cancellationToken);
                        break;

                    case RecoveryMethod.WalletFingerprinting:
                        result = await ExecuteWalletFingerprinting(job, cancellationToken);
                        break;

                    case RecoveryMethod.PassphraseRecovery:
                        result = await ExecutePassphraseRecovery(job, cancellationToken);
                        break;

                    case RecoveryMethod.SmartMnemonicRecovery:
                        result = await ExecuteSmartMnemonicRecovery(job, cancellationToken);
                        break;

                    case RecoveryMethod.BlockchainAnalysis:
                        result = await ExecuteBlockchainAnalysis(job, cancellationToken);
                        break;

                    case RecoveryMethod.TypoCorrection:
                        result = await ExecuteTypoCorrection(job, cancellationToken);
                        break;

                    case RecoveryMethod.GPUBruteForce:
                        result = await ExecuteGPUBruteForce(job, cancellationToken);
                        break;

                    default:
                        result.ErrorMessage = $"Unknown recovery method: {job.Method}";
                        break;
                }

                result.ElapsedTime = DateTime.Now - startTime;

                if (result.Success)
                {
                    DebugLogger.Log($"✅ {job.Method} completed successfully in {result.ElapsedTime.TotalSeconds:F1}s");
                    _resultCache[job.JobId] = result;
                }
                else
                {
                    DebugLogger.Log($"⚠️ {job.Method} completed without finding wallet ({result.ElapsedTime.TotalSeconds:F1}s)");
                }
            }
            catch (OperationCanceledException)
            {
                result.ErrorMessage = "Job cancelled";
                DebugLogger.Log($"⚠️ {job.Method} cancelled");
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                DebugLogger.Log($"❌ {job.Method} failed: {ex.Message}");

                // Retry logic
                if (job.RetryCount < job.MaxRetries)
                {
                    job.RetryCount++;
                    DebugLogger.Log($"🔄 Retrying {job.Method} (Attempt {job.RetryCount + 1}/{job.MaxRetries + 1})");
                    _jobQueue.Enqueue(job);
                }
            }

            return result;
        }

        // ========== Individual Method Executors ==========

        private async Task<RecoveryResult> ExecuteForensicScan(RecoveryJob job, CancellationToken cancellationToken)
        {
            var scanner = new ForensicWalletRecovery();
            var result = new RecoveryResult { JobId = job.JobId, Method = job.Method };

            var mnemonics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var privateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Scan all major browsers (Chrome, Brave, Edge, Opera, Firefox)
            foreach (ForensicWalletRecovery.BrowserType browser in Enum.GetValues(typeof(ForensicWalletRecovery.BrowserType)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var browserWallets = await scanner.ExtractFromBrowser(browser);
                if (browserWallets != null)
                {
                    foreach (var w in browserWallets)
                    {
                        if (w.FoundMnemonics != null)
                        {
                            foreach (var m in w.FoundMnemonics.Where(m => !MnemonicTracker.IsRestrictedTrivialMnemonic(m)))
                                mnemonics.Add(m);
                        }
                        if (w.FoundPrivateKeys != null)
                        {
                            foreach (var k in w.FoundPrivateKeys)
                                privateKeys.Add(k);
                        }
                    }
                }
            }

            // Scan disk for wallet files and extract mnemonics from readable text/backup files
            var walletFiles = scanner.ScanForWalletFiles();
            foreach (var wf in walletFiles.Where(f => !f.IsEncrypted && f.FileSize > 0 && f.FileSize <= 2 * 1024 * 1024))
            {
                try
                {
                    var text = System.IO.File.ReadAllText(wf.FilePath);
                    foreach (var m in scanner.ExtractMnemonicsFromText(text))
                    {
                        if (!MnemonicTracker.IsRestrictedTrivialMnemonic(m))
                            mnemonics.Add(m);
                    }
                    foreach (var k in scanner.ExtractPrivateKeysFromText(text))
                        privateKeys.Add(k);
                }
                catch { }
            }

            result.Success = mnemonics.Count > 0 || privateKeys.Count > 0 || walletFiles.Count > 0;
            if (mnemonics.Count > 0)
            {
                result.FoundMnemonic = mnemonics.First();
            }
            if (privateKeys.Count > 0)
            {
                result.FoundPrivateKey = privateKeys.First();
            }
            result.Metadata["FoundCount"] = mnemonics.Count;
            result.Metadata["PrivateKeyCount"] = privateKeys.Count;
            result.Metadata["WalletFileCount"] = walletFiles.Count;

            return result;
        }

        private async Task<RecoveryResult> ExecuteWalletFingerprinting(RecoveryJob job, CancellationToken cancellationToken)
        {
            var fingerprinter = new WalletFingerprinting();
            var result = new RecoveryResult { JobId = job.JobId, Method = job.Method };

            var address = job.Parameters.TryGetValue("address", out var addrObj) ? addrObj as string : null;
            if (string.IsNullOrEmpty(address))
            {
                result.ErrorMessage = "Missing 'address' parameter";
                return await Task.FromResult(result);
            }
            var detectionResult = WalletFingerprinting.DetectWalletSoftware(address);

            result.Success = detectionResult.ConfidenceScore > 50;
            result.Metadata["WalletType"] = detectionResult.WalletType;
            result.Metadata["DerivationPath"] = detectionResult.SuggestedDerivationPath;

            return await Task.FromResult(result);
        }

        private async Task<RecoveryResult> ExecutePassphraseRecovery(RecoveryJob job, CancellationToken cancellationToken)
        {
            var recovery = new PassphraseRecovery();
            var result = new RecoveryResult { JobId = job.JobId, Method = job.Method };

            var mnemonic = job.Parameters.TryGetValue("mnemonic", out var mnObj) ? mnObj as string : null;
            var address = job.Parameters.TryGetValue("address", out var adObj) ? adObj as string : null;
            var userProfile = job.Parameters.TryGetValue("userProfile", out var upObj) ? upObj as PassphraseRecovery.UserProfile : null;

            if (string.IsNullOrEmpty(mnemonic) || string.IsNullOrEmpty(address))
            {
                result.ErrorMessage = "Missing 'mnemonic' or 'address' parameter";
                return await Task.FromResult(result);
            }

            var passphraseResult = await recovery.BruteForcePassphrase(mnemonic, address, userProfile, cancellationToken);

            result.Success = passphraseResult != null && !string.IsNullOrEmpty(passphraseResult.Passphrase);
            if (result.Success)
            {
                result.FoundPassphrase = passphraseResult.Passphrase;
                result.FoundMnemonic = mnemonic;
                result.AttemptsCount = passphraseResult.AttemptsRequired;
            }

            return await Task.FromResult(result);
        }

        private async Task<RecoveryResult> ExecuteSmartMnemonicRecovery(RecoveryJob job, CancellationToken cancellationToken)
        {
            var recovery = new IntelligentMnemonicRecovery();
            var result = new RecoveryResult { JobId = job.JobId, Method = job.Method };

            var partialMnemonic = job.Parameters.TryGetValue("partialMnemonic", out var pmObj) ? pmObj as string : null;
            var address = job.Parameters.TryGetValue("address", out var addrObj) ? addrObj as string : null;
            var walletHint = job.Parameters.TryGetValue("walletHint", out var whObj) ? whObj as string : null;

            if (string.IsNullOrWhiteSpace(partialMnemonic))
            {
                result.ErrorMessage = "Missing 'partialMnemonic' parameter";
                return result;
            }

            // Parse partial mnemonic into dictionary format
            var knownWords = new Dictionary<int, string>();
            var words = partialMnemonic.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(words[i]) && words[i] != "?" && words[i] != "_" && words[i] != "*")
                    knownWords[i] = words[i];
            }

            int totalWords = words.Length == 24 ? 24 : 12;
            var recoveryResult = await recovery.SmartWordPrediction(knownWords, totalWords, walletHint, null, address, cancellationToken);

            result.Success = recoveryResult != null && !string.IsNullOrEmpty(recoveryResult.Mnemonic);
            if (result.Success)
            {
                result.FoundMnemonic = recoveryResult.Mnemonic;
                result.AttemptsCount = recoveryResult.AttemptsRequired;
            }

            return result;
        }

        private async Task<RecoveryResult> ExecuteBlockchainAnalysis(RecoveryJob job, CancellationToken cancellationToken)
        {
            var analyzer = new BlockchainPatternAnalyzer();
            var result = new RecoveryResult { JobId = job.JobId, Method = job.Method };

            var address = job.Parameters.TryGetValue("address", out var addrObj) ? addrObj as string : null;
            if (string.IsNullOrWhiteSpace(address))
            {
                result.ErrorMessage = "Missing 'address' parameter";
                return result;
            }

            var metadata = await analyzer.AnalyzeWalletBehavior(address);

            result.Success = metadata != null;
            if (metadata != null)
            {
                result.Metadata["WalletSoftware"] = metadata.LikelyWalletSoftware;
                result.Metadata["DerivationPath"] = metadata.DerivationPathHint;
                result.Metadata["TransactionCount"] = metadata.TransactionCount;
            }

            return result;
        }

        private async Task<RecoveryResult> ExecuteTypoCorrection(RecoveryJob job, CancellationToken cancellationToken)
        {
            var result = new RecoveryResult { JobId = job.JobId, Method = job.Method };

            var mnemonic = job.Parameters.TryGetValue("mnemonic", out var mnObj) ? mnObj as string : null;
            if (string.IsNullOrWhiteSpace(mnemonic))
            {
                result.ErrorMessage = "Missing 'mnemonic' parameter";
                return result;
            }

            var correctionResult = AdvancedTypoCorrection.AutoCorrectMnemonic(mnemonic);

            result.Success = correctionResult.IsValid;
            if (result.Success)
            {
                result.FoundMnemonic = correctionResult.CorrectedMnemonic;
                result.Metadata["CorrectionsMade"] = correctionResult.CorrectionsMade;
            }

            return await Task.FromResult(result);
        }

        private async Task<RecoveryResult> ExecuteGPUBruteForce(RecoveryJob job, CancellationToken cancellationToken)
        {
            var gpu = new GPUAccelerator();
            var result = new RecoveryResult { JobId = job.JobId, Method = job.Method };

            var partialMnemonic = job.Parameters.TryGetValue("partialMnemonic", out var pmObj) ? pmObj as string : null;
            var address = job.Parameters.TryGetValue("address", out var addrObj) ? addrObj as string : null;
            var maxAttempts = job.Parameters.TryGetValue("maxAttempts", out var maObj) && maObj is int ma ? ma : 1000000;

            if (string.IsNullOrWhiteSpace(partialMnemonic))
            {
                result.ErrorMessage = "Missing 'partialMnemonic' parameter";
                return result;
            }

            var foundMnemonics = await gpu.AcceleratedMnemonicSearch(partialMnemonic, address, maxAttempts, (count) =>
            {
                result.AttemptsCount = count;
            });

            result.Success = foundMnemonics.Count > 0;
            if (result.Success)
            {
                result.FoundMnemonic = foundMnemonics.First();
            }

            return result;
        }

        /// <summary>
        /// Gets recommended recovery workflow based on available information
        /// </summary>
        public List<RecoveryJob> GetRecommendedWorkflow(Dictionary<string, object> userInfo)
        {
            var workflow = new List<RecoveryJob>();

            // STEP 1: Always start with forensic scan (fastest, 20-40% success)
            workflow.Add(new RecoveryJob
            {
                Method = RecoveryMethod.ForensicScan,
                Priority = RecoveryPriority.Critical,
                Parameters = new Dictionary<string, object>()
            });

            // STEP 2: If typos suspected, run typo correction
            if (userInfo.ContainsKey("mnemonic"))
            {
                workflow.Add(new RecoveryJob
                {
                    Method = RecoveryMethod.TypoCorrection,
                    Priority = RecoveryPriority.Critical,
                    Parameters = new Dictionary<string, object> { { "mnemonic", userInfo["mnemonic"] } }
                });
            }

            // STEP 3: If address known, run wallet fingerprinting
            if (userInfo.ContainsKey("address"))
            {
                workflow.Add(new RecoveryJob
                {
                    Method = RecoveryMethod.WalletFingerprinting,
                    Priority = RecoveryPriority.High,
                    Parameters = new Dictionary<string, object> { { "address", userInfo["address"] } }
                });

                workflow.Add(new RecoveryJob
                {
                    Method = RecoveryMethod.BlockchainAnalysis,
                    Priority = RecoveryPriority.Medium,
                    Parameters = new Dictionary<string, object> { { "address", userInfo["address"] } }
                });
            }

            // STEP 4: If partial mnemonic (8+ words), use smart recovery
            if (userInfo.ContainsKey("partialMnemonic") && userInfo.ContainsKey("address"))
            {
                var partial = userInfo["partialMnemonic"] as string;
                var knownWords = partial.Split(' ').Count(w => w != "_" && !string.IsNullOrWhiteSpace(w));

                if (knownWords >= 8)
                {
                    workflow.Add(new RecoveryJob
                    {
                        Method = RecoveryMethod.SmartMnemonicRecovery,
                        Priority = RecoveryPriority.High,
                        Parameters = new Dictionary<string, object>
                        {
                            { "partialMnemonic", partial },
                            { "address", userInfo["address"] }
                        }
                    });
                }
            }

            // STEP 5: If complete mnemonic but wrong passphrase, try passphrase recovery
            if (userInfo.ContainsKey("mnemonic") && userInfo.ContainsKey("address") && userInfo.ContainsKey("userProfile"))
            {
                workflow.Add(new RecoveryJob
                {
                    Method = RecoveryMethod.PassphraseRecovery,
                    Priority = RecoveryPriority.Medium,
                    Parameters = new Dictionary<string, object>
                    {
                        { "mnemonic", userInfo["mnemonic"] },
                        { "address", userInfo["address"] },
                        { "userProfile", userInfo["userProfile"] }
                    }
                });
            }

            // STEP 6: GPU brute force as last resort
            if (userInfo.ContainsKey("partialMnemonic") && userInfo.ContainsKey("address"))
            {
                workflow.Add(new RecoveryJob
                {
                    Method = RecoveryMethod.GPUBruteForce,
                    Priority = RecoveryPriority.Low,
                    Parameters = new Dictionary<string, object>
                    {
                        { "partialMnemonic", userInfo["partialMnemonic"] },
                        { "address", userInfo["address"] },
                        { "maxAttempts", 10000000 }
                    }
                });
            }

            return workflow;
        }

        /// <summary>
        /// Clears result cache
        /// </summary>
        public void ClearCache()
        {
            _resultCache.Clear();
            DebugLogger.Log("✓ Result cache cleared");
        }

        /// <summary>
        /// Gets cached result for job
        /// </summary>
        public RecoveryResult GetCachedResult(string jobId)
        {
            return _resultCache.TryGetValue(jobId, out var result) ? result : null;
        }
    }
}
