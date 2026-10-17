using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RestSharp;

namespace WalletRecoveryTool.Core;

public class BalanceChecker
{
	/// <summary>
	/// Safely truncates an address for logging (avoids ArgumentOutOfRangeException)
	/// </summary>
	private static string TruncateAddress(string address, int length = 10)
	{
		if (string.IsNullOrEmpty(address)) return "???";
		return address.Length <= length ? address : address.Substring(0, length) + "...";
	}

	private static string TruncateResponse(string? response, int length = 200)
	{
		if (string.IsNullOrWhiteSpace(response)) return "<empty>";
		return response.Length <= length ? response : response.Substring(0, length) + "...";
	}

	private static DateTime _lastNetworkCheck = DateTime.MinValue;

	private static bool _lastNetworkStatus = true;

	private static readonly TimeSpan _networkCheckInterval = TimeSpan.FromMinutes(5.0);

	private static readonly HttpClient _sharedHttpClient = new HttpClient
	{
		Timeout = TimeSpan.FromSeconds(10.0)
	};

	private static readonly RestClient _blockstreamClient = new RestClient(new RestClientOptions("https://blockstream.info")
	{
		Timeout = TimeSpan.FromSeconds(2.0)  // Fast-fail: Blockstream is often blocked; fail quickly to reach Mempool
	});

	private static readonly RestClient _blockcypherClient = new RestClient(new RestClientOptions("https://api.blockcypher.com")
	{
		Timeout = TimeSpan.FromSeconds(10.0)
	});

	private static readonly RestClient _mempoolClient = new RestClient(new RestClientOptions("https://mempool.space")
	{
		Timeout = TimeSpan.FromSeconds(10.0)
	});

	private static readonly RestClient _blockchainInfoClient = new RestClient(new RestClientOptions("https://blockchain.info")
	{
		Timeout = TimeSpan.FromSeconds(10.0)
	});

	private static readonly RestClient _etherscanClient = new RestClient(new RestClientOptions("https://api.etherscan.io/v2/api")
	{
		Timeout = TimeSpan.FromSeconds(10.0)
	});

	private static readonly object _solanaClientLock = new object();
	private static RestClient _solanaClient;

	private static readonly RestClient _trongridClient = new RestClient(new RestClientOptions("https://api.trongrid.io")
	{
		Timeout = TimeSpan.FromSeconds(10.0)
	});

	private static readonly RestClient _ankrEthClient = new RestClient(new RestClientOptions("https://rpc.ankr.com/eth")
	{
		Timeout = TimeSpan.FromSeconds(10.0)
	});

	private static readonly RestClient _llamaEthClient = new RestClient(new RestClientOptions("https://eth.llamarpc.com")
	{
		Timeout = TimeSpan.FromSeconds(10.0)
	});

	private static readonly RestClient _publicNodeEthClient = new RestClient(new RestClientOptions("https://ethereum-rpc.publicnode.com")
	{
		Timeout = TimeSpan.FromSeconds(10.0)
	});

	private static DateTime _blockcypherCooldownUntil = DateTime.MinValue;

	private static DateTime _blockstreamCooldownUntil = DateTime.MinValue;

	private static DateTime _mempoolCooldownUntil = DateTime.MinValue;

	private static DateTime _blockchainInfoCooldownUntil = DateTime.MinValue;

	private static DateTime _etherscanCooldownUntil = DateTime.MinValue;

	private static int _blockcypherBackoffLevel = 0;

	private static int _blockstreamBackoffLevel = 0;

	private static int _mempoolBackoffLevel = 0;

	private static int _blockchainInfoBackoffLevel = 0;

	private static int _etherscanBackoffLevel = 0;

	private static DateTime _blockchairCooldownUntil = DateTime.MinValue;
	private static DateTime _bitapsCooldownUntil = DateTime.MinValue;
	private static DateTime _btccomCooldownUntil = DateTime.MinValue;
	private static int _blockchairBackoffLevel = 0;
	private static int _bitapsBackoffLevel = 0;
	private static int _btccomBackoffLevel = 0;

	private static readonly int[] BackoffSeconds = new int[7] { 5, 10, 20, 45, 90, 180, 300 };

	/// <summary>
	/// Reset all provider cooldowns and backoff levels. Call at the start of each recovery session
	/// to prevent stale backoff state from previous sessions blocking API calls.
	/// </summary>
	public static void ResetAllProviderCooldowns()
	{
		_blockcypherCooldownUntil = DateTime.MinValue;
		_blockstreamCooldownUntil = DateTime.MinValue;
		_mempoolCooldownUntil = DateTime.MinValue;
		_blockchainInfoCooldownUntil = DateTime.MinValue;
		_etherscanCooldownUntil = DateTime.MinValue;
		_blockchairCooldownUntil = DateTime.MinValue;
		_bitapsCooldownUntil = DateTime.MinValue;
		_btccomCooldownUntil = DateTime.MinValue;
		_blockcypherBackoffLevel = 0;
		_blockstreamBackoffLevel = 0;
		_mempoolBackoffLevel = 0;
		_blockchainInfoBackoffLevel = 0;
		_etherscanBackoffLevel = 0;
		_blockchairBackoffLevel = 0;
		_bitapsBackoffLevel = 0;
		_btccomBackoffLevel = 0;
		DebugLogger.Log("[BALANCE] All provider cooldowns and backoff levels reset for new session");
	}

	private static readonly ConcurrentDictionary<string, (decimal balance, DateTime checkedAt)> _balanceCache = new ConcurrentDictionary<string, (decimal, DateTime)>();
	private static readonly ConcurrentDictionary<string, Task<decimal>> _inFlightBalanceRequests = new ConcurrentDictionary<string, Task<decimal>>();

	private static TimeSpan GetCacheExpiry()
	{
		var settings = AppSettings.Current;
		return settings.EnableBalanceCache ? TimeSpan.FromMinutes(settings.CacheExpiryMinutes) : TimeSpan.Zero;
	}

	private const int MaxCacheSize = 10000;

	private static SemaphoreSlim _apiThrottle = CreateApiThrottle();

	private static readonly SemaphoreSlim _ethThrottle = new SemaphoreSlim(10, 10);

	private static DateTime _lastApiCall = DateTime.MinValue;

	private static readonly object _apiLock = new object();

	private static DateTime _lastEtherscanCall = DateTime.MinValue;

	private static DateTime _lastAnkrCall = DateTime.MinValue;

	private static DateTime _lastLlamaCall = DateTime.MinValue;

	private static DateTime _lastPublicNodeCall = DateTime.MinValue;

	private static readonly object _etherscanLock = new object();

	private static readonly object _ankrLock = new object();

	private static readonly object _llamaLock = new object();

	private static readonly object _publicNodeLock = new object();

	private static long _ethRoundRobin = 0L;

	private const int MaxRetries = 3;

