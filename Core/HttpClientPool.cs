using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using RestSharp;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Advanced HTTP client factory with connection pooling, DNS refresh, and circuit breaker
    /// Replaces static HttpClient instances for better reliability
    /// </summary>
    public class HttpClientPool : IDisposable
    {
        private static HttpClientPool _instance;
        private static readonly object _lock = new object();

        // Client pools by base URL
        private readonly ConcurrentDictionary<string, PooledClient> _clients = new();
        
        // Connection settings
        private readonly int _maxConnectionsPerServer;
        private readonly TimeSpan _connectionLifetime;
        private readonly TimeSpan _defaultTimeout;

        // Circuit breaker settings
        private readonly ConcurrentDictionary<string, CircuitBreakerState> _circuitBreakers = new();
        private readonly int _failureThreshold;
        private readonly TimeSpan _circuitOpenDuration;

        public static HttpClientPool Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new HttpClientPool();
                    }
                }
                return _instance;
            }
        }

        public HttpClientPool(
            int maxConnectionsPerServer = 100,
            int connectionLifetimeMinutes = 5,
            int defaultTimeoutSeconds = 10,
            int failureThreshold = 5,
            int circuitOpenSeconds = 30)
        {
            _maxConnectionsPerServer = maxConnectionsPerServer;
            _connectionLifetime = TimeSpan.FromMinutes(connectionLifetimeMinutes);
            _defaultTimeout = TimeSpan.FromSeconds(defaultTimeoutSeconds);
            _failureThreshold = failureThreshold;
            _circuitOpenDuration = TimeSpan.FromSeconds(circuitOpenSeconds);

            // Configure default connection limit
            ServicePointManager.DefaultConnectionLimit = maxConnectionsPerServer;
            ServicePointManager.ReusePort = true;
            ServicePointManager.EnableDnsRoundRobin = true;
            ServicePointManager.DnsRefreshTimeout = (int)TimeSpan.FromMinutes(1).TotalMilliseconds;
        }

        /// <summary>
        /// Gets or creates an HttpClient for the specified base URL
        /// </summary>
        public HttpClient GetClient(string baseUrl, TimeSpan? timeout = null)
        {
            var uri = new Uri(baseUrl);
            var key = $"{uri.Scheme}://{uri.Host}:{uri.Port}";

            var pooled = _clients.GetOrAdd(key, k => CreatePooledClient(k, timeout ?? _defaultTimeout));

            // Check if client needs refresh
            if (DateTime.UtcNow - pooled.CreatedAt > _connectionLifetime)
            {
                var newPooled = CreatePooledClient(key, timeout ?? _defaultTimeout);
                if (_clients.TryUpdate(key, newPooled, pooled))
                {
                    // Schedule delayed dispose to allow in-flight requests to complete
                    var oldClient = pooled.Client;
                    var oldHandler = pooled.Handler;
                    _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ =>
                    {
                        try { oldClient?.Dispose(); } catch { }
                        try { oldHandler?.Dispose(); } catch { }
                    });
                    pooled = newPooled;
                }
            }

            return pooled.Client;
        }

        /// <summary>
        /// Gets or creates a RestClient for the specified base URL
        /// </summary>
        public RestClient GetRestClient(string baseUrl, TimeSpan? timeout = null)
        {
            var uri = new Uri(baseUrl);
            var key = $"rest:{uri.Scheme}://{uri.Host}:{uri.Port}";

            var pooled = _clients.GetOrAdd(key, k =>
            {
                var options = new RestClientOptions(baseUrl)
                {
                    Timeout = timeout ?? _defaultTimeout,
                    ThrowOnAnyError = false
                };
                return new PooledClient
                {
                    RestClient = new RestClient(options),
                    CreatedAt = DateTime.UtcNow
                };
            });

            // Check if client needs refresh
            if (DateTime.UtcNow - pooled.CreatedAt > _connectionLifetime)
            {
                var options = new RestClientOptions(baseUrl)
                {
                    Timeout = timeout ?? _defaultTimeout
                };
                var newPooled = new PooledClient
                {
                    RestClient = new RestClient(options),
                    CreatedAt = DateTime.UtcNow
                };
                
                if (_clients.TryUpdate(key, newPooled, pooled))
                {
                    // Schedule delayed dispose to allow in-flight requests to complete
                    var oldClient = pooled.RestClient;
                    _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ =>
                    {
                        try { oldClient?.Dispose(); } catch { }
                    });
                    pooled = newPooled;
                }
            }

            return pooled.RestClient;
        }

        /// <summary>
        /// Executes a request with circuit breaker protection
        /// </summary>
        public async Task<HttpResponseMessage> ExecuteWithCircuitBreaker(
            string baseUrl,
            Func<HttpClient, Task<HttpResponseMessage>> action)
        {
            var uri = new Uri(baseUrl);
            var key = uri.Host;

            var circuitBreaker = _circuitBreakers.GetOrAdd(key, _ => new CircuitBreakerState());

            // Check if circuit is open
            if (circuitBreaker.State == CircuitState.Open)
            {
                if (DateTime.UtcNow - circuitBreaker.LastFailure < _circuitOpenDuration)
                {
                    throw new CircuitBreakerOpenException($"Circuit breaker is open for {key}");
                }
                // Allow one test request (half-open state)
                circuitBreaker.State = CircuitState.HalfOpen;
            }

            try
            {
                var client = GetClient(baseUrl);
                var response = await action(client);

                if (response.IsSuccessStatusCode)
                {
                    circuitBreaker.SuccessCount++;
                    circuitBreaker.FailureCount = 0;
                    circuitBreaker.State = CircuitState.Closed;
                }
                else if ((int)response.StatusCode >= 500)
                {
                    RecordFailure(circuitBreaker);
                }

                return response;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                RecordFailure(circuitBreaker);
                throw;
            }
        }

        /// <summary>
        /// Executes a REST request with circuit breaker protection
        /// </summary>
        public async Task<RestResponse> ExecuteRestWithCircuitBreaker(
            string baseUrl,
            RestRequest request)
        {
            var uri = new Uri(baseUrl);
            var key = uri.Host;

            var circuitBreaker = _circuitBreakers.GetOrAdd(key, _ => new CircuitBreakerState());

            if (circuitBreaker.State == CircuitState.Open)
            {
                if (DateTime.UtcNow - circuitBreaker.LastFailure < _circuitOpenDuration)
                {
                    throw new CircuitBreakerOpenException($"Circuit breaker is open for {key}");
                }
                circuitBreaker.State = CircuitState.HalfOpen;
            }

            try
            {
                var client = GetRestClient(baseUrl);
                var response = await client.ExecuteAsync(request);

                if (response.IsSuccessful)
                {
                    circuitBreaker.SuccessCount++;
                    circuitBreaker.FailureCount = 0;
                    circuitBreaker.State = CircuitState.Closed;
                }
                else if ((int)response.StatusCode >= 500)
                {
                    RecordFailure(circuitBreaker);
                }

                return response;
            }
            catch (Exception)
            {
                RecordFailure(circuitBreaker);
                throw;
            }
        }

        private void RecordFailure(CircuitBreakerState circuitBreaker)
        {
            circuitBreaker.FailureCount++;
            circuitBreaker.LastFailure = DateTime.UtcNow;

            if (circuitBreaker.FailureCount >= _failureThreshold)
            {
                circuitBreaker.State = CircuitState.Open;
                DebugLogger.Log($"Circuit breaker opened after {circuitBreaker.FailureCount} failures");
            }
        }

        private PooledClient CreatePooledClient(string baseUrl, TimeSpan timeout)
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = _connectionLifetime,
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = _maxConnectionsPerServer,
                EnableMultipleHttp2Connections = true,
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(5),
                ResponseDrainTimeout = TimeSpan.FromSeconds(2)
            };

            var client = new HttpClient(handler)
            {
                Timeout = timeout,
                BaseAddress = new Uri(baseUrl)
            };

            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.UserAgent.ParseAdd("WalletRecoveryTool/2.2");

            return new PooledClient
            {
                Client = client,
                Handler = handler,
                CreatedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Pre-warms HTTP connections by sending HEAD requests to well-known endpoints.
        /// Call once at startup to avoid cold-start timeouts on first real request.
        /// </summary>
        public async Task WarmupAsync(params string[] baseUrls)
        {
            var tasks = new List<Task>();
            foreach (var url in baseUrls)
            {
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var client = GetClient(url);
                        using var request = new HttpRequestMessage(HttpMethod.Head, "/");
                        request.Headers.ConnectionClose = false;
                        await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                            .WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch { /* Warmup failure is non-fatal */ }
                }));
            }
            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Gets circuit breaker status for all endpoints
        /// </summary>
        public ConcurrentDictionary<string, CircuitBreakerState> GetCircuitBreakerStatus()
        {
            return _circuitBreakers;
        }

        /// <summary>
        /// Resets circuit breaker for a specific endpoint
        /// </summary>
        public void ResetCircuitBreaker(string host)
        {
            if (_circuitBreakers.TryGetValue(host, out var state))
            {
                state.State = CircuitState.Closed;
                state.FailureCount = 0;
            }
        }

        /// <summary>
        /// Resets all circuit breakers
        /// </summary>
        public void ResetAllCircuitBreakers()
        {
            foreach (var kvp in _circuitBreakers)
            {
                kvp.Value.State = CircuitState.Closed;
                kvp.Value.FailureCount = 0;
            }
        }

        public void Dispose()
        {
            foreach (var pooled in _clients.Values)
            {
                pooled.Client?.Dispose();
                pooled.Handler?.Dispose();
                pooled.RestClient?.Dispose();
            }
            _clients.Clear();
        }

        private class PooledClient
        {
            public HttpClient Client { get; set; }
            public SocketsHttpHandler Handler { get; set; }
            public RestClient RestClient { get; set; }
            public DateTime CreatedAt { get; set; }
        }

        public enum CircuitState
        {
            Closed,
            Open,
            HalfOpen
        }

        public class CircuitBreakerState
        {
            public CircuitState State { get; set; } = CircuitState.Closed;
            public int FailureCount { get; set; }
            public int SuccessCount { get; set; }
            public DateTime LastFailure { get; set; }
        }
    }

    public class CircuitBreakerOpenException : Exception
    {
        public CircuitBreakerOpenException(string message) : base(message) { }
    }

    /// <summary>
    /// Rate limiter using token bucket algorithm
    /// </summary>
    public class RateLimiter
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly int _maxTokens;
        private readonly TimeSpan _refillInterval;
        private int _currentTokens;
        private DateTime _lastRefill;
        private readonly object _lock = new object();

        public RateLimiter(int maxRequestsPerSecond)
        {
            _maxTokens = maxRequestsPerSecond;
            _currentTokens = maxRequestsPerSecond;
            _refillInterval = TimeSpan.FromSeconds(1);
            _lastRefill = DateTime.UtcNow;
            _semaphore = new SemaphoreSlim(1, 1);
        }

        public async Task<bool> WaitForTokenAsync(CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                RefillTokens();

                if (_currentTokens > 0)
                {
                    _currentTokens--;
                    return true;
                }

                // Calculate wait time for next token
                var timeSinceRefill = DateTime.UtcNow - _lastRefill;
                var waitTime = _refillInterval - timeSinceRefill;
                
                if (waitTime > TimeSpan.Zero)
                {
                    await Task.Delay(waitTime, cancellationToken);
                    RefillTokens();
                }

                if (_currentTokens > 0)
                {
                    _currentTokens--;
                    return true;
                }

                return false;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private void RefillTokens()
        {
            var now = DateTime.UtcNow;
            var timeSinceRefill = now - _lastRefill;
            
            if (timeSinceRefill >= _refillInterval)
            {
                int tokensToAdd = (int)(timeSinceRefill.TotalSeconds) * _maxTokens;
                _currentTokens = Math.Min(_maxTokens, _currentTokens + tokensToAdd);
                _lastRefill = now;
            }
        }

        public int AvailableTokens => _currentTokens;
    }

    /// <summary>
    /// Exponential backoff retry policy
    /// </summary>
    public static class RetryPolicy
    {
        public static async Task<T> ExecuteWithRetryAsync<T>(
            Func<Task<T>> action,
            int maxRetries = 3,
            int baseDelayMs = 100,
            int maxDelayMs = 5000,
            CancellationToken cancellationToken = default)
        {
            int attempt = 0;
            Exception lastException = null;

            while (attempt < maxRetries)
            {
                try
                {
                    return await action();
                }
                catch (Exception ex) when (IsTransientError(ex))
                {
                    lastException = ex;
                    attempt++;

                    if (attempt >= maxRetries)
                        break;

                    // Calculate delay with exponential backoff and jitter
                    int delay = Math.Min(baseDelayMs * (int)Math.Pow(2, attempt), maxDelayMs);
                    delay += Random.Shared.Next(0, delay / 2); // Add jitter

                    await Task.Delay(delay, cancellationToken);
                }
            }

            throw new RetryExhaustedException($"Operation failed after {maxRetries} retries", lastException);
        }

        private static bool IsTransientError(Exception ex)
        {
            return ex is HttpRequestException ||
                   ex is TaskCanceledException ||
                   ex is TimeoutException ||
                   (ex is WebException webEx && 
                    (webEx.Status == WebExceptionStatus.Timeout ||
                     webEx.Status == WebExceptionStatus.ConnectFailure));
        }
    }

    public class RetryExhaustedException : Exception
    {
        public RetryExhaustedException(string message, Exception inner) : base(message, inner) { }
    }
}
