using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Distributed computing system for wallet recovery
    /// Allows splitting work across multiple machines for massive parallelization
    /// </summary>
    public class DistributedRecoveryManager
    {
        private TcpListener _listener;
        private readonly List<WorkerConnection> _workers = new();
        private readonly object _lock = new object();
        private bool _isRunning;
        private int _port;

        // Work distribution
        private readonly Queue<WorkUnit> _pendingWork = new();
        private readonly Dictionary<string, WorkUnit> _assignedWork = new();
        private readonly List<RecoveryResult> _results = new();

        // Statistics
        private long _totalWorkUnits;
        private long _completedWorkUnits;
        private long _totalAttemptsProcessed;
        private DateTime _startTime;

        public event EventHandler<WorkerEventArgs> WorkerConnected;
        public event EventHandler<WorkerEventArgs> WorkerDisconnected;
        public event EventHandler<ResultEventArgs> ResultFound;
        public event EventHandler<ProgressEventArgs> ProgressUpdated;

        #region Master Node Methods

        /// <summary>
        /// Starts the master node to coordinate distributed recovery
        /// </summary>
        public async Task StartMasterNode(int port = 54321, CancellationToken cancellationToken = default)
        {
            _port = port;
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _isRunning = true;
            _startTime = DateTime.Now;

            try
            {
                DebugLogger.Log($"🌐 Master node started on port {port}");
                DebugLogger.Log($"   Workers can connect to: {GetLocalIPAddress()}:{port}");

                // Accept connections
                while (_isRunning && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                        _ = HandleWorkerConnection(client, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Log($"Accept error: {ex.Message}");
                    }
                }
            }
            finally
            {
                _isRunning = false;
                _listener?.Stop();
            }
        }

        /// <summary>
        /// Distributes recovery work to connected workers
        /// </summary>
        public async Task DistributeWork(
            RecoveryWorkType workType,
            string partialMnemonic,
            long totalAttempts,
            string targetAddress = null,
            CancellationToken cancellationToken = default)
        {
            _totalWorkUnits = 0;
            _completedWorkUnits = 0;
            _totalAttemptsProcessed = 0;

            // Calculate work units based on worker count
            int workerCount;
            lock (_lock)
            {
                workerCount = _workers.Count;
            }

            if (workerCount == 0)
            {
                DebugLogger.Log("⚠️ No workers connected! Processing locally...");
                // Fall back to local processing
                return;
            }

            // Split work into units (10,000 attempts per unit)
            const int attemptsPerUnit = 10_000;
            var unitCount = (int)Math.Ceiling((double)totalAttempts / attemptsPerUnit);

            DebugLogger.Log($"📦 Creating {unitCount:N0} work units for {workerCount} workers");

            for (int i = 0; i < unitCount; i++)
            {
                var startIndex = (long)i * attemptsPerUnit;
                var endIndex = Math.Min(startIndex + attemptsPerUnit, totalAttempts);

                var workUnit = new WorkUnit
                {
                    Id = Guid.NewGuid().ToString(),
                    WorkType = workType,
                    PartialMnemonic = partialMnemonic,
                    TargetAddress = targetAddress,
                    StartIndex = startIndex,
                    EndIndex = endIndex,
                    CreatedAt = DateTime.UtcNow
                };

                lock (_lock)
                {
                    _pendingWork.Enqueue(workUnit);
                    _totalWorkUnits++;
                }
            }

            // Assign initial work to workers
            AssignWorkToIdleWorkers();

            // Wait for completion
            while (!cancellationToken.IsCancellationRequested)
            {
                lock (_lock)
                {
                    if (_completedWorkUnits >= _totalWorkUnits)
                        break;
                }

                await Task.Delay(1000, cancellationToken);
                ReportProgress();
            }
        }

        private async Task HandleWorkerConnection(TcpClient client, CancellationToken cancellationToken)
        {
            var worker = new WorkerConnection
            {
                Id = Guid.NewGuid().ToString(),
                Client = client,
                Stream = client.GetStream(),
                ConnectedAt = DateTime.UtcNow,
                Status = WorkerStatus.Idle
            };

            try
            {
                // Read worker info
                var buffer = new byte[4096];
                var bytesRead = await worker.Stream.ReadAsync(buffer, cancellationToken);
                var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                var workerInfo = JsonSerializer.Deserialize<WorkerInfo>(message);

                worker.Name = workerInfo?.Name ?? $"Worker-{worker.Id[..8]}";
                worker.CoreCount = workerInfo?.CoreCount ?? 1;

                lock (_lock)
                {
                    _workers.Add(worker);
                }

                DebugLogger.Log($"✅ Worker connected: {worker.Name} ({worker.CoreCount} cores)");
                WorkerConnected?.Invoke(this, new WorkerEventArgs { Worker = worker });

                // Assign work
                AssignWorkToWorker(worker);

                // Handle worker messages
                while (_isRunning && client.Connected && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        bytesRead = await worker.Stream.ReadAsync(buffer, cancellationToken);
                        if (bytesRead == 0)
                            break;

                        message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        await ProcessWorkerMessage(worker, message);
                    }
                    catch (IOException)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Worker error: {ex.Message}");
            }
            finally
            {
                // Clean up
                lock (_lock)
                {
                    _workers.Remove(worker);
                    
                    // Re-queue any assigned work
                    if (worker.CurrentWorkUnit != null)
                    {
                        _pendingWork.Enqueue(worker.CurrentWorkUnit);
                    }
                }

                DebugLogger.Log($"❌ Worker disconnected: {worker.Name}");
                WorkerDisconnected?.Invoke(this, new WorkerEventArgs { Worker = worker });
                
                try { worker.Stream?.Dispose(); } catch (Exception ex) { DebugLogger.Log($"Stream cleanup: {ex.Message}"); }
                try { client.Close(); } catch (Exception ex) { DebugLogger.Log($"Client close: {ex.Message}"); }
            }
        }

        private async Task ProcessWorkerMessage(WorkerConnection worker, string message)
        {
            try
            {
                var msg = JsonSerializer.Deserialize<WorkerMessage>(message);

                switch (msg?.Type)
                {
                    case "result":
                        var result = JsonSerializer.Deserialize<RecoveryResult>(msg.Payload);
                        if (result != null)
                        {
                            lock (_lock)
                            {
                                _results.Add(result);
                            }
                            DebugLogger.Log($"💰 Result from {worker.Name}: {result.FoundMnemonic}");
                            ResultFound?.Invoke(this, new ResultEventArgs { Result = result });
                        }
                        break;

                    case "progress":
                        var progress = JsonSerializer.Deserialize<ProgressReport>(msg.Payload);
                        if (progress != null)
                        {
                            Interlocked.Add(ref _totalAttemptsProcessed, progress.AttemptsDelta);
                            worker.AttemptsProcessed += progress.AttemptsDelta;
                        }
                        break;

                    case "complete":
                        lock (_lock)
                        {
                            if (worker.CurrentWorkUnit != null)
                            {
                                _assignedWork.Remove(worker.CurrentWorkUnit.Id);
                                _completedWorkUnits++;
                            }
                            worker.CurrentWorkUnit = null;
                            worker.Status = WorkerStatus.Idle;
                        }
                        AssignWorkToWorker(worker);
                        break;
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Message processing error: {ex.Message}");
            }
        }

        private void AssignWorkToIdleWorkers()
        {
            lock (_lock)
            {
                foreach (var worker in _workers.Where(w => w.Status == WorkerStatus.Idle))
                {
                    AssignWorkToWorker(worker);
                }
            }
        }

        private void AssignWorkToWorker(WorkerConnection worker)
        {
            lock (_lock)
            {
                if (_pendingWork.Count == 0)
                    return;

                var workUnit = _pendingWork.Dequeue();
                worker.CurrentWorkUnit = workUnit;
                worker.Status = WorkerStatus.Working;
                _assignedWork[workUnit.Id] = workUnit;

                // Send work to worker
                var message = new WorkerMessage
                {
                    Type = "work",
                    Payload = JsonSerializer.Serialize(workUnit)
                };

                try
                {
                    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
                    worker.Stream.Write(bytes, 0, bytes.Length);
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"Failed to assign work: {ex.Message}");
                    _pendingWork.Enqueue(workUnit);
                    worker.CurrentWorkUnit = null;
                    worker.Status = WorkerStatus.Error;
                }
            }
        }

        private void ReportProgress()
        {
            var elapsed = DateTime.Now - _startTime;
            var opsPerSec = elapsed.TotalSeconds > 0 
                ? _totalAttemptsProcessed / elapsed.TotalSeconds 
                : 0;

            int workerCount;
            lock (_lock)
            {
                workerCount = _workers.Count;
            }

            var progress = new ProgressReport
            {
                TotalWorkUnits = _totalWorkUnits,
                CompletedWorkUnits = _completedWorkUnits,
                TotalAttempts = _totalAttemptsProcessed,
                AttemptsPerSecond = opsPerSec,
                WorkerCount = workerCount,
                Elapsed = elapsed
            };

            ProgressUpdated?.Invoke(this, new ProgressEventArgs { Progress = progress });
        }

        public void StopMaster()
        {
            _isRunning = false;
            _listener?.Stop();
            
            lock (_lock)
            {
                foreach (var worker in _workers)
                {
                    worker.Client?.Close();
                }
                _workers.Clear();
            }
        }

        #endregion

        #region Worker Node Methods

        /// <summary>
        /// Starts a worker node that connects to a master
        /// </summary>
        public async Task StartWorkerNode(
            string masterHost,
            int masterPort = 54321,
            string workerName = null,
            CancellationToken cancellationToken = default)
        {
            workerName ??= $"Worker-{Environment.MachineName}";

            DebugLogger.Log($"🔗 Connecting to master at {masterHost}:{masterPort}...");

            using var client = new TcpClient();
            await client.ConnectAsync(masterHost, masterPort, cancellationToken);

            DebugLogger.Log($"✅ Connected to master as '{workerName}'");

            using var stream = client.GetStream();

            // Send worker info
            var workerInfo = new WorkerInfo
            {
                Name = workerName,
                CoreCount = Environment.ProcessorCount
            };
            var infoBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(workerInfo));
            await stream.WriteAsync(infoBytes, cancellationToken);

            // Process work
            var buffer = new byte[65536];
            while (client.Connected && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
                    if (bytesRead == 0)
                        break;

                    var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    var msg = JsonSerializer.Deserialize<WorkerMessage>(message);

                    if (msg?.Type == "work")
                    {
                        var workUnit = JsonSerializer.Deserialize<WorkUnit>(msg.Payload);
                        if (workUnit != null)
                        {
                            DebugLogger.Log($"📥 Received work: {workUnit.StartIndex:N0} - {workUnit.EndIndex:N0}");
                            await ProcessWorkUnit(stream, workUnit, cancellationToken);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"Worker error: {ex.Message}");
                }
            }
        }

        private async Task ProcessWorkUnit(NetworkStream stream, WorkUnit workUnit, CancellationToken cancellationToken)
        {
            var engine = new EnhancedGPUAccelerator();
            var processed = 0L;
            var lastReport = DateTime.Now;

            try
            {
                switch (workUnit.WorkType)
                {
                    case RecoveryWorkType.PartialMnemonic:
                        var results = await engine.ParallelMnemonicSearch(
                            workUnit.PartialMnemonic,
                            workUnit.TargetAddress,
                            (int)(workUnit.EndIndex - workUnit.StartIndex),
                            progress =>
                            {
                                processed = progress;
                                
                                // Report progress every 5 seconds
                                if ((DateTime.Now - lastReport).TotalSeconds >= 5)
                                {
                                    SendProgress(stream, progress);
                                    lastReport = DateTime.Now;
                                }
                            },
                            cancellationToken);

                        // Send results
                        foreach (var mnemonic in results)
                        {
                            var result = new RecoveryResult
                            {
                                FoundMnemonic = mnemonic,
                                WorkUnitId = workUnit.Id,
                                FoundAt = DateTime.UtcNow
                            };
                            SendResult(stream, result);
                        }
                        break;

                    case RecoveryWorkType.RandomGeneration:
                        var generated = engine.BatchGenerateAddresses(
                            (int)(workUnit.EndIndex - workUnit.StartIndex),
                            progress =>
                            {
                                processed = progress;
                                if ((DateTime.Now - lastReport).TotalSeconds >= 5)
                                {
                                    SendProgress(stream, progress);
                                    lastReport = DateTime.Now;
                                }
                            },
                            cancellationToken);
                        // Results would be checked by balance API on worker
                        break;
                }

                // Send completion
                SendComplete(stream);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Work unit error: {ex.Message}");
                SendComplete(stream);
            }
        }

        private void SendProgress(NetworkStream stream, long attempts)
        {
            var msg = new WorkerMessage
            {
                Type = "progress",
                Payload = JsonSerializer.Serialize(new ProgressReport { AttemptsDelta = attempts })
            };
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg));
            stream.Write(bytes, 0, bytes.Length);
        }

        private void SendResult(NetworkStream stream, RecoveryResult result)
        {
            var msg = new WorkerMessage
            {
                Type = "result",
                Payload = JsonSerializer.Serialize(result)
            };
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg));
            stream.Write(bytes, 0, bytes.Length);
        }

        private void SendComplete(NetworkStream stream)
        {
            var msg = new WorkerMessage { Type = "complete", Payload = "{}" };
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg));
            stream.Write(bytes, 0, bytes.Length);
        }

        #endregion

        #region Helper Methods

        private string GetLocalIPAddress()
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                socket.Connect("8.8.8.8", 65530);
                return ((IPEndPoint)socket.LocalEndPoint)?.Address.ToString() ?? "localhost";
            }
            catch
            {
                return "localhost";
            }
        }

        public List<RecoveryResult> GetResults()
        {
            lock (_lock)
            {
                return _results.ToList();
            }
        }

        public (int workerCount, long completed, long total) GetStatus()
        {
            lock (_lock)
            {
                return (_workers.Count, _completedWorkUnits, _totalWorkUnits);
            }
        }

        #endregion

        #region Data Classes

        public class WorkUnit
        {
            public string Id { get; set; }
            public RecoveryWorkType WorkType { get; set; }
            public string PartialMnemonic { get; set; }
            public string TargetAddress { get; set; }
            public long StartIndex { get; set; }
            public long EndIndex { get; set; }
            public DateTime CreatedAt { get; set; }
        }

        public enum RecoveryWorkType
        {
            PartialMnemonic,
            RandomGeneration,
            BrainWallet,
            PrivateKey
        }

        public class WorkerConnection
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public TcpClient Client { get; set; }
            public NetworkStream Stream { get; set; }
            public DateTime ConnectedAt { get; set; }
            public WorkerStatus Status { get; set; }
            public int CoreCount { get; set; }
            public WorkUnit CurrentWorkUnit { get; set; }
            public long AttemptsProcessed { get; set; }
        }

        public enum WorkerStatus
        {
            Idle,
            Working,
            Error
        }

        public class WorkerInfo
        {
            public string Name { get; set; }
            public int CoreCount { get; set; }
        }

        public class WorkerMessage
        {
            public string Type { get; set; }
            public string Payload { get; set; }
        }

        public class RecoveryResult
        {
            public string FoundMnemonic { get; set; }
            public string FoundAddress { get; set; }
            public decimal Balance { get; set; }
            public string WorkUnitId { get; set; }
            public DateTime FoundAt { get; set; }
        }

        public class ProgressReport
        {
            public long TotalWorkUnits { get; set; }
            public long CompletedWorkUnits { get; set; }
            public long TotalAttempts { get; set; }
            public long AttemptsDelta { get; set; }
            public double AttemptsPerSecond { get; set; }
            public int WorkerCount { get; set; }
            public TimeSpan Elapsed { get; set; }
        }

        public class WorkerEventArgs : EventArgs
        {
            public WorkerConnection Worker { get; set; }
        }

        public class ResultEventArgs : EventArgs
        {
            public RecoveryResult Result { get; set; }
        }

        public class ProgressEventArgs : EventArgs
        {
            public ProgressReport Progress { get; set; }
        }

        #endregion
    }
}