	private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500.0);

	private static CancellationToken _globalCancellationToken = CancellationToken.None;

	private readonly AppSettings _customSettings;
	private AppSettings _settings => _customSettings ?? AppSettings.Current;

	private static long _apiQuotaSaved = 0;
	public static long ApiQuotaSavedCount => Interlocked.Read(ref _apiQuotaSaved);
	public static void RecordQuotaSaved(int count = 1)
	{
		if (count > 0) Interlocked.Add(ref _apiQuotaSaved, count);
	}

	public string GetAlchemyEthRpcUrl()
	{
		var key = _settings?.AlchemyApiKey?.Trim();
		if (string.IsNullOrEmpty(key)) return null;
		if (key.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
		    key.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
			return key;
		return $"https://eth-mainnet.g.alchemy.com/v2/{key}";
	}

	public string GetQuickNodeSolanaRpcUrl()
	{
		var key = _settings?.QuickNodeApiKey?.Trim();
		if (string.IsNullOrEmpty(key)) return null;
		if (key.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
		    key.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
			return key;
		return $"https://orbital-proud-tree.solana-mainnet.quiknode.pro/{key}/";
	}

	private const int MinApiDelayMs = 250;

	private static readonly RestClient _polygonscanClient = new RestClient(new RestClientOptions("https://api.polygonscan.com")
	{
		Timeout = TimeSpan.FromSeconds(10.0)
	});

	private static readonly RestClient _bscscanClient = new RestClient(new RestClientOptions("https://api.bscscan.com")
	{
		Timeout = TimeSpan.FromSeconds(10.0)
	});

	private static readonly RestClient _blockchairClient = new RestClient(new RestClientOptions("https://api.blockchair.com")
	{
		Timeout = TimeSpan.FromSeconds(10.0)
	});

	private static readonly RestClient _bitapsClient = new RestClient(new RestClientOptions("https://api.bitaps.com")
	{
		Timeout = TimeSpan.FromSeconds(10.0)
	});

	private static readonly RestClient _btccomClient = new RestClient(new RestClientOptions("https://chain.api.btc.com")
	{
		Timeout = TimeSpan.FromSeconds(10.0)
	});

	public static event EventHandler<string> ApiCallMade;

	private static bool IsProviderAvailable(DateTime cooldownUntil)
	{
		return DateTime.Now >= cooldownUntil;
	}

	/// <summary>
	/// Check provider availability and auto-reset backoff level when cooldown has expired.
	/// This prevents the death spiral where backoff stays at max forever.
	/// </summary>
	private static bool IsProviderAvailableWithReset(DateTime cooldownUntil, ref int backoffLevel)
	{
		if (DateTime.Now >= cooldownUntil)
		{
			// Provider cooldown expired - reduce backoff level so it doesn't stay at max
			if (backoffLevel > 1)
				backoffLevel = Math.Max(0, backoffLevel - 2); // Step down 2 levels after serving cooldown
			return true;
		}
		return false;
	}

	private static void SetProviderCooldown(ref DateTime cooldownUntil, ref int backoffLevel, string providerName)
	{
		int num = Math.Min(backoffLevel, BackoffSeconds.Length - 1);
		int num2 = BackoffSeconds[num];
		cooldownUntil = DateTime.Now.AddSeconds(num2);
		backoffLevel++;
		DebugLogger.Log($"{providerName} rate limited - cooldown {num2}s (backoff level {num})");
	}

	private static void ResetProviderBackoff(ref int backoffLevel)
	{
		backoffLevel = 0;
	}

	private static SemaphoreSlim CreateApiThrottle()
	{
		int maxConcurrent = Math.Clamp(AppSettings.Current.MaxConcurrentApiCalls, 5, 200);
		return new SemaphoreSlim(maxConcurrent, maxConcurrent);
	}

	/// <summary>
	/// Dynamically scale API throttle capacity based on enabled coin count.
	/// Called at scan start — fewer coins = more concurrent wallet checks fit within same API budget.
	/// </summary>
	public static void ReconfigureApiThrottle(int maxConcurrent)
	{
		maxConcurrent = Math.Clamp(maxConcurrent, 5, 500);
		_apiThrottle = new SemaphoreSlim(maxConcurrent, maxConcurrent);
		DebugLogger.Log($"[API] Throttle reconfigured: {maxConcurrent} concurrent slots");
	}

	public static void SetGlobalCancellationToken(CancellationToken token)
	{
		_globalCancellationToken = token;
	}

	/// <summary>
	/// Returns aggregate API health: "Healthy", "Degraded", or "Down".
	/// Based on how many BTC + ETH providers are currently on cooldown.
	/// </summary>
	public static ApiHealthStatus GetApiHealthStatus()
	{
		var now = DateTime.Now;
		int btcTotal = 7; // blockcypher, blockstream, mempool, blockchain.info, blockchair, bitaps, btccom
		int btcDown = 0;
		if (now < _blockcypherCooldownUntil) btcDown++;
		if (now < _blockstreamCooldownUntil) btcDown++;
		if (now < _mempoolCooldownUntil) btcDown++;
		if (now < _blockchainInfoCooldownUntil) btcDown++;
		if (now < _blockchairCooldownUntil) btcDown++;
		if (now < _bitapsCooldownUntil) btcDown++;
		if (now < _btccomCooldownUntil) btcDown++;

		int ethTotal = 1; // etherscan (main throttled provider)
		int ethDown = 0;
		if (now < _etherscanCooldownUntil) ethDown++;

		int totalProviders = btcTotal + ethTotal;
		int totalDown = btcDown + ethDown;

		return new ApiHealthStatus
		{
			TotalProviders = totalProviders,
			ProvidersDown = totalDown,
			BtcProvidersDown = btcDown,
			EthProvidersDown = ethDown,
			Status = totalDown == 0 ? "Healthy"
			       : totalDown <= 3 ? "Degraded"
			       : "Down"
		};
	}

	public BalanceChecker()
	{
		_customSettings = null;
		InitializeSolanaClient();
	}

	public BalanceChecker(AppSettings settings)
	{
		_customSettings = settings;
		InitializeSolanaClient();
	}

	private static void AddToCache(string key, decimal balance)
	{
		var cacheExpiry = GetCacheExpiry();
		if (cacheExpiry == TimeSpan.Zero) return; // Cache disabled
		
		if (_balanceCache.Count >= 10000)
		{
			DateTime now = DateTime.Now;
			int num = 0;
			(decimal, DateTime) value;
			foreach (KeyValuePair<string, (decimal, DateTime)> item in _balanceCache)
			{
				if (now - item.Value.Item2 > cacheExpiry && _balanceCache.TryRemove(item.Key, out value))
				{
					num++;
				}
			}
			if (num == 0 && _balanceCache.Count >= 10000)
			{
				int num2 = 2000;
				List<(string, DateTime)> list = new List<(string, DateTime)>(num2 + 1);
				foreach (KeyValuePair<string, (decimal, DateTime)> item2 in _balanceCache)
				{
					list.Add((item2.Key, item2.Value.Item2));
					if (list.Count > num2 * 2)
					{
						list.Sort(((string key, DateTime time) a, (string key, DateTime time) b) => (!(a.time == b.time)) ? a.time.CompareTo(b.time) : string.Compare(a.key, b.key, StringComparison.Ordinal));
						list.RemoveRange(num2, list.Count - num2);
					}
				}
				list.Sort(((string key, DateTime time) a, (string key, DateTime time) b) => (!(a.time == b.time)) ? a.time.CompareTo(b.time) : string.Compare(a.key, b.key, StringComparison.Ordinal));
				for (int num3 = 0; num3 < Math.Min(num2, list.Count); num3++)
				{
					_balanceCache.TryRemove(list[num3].Item1, out value);
				}
			}
		}
		_balanceCache[key] = (balance, DateTime.Now);
	}

	private async Task<decimal> GetBalanceWithCoalescing(string cacheKey, Func<Task<decimal>> fetcher)
	{
		var cacheExpiry = GetCacheExpiry();
		if (cacheExpiry > TimeSpan.Zero && _balanceCache.TryGetValue(cacheKey, out (decimal, DateTime) cachedValue) && DateTime.Now - cachedValue.Item2 < cacheExpiry)
		{
			return cachedValue.Item1;
		}

		var pendingRequest = _inFlightBalanceRequests.GetOrAdd(cacheKey, _ => fetcher());
		try
		{
			return await pendingRequest.ConfigureAwait(false);
		}
		finally
		{
			_inFlightBalanceRequests.TryRemove(cacheKey, out _);
		}
	}

	private void InitializeSolanaClient()
	{
		if (_solanaClient == null)
		{
			lock (_solanaClientLock)
			{
				if (_solanaClient != null) return; // double-checked locking
				if (!string.IsNullOrEmpty(_settings.LocalSolanaRpcUrl))
				{
					_solanaClient = new RestClient(new RestClientOptions(_settings.LocalSolanaRpcUrl)
					{
						Timeout = TimeSpan.FromSeconds(15.0)
					});
				}
				else if (!string.IsNullOrEmpty(_settings.QuickNodeApiKey))
				{
					_solanaClient = new RestClient(new RestClientOptions("https://orbital-proud-tree.solana-mainnet.quiknode.pro/" + _settings.QuickNodeApiKey + "/")
					{
						Timeout = TimeSpan.FromSeconds(10.0)
					});
				}
				else
				{
					_solanaClient = new RestClient(new RestClientOptions("https://api.mainnet-beta.solana.com")
					{
						Timeout = TimeSpan.FromSeconds(15.0)
					});
				}
			}
		}
	}

	private bool HasConfiguredLocalNode() => _settings.UseLocalNodeForBalances && (!string.IsNullOrWhiteSpace(_settings.LocalBitcoinRpcUrl) || !string.IsNullOrWhiteSpace(_settings.LocalEthereumRpcUrl) || !string.IsNullOrWhiteSpace(_settings.LocalSolanaRpcUrl));

	private async Task<decimal?> TryGetBitcoinBalanceFromLocalNode(string address, DateTime startTime)
	{
		if (!_settings.UseLocalNodeForBalances || string.IsNullOrWhiteSpace(_settings.LocalBitcoinRpcUrl))
		{
			return null;
		}
		try
		{
			var localClient = new RestClient(new RestClientOptions(_settings.LocalBitcoinRpcUrl)
			{
				Timeout = TimeSpan.FromSeconds(10.0)
			});
			var request = new RestRequest("/", Method.Post);
			request.AddHeader("Content-Type", "application/json");
			if (!string.IsNullOrWhiteSpace(_settings.LocalBitcoinRpcUser) || !string.IsNullOrWhiteSpace(_settings.LocalBitcoinRpcPassword))
			{
				var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_settings.LocalBitcoinRpcUser}:{_settings.LocalBitcoinRpcPassword}"));
				request.AddHeader("Authorization", "Basic " + auth);
			}
			request.AddJsonBody(new
			{
				jsonrpc = "2.0",
				method = "scantxoutset",
				@params = new object[] { "start", new[] { $"addr({address})" } },
				id = 1
			});
			var response = await localClient.ExecuteAsync(request, _globalCancellationToken);
			if (response.IsSuccessful && !string.IsNullOrWhiteSpace(response.Content))
			{
				var json = JObject.Parse(response.Content);
				var totalAmountToken = json["result"]?["total_amount"];
				if (totalAmountToken != null)
				{
					var balance = totalAmountToken.Value<decimal>() / 100000000m;
					DebugLogger.LogApi("LocalBitcoinRPC", "BTC/" + TruncateAddress(address), success: true, (DateTime.Now - startTime).TotalMilliseconds, $"Balance: {balance}");
					DebugLogger.LogBalanceCheck(address, "BTC", balance, (DateTime.Now - startTime).TotalMilliseconds);
					AddToCache("BTC:" + address, balance);
					return balance;
				}
				DebugLogger.Log($"Local Bitcoin RPC response did not contain total_amount for {address}: {TruncateResponse(response.Content)}");
			}
			else
			{
				DebugLogger.Log($"Local Bitcoin RPC failed: Status={response?.StatusCode} Error={response?.ErrorMessage} Content={TruncateResponse(response?.Content)}");
			}
		}
		catch (Exception ex)
		{
			DebugLogger.Log("Local Bitcoin RPC balance lookup failed: " + ex.Message);
		}
		return null;
	}

	private async Task<decimal?> TryGetEthereumBalanceFromLocalNode(string address, DateTime startTime)
	{
		if (!_settings.UseLocalNodeForBalances || string.IsNullOrWhiteSpace(_settings.LocalEthereumRpcUrl))
		{
			return null;
		}
		try
		{
			var localClient = new RestClient(new RestClientOptions(_settings.LocalEthereumRpcUrl)
			{
				Timeout = TimeSpan.FromSeconds(10.0)
			});
			var request = new RestRequest("/", Method.Post);
			request.AddHeader("Content-Type", "application/json");
			if (!string.IsNullOrWhiteSpace(_settings.LocalEthereumRpcUser) || !string.IsNullOrWhiteSpace(_settings.LocalEthereumRpcPassword))
			{
				var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_settings.LocalEthereumRpcUser}:{_settings.LocalEthereumRpcPassword}"));
				request.AddHeader("Authorization", "Basic " + auth);
			}
			request.AddJsonBody(new
			{
				jsonrpc = "2.0",
				method = "eth_getBalance",
				@params = new object[] { address, "latest" },
				id = 1
			});
			var response = await localClient.ExecuteAsync(request, _globalCancellationToken);
			if (response.IsSuccessful && !string.IsNullOrWhiteSpace(response.Content))
			{
				var json = JObject.Parse(response.Content);
				var result = json["result"]?.Value<string>();
				if (!string.IsNullOrWhiteSpace(result) && result != "0x")
				{
					var hexValue = result.StartsWith("0x") ? result.Substring(2) : result;
					var balance = (decimal)BigInteger.Parse("0" + hexValue, NumberStyles.HexNumber) / 1000000000000000000m;
					DebugLogger.LogApi("LocalEthereumRPC", "ETH/" + TruncateAddress(address), success: true, (DateTime.Now - startTime).TotalMilliseconds, $"Balance: {balance}");
					DebugLogger.LogBalanceCheck(address, "ETH", balance, (DateTime.Now - startTime).TotalMilliseconds);
					AddToCache("ETH:" + address, balance);
					return balance;
				}
				if (result == "0x" || result == "0x0")
				{
					AddToCache("ETH:" + address, 0m);
					return 0m;
				}
				DebugLogger.Log($"Local Ethereum RPC response did not contain valid result for {address}: {TruncateResponse(response.Content)}");
			}
			else
			{
				DebugLogger.Log($"Local Ethereum RPC failed: Status={response?.StatusCode} Error={response?.ErrorMessage} Content={TruncateResponse(response?.Content)}");
			}
		}
		catch (Exception ex)
		{
			DebugLogger.Log("Local Ethereum RPC balance lookup failed: " + ex.Message);
		}
		return null;
	}

	public async Task<decimal> GetBitcoinBalance(string address)
	{
		DateTime startTime = DateTime.Now;
		string balanceKey = "BTC:" + address;
		return await GetBalanceWithCoalescing(balanceKey, async () =>
		{
			var cacheExpiry = GetCacheExpiry();
			if (cacheExpiry > TimeSpan.Zero && _balanceCache.TryGetValue(balanceKey, out (decimal, DateTime) value) && DateTime.Now - value.Item2 < cacheExpiry)
			{
				return value.Item1;
			}
			var localNodeBalance = await TryGetBitcoinBalanceFromLocalNode(address, startTime);
		if (localNodeBalance.HasValue)
		{
			return localNodeBalance.Value;
		}
		if (!IsProviderAvailable(_blockcypherCooldownUntil) && !IsProviderAvailable(_blockstreamCooldownUntil) && !IsProviderAvailable(_blockchainInfoCooldownUntil) && !IsProviderAvailable(_mempoolCooldownUntil) && !IsProviderAvailable(_blockchairCooldownUntil) && !IsProviderAvailable(_bitapsCooldownUntil) && !IsProviderAvailable(_btccomCooldownUntil))
		{
			// All providers on cooldown - wait for the shortest one instead of skipping
			var shortestWait = new[] { _blockcypherCooldownUntil, _blockstreamCooldownUntil, _mempoolCooldownUntil, _blockchainInfoCooldownUntil, _blockchairCooldownUntil, _bitapsCooldownUntil, _btccomCooldownUntil }
				.Min() - DateTime.Now;
			if (shortestWait > TimeSpan.Zero && shortestWait < TimeSpan.FromSeconds(60))
			{
				DebugLogger.Log($"All BTC APIs on cooldown - waiting {shortestWait.TotalSeconds:F0}s for next available");
				try { await Task.Delay(shortestWait, _globalCancellationToken); }
				catch (OperationCanceledException) { return default(decimal); }
			}
			else if (shortestWait >= TimeSpan.FromSeconds(60))
			{
				// Cooldown too long - skip this one
				return default(decimal);
			}
		}
		try
		{
			await _apiThrottle.WaitAsync(_globalCancellationToken);
			try
			{
				if (_globalCancellationToken.IsCancellationRequested)
				{
					return default(decimal);
				}
				await EnforceRateLimit();
				if (IsProviderAvailableWithReset(_blockcypherCooldownUntil, ref _blockcypherBackoffLevel) && !string.IsNullOrEmpty(_settings.BlockCypherApiKey))
				{
					try
					{
						decimal result = await GetBitcoinBalanceBlockCypher(address, startTime);
						ResetProviderBackoff(ref _blockcypherBackoffLevel);
						return result;
					}
					catch (InvalidOperationException ex) when (ex.Message.Contains("rate limit"))
					{
						SetProviderCooldown(ref _blockcypherCooldownUntil, ref _blockcypherBackoffLevel, "BlockCypher");
						BalanceChecker.ApiCallMade?.Invoke(null, "?\ufe0f BTC: BlockCypher rate limited, trying Blockstream...");
					}
					catch (Exception ex2)
					{
						DebugLogger.Log("BlockCypher error: " + ex2.Message + ", trying backup...");
					}
				}
				// Mempool is primary BTC provider (Blockstream is often network-blocked)
				if (IsProviderAvailableWithReset(_mempoolCooldownUntil, ref _mempoolBackoffLevel))
				{
					try
					{
						decimal result3 = await GetBitcoinBalanceMempool(address, startTime);
						ResetProviderBackoff(ref _mempoolBackoffLevel);
						return result3;
					}
					catch (InvalidOperationException ex5) when (ex5.Message.Contains("rate limit"))
					{
						SetProviderCooldown(ref _mempoolCooldownUntil, ref _mempoolBackoffLevel, "Mempool");
						BalanceChecker.ApiCallMade?.Invoke(null, "⚠️ BTC: Mempool rate limited, trying Blockstream...");
					}
					catch (Exception ex6)
					{
						SetProviderCooldown(ref _mempoolCooldownUntil, ref _mempoolBackoffLevel, "Mempool");
						DebugLogger.Log("Mempool error: " + ex6.Message + ", trying Blockstream...");
					}
				}
				// Blockstream as fallback (2s timeout for fast-fail when network-blocked)
				if (IsProviderAvailableWithReset(_blockstreamCooldownUntil, ref _blockstreamBackoffLevel))
				{
					try
					{
						decimal result2 = await GetBitcoinBalanceBlockstream(address, startTime);
						ResetProviderBackoff(ref _blockstreamBackoffLevel);
						return result2;
					}
					catch (InvalidOperationException ex3) when (ex3.Message.Contains("rate limit"))
					{
						SetProviderCooldown(ref _blockstreamCooldownUntil, ref _blockstreamBackoffLevel, "Blockstream");
						BalanceChecker.ApiCallMade?.Invoke(null, "⚠️ BTC: Blockstream rate limited, trying Blockchain.info...");
					}
					catch (Exception ex4)
					{
						SetProviderCooldown(ref _blockstreamCooldownUntil, ref _blockstreamBackoffLevel, "Blockstream");
						DebugLogger.Log("Blockstream error: " + ex4.Message + ", trying Blockchain.info...");
					}
				}
				if (IsProviderAvailableWithReset(_blockchainInfoCooldownUntil, ref _blockchainInfoBackoffLevel))
				{
					try
					{
						decimal result4 = await GetBitcoinBalanceBlockchainInfo(address, startTime);
						ResetProviderBackoff(ref _blockchainInfoBackoffLevel);
						return result4;
					}
					catch (InvalidOperationException ex7) when (ex7.Message.Contains("rate limit"))
					{
						SetProviderCooldown(ref _blockchainInfoCooldownUntil, ref _blockchainInfoBackoffLevel, "Blockchain.info");
						BalanceChecker.ApiCallMade?.Invoke(null, "⚠️ BTC: Blockchain.info rate limited, trying Blockchair...");
					}
					catch (Exception ex8)
					{
						SetProviderCooldown(ref _blockchainInfoCooldownUntil, ref _blockchainInfoBackoffLevel, "Blockchain.info");
						DebugLogger.Log("Blockchain.info error: " + ex8.Message + ", trying Blockchair...");
					}
				}
				if (IsProviderAvailableWithReset(_blockchairCooldownUntil, ref _blockchairBackoffLevel))
				{
					try
					{
						decimal result5 = await GetBitcoinBalanceBlockchair(address, startTime);
						ResetProviderBackoff(ref _blockchairBackoffLevel);
						return result5;
					}
					catch (InvalidOperationException ex9) when (ex9.Message.Contains("rate limit"))
					{
						SetProviderCooldown(ref _blockchairCooldownUntil, ref _blockchairBackoffLevel, "Blockchair");
						BalanceChecker.ApiCallMade?.Invoke(null, "⚠️ BTC: Blockchair rate limited, trying SoChain...");
					}
					catch (Exception ex10)
					{
						SetProviderCooldown(ref _blockchairCooldownUntil, ref _blockchairBackoffLevel, "Blockchair");
						DebugLogger.Log("Blockchair error: " + ex10.Message + ", trying SoChain...");
					}
				}
				if (IsProviderAvailableWithReset(_bitapsCooldownUntil, ref _bitapsBackoffLevel))
				{
					try
					{
						decimal result6 = await GetBitcoinBalanceBitaps(address, startTime);
						ResetProviderBackoff(ref _bitapsBackoffLevel);
						return result6;
					}
					catch (InvalidOperationException ex11) when (ex11.Message.Contains("rate limit"))
					{
						SetProviderCooldown(ref _bitapsCooldownUntil, ref _bitapsBackoffLevel, "Bitaps");
						BalanceChecker.ApiCallMade?.Invoke(null, "⚠️ BTC: Bitaps rate limited, trying BlockCypher-Free...");
					}
					catch (Exception ex12)
					{
						SetProviderCooldown(ref _bitapsCooldownUntil, ref _bitapsBackoffLevel, "Bitaps");
						DebugLogger.Log("Bitaps error: " + ex12.Message + ", trying BlockCypher-Free...");
					}
				}
				if (IsProviderAvailableWithReset(_btccomCooldownUntil, ref _btccomBackoffLevel))
				{
					try
					{
						decimal result7 = await GetBitcoinBalanceBtcCom(address, startTime);
						ResetProviderBackoff(ref _btccomBackoffLevel);
						return result7;
					}
					catch (InvalidOperationException ex13) when (ex13.Message.Contains("rate limit"))
					{
						SetProviderCooldown(ref _btccomCooldownUntil, ref _btccomBackoffLevel, "BlockCypher-Free");
						BalanceChecker.ApiCallMade?.Invoke(null, "⚠️ ALL 7 BTC APIs in cooldown - will retry after backoff period.");
					}
					catch (Exception ex14)
					{
						SetProviderCooldown(ref _btccomCooldownUntil, ref _btccomBackoffLevel, "BlockCypher-Free");
						DebugLogger.Log("BlockCypher-Free error: " + ex14.Message);
					}
				}
				return default(decimal);
			}
			finally
			{
				_apiThrottle.Release();
			}
		}
		catch (OperationCanceledException)
		{
			return default(decimal);
		}
		catch (Exception ex10)
		{
			DebugLogger.LogError("GetBitcoinBalance", ex10);
			throw;
		}
		});
	}

	private async Task<decimal> GetBitcoinBalanceBlockCypher(string address, DateTime startTime)
	{
		BalanceChecker.ApiCallMade?.Invoke(null, "BTC: BlockCypher API (with key)");
		ApiUsageTracker.RecordCall("BlockCypher");
		RestRequest request = new RestRequest("/v1/btc/main/addrs/" + address + "/balance");
		request.AddParameter("token", _settings.BlockCypherApiKey);
		RestResponse restResponse = await _blockcypherClient.ExecuteAsync(request, _globalCancellationToken);
		double totalMilliseconds = (DateTime.Now - startTime).TotalMilliseconds;
		if (restResponse.IsSuccessful)
		{
			dynamic val = JsonConvert.DeserializeObject(restResponse.Content);
			decimal num = (decimal)(long)(val.balance ?? ((object)0)) / 100000000m;
			DebugLogger.LogApi("BlockCypher", "BTC/" + TruncateAddress(address), success: true, totalMilliseconds, $"Balance: {num}");
			DebugLogger.LogBalanceCheck(address, "BTC", num, totalMilliseconds);
			AddToCache("BTC:" + address, num);
			return num;
		}
		if (restResponse.StatusCode == HttpStatusCode.TooManyRequests || (restResponse.Content?.Contains("rate limit") ?? false))
		{
			DebugLogger.LogApi("BlockCypher", "BTC/" + TruncateAddress(address), success: false, totalMilliseconds, "RATE LIMITED!");
			throw new InvalidOperationException("BlockCypher rate limit reached");
		}
		throw new InvalidOperationException($"BlockCypher API error: {restResponse.StatusCode}");
	}

	private async Task<decimal> GetBitcoinBalanceBlockstream(string address, DateTime startTime)
	{
		BalanceChecker.ApiCallMade?.Invoke(null, "BTC: Blockstream API (free backup)");
		ApiUsageTracker.RecordCall("Blockstream");
		RestRequest request = new RestRequest("/api/address/" + address);
		RestResponse restResponse = await _blockstreamClient.ExecuteAsync(request, _globalCancellationToken);
		double totalMilliseconds = (DateTime.Now - startTime).TotalMilliseconds;
		if (restResponse.IsSuccessful)
		{
			dynamic val = JsonConvert.DeserializeObject(restResponse.Content);
			object obj = val.chain_stats;
			object obj2 = val.mempool_stats;
			long num = (long)(((dynamic)obj)?.funded_txo_sum ?? ((object)0));
			long num2 = (long)(((dynamic)obj)?.spent_txo_sum ?? ((object)0));
			long num3 = (long)(((dynamic)obj2)?.funded_txo_sum ?? ((object)0));
			long num4 = (long)(((dynamic)obj2)?.spent_txo_sum ?? ((object)0));
			decimal num5 = (decimal)(num - num2 + (num3 - num4)) / 100000000m;
			DebugLogger.LogApi("Blockstream", "BTC/" + TruncateAddress(address), success: true, totalMilliseconds, $"Balance: {num5}");
			DebugLogger.LogBalanceCheck(address, "BTC", num5, totalMilliseconds);
			_balanceCache["BTC:" + address] = (num5, DateTime.Now);
			return num5;
		}
		if (restResponse.StatusCode == HttpStatusCode.TooManyRequests)
		{
			throw new InvalidOperationException("Blockstream rate limit reached");
		}
		throw new InvalidOperationException($"Blockstream API error: {restResponse.StatusCode}");
	}

	private async Task<decimal> GetBitcoinBalanceMempool_Internal(string address, DateTime startTime)
	{
		BalanceChecker.ApiCallMade?.Invoke(null, "BTC: Mempool.space API (free backup)");
		ApiUsageTracker.RecordCall("Mempool");
		RestRequest request = new RestRequest("/api/address/" + address);
		RestResponse restResponse = await _mempoolClient.ExecuteAsync(request, _globalCancellationToken);
		double totalMilliseconds = (DateTime.Now - startTime).TotalMilliseconds;
		if (restResponse.IsSuccessful)
		{
			dynamic val = JsonConvert.DeserializeObject(restResponse.Content);
			object obj = val.chain_stats;
			object obj2 = val.mempool_stats;
			long num = (long)(((dynamic)obj)?.funded_txo_sum ?? ((object)0));
			long num2 = (long)(((dynamic)obj)?.spent_txo_sum ?? ((object)0));
			long num3 = (long)(((dynamic)obj2)?.funded_txo_sum ?? ((object)0));
			long num4 = (long)(((dynamic)obj2)?.spent_txo_sum ?? ((object)0));
			decimal num5 = (decimal)(num - num2 + (num3 - num4)) / 100000000m;
			DebugLogger.LogApi("Mempool", "BTC/" + TruncateAddress(address), success: true, totalMilliseconds, $"Balance: {num5}");
			DebugLogger.LogBalanceCheck(address, "BTC", num5, totalMilliseconds);
			_balanceCache["BTC:" + address] = (num5, DateTime.Now);
			return num5;
		}
		if (restResponse.StatusCode == HttpStatusCode.TooManyRequests)
		{
			throw new InvalidOperationException("Mempool rate limit reached");
		}
		throw new InvalidOperationException($"Mempool API error: {restResponse.StatusCode}");
	}

	private async Task<decimal> GetBitcoinBalanceMempool(string address, DateTime startTime)
	{
		return await GetBitcoinBalanceMempool_Internal(address, startTime);
	}

	private async Task<decimal> GetBitcoinBalanceBlockchainInfo(string address, DateTime startTime)
	{
		BalanceChecker.ApiCallMade?.Invoke(null, "BTC: Blockchain.info API (free backup)");
		ApiUsageTracker.RecordCall("Blockchain.info");
		RestRequest request = new RestRequest("/rawaddr/" + address);
		request.AddParameter("limit", "0");
		RestResponse restResponse = await _blockchainInfoClient.ExecuteAsync(request, _globalCancellationToken);
		double totalMilliseconds = (DateTime.Now - startTime).TotalMilliseconds;
		if (restResponse.IsSuccessful)
		{
			dynamic val = JsonConvert.DeserializeObject(restResponse.Content);
			decimal num = (decimal)(long)(val.final_balance ?? ((object)0)) / 100000000m;
			DebugLogger.LogApi("Blockchain.info", "BTC/" + TruncateAddress(address), success: true, totalMilliseconds, $"Balance: {num}");
			DebugLogger.LogBalanceCheck(address, "BTC", num, totalMilliseconds);
			_balanceCache["BTC:" + address] = (num, DateTime.Now);
			return num;
		}
		if (restResponse.StatusCode == HttpStatusCode.TooManyRequests)
		{
			DebugLogger.LogApi("Blockchain.info", "BTC/" + TruncateAddress(address), success: false, totalMilliseconds, "RATE LIMITED!");
			throw new InvalidOperationException("Blockchain.info rate limit reached");
		}
		throw new InvalidOperationException($"Blockchain.info API error: {restResponse.StatusCode}");
	}

	private async Task<decimal> GetBitcoinBalanceBlockchair(string address, DateTime startTime)
	{
		BalanceChecker.ApiCallMade?.Invoke(null, "BTC: Blockchair API (free)");
		ApiUsageTracker.RecordCall("Blockchair");
		RestRequest request = new RestRequest($"/bitcoin/dashboards/address/{address}");
		RestResponse restResponse = await _blockchairClient.ExecuteAsync(request, _globalCancellationToken);
		double totalMilliseconds = (DateTime.Now - startTime).TotalMilliseconds;
		if (restResponse.IsSuccessful)
		{
			dynamic val = JsonConvert.DeserializeObject(restResponse.Content);
			var data = val?.data;
			if (data != null)
			{
				var addrData = data[address];
				if (addrData != null)
				{
					long balanceSatoshis = (long)(addrData.address?.balance ?? 0);
					decimal balance = balanceSatoshis / 100000000m;
					DebugLogger.LogApi("Blockchair", "BTC/" + TruncateAddress(address), success: true, totalMilliseconds, $"Balance: {balance}");
					DebugLogger.LogBalanceCheck(address, "BTC", balance, totalMilliseconds);
					_balanceCache["BTC:" + address] = (balance, DateTime.Now);
					return balance;
				}
			}
			throw new InvalidOperationException("Blockchair returned empty data");
		}
		if (restResponse.StatusCode == HttpStatusCode.TooManyRequests || restResponse.StatusCode == (HttpStatusCode)402 || restResponse.StatusCode == (HttpStatusCode)430)
		{
			DebugLogger.LogApi("Blockchair", "BTC/" + TruncateAddress(address), success: false, totalMilliseconds, "RATE LIMITED!");
			throw new InvalidOperationException("Blockchair rate limit reached");
		}
		throw new InvalidOperationException($"Blockchair API error: {restResponse.StatusCode}");
	}

	private async Task<decimal> GetBitcoinBalanceBitaps(string address, DateTime startTime)
	{
		BalanceChecker.ApiCallMade?.Invoke(null, "BTC: Bitaps API (free)");
		ApiUsageTracker.RecordCall("Bitaps");
		RestRequest request = new RestRequest($"/btc/v1/blockchain/address/state/{address}");
		RestResponse restResponse = await _bitapsClient.ExecuteAsync(request, _globalCancellationToken);
		double totalMilliseconds = (DateTime.Now - startTime).TotalMilliseconds;
		if (restResponse.IsSuccessful)
		{
			dynamic val = JsonConvert.DeserializeObject(restResponse.Content);
			if (val != null)
			{
				long balanceSatoshis = (long)(val.balance ?? 0);
				decimal balance = balanceSatoshis / 100000000m;
				DebugLogger.LogApi("Bitaps", "BTC/" + TruncateAddress(address), success: true, totalMilliseconds, $"Balance: {balance}");
				DebugLogger.LogBalanceCheck(address, "BTC", balance, totalMilliseconds);
				_balanceCache["BTC:" + address] = (balance, DateTime.Now);
				return balance;
			}
			throw new InvalidOperationException("Bitaps returned empty data");
		}
		if (restResponse.StatusCode == HttpStatusCode.TooManyRequests || restResponse.StatusCode == HttpStatusCode.Forbidden)
		{
			DebugLogger.LogApi("Bitaps", "BTC/" + TruncateAddress(address), success: false, totalMilliseconds, "RATE LIMITED!");
			throw new InvalidOperationException("Bitaps rate limit reached");
		}
		throw new InvalidOperationException($"Bitaps API error: {restResponse.StatusCode}");
	}

	private async Task<decimal> GetBitcoinBalanceBtcCom(string address, DateTime startTime)
	{
		// BTC.com API - separate provider from BlockCypher
		BalanceChecker.ApiCallMade?.Invoke(null, "BTC: BTC.com API");
		ApiUsageTracker.RecordCall("BTC.com");
		RestRequest request = new RestRequest($"/v3/address/{address}");
		RestResponse restResponse = await _btccomClient.ExecuteAsync(request, _globalCancellationToken);
		double totalMilliseconds = (DateTime.Now - startTime).TotalMilliseconds;
		if (restResponse.IsSuccessful && !string.IsNullOrWhiteSpace(restResponse.Content))
		{
			dynamic val = JsonConvert.DeserializeObject(restResponse.Content);
			if (val?.data != null)
			{
				long balanceSatoshis = (long)(val.data.balance ?? 0);
				decimal balance = balanceSatoshis / 100000000m;
				DebugLogger.LogApi("BTC.com", "BTC/" + TruncateAddress(address), success: true, totalMilliseconds, $"Balance: {balance}");
				DebugLogger.LogBalanceCheck(address, "BTC", balance, totalMilliseconds);
				_balanceCache["BTC:" + address] = (balance, DateTime.Now);
				return balance;
			}
			throw new InvalidOperationException("BTC.com returned empty data");
		}
		if (restResponse.StatusCode == HttpStatusCode.TooManyRequests)
		{
			DebugLogger.LogApi("BTC.com", "BTC/" + TruncateAddress(address), success: false, totalMilliseconds, "RATE LIMITED!");
			throw new InvalidOperationException("BTC.com rate limit reached");
		}
		throw new InvalidOperationException($"BTC.com API error: {restResponse.StatusCode}");
	}

	public async Task<Dictionary<string, decimal>> GetBitcoinBalancesBatch(List<string> addresses)
	{
		Dictionary<string, decimal> results = new Dictionary<string, decimal>();
		if (addresses == null || addresses.Count == 0)
		{
			return results;
		}
		var cacheExpiry = GetCacheExpiry();
		List<string> list = new List<string>();
		foreach (string address in addresses)
		{
			if (cacheExpiry > TimeSpan.Zero && _balanceCache.TryGetValue("BTC:" + address, out (decimal, DateTime) value) && DateTime.Now - value.Item2 < cacheExpiry)
			{
				(results[address], _) = value;
			}
			else
			{
				list.Add(address);
			}
		}
		if (list.Count == 0)
		{
			return results;
		}
		if (_settings.UseLocalNodeForBalances && !string.IsNullOrWhiteSpace(_settings.LocalBitcoinRpcUrl))
		{
			foreach (string addr in list)
			{
				var localBalance = await TryGetBitcoinBalanceFromLocalNode(addr, DateTime.Now);
				if (localBalance.HasValue)
				{
					results[addr] = localBalance.Value;
				}
				else
				{
					results[addr] = 0m;
				}
			}
			return results;
		}
		List<List<string>> list2 = (from x in list.Select((string addr2, int idx) => new
			{
				addr = addr2,
				idx = idx
			})
			group x by x.idx / 50 into g
			select g.Select(x => x.addr).ToList()).ToList();
		foreach (List<string> batch in list2)
		{
			try
			{
				bool batchSucceeded = false;

				// PRIMARY (ZERO API KEY WASTE): blockchain.info multiaddr (free, no API key needed, 50 addresses in 1 call)
				if (IsProviderAvailable(_blockchainInfoCooldownUntil))
				{
					try
					{
						await _apiThrottle.WaitAsync(_globalCancellationToken);
						try
						{
							await EnforceRateLimit();
							string active = string.Join("|", batch);
							BalanceChecker.ApiCallMade?.Invoke(null, $"BTC: Blockchain.info MULTIADDR ({batch.Count} addresses, 0 API key credits)");
							ApiUsageTracker.RecordCall("Blockchain.info-Batch");
							var req = new RestRequest("/multiaddr");
							req.AddParameter("active", active);
							req.AddParameter("n", "0");
							var resp = await _blockchainInfoClient.ExecuteAsync(req, _globalCancellationToken);
							if (resp.IsSuccessful && !string.IsNullOrWhiteSpace(resp.Content))
							{
								dynamic data = JsonConvert.DeserializeObject(resp.Content);
								if (data?.addresses != null)
								{
									foreach (dynamic addrData in data.addresses)
									{
										string a = (string)addrData.address;
										decimal bal = (decimal)(long)(addrData.final_balance ?? (object)0L) / 100_000_000m;
										results[a] = bal;
										_balanceCache["BTC:" + a] = (bal, DateTime.Now);
									}
									batchSucceeded = true;
									RecordQuotaSaved(batch.Count);
								}
							}
							else if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
							{
								SetProviderCooldown(ref _blockchainInfoCooldownUntil, ref _blockchainInfoBackoffLevel, "Blockchain.info-Batch");
							}
						}
						finally { _apiThrottle.Release(); }
					}
					catch (Exception ex) { DebugLogger.Log($"Blockchain.info multiaddr error: {ex.Message}"); }
				}

				if (batchSucceeded)
				{
					continue;
				}

				// SECONDARY (PROTECTED API KEY FALLBACK): BlockCypher batch only when free multiaddr is on cooldown and quota permits
				if (!batchSucceeded &&
				    !string.IsNullOrEmpty(_settings.BlockCypherApiKey) &&
				    IsProviderAvailableWithReset(_blockcypherCooldownUntil, ref _blockcypherBackoffLevel) &&
				    ApiUsageTracker.CanMakeApiCall("BlockCypher"))
				{
					foreach (var subBatch in batch.Chunk(15))
					{
						await _apiThrottle.WaitAsync(_globalCancellationToken);
						try
						{
							await EnforceRateLimit();
							string text = string.Join(";", subBatch);
							BalanceChecker.ApiCallMade?.Invoke(null, $"BTC: BlockCypher Key Fallback ({subBatch.Length} addresses)");
							ApiUsageTracker.RecordCall("BlockCypher");
							RestRequest request = new RestRequest("/v1/btc/main/addrs/" + text + "/balance");
							request.AddParameter("token", _settings.BlockCypherApiKey);
							RestResponse restResponse = await _blockcypherClient.ExecuteAsync(request, _globalCancellationToken);
							if (restResponse.IsSuccessful && !string.IsNullOrWhiteSpace(restResponse.Content))
							{
								if (restResponse.Content.TrimStart().StartsWith("["))
								{
									List<object> list3 = JsonConvert.DeserializeObject<List<object>>(restResponse.Content);
									if (list3 != null)
									{
										foreach (dynamic item2 in list3)
										{
											string text2 = item2.address;
											decimal item = (results[text2] = (decimal)(long)(item2.balance ?? ((object)0)) / 100000000m);
											_balanceCache["BTC:" + text2] = (item, DateTime.Now);
										}
									}
								}
								batchSucceeded = true;
							}
							else if (restResponse.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
							{
								SetProviderCooldown(ref _blockcypherCooldownUntil, ref _blockcypherBackoffLevel, "BlockCypher");
								break;
							}
						}
						finally
						{
							_apiThrottle.Release();
						}
					}
				}
				if (batchSucceeded) continue;
				// Last resort: individual per-address fallback
				foreach (string addr in batch)
				{
					if (!_globalCancellationToken.IsCancellationRequested)
					{
						try
						{
							results[addr] = await GetBitcoinBalance(addr);
						}
						catch
						{
							results[addr] = 0m;
						}
						continue;
					}
					break;
				}
			}
			catch (Exception ex)
			{
				DebugLogger.Log("Batch balance check error: " + ex.Message);
				foreach (string item3 in batch)
				{
					if (!results.ContainsKey(item3))
					{
						results[item3] = 0m;
					}
				}
			}
		}
		return results;
	}

	private async Task EnforceRateLimit()
	{
		int num = (_settings.TurboMode ? 0 : Math.Max(_settings.CheckDelayMs, 0));
		int num2 = 0;
		lock (_apiLock)
		{
			DateTime now = DateTime.Now;
			DateTime dateTime = _lastApiCall.AddMilliseconds(num);
			if (now < dateTime)
			{
				num2 = (int)(dateTime - now).TotalMilliseconds;
			}
			_lastApiCall = now.AddMilliseconds(Math.Max(num2, 0));
		}
		if (num2 > 0)
		{
			await Task.Delay(num2).ConfigureAwait(continueOnCapturedContext: false);
		}
	}

	private async Task EnforceProviderRateLimit(int providerIndex, int minDelayMs)
	{
		object obj = providerIndex switch
		{
			0 => _etherscanLock, 
			1 => _ankrLock, 
			2 => _llamaLock, 
			_ => _publicNodeLock, 
		};
		int num = 0;
		lock (obj)
		{
			DateTime now = DateTime.Now;
			DateTime dateTime = (providerIndex switch
			{
				0 => _lastEtherscanCall, 
				1 => _lastAnkrCall, 
				2 => _lastLlamaCall, 
				_ => _lastPublicNodeCall, 
			}).AddMilliseconds(minDelayMs);
			if (now < dateTime)
			{
				num = (int)(dateTime - now).TotalMilliseconds;
			}
			DateTime dateTime2 = now.AddMilliseconds(Math.Max(num, 0));
			switch (providerIndex)
			{
			case 0:
				_lastEtherscanCall = dateTime2;
				break;
			case 1:
				_lastAnkrCall = dateTime2;
				break;
			case 2:
				_lastLlamaCall = dateTime2;
				break;
			default:
				_lastPublicNodeCall = dateTime2;
				break;
			}
		}
		if (num > 0)
		{
			await Task.Delay(num).ConfigureAwait(continueOnCapturedContext: false);
		}
	}

	private async Task<T> ExecuteWithRetry<T>(Func<Task<T>> apiCall, string apiName)
	{
		Exception lastException = null;
		for (int attempt = 1; attempt <= 3; attempt++)
		{
			try
			{
				return await apiCall();
			}
			catch (HttpRequestException ex) when (attempt < 3)
			{
				lastException = ex;
				int num = (int)(RetryDelay.TotalMilliseconds * Math.Pow(2.0, attempt - 1));
				int num2 = Random.Shared.Next(0, num / 4);
				DebugLogger.Log($"{apiName} attempt {attempt}/{3} failed: {ex.Message}. Retrying in {num + num2}ms...");
				await Task.Delay(num + num2);
			}
			catch (TaskCanceledException ex2) when (attempt < 3)
			{
				lastException = ex2;
				int num3 = (int)(RetryDelay.TotalMilliseconds * Math.Pow(2.0, attempt - 1));
				int num4 = Random.Shared.Next(0, num3 / 4);
				DebugLogger.Log($"{apiName} timeout on attempt {attempt}/{3}. Retrying in {num3 + num4}ms...");
				await Task.Delay(num3 + num4);
			}
		}
		throw lastException ?? new Exception($"{apiName} failed after {3} attempts");
	}

	public async Task<decimal> GetEthereumBalance(string address)
	{
		DateTime startTime = DateTime.Now;
		string balanceKey = "ETH:" + address;
		return await GetBalanceWithCoalescing(balanceKey, async () =>
		{
			await EnsureNetworkConnectivity();
			var cacheExpiry = GetCacheExpiry();
			if (cacheExpiry > TimeSpan.Zero && _balanceCache.TryGetValue(balanceKey, out (decimal, DateTime) value) && DateTime.Now - value.Item2 < cacheExpiry)
			{
				DebugLogger.Log("[ETH] Cache hit for " + TruncateAddress(address));
				return value.Item1;
			}
			var localNodeBalance = await TryGetEthereumBalanceFromLocalNode(address, startTime);
			if (localNodeBalance.HasValue)
			{
				return localNodeBalance.Value;
			}
			try
			{
				await _ethThrottle.WaitAsync(_globalCancellationToken);
				try
				{
					if (_globalCancellationToken.IsCancellationRequested)
					{
						return default(decimal);
					}
					int providerCount = 5;
					int startIdx = (int)(Interlocked.Increment(ref _ethRoundRobin) % providerCount);
					for (int attempt = 0; attempt < providerCount; attempt++)
					{
						int num = (startIdx + attempt) % providerCount;
						try
						{
							decimal? num2 = null;
							switch (num)
							{
							case 0:
								num2 = await GetEthBalanceSingleRpc(_publicNodeEthClient, "PublicNode", address, 3);
								if (num2.HasValue) RecordQuotaSaved(1);
								break;
							case 1:
								num2 = await GetEthBalanceSingleRpc(_llamaEthClient, "LlamaRPC", address, 2);
								if (num2.HasValue) RecordQuotaSaved(1);
								break;
							case 2:
								num2 = await GetEthBalanceSingleRpc(_ankrEthClient, "Ankr", address, 1);
								if (num2.HasValue) RecordQuotaSaved(1);
								break;
							case 3:
							{
								var alchemyRpc = GetAlchemyEthRpcUrl();
								if (!string.IsNullOrEmpty(alchemyRpc) && ApiUsageTracker.CanMakeApiCall("Alchemy"))
								{
									var alchemyClient = HttpClientPool.Instance.GetRestClient(alchemyRpc);
									num2 = await GetEthBalanceSingleRpc(alchemyClient, "Alchemy", address, 1);
								}
								break;
							}
							case 4:
								if (!string.IsNullOrEmpty(_settings.EtherscanApiKey) && ApiUsageTracker.CanMakeApiCall("Etherscan"))
								{
									num2 = await GetEthBalanceEtherscan(address, startTime);
								}
								break;
							}
							if (num2.HasValue)
							{
								return num2.Value;
							}
						}
						catch (InvalidOperationException ex_rl) when (ex_rl.Message.Contains("rate limit"))
						{
							DebugLogger.Log($"[ETH] Provider {num} rate limited, trying next...");
							// Continue to next provider in the loop
						}
						catch (InvalidOperationException)
						{
							throw;
						}
						catch
						{
						}
					}
					return default(decimal);
				}
				finally
				{
					_ethThrottle.Release();
				}
			}
			catch (InvalidOperationException ex_rl2) when (ex_rl2.Message.Contains("rate limit"))
			{
				DebugLogger.Log($"[ETH] All providers rate limited for {TruncateAddress(address)}");
				return default(decimal);
			}
			catch (InvalidOperationException)
			{
				throw;
			}
			catch (OperationCanceledException)
			{
				return default(decimal);
			}
			catch (Exception ex4)
			{
				DebugLogger.Log("[ETH] Balance check error for " + TruncateAddress(address) + ": " + ex4.Message);
				return default(decimal);
			}
		});
	}

	private async Task<decimal?> GetEthBalanceEtherscan(string address, DateTime startTime)
	{
		// Check Etherscan cooldown before attempting call
		if (!IsProviderAvailableWithReset(_etherscanCooldownUntil, ref _etherscanBackoffLevel))
		{
			return null; // Skip to next provider
		}
		await EnforceProviderRateLimit(0, 220);
		BalanceChecker.ApiCallMade?.Invoke(null, "ETH: Etherscan API V2 (with key)");
		ApiUsageTracker.RecordCall("Etherscan");
		RestRequest request = new RestRequest("");
		request.AddParameter("chainid", "1");
		request.AddParameter("module", "account");
		request.AddParameter("action", "balance");
		request.AddParameter("address", address);
		request.AddParameter("tag", "latest");
		request.AddParameter("apikey", _settings.EtherscanApiKey);
		DateTime apiCallStart = DateTime.Now;
		RestResponse restResponse = await _etherscanClient.ExecuteAsync(request, _globalCancellationToken);
		if (restResponse.IsSuccessful)
		{
			dynamic val = JsonConvert.DeserializeObject(restResponse.Content);
			if (val.status == "1")
			{
				string resultStr = val.result?.ToString();
				if (string.IsNullOrEmpty(resultStr) || !decimal.TryParse(resultStr, out decimal val2))
				{
					DebugLogger.Log($"Etherscan returned status=1 but unparseable result: '{resultStr}'");
					return null;
				}
				dynamic val3 = val2 / 1000000000000000000m;
				double totalMilliseconds = (DateTime.Now - apiCallStart).TotalMilliseconds;
				DebugLogger.LogApi("Etherscan", "ETH/" + TruncateAddress(address), success: true, totalMilliseconds, $"Balance: {(object)val3}");
				DebugLogger.LogBalanceCheck(address, "ETH", val3, totalMilliseconds);
				AddToCache("ETH:" + address, val3);
				return val3;
			}
			// Etherscan returns "NOTOK" for free-tier rate limit (5 calls/sec)
			string msg = val.message?.ToString() ?? "";
			if (msg.Contains("rate limit") || msg == "NOTOK")
			{
				DebugLogger.LogApi("Etherscan", "ETH/" + TruncateAddress(address), success: false, (DateTime.Now - apiCallStart).TotalMilliseconds, "RATE LIMITED!");
				SetProviderCooldown(ref _etherscanCooldownUntil, ref _etherscanBackoffLevel, "Etherscan");
				return null;
			}
			DebugLogger.Log($"Etherscan error: {(object)val.message}");
			return null;
		}
		if (restResponse.StatusCode == HttpStatusCode.TooManyRequests)
		{
			DebugLogger.LogApi("Etherscan", "ETH/" + TruncateAddress(address), success: false, (DateTime.Now - apiCallStart).TotalMilliseconds, "429 RATE LIMITED!");
			SetProviderCooldown(ref _etherscanCooldownUntil, ref _etherscanBackoffLevel, "Etherscan");
			return null;
		}
		DebugLogger.Log($"Etherscan error: {restResponse.StatusCode}");
		return null;
	}

	private async Task<decimal?> GetEthBalanceSingleRpc(RestClient client, string name, string address, int providerIndex)
	{
		await EnforceProviderRateLimit(providerIndex, 150);
		BalanceChecker.ApiCallMade?.Invoke(null, "ETH: " + name + " RPC");
		ApiUsageTracker.RecordCall(name);
		RestRequest request = new RestRequest("/", Method.Post);
		request.AddHeader("Content-Type", "application/json");
		request.AddJsonBody(new
		{
			jsonrpc = "2.0",
			method = "eth_getBalance",
			@params = new string[2] { address, "latest" },
			id = 1
		});
		DateTime callStart = DateTime.Now;
		RestResponse restResponse = await client.ExecuteAsync(request, _globalCancellationToken);
		_ = (DateTime.Now - callStart).TotalMilliseconds;
		if (restResponse.IsSuccessful && restResponse.Content != null)
		{
			dynamic jsonResp = JsonConvert.DeserializeObject(restResponse.Content);
			string val = jsonResp?.result?.ToString();
			if (!string.IsNullOrEmpty(val) && val != "0x")
			{
				string val2 = val.StartsWith("0x") ? val.Substring(2) : val;
				if (val2 == "0" || string.IsNullOrEmpty(val2))
				{
					AddToCache("ETH:" + address, 0m);
					return default(decimal);
				}
				var val3 = BigInteger.Parse("0" + val2, NumberStyles.HexNumber);
				decimal num = (decimal)val3 / 1000000000000000000m;
				DebugLogger.LogApi(name, "ETH/" + TruncateAddress(address), success: true, 0.0, $"Balance: {num}");
				DebugLogger.LogBalanceCheck(address, "ETH", num, 0.0);
				AddToCache("ETH:" + address, num);
				return num;
			}
			if (val == "0x0" || val == "0x")
			{
				AddToCache("ETH:" + address, 0m);
				return default(decimal);
			}
			// Result is null/empty — RPC returned error or empty result. Treat as zero, not provider failure.
			if (string.IsNullOrEmpty(val))
			{
				AddToCache("ETH:" + address, 0m);
				return default(decimal);
			}
		}
		if (restResponse.StatusCode == HttpStatusCode.TooManyRequests)
		{
			DebugLogger.Log($"[ETH-RPC] {name} RATE LIMITED for {address.Substring(0, Math.Min(10, address.Length))}...");
			return null;
		}
		string value = restResponse.ErrorMessage ?? restResponse.StatusDescription ?? "Unknown error";
		DebugLogger.Log($"[ETH-RPC] {name} FAILED for {address.Substring(0, Math.Min(10, address.Length))}...: {value} (HTTP {restResponse.StatusCode})");
		return null;
	}

	public async Task<decimal> GetSolanaBalance(string address)
	{
		DateTime startTime = DateTime.Now;
		await EnsureNetworkConnectivity();
		var cacheExpiry = GetCacheExpiry();
		if (cacheExpiry > TimeSpan.Zero && _balanceCache.TryGetValue("SOL:" + address, out (decimal, DateTime) value) && DateTime.Now - value.Item2 < cacheExpiry)
		{
			return value.Item1;
		}
		if (_settings.UseLocalNodeForBalances && !string.IsNullOrWhiteSpace(_settings.LocalSolanaRpcUrl))
		{
			var localClient = new RestClient(new RestClientOptions(_settings.LocalSolanaRpcUrl)
			{
				Timeout = TimeSpan.FromSeconds(15.0)
			});
			var request = new RestRequest("/", Method.Post);
			request.AddHeader("Content-Type", "application/json");
			request.AddJsonBody(new
			{
				jsonrpc = "2.0",
				id = 1,
				method = "getBalance",
				@params = new object[] { address }
			});
			var response = await localClient.ExecuteAsync(request, _globalCancellationToken);
			if (response.IsSuccessful && !string.IsNullOrWhiteSpace(response.Content))
			{
				var json = JObject.Parse(response.Content);
				var value2 = json["result"]?["value"]?.Value<long>();
				if (value2.HasValue)
				{
					var balance = value2.Value / 1000000000m;
					DebugLogger.LogApi("LocalSolanaRPC", "SOL/" + TruncateAddress(address), success: true, (DateTime.Now - startTime).TotalMilliseconds, $"Balance: {balance}");
					_balanceCache["SOL:" + address] = (balance, DateTime.Now);
					return balance;
				}
				DebugLogger.Log($"Local Solana RPC response did not contain value for {address}: {TruncateResponse(response.Content)}");
			}
			else
			{
				DebugLogger.Log($"Local Solana RPC failed: Status={response?.StatusCode} Error={response?.ErrorMessage} Content={TruncateResponse(response?.Content)}");
			}
		}
		try
		{
			await _apiThrottle.WaitAsync(_globalCancellationToken);
			try
			{
				await EnforceRateLimit();
				var solEndpoints = new List<(string url, string name, bool isFree)>
				{
					("https://solana-rpc.publicnode.com", "Solana-Public", true),
					("https://api.mainnet-beta.solana.com", "Solana-Public", true)
				};
				var qnUrl = GetQuickNodeSolanaRpcUrl();
				if (!string.IsNullOrEmpty(qnUrl))
				{
					if (qnUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || qnUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
						solEndpoints.Add((qnUrl, "QuickNode", false));
				}

				foreach (var (rpcUrl, providerName, isFree) in solEndpoints)
				{
					try
					{
						var client = HttpClientPool.Instance.GetRestClient(rpcUrl);
						var request = new RestRequest("", Method.Post);
						request.AddHeader("Content-Type", "application/json");
						request.AddJsonBody(new
						{
							jsonrpc = "2.0",
							id = 1,
							method = "getBalance",
							@params = new string[1] { address }
						});
						var restResponse = await client.ExecuteAsync(request, _globalCancellationToken);
						if (!restResponse.IsSuccessful || string.IsNullOrWhiteSpace(restResponse.Content))
							continue;

						var root = JObject.Parse(restResponse.Content);
						var lamportsToken = root["result"]?["value"];
						if (lamportsToken != null && long.TryParse(lamportsToken.ToString(), out long lamports))
						{
							decimal num = lamports / 1_000_000_000m;
							_balanceCache["SOL:" + address] = (num, DateTime.Now);
							ApiUsageTracker.RecordCall(providerName);
							if (isFree) RecordQuotaSaved(1);
							return num;
						}
					}
					catch (Exception innerEx)
					{
						DebugLogger.Log($"Solana RPC {providerName} fallback: {innerEx.Message}");
					}
				}
			}
			finally
			{
				_apiThrottle.Release();
			}
		}
		catch (Exception ex)
		{
			DebugLogger.Log("Solana balance check error: " + ex.Message);
			throw;
		}
		return default(decimal);
	}

	public async Task<decimal> GetUSDTBalance(string address, string network = "ethereum")
	{
		_ = 3;
		try
		{
			return network.ToLower() switch
			{
				"ethereum" => await GetERC20Balance(address, "0xdAC17F958D2ee523a2206206994597C13D831ec7"), 
				"polygon" => await GetPolygonUSDTBalance(address), 
				"bsc" => await GetBscUSDTBalance(address), 
				"tron" => await GetTronUSDTBalance(address), 
				_ => default(decimal), 
			};
		}
		catch (Exception ex)
		{
			DebugLogger.Log("Error checking USDT balance on " + network + ": " + ex.Message);
			return default(decimal);
		}
	}

	private async Task<decimal> GetPolygonUSDTBalance(string address)
	{
		var cacheExpiry = GetCacheExpiry();
		if (cacheExpiry > TimeSpan.Zero && _balanceCache.TryGetValue("USDT-Polygon:" + address, out (decimal, DateTime) value) && DateTime.Now - value.Item2 < cacheExpiry)
		{
			return value.Item1;
		}
		try
		{
			ApiUsageTracker.RecordCall("Polygonscan");
			RestRequest request = new RestRequest("/api");
			request.AddParameter("module", "account");
			request.AddParameter("action", "tokenbalance");
			request.AddParameter("contractaddress", "0xc2132D05D31c914a87C6611C10748AEb04B58e8F");
			request.AddParameter("address", address);
			request.AddParameter("tag", "latest");
			if (!string.IsNullOrEmpty(_settings.PolygonscanApiKey))
			{
				request.AddParameter("apikey", _settings.PolygonscanApiKey);
			}
			RestResponse restResponse = await _polygonscanClient.ExecuteAsync(request, _globalCancellationToken);
			if (restResponse.IsSuccessful)
			{
				dynamic val = JsonConvert.DeserializeObject(restResponse.Content);
				if (val.status == "1")
				{
					dynamic val2 = decimal.Parse(val.result.ToString()) / 1000000m;
					_balanceCache["USDT-Polygon:" + address] = (val2, DateTime.Now);
					return val2;
				}
			}
		}
		catch (Exception ex)
		{
			DebugLogger.Log("Error checking Polygon USDT: " + ex.Message);
		}
		return default(decimal);
	}

	private async Task<decimal> GetBscUSDTBalance(string address)
	{
		var cacheExpiry = GetCacheExpiry();
		if (cacheExpiry > TimeSpan.Zero && _balanceCache.TryGetValue("USDT-BSC:" + address, out (decimal, DateTime) value) && DateTime.Now - value.Item2 < cacheExpiry)
		{
			return value.Item1;
		}
		try
		{
			ApiUsageTracker.RecordCall("BscScan");
			RestRequest request = new RestRequest("/api");
			request.AddParameter("module", "account");
			request.AddParameter("action", "tokenbalance");
			request.AddParameter("contractaddress", "0x55d398326f99059fF775485246999027B3197955");
			request.AddParameter("address", address);
			request.AddParameter("tag", "latest");
			if (!string.IsNullOrEmpty(_settings.BscscanApiKey))
			{
				request.AddParameter("apikey", _settings.BscscanApiKey);
			}
			RestResponse restResponse = await _bscscanClient.ExecuteAsync(request, _globalCancellationToken);
			if (restResponse.IsSuccessful)
			{
				dynamic val = JsonConvert.DeserializeObject(restResponse.Content);
				if (val.status == "1")
				{
					dynamic val2 = decimal.Parse(val.result.ToString()) / 1000000000000000000m;
					_balanceCache["USDT-BSC:" + address] = (val2, DateTime.Now);
					return val2;
				}
			}
		}
		catch (Exception ex)
		{
			DebugLogger.Log("Error checking BSC USDT: " + ex.Message);
		}
		return default(decimal);
	}

	private async Task<decimal> GetERC20Balance(string address, string contractAddress)
	{
		try
		{
			// Reuse the shared _etherscanClient instead of allocating a new RestClient per call
			RestRequest request = new RestRequest("");
			request.AddParameter("chainid", "1");
			request.AddParameter("module", "account");
			request.AddParameter("action", "tokenbalance");
			request.AddParameter("contractaddress", contractAddress);
			request.AddParameter("address", address);
			request.AddParameter("tag", "latest");
			if (!string.IsNullOrEmpty(_settings.EtherscanApiKey))
			{
				request.AddParameter("apikey", _settings.EtherscanApiKey);
			}
			RestResponse restResponse = await _etherscanClient.ExecuteAsync(request, _globalCancellationToken);
			if (restResponse.IsSuccessful)
			{
				DebugLogger.Log("ERC20 API Response: " + restResponse.Content);
				dynamic val = JsonConvert.DeserializeObject(restResponse.Content);
				if (val.status == "1")
				{
					dynamic val2 = decimal.Parse(val.result.ToString());
					DebugLogger.Log($"USDT Balance: {(object)(val2 / 1000000m)} USDT");
					return val2 / 1000000m;
				}
			}
		}
		catch (Exception ex)
		{
			DebugLogger.Log("Error checking ERC20 balance: " + ex.Message);
		}
		return default(decimal);
	}

	public async Task<Dictionary<string, decimal>> GetERC20TokenBalances(string address)
	{
		Dictionary<string, decimal> result = new Dictionary<string, decimal>();
		(string, string, int)[] array = new(string, string, int)[4]
		{
			("0xdAC17F958D2ee523a2206206994597C13D831ec7", "USDT", 6),
			("0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48", "USDC", 6),
			("0x6B175474E89094C44Da98b954EedeAC495271d0F", "DAI", 18),
			("0x2260FAC5E5542a773Aa44fBCfeDf7C193bc2C599", "WBTC", 8)
		};
		(string contract, string symbol, int decimals)[] array2 = array;
		for (int i = 0; i < array2.Length; i++)
		{
			var (contract, symbol, decimals) = array2[i];
			try
			{
				decimal num = await GetERC20BalanceWithDecimals(address, contract, decimals);
				if (num > 0m)
				{
					result[symbol] = num;
				}
			}
			catch
			{
			}
		}
		return result;
	}

	private async Task<decimal> GetERC20BalanceWithDecimals(string address, string contractAddress, int decimals)
	{
		_ = 1;
		try
		{
			await _ethThrottle.WaitAsync(_globalCancellationToken);
			try
			{
				if (_globalCancellationToken.IsCancellationRequested)
				{
					return default(decimal);
				}
				RestRequest request = new RestRequest("");
				request.AddParameter("chainid", "1");
				request.AddParameter("module", "account");
				request.AddParameter("action", "tokenbalance");
				request.AddParameter("contractaddress", contractAddress);
				request.AddParameter("address", address);
				request.AddParameter("tag", "latest");
				if (!string.IsNullOrEmpty(_settings.EtherscanApiKey))
				{
					request.AddParameter("apikey", _settings.EtherscanApiKey);
				}
				RestResponse restResponse = await _etherscanClient.ExecuteAsync(request, _globalCancellationToken);
				if (restResponse.IsSuccessful)
				{
					dynamic val = JsonConvert.DeserializeObject(restResponse.Content);
					if (val?.status == "1")
					{
						dynamic val2 = decimal.Parse(val.result.ToString());
						return val2 / (decimal)Math.Pow(10.0, decimals);
					}
				}
			}
			finally
			{
				_ethThrottle.Release();
			}
		}
		catch
		{
		}
		return default(decimal);
	}

	public async Task<Dictionary<string, decimal>> GetCrossChainEVMBalances(string address)
	{
		Dictionary<string, decimal> result = new Dictionary<string, decimal>();
		await Task.WhenAll(((IEnumerable<(string, string)>)new(string, string)[4]
		{
			("Arbitrum", "https://arb1.arbitrum.io/rpc"),
			("Optimism", "https://mainnet.optimism.io"),
			("Base", "https://mainnet.base.org"),
			("Avalanche", "https://api.avax.network/ext/bc/C/rpc")
		}).Select((Func<(string, string), Task>)async delegate((string name, string rpcUrl) chain)
		{
			try
			{
				using RestClient client = new RestClient(new RestClientOptions(chain.rpcUrl)
				{
					Timeout = TimeSpan.FromSeconds(5.0)
				});
				RestRequest request = new RestRequest("", Method.Post);
				request.AddJsonBody(new
				{
					jsonrpc = "2.0",
					method = "eth_getBalance",
					@params = new string[2] { address, "latest" },
					id = 1
				});
				RestResponse restResponse = await client.ExecuteAsync(request, _globalCancellationToken);
				if (restResponse.IsSuccessful)
				{
					string text = ((dynamic)JsonConvert.DeserializeObject(restResponse.Content))?.result?.ToString();
					if (!string.IsNullOrEmpty(text) && text != "0x0")
					{
						decimal num = (decimal)BigInteger.Parse("0" + text.Replace("0x", ""), NumberStyles.HexNumber) / 1000000000000000000m;
						if (num > 0m)
						{
							result[chain.name] = num;
							DebugLogger.Log($"[CROSS-CHAIN] {chain.name} balance for {address}: {num}");
						}
					}
				}
			}
			catch
			{
			}
		}));
		return result;
	}

	private async Task<decimal> GetTronUSDTBalance(string address)
	{
		var cacheExpiry = GetCacheExpiry();
		if (cacheExpiry > TimeSpan.Zero && _balanceCache.TryGetValue("USDT-TRC20:" + address, out (decimal, DateTime) value) && DateTime.Now - value.Item2 < cacheExpiry)
		{
			return value.Item1;
		}
		try
		{
			await _apiThrottle.WaitAsync();
			try
			{
				await EnforceRateLimit();
				ApiUsageTracker.RecordCall("TronGrid");
				RestRequest request = new RestRequest("/v1/accounts/" + address + "/tokens");
				if (!string.IsNullOrEmpty(_settings.TronGridApiKey))
				{
					request.AddHeader("TRON-PRO-API-KEY", _settings.TronGridApiKey);
				}
				RestResponse restResponse = await _trongridClient.ExecuteAsync(request, _globalCancellationToken);
				if (restResponse.IsSuccessful)
				{
					dynamic val = JsonConvert.DeserializeObject(restResponse.Content);
					if (val.data != null)
					{
						foreach (dynamic item in val.data)
						{
							if (item.tokenId == "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t")
							{
								dynamic val2 = decimal.Parse(item.balance.ToString()) / 1000000m;
								_balanceCache["USDT-TRC20:" + address] = (val2, DateTime.Now);
								return val2;
							}
						}
					}
				}
			}
			finally
			{
				_apiThrottle.Release();
			}
		}
		catch (Exception ex)
		{
			DebugLogger.Log("Error checking Tron USDT balance: " + ex.Message);
		}
		return default(decimal);
	}

	public async Task<Dictionary<string, decimal>> CheckAllBalances(CryptoWalletGenerator.WalletInfo wallet, bool scanBitcoin = true, bool scanEthereum = true, bool scanSolana = true, bool scanUSDT = true, bool scanLitecoin = false, bool scanDogecoin = false, bool scanPolygon = false, bool scanBnb = false)
	{
		Dictionary<string, decimal> balances = new Dictionary<string, decimal>();
		AppSettings current = _settings ?? AppSettings.Current;
		bool strictScan = current.StrictScanMode;
		bool quickScan = current.QuickScanMode && !strictScan;
		bool skipEmptyTypes = current.SkipEmptyAddressTypes && !strictScan;
		bool useOfflineFilter = current.EnableOfflineFilter && !strictScan;
		DebugLogger.Log($"[BALANCE] CheckAllBalances START | BTC={scanBitcoin} ETH={scanEthereum} SOL={scanSolana} USDT={scanUSDT} LTC={scanLitecoin} DOGE={scanDogecoin} MATIC={scanPolygon} BNB={scanBnb} | QuickScan={quickScan} | SkipEmptyTypes={skipEmptyTypes}");

		// Reset historical activity hint for this check pass
		if (wallet != null)
		{
			wallet.HistoricalActivityLikely = false;
			wallet.HistoricalActivityConfirmed = false;
			wallet.HistoricalActivityEvaluated = false;
			wallet.HistoricalActivityDetails = "Historical check not performed";
		}
		
		// BLOOM FILTER PRE-CHECK: Skip API calls if addresses are definitely NOT funded
		// Only apply when filter has a meaningful dataset (>10K addresses) to avoid false negatives
		if (useOfflineFilter)
		{
			bool anyMightBeFunded = false;
			bool historicalEvaluated = false;
			bool historicalLikely = false;
			var historicalHits = new List<string>();
			
			if (scanBitcoin && !string.IsNullOrEmpty(wallet.BitcoinAddress))
			{
				// Only trust the bloom filter if it has a substantial dataset (>10K addresses)
				// A small/empty filter would incorrectly block all addresses
				if (FundedAddressFilter.BTC.IsLoaded && FundedAddressFilter.BTC.AddressCount > 10000)
				{
					historicalEvaluated = true;
					bool btcMightBeFunded = FundedAddressFilter.BTC.MightBeFunded(wallet.BitcoinAddress) ||
						(!string.IsNullOrEmpty(wallet.BitcoinAddressSegWit) && FundedAddressFilter.BTC.MightBeFunded(wallet.BitcoinAddressSegWit)) ||
						(!string.IsNullOrEmpty(wallet.BitcoinAddressNativeSegWit) && FundedAddressFilter.BTC.MightBeFunded(wallet.BitcoinAddressNativeSegWit)) ||
						(!string.IsNullOrEmpty(wallet.BitcoinAddressTaproot) && FundedAddressFilter.BTC.MightBeFunded(wallet.BitcoinAddressTaproot));
					if (btcMightBeFunded) anyMightBeFunded = true;
					if (btcMightBeFunded)
					{
						historicalLikely = true;
						historicalHits.Add("BTC");
					}
				}
				else
				{
					anyMightBeFunded = true; // No filter or too small — always check via API
				}
			}
			
			if (scanEthereum && !string.IsNullOrEmpty(wallet.EthereumAddress))
			{
				if (FundedAddressFilter.ETH.IsLoaded && FundedAddressFilter.ETH.AddressCount > 10000)
				{
					historicalEvaluated = true;
					if (FundedAddressFilter.ETH.MightBeFunded(wallet.EthereumAddress))
					{
						anyMightBeFunded = true;
						historicalLikely = true;
						historicalHits.Add("ETH");
					}
				}
				else
				{
					anyMightBeFunded = true; // No filter or too small — always check via API
				}
			}

			if (wallet != null)
			{
				wallet.HistoricalActivityEvaluated = historicalEvaluated;
				wallet.HistoricalActivityLikely = historicalLikely;
				if (historicalLikely)
				{
					wallet.HistoricalActivityDetails = $"Historical funded-address match: {string.Join(", ", historicalHits.Distinct())}";
				}
				else if (historicalEvaluated)
				{
					wallet.HistoricalActivityDetails = "No historical funded-address match in loaded datasets";
				}
				else
				{
					wallet.HistoricalActivityDetails = "Historical dataset not loaded";
				}
			}
			
			// For other cryptocurrencies, we don't have bloom filters yet, so assume they might be funded
			if ((scanSolana && !string.IsNullOrEmpty(wallet.SolanaAddress)) ||
				(scanUSDT) || // USDT can be on multiple chains
				(scanLitecoin && !string.IsNullOrEmpty(wallet.LitecoinAddress)) ||
				(scanDogecoin && !string.IsNullOrEmpty(wallet.DogecoinAddress)) ||
				(scanPolygon && !string.IsNullOrEmpty(wallet.PolygonAddress)) ||
				(scanBnb && !string.IsNullOrEmpty(wallet.BnbAddress)))
			{
				anyMightBeFunded = true;
			}
			
			if (!anyMightBeFunded)
			{
				DebugLogger.Log("[BALANCE] Bloom filter: No addresses might be funded, skipping API calls");
				return balances; // Empty dictionary - no balances to check
			}
		}
		
		try
		{
			List<Task<(string, decimal)>> tasks = new List<Task<(string, decimal)>>();
			if (scanBitcoin && !string.IsNullOrEmpty(wallet.BitcoinAddress))
			{
				// Check legacy address first
				decimal legacyBalance = (balances["BTC"] = await GetBitcoinBalance(wallet.BitcoinAddress));
				
				// Always check all BTC address types — funds may exist only in SegWit/NativeSegWit/Taproot
				// even when the legacy address is empty. Never skip based on legacy balance.
				bool checkSegWitAddresses = true;
				
				if (quickScan)
				{
					// QuickScan: Check SegWit (P2SH-P2WPKH) as sentinel alongside NativeSegWit.
					// Previously SegWit was only checked if Legacy or NativeSegWit had balance,
					// which missed wallets with funds ONLY in SegWit addresses.
					decimal segwitBal = default(decimal);
					if (!string.IsNullOrEmpty(wallet.BitcoinAddressSegWit) && checkSegWitAddresses)
					{
						segwitBal = (balances["BTC-SegWit"] = await GetBitcoinBalance(wallet.BitcoinAddressSegWit));
					}
					decimal sentinelBal = default(decimal);
					if (!string.IsNullOrEmpty(wallet.BitcoinAddressNativeSegWit) && checkSegWitAddresses)
					{
						sentinelBal = (balances["BTC-NativeSegWit"] = await GetBitcoinBalance(wallet.BitcoinAddressNativeSegWit));
					}
					// Check Taproot if ANY address type has balance (or always - Taproot-only wallets exist)
					if (sentinelBal > 0.00000001m || legacyBalance > 0.00000001m || segwitBal > 0.00000001m ||
						!string.IsNullOrEmpty(wallet.BitcoinAddressTaproot))
					{
						if (!string.IsNullOrEmpty(wallet.BitcoinAddressTaproot))
						{
							tasks.Add(GetBitcoinBalance(wallet.BitcoinAddressTaproot).ContinueWith((Task<decimal> t) => (!t.IsFaulted) ? ("BTC-Taproot", t.Result) : ("BTC-Taproot", 0m)));
						}
					}
				}
				else
				{
					if (!string.IsNullOrEmpty(wallet.BitcoinAddressSegWit) && checkSegWitAddresses)
					{
						tasks.Add(GetBitcoinBalance(wallet.BitcoinAddressSegWit).ContinueWith((Task<decimal> t) => (!t.IsFaulted) ? ("BTC-SegWit", t.Result) : ("BTC-SegWit", 0m)));
					}
					if (!string.IsNullOrEmpty(wallet.BitcoinAddressNativeSegWit) && checkSegWitAddresses)
					{
						tasks.Add(GetBitcoinBalance(wallet.BitcoinAddressNativeSegWit).ContinueWith((Task<decimal> t) => (!t.IsFaulted) ? ("BTC-NativeSegWit", t.Result) : ("BTC-NativeSegWit", 0m)));
					}
					if (!string.IsNullOrEmpty(wallet.BitcoinAddressTaproot))
					{
						tasks.Add(GetBitcoinBalance(wallet.BitcoinAddressTaproot).ContinueWith((Task<decimal> t) => (!t.IsFaulted) ? ("BTC-Taproot", t.Result) : ("BTC-Taproot", 0m)));
					}
				}
			}
			if (scanEthereum && !string.IsNullOrEmpty(wallet.EthereumAddress))
			{
				if (quickScan)
				{
					decimal num4 = (balances["ETH"] = await GetEthereumBalance(wallet.EthereumAddress));
					// ALWAYS check USDT even if ETH=0 (many wallets hold tokens with 0 ETH balance)
					if (scanUSDT)
					{
						tasks.Add(GetUSDTBalance(wallet.EthereumAddress).ContinueWith((Task<decimal> t) => (!t.IsFaulted) ? ("USDT", t.Result) : ("USDT", 0m)));
						if (scanPolygon)
						{
							tasks.Add(GetUSDTBalance(wallet.EthereumAddress, "polygon").ContinueWith((Task<decimal> t) => (!t.IsFaulted) ? ("USDT-Polygon", t.Result) : ("USDT-Polygon", 0m)));
						}
						if (scanBnb)
						{
							tasks.Add(GetUSDTBalance(wallet.EthereumAddress, "bsc").ContinueWith((Task<decimal> t) => (!t.IsFaulted) ? ("USDT-BSC", t.Result) : ("USDT-BSC", 0m)));
						}
					}
					if (scanPolygon && !string.IsNullOrEmpty(wallet.PolygonAddress))
					{
						tasks.Add(GetPolygonBalance(wallet.PolygonAddress).ContinueWith((Task<decimal> t) => (!t.IsFaulted) ? ("MATIC", t.Result) : ("MATIC", 0m)));
					}
					if (scanBnb && !string.IsNullOrEmpty(wallet.BnbAddress))
					{
						tasks.Add(GetBnbBalance(wallet.BnbAddress).ContinueWith((Task<decimal> t) => (!t.IsFaulted) ? ("BNB", t.Result) : ("BNB", 0m)));
					}
				}
				else
				{
					tasks.Add(GetEthereumBalance(wallet.EthereumAddress).ContinueWith((Task<decimal> t) => (!t.IsFaulted) ? ("ETH", t.Result) : ("ETH", 0m)));
					if (scanUSDT)
					{
						tasks.Add(GetUSDTBalance(wallet.EthereumAddress).ContinueWith((Task<decimal> t) => (!t.IsFaulted) ? ("USDT", t.Result) : ("USDT", 0m)));
						if (scanPolygon)
						{
							tasks.Add(GetUSDTBalance(wallet.EthereumAddress, "polygon").ContinueWith((Task<decimal> t) => (!t.IsFaulted) ? ("USDT-Polygon", t.Result) : ("USDT-Polygon", 0m)));
						}
						if (scanBnb)
						{
							tasks.Add(GetUSDTBalance(wallet.EthereumAddress, "bsc").ContinueWith((Task<decimal> t) => (!t.IsFaulted) ? ("USDT-BSC", t.Result) : ("USDT-BSC", 0m)));
						}
					}
					if (scanPolygon && !string.IsNullOrEmpty(wallet.PolygonAddress))
					{
						tasks.Add(GetPolygonBalance(wallet.PolygonAddress).ContinueWith((Task<decimal> t) => (!t.IsFaulted) ? ("MATIC", t.Result) : ("MATIC", 0m)));
					}
					if (scanBnb && !string.IsNullOrEmpty(wallet.BnbAddress))
					{
						tasks.Add(GetBnbBalance(wallet.BnbAddress).ContinueWith((Task<decimal> t) => (!t.IsFaulted) ? ("BNB", t.Result) : ("BNB", 0m)));
					}
				}
			}
			else
			{
				if (scanPolygon && !string.IsNullOrEmpty(wallet.PolygonAddress))
				{
					tasks.Add(GetPolygonBalance(wallet.PolygonAddress).ContinueWith((Task<decimal> t) => (!t.IsFaulted) ? ("MATIC", t.Result) : ("MATIC", 0m)));
				}
				if (scanBnb && !string.IsNullOrEmpty(wallet.BnbAddress))
				{
					tasks.Add(GetBnbBalance(wallet.BnbAddress).ContinueWith((Task<decimal> t) => (!t.IsFaulted) ? ("BNB", t.Result) : ("BNB", 0m)));
				}
			}
			if (scanSolana && !string.IsNullOrEmpty(wallet.SolanaAddress))
			{
				tasks.Add(GetSolanaBalance(wallet.SolanaAddress).ContinueWith((Task<decimal> t) => (!t.IsFaulted) ? ("SOL", t.Result) : ("SOL", 0m)));
			}
			if (scanLitecoin && !string.IsNullOrEmpty(wallet.LitecoinAddress))
			{
				tasks.Add(GetLitecoinBalance(wallet.LitecoinAddress).ContinueWith((Task<decimal> t) => (!t.IsFaulted) ? ("LTC", t.Result) : ("LTC", 0m)));
			}
			if (scanDogecoin && !string.IsNullOrEmpty(wallet.DogecoinAddress))
			{
				tasks.Add(GetDogecoinBalance(wallet.DogecoinAddress).ContinueWith((Task<decimal> t) => (!t.IsFaulted) ? ("DOGE", t.Result) : ("DOGE", 0m)));
			}
			if (!string.IsNullOrEmpty(wallet.RippleAddress) && false) // Disabled: no ScanXRP flag in RecoveryOptions
			{
				tasks.Add(GetRippleBalance(wallet.RippleAddress).ContinueWith((Task<decimal> t) => (!t.IsFaulted) ? ("XRP", t.Result) : ("XRP", 0m)));
			}
			if (!string.IsNullOrEmpty(wallet.CardanoAddress) && false) // Disabled: no ScanCardano flag, Cardano API requires BlockfrostApiKey
			{
				tasks.Add(GetCardanoBalance(wallet.CardanoAddress).ContinueWith((Task<decimal> t) => (!t.IsFaulted) ? ("ADA", t.Result) : ("ADA", 0m)));
			}
			if (tasks.Count > 0)
			{
				await Task.WhenAll(((IEnumerable<Task<(string, decimal)>>)tasks).Select((Func<Task<(string, decimal)>, Task>)async delegate(Task<(string, decimal)> t)
				{
					try
					{
						await t;
					}
					catch
					{
					}
				}));
				foreach (Task<(string, decimal)> item3 in tasks)
				{
					try
					{
						if (item3.IsCompletedSuccessfully)
						{
							(string, decimal) result = item3.Result;
							string item = result.Item1;
							decimal item2 = result.Item2;
							balances[item] = item2;
						}
						else if (item3.IsFaulted)
						{
							Exception ex2 = item3.Exception?.InnerExceptions.FirstOrDefault();
							if (ex2 != null)
							{
								DebugLogger.Log("Balance check failed for a chain: " + ex2.Message);
							}
						}
					}
					catch (Exception ex4)
					{
						DebugLogger.Log("Balance task error: " + ex4.Message);
					}
				}
			}
		}
		catch (Exception ex6)
		{
			DebugLogger.Log("Error checking balances: " + ex6.Message);
		}

		if (wallet != null && balances.Any(b => b.Value > 0.00000001m))
		{
			wallet.HistoricalActivityConfirmed = true;
			wallet.HistoricalActivityLikely = true;
			wallet.HistoricalActivityEvaluated = true;
			wallet.HistoricalActivityDetails = "Confirmed by current on-chain balance > 0";
		}

		DebugLogger.Log($"[BALANCE] CheckAllBalances DONE | {balances.Count} chains checked | Results: {string.Join(", ", balances.Select(b => $"{b.Key}={b.Value:F8}"))}");
		return balances;
	}

	public async Task<Dictionary<string, decimal>> CheckAllBalancesDeepScan(CryptoWalletGenerator.WalletInfo wallet, bool scanBitcoin = true, bool scanEthereum = true)
	{
		Dictionary<string, decimal> balances = await CheckAllBalances(wallet, scanBitcoin, scanEthereum, scanSolana: false, scanUSDT: false);
		if (scanBitcoin)
		{
			List<string> allBitcoinAddresses = wallet.AllBitcoinAddresses;
			if (allBitcoinAddresses != null && allBitcoinAddresses.Count > 1 && _settings.EnableDeepScan)
			{
				foreach (var item in (await Task.WhenAll(wallet.AllBitcoinAddresses.Skip(1).Select((string address, int idx) => Task.Run(async delegate
				{
					try
					{
						decimal num2 = await GetBitcoinBalance(address);
						if (num2 > 0m)
						{
							DebugLogger.Log($"\ud83d\udcb0 DEEP SCAN FOUND: {address} = {num2} BTC");
							return (Key: $"BTC-Extended-{idx + 1}", Balance: num2);
						}
					}
					catch
					{
					}
					return ((string Key, decimal Balance))(Key: null, Balance: 0m);
				})).ToList())).Where(((string Key, decimal Balance) r) => r.Key != null))
				{
					balances[item.Key] = item.Balance;
				}
			}
		}
		if (scanEthereum)
		{
			List<string> allEthereumAddresses = wallet.AllEthereumAddresses;
			if (allEthereumAddresses != null && allEthereumAddresses.Count > 1 && _settings.EnableDeepScan)
			{
				List<string> extendedEthAddresses = wallet.AllEthereumAddresses.Skip(1).ToList();
				try
				{
					Dictionary<string, decimal> obj = await GetEthereumBalancesBatch(extendedEthAddresses);
					int num = 0;
					foreach (KeyValuePair<string, decimal> item2 in obj)
					{
						if (item2.Value > 0m)
						{
							DebugLogger.Log($"\ud83d\udcb0 DEEP SCAN FOUND: {item2.Key} = {item2.Value} ETH");
							balances[$"ETH-Extended-{num + 1}"] = item2.Value;
						}
						num++;
					}
				}
				catch
				{
					foreach (var item3 in (await Task.WhenAll(extendedEthAddresses.Select((string address, int i) => Task.Run(async delegate
					{
						try
						{
							decimal num2 = await GetEthereumBalance(address);
							if (num2 > 0m)
							{
								return (Key: $"ETH-Extended-{i + 1}", Balance: num2);
							}
						}
						catch
						{
						}
						return ((string Key, decimal Balance))(Key: null, Balance: 0m);
					})).ToList())).Where(((string Key, decimal Balance) r) => r.Key != null))
					{
						balances[item3.Key] = item3.Balance;
					}
				}
			}
		}
		return balances;
	}

	private bool IsValidBitcoinAddress(string address)
	{
		if (string.IsNullOrWhiteSpace(address))
		{
			return false;
		}
		if (address.StartsWith("1") && address.Length >= 26 && address.Length <= 35)
		{
			return address.All((char c) => "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz".Contains(c));
		}
		if (address.StartsWith("3") && address.Length >= 26 && address.Length <= 35)
		{
			return address.All((char c) => "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz".Contains(c));
		}
		if (address.StartsWith("bc1") && address.Length >= 42 && address.Length <= 62)
		{
			return address.All((char c) => "023456789acdefghjklmnpqrstuvwxyz".Contains(c));
		}
		return false;
	}

	public async Task<decimal> GetDogecoinBalance(string address)
	{
		var cacheExpiry = GetCacheExpiry();
		if (cacheExpiry > TimeSpan.Zero && _balanceCache.TryGetValue("DOGE:" + address, out (decimal, DateTime) value) && DateTime.Now - value.Item2 < cacheExpiry)
		{
			return value.Item1;
		}
		try
		{
			await _apiThrottle.WaitAsync();
			try
			{
				await EnforceRateLimit();
				return await ExecuteWithRetry(async delegate
				{
					RestRequest request = new RestRequest("/v1/doge/main/addrs/" + address + "/balance");
					if (!string.IsNullOrEmpty(_settings.BlockCypherApiKey))
					{
						request.AddParameter("token", _settings.BlockCypherApiKey);
					}
					RestResponse restResponse = await _blockcypherClient.ExecuteAsync(request, _globalCancellationToken);
					if (restResponse.IsSuccessful)
					{
						dynamic val = JsonConvert.DeserializeObject(restResponse.Content);
						if (val.balance != null)
						{
							decimal num = (decimal)(long)val.balance / 100000000m;
							_balanceCache["DOGE:" + address] = (num, DateTime.Now);
							return num;
						}
					}
					return default(decimal);
				}, "DOGE-BlockCypher");
			}
			finally
			{
				_apiThrottle.Release();
			}
		}
		catch (Exception ex)
		{
			DebugLogger.Log("Error checking Dogecoin balance: " + ex.Message);
		}
		return default(decimal);
	}

	public async Task<decimal> GetLitecoinBalance(string address)
	{
		var cacheExpiry = GetCacheExpiry();
		if (cacheExpiry > TimeSpan.Zero && _balanceCache.TryGetValue("LTC:" + address, out (decimal, DateTime) value) && DateTime.Now - value.Item2 < cacheExpiry)
		{
			return value.Item1;
		}
		try
		{
			await _apiThrottle.WaitAsync();
			try
			{
				await EnforceRateLimit();
				return await ExecuteWithRetry(async delegate
				{
					RestRequest request = new RestRequest("/v1/ltc/main/addrs/" + address + "/balance");
					if (!string.IsNullOrEmpty(_settings.BlockCypherApiKey))
					{
						request.AddParameter("token", _settings.BlockCypherApiKey);
					}
					RestResponse restResponse = await _blockcypherClient.ExecuteAsync(request, _globalCancellationToken);
					if (restResponse.IsSuccessful)
					{
						dynamic val = JsonConvert.DeserializeObject(restResponse.Content);
						if (val.balance != null)
						{
							decimal num = (decimal)(long)val.balance / 100000000m;
							_balanceCache["LTC:" + address] = (num, DateTime.Now);
							return num;
						}
					}
					return default(decimal);
				}, "LTC-BlockCypher");
			}
			finally
			{
				_apiThrottle.Release();
			}
		}
		catch (Exception ex)
		{
			DebugLogger.Log("Error checking Litecoin balance: " + ex.Message);
		}
		return default(decimal);
	}

	public async Task<Dictionary<string, decimal>> GetLitecoinBalancesBatch(List<string> addresses)
	{
		Dictionary<string, decimal> results = new Dictionary<string, decimal>();
		if (addresses == null || addresses.Count == 0)
		{
			return results;
		}
		List<string> list = new List<string>();
		foreach (string address in addresses)
		{
			var cacheExpiry = GetCacheExpiry();
			if (cacheExpiry > TimeSpan.Zero && _balanceCache.TryGetValue("LTC:" + address, out (decimal, DateTime) value) && DateTime.Now - value.Item2 < cacheExpiry)
			{
				(results[address], _) = value;
			}
			else
			{
				list.Add(address);
			}
		}
		if (list.Count == 0)
		{
			return results;
		}
		List<List<string>> list2 = (from x in list.Select((string addr2, int idx) => new
			{
				addr = addr2,
				idx = idx
			})
			group x by x.idx / 50 into g
			select g.Select(x => x.addr).ToList()).ToList();
		foreach (List<string> batch in list2)
		{
			try
			{
				await _apiThrottle.WaitAsync();
				try
				{
					await EnforceRateLimit();
					string text = string.Join(";", batch);
					if (string.IsNullOrEmpty(_settings.BlockCypherApiKey))
					{
						goto IL_04f0;
					}
					RestRequest request = new RestRequest("/v1/ltc/main/addrs/" + text + "/balance");
					request.AddParameter("token", _settings.BlockCypherApiKey);
					RestResponse restResponse = await _blockcypherClient.ExecuteAsync(request, _globalCancellationToken);
					if (!restResponse.IsSuccessful)
					{
						goto IL_04f0;
					}
					List<object> list3 = JsonConvert.DeserializeObject<List<object>>(restResponse.Content);
					if (list3 == null)
					{
						continue;
					}
					foreach (dynamic item2 in list3)
					{
						string text2 = item2.address;
						decimal item = (results[text2] = (decimal)(long)(item2.balance ?? ((object)0)) / 100000000m);
						_balanceCache["LTC:" + text2] = (item, DateTime.Now);
					}
					goto end_IL_01e6;
					IL_04f0:
					foreach (string addr in batch)
					{
						try
						{
							results[addr] = await GetLitecoinBalance(addr);
						}
						catch
						{
							results[addr] = 0m;
						}
					}
				}
				finally
				{
					_apiThrottle.Release();
				}
				end_IL_01e6:;
			}
			catch (Exception ex)
			{
				DebugLogger.Log("Error in batch LTC balance check: " + ex.Message);
			}
		}
		return results;
	}

	public async Task<Dictionary<string, decimal>> GetDogecoinBalancesBatch(List<string> addresses)
	{
		Dictionary<string, decimal> results = new Dictionary<string, decimal>();
		if (addresses == null || addresses.Count == 0)
		{
			return results;
		}
		List<string> list = new List<string>();
		foreach (string address in addresses)
		{
			var cacheExpiry = GetCacheExpiry();
			if (cacheExpiry > TimeSpan.Zero && _balanceCache.TryGetValue("DOGE:" + address, out (decimal, DateTime) value) && DateTime.Now - value.Item2 < cacheExpiry)
			{
				(results[address], _) = value;
			}
			else
			{
				list.Add(address);
			}
		}
		if (list.Count == 0)
		{
			return results;
		}
		List<List<string>> list2 = (from x in list.Select((string addr2, int idx) => new
			{
				addr = addr2,
				idx = idx
			})
			group x by x.idx / 50 into g
			select g.Select(x => x.addr).ToList()).ToList();
		foreach (List<string> batch in list2)
		{
			try
			{
				await _apiThrottle.WaitAsync();
				try
				{
					await EnforceRateLimit();
					string text = string.Join(";", batch);
					if (string.IsNullOrEmpty(_settings.BlockCypherApiKey))
					{
						goto IL_04f0;
					}
					RestRequest request = new RestRequest("/v1/doge/main/addrs/" + text + "/balance");
					request.AddParameter("token", _settings.BlockCypherApiKey);
					RestResponse restResponse = await _blockcypherClient.ExecuteAsync(request, _globalCancellationToken);
					if (!restResponse.IsSuccessful)
					{
						goto IL_04f0;
					}
					List<object> list3 = JsonConvert.DeserializeObject<List<object>>(restResponse.Content);
					if (list3 == null)
					{
						continue;
					}
					foreach (dynamic item2 in list3)
					{
						string text2 = item2.address;
						decimal item = (results[text2] = (decimal)(long)(item2.balance ?? ((object)0)) / 100000000m);
						_balanceCache["DOGE:" + text2] = (item, DateTime.Now);
					}
					goto end_IL_01e6;
					IL_04f0:
					foreach (string addr in batch)
					{
						try
						{
							results[addr] = await GetDogecoinBalance(addr);
						}
						catch
						{
							results[addr] = 0m;
						}
					}
				}
				finally
				{
					_apiThrottle.Release();
				}
				end_IL_01e6:;
			}
			catch (Exception ex)
			{
				DebugLogger.Log("Error in batch DOGE balance check: " + ex.Message);
			}
		}
		return results;
	}

	public async Task<decimal> GetPolygonBalance(string address)
	{
		var cacheExpiry = GetCacheExpiry();
		if (cacheExpiry > TimeSpan.Zero && _balanceCache.TryGetValue("MATIC:" + address, out (decimal, DateTime) value) && DateTime.Now - value.Item2 < cacheExpiry)
		{
			return value.Item1;
		}
		try
		{
			// 1. Free Polygon JSON-RPC first (zero API key waste)
			var rpcClient = HttpClientPool.Instance.GetRestClient("https://polygon-rpc.com");
			var rpcBal = await GetEthBalanceSingleRpc(rpcClient, "Polygon-RPC", address, 3);
			if (rpcBal.HasValue)
			{
				AddToCache("MATIC:" + address, rpcBal.Value);
				RecordQuotaSaved(1);
				return rpcBal.Value;
			}
		}
		catch { }

		try
		{
			string apiKey = !string.IsNullOrEmpty(_settings.PolygonscanApiKey) ? _settings.PolygonscanApiKey : _settings.EtherscanApiKey;
			if (!string.IsNullOrEmpty(apiKey) && ApiUsageTracker.CanMakeApiCall("Polygonscan"))
			{
				ApiUsageTracker.RecordCall("Polygonscan");
				RestRequest request = new RestRequest("");
				request.AddParameter("chainid", "137");
				request.AddParameter("module", "account");
				request.AddParameter("action", "balance");
				request.AddParameter("address", address);
				request.AddParameter("tag", "latest");
				request.AddParameter("apikey", apiKey);
				RestResponse restResponse = await _etherscanClient.ExecuteAsync(request, _globalCancellationToken);
				if (restResponse.IsSuccessful && !string.IsNullOrWhiteSpace(restResponse.Content))
				{
					dynamic val = JsonConvert.DeserializeObject(restResponse.Content);
					if (val.status == "1")
					{
						decimal val2 = decimal.Parse(val.result.ToString(), CultureInfo.InvariantCulture) / 1000000000000000000m;
						AddToCache("MATIC:" + address, val2);
						return val2;
					}
				}
			}
		}
		catch (Exception ex)
		{
			DebugLogger.Log("Error checking Polygon balance: " + ex.Message);
		}
		return default(decimal);
	}

	public async Task<decimal> GetBnbBalance(string address)
	{
		var cacheExpiry = GetCacheExpiry();
		if (cacheExpiry > TimeSpan.Zero && _balanceCache.TryGetValue("BNB:" + address, out (decimal, DateTime) value) && DateTime.Now - value.Item2 < cacheExpiry)
		{
			return value.Item1;
		}
		try
		{
			// 1. Free BSC JSON-RPC first (zero API key waste)
			var rpcClient = HttpClientPool.Instance.GetRestClient("https://bsc-dataseed.binance.org");
			var rpcBal = await GetEthBalanceSingleRpc(rpcClient, "BSC-RPC", address, 3);
			if (rpcBal.HasValue)
			{
				AddToCache("BNB:" + address, rpcBal.Value);
				RecordQuotaSaved(1);
				return rpcBal.Value;
			}
		}
		catch { }

		try
		{
			string apiKey = !string.IsNullOrEmpty(_settings.BscscanApiKey) ? _settings.BscscanApiKey : _settings.EtherscanApiKey;
			if (!string.IsNullOrEmpty(apiKey) && ApiUsageTracker.CanMakeApiCall("BscScan"))
			{
				ApiUsageTracker.RecordCall("BscScan");
				RestRequest request = new RestRequest("");
				request.AddParameter("chainid", "56");
				request.AddParameter("module", "account");
				request.AddParameter("action", "balance");
				request.AddParameter("address", address);
				request.AddParameter("tag", "latest");
				request.AddParameter("apikey", apiKey);
				RestResponse restResponse = await _etherscanClient.ExecuteAsync(request, _globalCancellationToken);
				if (restResponse.IsSuccessful && !string.IsNullOrWhiteSpace(restResponse.Content))
				{
					dynamic val = JsonConvert.DeserializeObject(restResponse.Content);
					if (val.status == "1")
					{
						decimal val2 = decimal.Parse(val.result.ToString(), CultureInfo.InvariantCulture) / 1000000000000000000m;
						AddToCache("BNB:" + address, val2);
						return val2;
					}
				}
			}
			return default(decimal);
		}
		catch (Exception ex)
		{
			DebugLogger.Log("Error checking BNB balance: " + ex.Message);
			return default(decimal);
		}
	}

	public async Task<decimal> GetRippleBalance(string address)
	{
		var cacheExpiry = GetCacheExpiry();
		if (cacheExpiry > TimeSpan.Zero && _balanceCache.TryGetValue("XRP:" + address, out (decimal, DateTime) value) && DateTime.Now - value.Item2 < cacheExpiry)
		{
			return value.Item1;
		}
		try
		{
			await _apiThrottle.WaitAsync();
			try
			{
				await EnforceRateLimit();
				ApiUsageTracker.RecordCall("XRPL");
				BalanceChecker.ApiCallMade?.Invoke(null, "XRP: XRPL public API");
				RestRequest request = new RestRequest("/", Method.Post);
				request.AddHeader("Content-Type", "application/json");
				request.AddJsonBody(new
				{
					method = "account_info",
					@params = new[]
					{
						new
						{
							account = address,
							ledger_index = "validated"
						}
					}
				});
				RestResponse restResponse = await new RestClient(new RestClientOptions("https://s1.ripple.com:51234")
				{
					Timeout = TimeSpan.FromSeconds(10.0)
				}).ExecuteAsync(request, _globalCancellationToken);
				if (restResponse.IsSuccessful && restResponse.Content != null)
				{
					dynamic val = JsonConvert.DeserializeObject(restResponse.Content);
					if (val?.result?.status?.ToString() == "success")
					{
						dynamic val2 = long.Parse(val.result.account_data.Balance.ToString());
						dynamic val3 = val2 / 1000000m;
						val3 = Math.Max(0, val3 - 10m);
						AddToCache("XRP:" + address, val3);
						return val3;
					}
				}
			}
			finally
			{
				_apiThrottle.Release();
			}
		}
		catch (Exception ex)
		{
			DebugLogger.Log("XRP balance check error: " + ex.Message);
		}
		return default(decimal);
	}

	public async Task<decimal> GetCardanoBalance(string address)
	{
		var cacheExpiry = GetCacheExpiry();
		if (cacheExpiry > TimeSpan.Zero && _balanceCache.TryGetValue("ADA:" + address, out (decimal, DateTime) value) && DateTime.Now - value.Item2 < cacheExpiry)
		{
			return value.Item1;
		}
		try
		{
			await _apiThrottle.WaitAsync();
			try
			{
				await EnforceRateLimit();
				ApiUsageTracker.RecordCall("Blockfrost");
				BalanceChecker.ApiCallMade?.Invoke(null, "ADA: Blockfrost API");
				RestClient restClient = new RestClient(new RestClientOptions("https://cardano-mainnet.blockfrost.io")
				{
					Timeout = TimeSpan.FromSeconds(10.0)
				});
				RestRequest request = new RestRequest("/api/v0/addresses/" + address);
				string blockfrostApiKey = _settings.BlockfrostApiKey;
				if (string.IsNullOrEmpty(blockfrostApiKey))
				{
					return default(decimal);
				}
				request.AddHeader("project_id", blockfrostApiKey);
				RestResponse restResponse = await restClient.ExecuteAsync(request, _globalCancellationToken);
				if (restResponse.IsSuccessful && restResponse.Content != null)
				{
					dynamic val = ((dynamic)JsonConvert.DeserializeObject(restResponse.Content))?.amount;
					if (val != null)
					{
						foreach (dynamic item in val)
						{
							if (item.unit?.ToString() == "lovelace")
							{
								dynamic val2 = long.Parse(item.quantity.ToString());
								dynamic val3 = val2 / 1000000m;
								AddToCache("ADA:" + address, val3);
								return val3;
							}
						}
					}
				}
			}
			finally
			{
				_apiThrottle.Release();
			}
		}
		catch (Exception ex)
		{
			DebugLogger.Log("ADA balance check error: " + ex.Message);
		}
		return default(decimal);
	}

	public async Task<Dictionary<string, decimal>> GetEthereumBalancesBatch(List<string> addresses)
	{
		Dictionary<string, decimal> results = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
		if (addresses == null || addresses.Count == 0)
		{
			return results;
		}
		List<string> uncachedAddresses = new List<string>();
		foreach (string address in addresses)
		{
			var cacheExpiry = GetCacheExpiry();
			if (cacheExpiry > TimeSpan.Zero && _balanceCache.TryGetValue("ETH:" + address, out (decimal, DateTime) value) && DateTime.Now - value.Item2 < cacheExpiry)
			{
				(results[address], _) = value;
			}
			else if (cacheExpiry > TimeSpan.Zero && _balanceCache.TryGetValue("ETH:" + address.ToLowerInvariant(), out (decimal, DateTime) valueLower) && DateTime.Now - valueLower.Item2 < cacheExpiry)
			{
				(results[address], _) = valueLower;
			}
			else
			{
				uncachedAddresses.Add(address);
			}
		}
		if (uncachedAddresses.Count == 0)
		{
			return results;
		}
		if (_settings.UseLocalNodeForBalances && !string.IsNullOrWhiteSpace(_settings.LocalEthereumRpcUrl))
		{
			foreach (string addr in uncachedAddresses)
			{
				var localBalance = await TryGetEthereumBalanceFromLocalNode(addr, DateTime.Now);
				if (localBalance.HasValue)
				{
					results[addr] = localBalance.Value;
				}
			}
			return results;
		}
		// Etherscan balancemulti supports up to 20 addresses per call.
		// Process all uncached addresses in pages of 20 so nothing is silently dropped.
		const int ethBatchSize = 20;
		for (int offset = 0; offset < uncachedAddresses.Count; offset += ethBatchSize)
		{
			var page = uncachedAddresses.Skip(offset).Take(ethBatchSize).ToList();
			bool batchSucceeded = false;

			// PRIMARY (ZERO API KEY WASTE): Free public Ethereum JSON-RPC 2.0 batch (20 addresses in 1 POST, 0 API key credits)
			string[] freeBatchRpcEndpoints = new[]
			{
				"https://ethereum-rpc.publicnode.com",
				"https://eth.llamarpc.com",
				"https://1rpc.io/eth",
				"https://rpc.ankr.com/eth"
			};

			var batchPayload = page.Select((addr, idx) => new
			{
				jsonrpc = "2.0",
				method = "eth_getBalance",
				@params = new object[] { addr, "latest" },
				id = idx + 1
			}).ToArray();
			string batchPayloadJson = JsonConvert.SerializeObject(batchPayload);

			foreach (var rpcUrl in freeBatchRpcEndpoints)
			{
				if (_globalCancellationToken.IsCancellationRequested) break;
				try
				{
					var client = HttpClientPool.Instance.GetRestClient(rpcUrl);
					var rpcReq = new RestRequest("", Method.Post);
					rpcReq.AddStringBody(batchPayloadJson, DataFormat.Json);
					var rpcResp = await client.ExecuteAsync(rpcReq, _globalCancellationToken);
					if (rpcResp.IsSuccessful && !string.IsNullOrWhiteSpace(rpcResp.Content))
					{
						var arr = JArray.Parse(rpcResp.Content);
						int parsedCount = 0;
						foreach (var item in arr)
						{
							int id = item["id"]?.Value<int>() ?? 0;
							string hex = item["result"]?.ToString();
							if (id >= 1 && id <= page.Count && !string.IsNullOrEmpty(hex) && hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
							{
								string hexDigits = hex.Substring(2);
								decimal ethBal = 0m;
								if (hexDigits.Length > 0 && hexDigits != "0")
								{
									BigInteger wei = BigInteger.Parse("0" + hexDigits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
									ethBal = (decimal)wei / 1000000000000000000m;
								}
								string addr = page[id - 1];
								results[addr] = ethBal;
								_balanceCache[$"ETH:{addr}"] = (ethBal, DateTime.Now);
								parsedCount++;
							}
						}
						if (parsedCount == page.Count)
						{
							batchSucceeded = true;
							RecordQuotaSaved(page.Count);
							break;
						}
					}
				}
				catch (Exception ex)
				{
					DebugLogger.Log($"ETH JSON-RPC batch failed on {rpcUrl}: {ex.Message}");
				}
			}

			// FALLBACK 1 (PROTECTED ALCHEMY KEY): Use Alchemy JSON-RPC batch only if free RPCs failed
			var alchemyUrl = GetAlchemyEthRpcUrl();
			if (!batchSucceeded && !string.IsNullOrEmpty(alchemyUrl) && ApiUsageTracker.CanMakeApiCall("Alchemy"))
			{
				try
				{
					ApiUsageTracker.RecordCall("Alchemy");
					BalanceChecker.ApiCallMade?.Invoke(null, $"ETH: Alchemy JSON-RPC Batch ({page.Count} addresses)");
					var client = HttpClientPool.Instance.GetRestClient(alchemyUrl);
					var rpcReq = new RestRequest("", Method.Post);
					rpcReq.AddStringBody(batchPayloadJson, DataFormat.Json);
					var rpcResp = await client.ExecuteAsync(rpcReq, _globalCancellationToken);
					if (rpcResp.IsSuccessful && !string.IsNullOrWhiteSpace(rpcResp.Content))
					{
						var arr = JArray.Parse(rpcResp.Content);
						int parsedCount = 0;
						foreach (var item in arr)
						{
							int id = item["id"]?.Value<int>() ?? 0;
							string hex = item["result"]?.ToString();
							if (id >= 1 && id <= page.Count && !string.IsNullOrEmpty(hex) && hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
							{
								string hexDigits = hex.Substring(2);
								decimal ethBal = 0m;
								if (hexDigits.Length > 0 && hexDigits != "0")
								{
									BigInteger wei = BigInteger.Parse("0" + hexDigits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
									ethBal = (decimal)wei / 1000000000000000000m;
								}
								string addr = page[id - 1];
								results[addr] = ethBal;
								_balanceCache[$"ETH:{addr}"] = (ethBal, DateTime.Now);
								parsedCount++;
							}
						}
						if (parsedCount == page.Count)
						{
							batchSucceeded = true;
						}
					}
				}
				catch (Exception ex)
				{
					DebugLogger.Log($"ETH Alchemy batch failed: {ex.Message}");
				}
			}

			// FALLBACK 2 (PROTECTED ETHERSCAN KEY): Use Etherscan V2 balancemulti only if free RPCs & Alchemy failed
			if (!batchSucceeded &&
			    !string.IsNullOrEmpty(_settings.EtherscanApiKey) &&
			    DateTime.Now >= _etherscanCooldownUntil &&
			    ApiUsageTracker.CanMakeApiCall("Etherscan"))
			{
				try
				{
					await _apiThrottle.WaitAsync(_globalCancellationToken);
					try
					{
						await EnforceRateLimit();
						ApiUsageTracker.RecordCall("Etherscan");
						string value2 = string.Join(",", page);
						RestRequest request = new RestRequest("");
						request.AddParameter("chainid", "1");
						request.AddParameter("module", "account");
						request.AddParameter("action", "balancemulti");
						request.AddParameter("address", value2);
						request.AddParameter("tag", "latest");
						request.AddParameter("apikey", _settings.EtherscanApiKey);
						RestResponse restResponse = await _etherscanClient.ExecuteAsync(request, _globalCancellationToken);
						if (restResponse.IsSuccessful && restResponse.Content != null)
						{
							dynamic val = JsonConvert.DeserializeObject(restResponse.Content);
							if (val.status == "1" && val.result != null)
							{
								foreach (dynamic item in val.result)
								{
									string accountAddr = item.account.ToString();
									decimal wei = decimal.Parse(item.balance.ToString(), CultureInfo.InvariantCulture);
									decimal ethBal = wei / 1000000000000000000m;
									results[accountAddr] = ethBal;
									_balanceCache[$"ETH:{accountAddr}"] = (ethBal, DateTime.Now);
								}
								batchSucceeded = true;
							}
						}
					}
					finally
					{
						_apiThrottle.Release();
					}
				}
				catch (Exception ex)
				{
					DebugLogger.Log("Error in batch Ethereum balance check: " + ex.Message);
				}
			}

			// Fallback 2: individual ETH balance lookup if both Etherscan and JSON-RPC batch failed
			if (!batchSucceeded)
			{
				foreach (var addr in page)
				{
					if (_globalCancellationToken.IsCancellationRequested) break;
					if (results.ContainsKey(addr)) continue;
					try
					{
						decimal bal = await GetEthereumBalance(addr);
						results[addr] = bal;
					}
					catch (Exception ex)
					{
						DebugLogger.Log($"ETH batch fallback failed for {TruncateAddress(addr)}: {ex.Message}");
					}
				}
			}
		}
		return results;
	}

	/// <summary>
	/// Batch check Solana balances (up to 100 addresses per RPC call) using getMultipleAccounts.
	/// </summary>
	public async Task<Dictionary<string, decimal>> GetSolanaBalancesBatch(List<string> addresses)
	{
		var results = new Dictionary<string, decimal>(StringComparer.Ordinal);
		if (addresses == null || addresses.Count == 0)
			return results;

		var uncached = new List<string>();
		var cacheExpiry = GetCacheExpiry();
		foreach (var addr in addresses)
		{
			if (string.IsNullOrWhiteSpace(addr)) continue;
			if (cacheExpiry > TimeSpan.Zero && _balanceCache.TryGetValue("SOL:" + addr, out var cached) && DateTime.Now - cached.Item2 < cacheExpiry)
				results[addr] = cached.Item1;
			else
				uncached.Add(addr);
		}
		if (uncached.Count == 0)
			return results;

		string[] solRpcEndpoints = new[]
		{
			!string.IsNullOrWhiteSpace(_settings.LocalSolanaRpcUrl) ? _settings.LocalSolanaRpcUrl : null,
			"https://api.mainnet-beta.solana.com",
			"https://solana-rpc.publicnode.com",
			"https://rpc.ankr.com/solana"
		}.Where(u => !string.IsNullOrWhiteSpace(u)).ToArray()!;

		const int solBatchSize = 100;
		for (int offset = 0; offset < uncached.Count; offset += solBatchSize)
		{
			var page = uncached.Skip(offset).Take(solBatchSize).ToList();
			bool batchSucceeded = false;

			foreach (var rpcUrl in solRpcEndpoints)
			{
				if (_globalCancellationToken.IsCancellationRequested) break;
				try
				{
					var payload = new
					{
						jsonrpc = "2.0",
						id = 1,
						method = "getMultipleAccounts",
						@params = new object[]
						{
							page,
							new { encoding = "base64" }
						}
					};

					var client = HttpClientPool.Instance.GetRestClient(rpcUrl);
					var req = new RestRequest("", Method.Post);
					req.AddStringBody(JsonConvert.SerializeObject(payload), DataFormat.Json);
					var resp = await client.ExecuteAsync(req, _globalCancellationToken);
					if (resp.IsSuccessful && !string.IsNullOrWhiteSpace(resp.Content))
					{
						var root = JObject.Parse(resp.Content);
						var values = root["result"]?["value"] as JArray;
						if (values != null && values.Count == page.Count)
						{
							for (int i = 0; i < page.Count; i++)
							{
								decimal solBal = 0m;
								var item = values[i];
								if (item != null && item.Type == JTokenType.Object)
								{
									long lamports = item["lamports"]?.Value<long>() ?? 0L;
									solBal = lamports / 1_000_000_000m;
								}
								results[page[i]] = solBal;
								_balanceCache["SOL:" + page[i]] = (solBal, DateTime.Now);
							}
							batchSucceeded = true;
							break;
						}
					}
				}
				catch (Exception ex)
				{
					DebugLogger.Log($"SOL getMultipleAccounts batch failed on {rpcUrl}: {ex.Message}");
				}
			}

			if (!batchSucceeded)
			{
				foreach (var addr in page)
				{
					if (_globalCancellationToken.IsCancellationRequested) break;
					try
					{
						results[addr] = await GetSolanaBalance(addr);
					}
					catch (Exception ex)
					{
						DebugLogger.Log($"SOL batch fallback failed for {TruncateAddress(addr)}: {ex.Message}");
					}
				}
			}
		}

		return results;
	}

	/// <summary>
	/// Batch check EVM balances (Polygon MATIC or BSC BNB) via JSON-RPC batch array with per-address fallback.
	/// </summary>
	private async Task<Dictionary<string, decimal>> GetEvmBalancesBatchAsync(
		List<string> addresses,
		string cachePrefix,
		string[] rpcEndpoints,
		Func<string, Task<decimal>> singleFallback)
	{
		var results = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
		if (addresses == null || addresses.Count == 0)
			return results;

		var uncached = new List<string>();
		var cacheExpiry = GetCacheExpiry();
		foreach (var addr in addresses)
		{
			if (string.IsNullOrWhiteSpace(addr)) continue;
			if (cacheExpiry > TimeSpan.Zero && _balanceCache.TryGetValue($"{cachePrefix}:{addr}", out var cached) && DateTime.Now - cached.Item2 < cacheExpiry)
				results[addr] = cached.Item1;
			else
				uncached.Add(addr);
		}
		if (uncached.Count == 0)
			return results;

		const int batchSize = 20;
		for (int offset = 0; offset < uncached.Count; offset += batchSize)
		{
			var page = uncached.Skip(offset).Take(batchSize).ToList();
			bool batchSucceeded = false;

			foreach (var rpcUrl in rpcEndpoints)
			{
				if (_globalCancellationToken.IsCancellationRequested) break;
				try
				{
					var batchPayload = page.Select((addr, idx) => new
					{
						jsonrpc = "2.0",
						method = "eth_getBalance",
						@params = new object[] { addr, "latest" },
						id = idx + 1
					}).ToArray();

					var client = HttpClientPool.Instance.GetRestClient(rpcUrl);
					var req = new RestRequest("", Method.Post);
					req.AddStringBody(JsonConvert.SerializeObject(batchPayload), DataFormat.Json);
					var resp = await client.ExecuteAsync(req, _globalCancellationToken);
					if (resp.IsSuccessful && !string.IsNullOrWhiteSpace(resp.Content))
					{
						var arr = JArray.Parse(resp.Content);
						int parsedCount = 0;
						foreach (var item in arr)
						{
							int id = item["id"]?.Value<int>() ?? 0;
							string hex = item["result"]?.ToString();
							if (id >= 1 && id <= page.Count && !string.IsNullOrEmpty(hex) && hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
							{
								string hexDigits = hex.Substring(2);
								decimal bal = 0m;
								if (hexDigits.Length > 0 && hexDigits != "0")
								{
									BigInteger wei = BigInteger.Parse("0" + hexDigits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
									bal = (decimal)wei / 1000000000000000000m;
								}
								string addr = page[id - 1];
								results[addr] = bal;
								_balanceCache[$"{cachePrefix}:{addr}"] = (bal, DateTime.Now);
								parsedCount++;
							}
						}
						if (parsedCount == page.Count)
						{
							batchSucceeded = true;
							break;
						}
					}
				}
				catch (Exception ex)
				{
					DebugLogger.Log($"{cachePrefix} JSON-RPC batch failed on {rpcUrl}: {ex.Message}");
				}
			}

			if (!batchSucceeded)
			{
				foreach (var addr in page)
				{
					if (_globalCancellationToken.IsCancellationRequested) break;
					try
					{
						results[addr] = await singleFallback(addr);
					}
					catch (Exception ex)
					{
						DebugLogger.Log($"{cachePrefix} batch fallback failed for {TruncateAddress(addr)}: {ex.Message}");
					}
				}
			}
		}

		return results;
	}

	public Task<Dictionary<string, decimal>> GetPolygonBalancesBatch(List<string> addresses) =>
		GetEvmBalancesBatchAsync(
			addresses,
			"MATIC",
			new[] { "https://polygon-bor-rpc.publicnode.com", "https://polygon-rpc.com", "https://1rpc.io/matic" },
			GetPolygonBalance);

	public Task<Dictionary<string, decimal>> GetBnbBalancesBatch(List<string> addresses) =>
		GetEvmBalancesBatchAsync(
			addresses,
			"BNB",
			new[] { "https://bsc-rpc.publicnode.com", "https://bsc-dataseed.binance.org", "https://1rpc.io/bnb" },
			GetBnbBalance);

	public void Dispose()
	{
	}

	public static void ClearCache()
	{
		_balanceCache.Clear();
	}

	public static int GetCacheSize()
	{
		return _balanceCache.Count;
	}

	public async Task<Dictionary<string, (bool IsValid, string Message)>> ValidateAllApiKeys()
	{
		Dictionary<string, (bool, string)> results = new Dictionary<string, (bool, string)>();
		if (string.IsNullOrEmpty(_settings.BlockCypherApiKey))
		{
			results["BlockCypher (BTC/LTC/DOGE)"] = (false, "No key (Free Blockchain.info/Mempool Batch active)");
		}
		else
		{
			try
			{
				decimal value = await GetBitcoinBalance("1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNa");
				results["BlockCypher (BTC/LTC/DOGE)"] = (true, $"Active & Protected (genesis: {value:F4} BTC)");
			}
			catch (Exception ex)
			{
				results["BlockCypher (BTC/LTC/DOGE)"] = (false, ex.Message);
			}
		}
		if (string.IsNullOrEmpty(_settings.EtherscanApiKey))
		{
			results["Etherscan V2 (ETH/ERC20)"] = (false, "No key (Free JSON-RPC Batch active)");
		}
		else
		{
			try
			{
				var ethVal = await GetEthBalanceEtherscan("0xd8dA6BF26964aF9D7eEd9e03E53415D37aA96045", DateTime.Now);
				if (ethVal.HasValue)
					results["Etherscan V2 (ETH/ERC20)"] = (true, $"Active & Protected (test: {ethVal.Value:F4} ETH)");
				else
					results["Etherscan V2 (ETH/ERC20)"] = (true, "Key configured (Protected Zero-Waste Mode)");
			}
			catch (Exception ex2)
			{
				results["Etherscan V2 (ETH/ERC20)"] = (false, ex2.Message);
			}
		}
		if (string.IsNullOrEmpty(_settings.AlchemyApiKey))
		{
			results["Alchemy (ETH/EVM RPC)"] = (false, "Optional (not configured)");
		}
		else
		{
			try
			{
				var alchemyRpc = GetAlchemyEthRpcUrl();
				var alchemyClient = HttpClientPool.Instance.GetRestClient(alchemyRpc);
				var bal = await GetEthBalanceSingleRpc(alchemyClient, "Alchemy", "0xd8dA6BF26964aF9D7eEd9e03E53415D37aA96045", 1);
				results["Alchemy (ETH/EVM RPC)"] = bal.HasValue
					? (true, $"Active & Protected (test: {bal.Value:F4} ETH)")
					: (true, "Key configured (Protected Zero-Waste Mode)");
			}
			catch (Exception exA)
			{
				results["Alchemy (ETH/EVM RPC)"] = (false, exA.Message);
			}
		}
		if (string.IsNullOrEmpty(_settings.QuickNodeApiKey))
		{
			results["QuickNode (SOL)"] = (false, "Optional (Free Solana Batch RPC active)");
		}
		else
		{
			try
			{
				decimal value3 = await GetSolanaBalance("So11111111111111111111111111111111111111112");
				results["QuickNode (SOL)"] = (true, $"Active & Protected (system: {value3:F4} SOL)");
			}
			catch (Exception ex3)
			{
				results["QuickNode (SOL)"] = (false, ex3.Message);
			}
		}
		if (!string.IsNullOrEmpty(_settings.PolygonscanApiKey))
		{
			results["Polygonscan (MATIC)"] = (true, "Active & Protected (Zero-Waste RPC-First)");
		}
		if (!string.IsNullOrEmpty(_settings.BscscanApiKey))
		{
			results["BscScan (BNB)"] = (true, "Active & Protected (Zero-Waste RPC-First)");
		}
		if (!string.IsNullOrEmpty(_settings.TronGridApiKey))
		{
			results["TronGrid (USDT-TRC20)"] = (true, "Active & Protected");
		}
		return results;
	}

	private async Task EnsureNetworkConnectivity()
	{
		// Fast path: skip check if interval hasn't elapsed and last status was healthy
		if (DateTime.Now - _lastNetworkCheck < _networkCheckInterval && _lastNetworkStatus)
			return;

		// Fire-and-forget background check so parallel ETH threads aren't all blocked
		// waiting for the same network probe. Only one thread does the actual check.
		var now = DateTime.Now;
		if (now - _lastNetworkCheck >= _networkCheckInterval)
		{
			_lastNetworkCheck = now; // optimistic update to prevent thundering herd
			_lastNetworkStatus = await NetworkMonitor.IsInternetAvailable();
		}

		if (!_lastNetworkStatus)
		{
			throw new HttpRequestException("No internet connection detected. Please check your network connection and try again.");
		}
	}

	public static async Task<int> GetOptimalBatchSize()
	{
		return await NetworkMonitor.EstimateNetworkSpeed() switch
		{
			NetworkMonitor.NetworkSpeed.Fast => 20, 
			NetworkMonitor.NetworkSpeed.Medium => 10, 
			NetworkMonitor.NetworkSpeed.Slow => 5, 
			_ => 10, 
		};
	}

	public static void ResetApiAvailability()
	{
		_blockcypherCooldownUntil = DateTime.MinValue;
		_blockstreamCooldownUntil = DateTime.MinValue;
		_mempoolCooldownUntil = DateTime.MinValue;
		_blockchainInfoCooldownUntil = DateTime.MinValue;
		_blockcypherBackoffLevel = 0;
		_blockstreamBackoffLevel = 0;
		_mempoolBackoffLevel = 0;
		_blockchainInfoBackoffLevel = 0;
	}
}

public class ApiHealthStatus
{
	public int TotalProviders { get; set; }
	public int ProvidersDown { get; set; }
	public int BtcProvidersDown { get; set; }
	public int EthProvidersDown { get; set; }
	/// <summary>"Healthy", "Degraded", or "Down"</summary>
	public string Status { get; set; }
	public double HealthPercent => TotalProviders > 0 ? (double)(TotalProviders - ProvidersDown) / TotalProviders * 100 : 0;
}
