# Copilot Instructions for Wallet Recovery Tool

## Project Overview
Professional C# WPF desktop application (.NET 8.0) for cryptocurrency wallet recovery supporting Bitcoin, Ethereum, Solana, USDT, and other EVM chains. Uses Material Design UI with multi-threaded recovery engine, blockchain API integration, and JSON file-based local storage.

## Architecture & Data Flow

### Core Processing Pipeline
```
User Input → WalletRecoveryEngine → CryptoWalletGenerator → BalanceChecker → UI/JSON Files
                    ↓                        ↓                      ↓
            SessionManager         NBitcoin/Nethereum         RestSharp APIs
```

**Key architectural decisions:**
- Event-driven communication between engine and UI (WalletFound, ProgressUpdated, StatisticsUpdated events)
- Static HTTP clients in `BalanceChecker` for connection pooling across all threads
- Parallel.ForEach with MaxDegreeOfParallelism for multi-threaded wallet processing
- Settings loaded once at startup, persisted to AppData/Roaming as JSON
- **Local JSON file storage** - No database server required, all data in human-readable JSON files

### Critical Threading Pattern
**ALL UI updates from background threads MUST use Dispatcher.Invoke:**
```csharp
// In event handlers called from WalletRecoveryEngine threads:
private void OnWalletFound(object sender, WalletFoundEventArgs e)
{
    Dispatcher.Invoke(() => {
        _mnemonicResults.Add(e.Wallet);
        UpdateUI();
    });
}
```
Search codebase for existing `Dispatcher.Invoke` patterns before modifying UI update code.

### Service Boundaries
- **CryptoWalletGenerator** - Pure crypto operations, NO API calls, throws on invalid input
- **BalanceChecker** - All blockchain API interaction, handles rate limits, caching, network errors
- **WalletRecoveryEngine** - Orchestrates parallel processing, manages cancellation tokens, fires events
- **MainWindow** - UI state only, subscribes to engine events, no business logic
- **WalletDatabase** - JSON file persistence for found wallets (AppData/Roaming/WalletRecoveryTool/Database/wallets.json)

## Data Persistence Strategy

**All storage is local JSON files** (no remote database):
- **Wallets**: `AppData/Roaming/WalletRecoveryTool/Database/wallets.json` - **ONLY wallets with balance** (empty wallets NOT saved)
- **Session Stats**: `AppData/Roaming/WalletRecoveryTool/Database/session_stats.json` - Recovery session history
- **Settings**: `AppData/Roaming/WalletRecoveryTool/settings.json` - User configuration & API keys
- **Sessions**: `AppData/Roaming/WalletRecoveryTool/sessions/session_[id].json` - Active recovery state
- **Logs**: `Documents/WalletRecoveryTool/Logs/recovery_log_[timestamp].txt` - **ONLY wallets with balance** (by default)

**WalletDatabase pattern** (Core/WalletDatabase.cs):
```csharp
// Thread-safe JSON file operations with lock
lock (_fileLock)
{
    var wallets = LoadWalletsFromFile(); // Deserialize from JSON
    wallets.Add(newRecord);
    SaveWalletsToFile(wallets); // Serialize back to JSON
}
```

**Logging behavior** (Core/WalletLogger.cs):
```csharp
// LogWalletChecked - controlled by AppSettings.LogAllWallets (default: false)
// Only logs wallets with balance unless explicitly enabled
if (!settings.LogAllWallets) return; // Skip empty wallets

// LogWalletFound - ALWAYS logs wallets with balance
```

**Storage settings** (AppSettings.cs):
- `SaveFoundWalletsOnly` (default: true) - Save ONLY wallets with balance to JSON
- `LogAllWallets` (default: false) - If true, logs ALL checked wallets; if false, only wallets with balance

**Benefits of JSON storage:**
- ✅ No database server needed
- ✅ Human-readable files (easy debugging)
- ✅ Simple backup (copy files)
- ✅ Cross-platform compatible
- ✅ No schema migrations
- ✅ **Only stores valuable data** (wallets with funds)

## Critical Development Workflows

### Build & Run Commands
```powershell
# Standard build (or use VS Code task "Build Application")
dotnet build

# Release build with batch file validation
.\build.bat

# Run application (auto-builds if needed)
.\run.bat
```

**Batch files check for .NET SDK and provide user-friendly error messages - use them for deployment testing.**

### API Key Management
**Never hardcode API keys.** All keys stored in AppSettings (AppData/Roaming/WalletRecoveryTool/settings.json):
```csharp
// Load settings in constructors
var settings = AppSettings.Load();
var apiKey = settings.EtherscanApiKey;

// Save from SettingsWindow.xaml.cs
settings.EtherscanApiKey = EtherscanApiKeyTextBox.Text;
settings.Save();
```

Check `SettingsWindow.xaml.cs` for API key validation patterns before adding new services.

### Performance Optimization Patterns

**1. Balance Cache (5-minute TTL):**
```csharp
// In BalanceChecker.cs - check cache before API calls
if (_balanceCache.TryGetValue($"BTC:{address}", out var cached))
{
    if (DateTime.Now - cached.checkedAt < _cacheExpiry)
        return cached.balance;
}
```

**2. API Rate Limiting (critical to prevent bans):**
```csharp
await _apiThrottle.WaitAsync(); // Semaphore with max concurrent calls
try {
    await EnforceRateLimit(); // Delay between calls from settings
    // Make API call
} finally {
    _apiThrottle.Release();
}
```

**3. Quick Scan Mode** - Stop checking a wallet if first address is empty (see `BalanceChecker.CheckAllBalances` for early-exit logic)

**4. Multi-threading Configuration:**
```csharp
// Auto-detect in AppSettings.cs
ThreadCount = Math.Max(4, Environment.ProcessorCount - 1);

// Applied in WalletRecoveryEngine.cs
Parallel.ForEach(variations, 
    new ParallelOptions { MaxDegreeOfParallelism = threadCount, CancellationToken = token },
    mnemonic => { /* process */ });
```

### Error Handling Conventions

**Blockchain API errors have specific handling:**
```csharp
try {
    var balances = await _balanceChecker.CheckAllBalances(wallet);
}
catch (AggregateException ae) when (ae.InnerException is InvalidOperationException ioe 
    && ioe.Message.Contains("rate limit"))
{
    _logger.LogError("API rate limit reached. Stopping recovery.", ioe);
    _cancellationTokenSource?.Cancel(); // STOP ALL PROCESSING
    throw ioe; // Re-throw to propagate to UI
}
catch (HttpRequestException ex) {
    // Network error - show user-friendly message
    ErrorOccurred?.Invoke(this, new ErrorEventArgs($"Network error - check connection: {ex.Message}"));
}
```

**Crypto operations always throw on invalid input - wrap in try/catch:**
```csharp
try {
    var mnemonic = new Mnemonic(mnemonicString); // Throws if invalid
    var wallet = CryptoWalletGenerator.GenerateWallet(mnemonic.ToString());
} catch (Exception ex) {
    throw new InvalidOperationException($"Failed to generate wallet: {ex.Message}", ex);
}
```

## Project-Specific Patterns

### BIP39 Mnemonic Handling
**Use NBitcoin.Mnemonic for Bitcoin paths, Nethereum.HdWallet.Wallet for Ethereum:**
```csharp
// Bitcoin (Legacy, SegWit, Native SegWit)
var mnemonicObj = new NBitcoin.Mnemonic(phrase);
var masterKey = mnemonicObj.DeriveExtKey();
var legacyKey = masterKey.Derive(new KeyPath("m/44'/0'/0'/0/0"));
var segwitKey = masterKey.Derive(new KeyPath("m/49'/0'/0'/0/0"));
var nativeSegwitKey = masterKey.Derive(new KeyPath("m/84'/0'/0'/0/0"));

// Ethereum (same address for Polygon, BNB, other EVM chains)
var ethWallet = new Nethereum.HdWallet.Wallet(phrase, "");
var ethAddress = ethWallet.GetAccount(0).Address;
```

### Session Persistence
Recovery sessions auto-save to AppData/Roaming/WalletRecoveryTool/sessions/:
```csharp
// In WalletRecoveryEngine.cs - create session before recovery starts
CurrentSession = new SessionManager.RecoverySession
{
    RecoveryType = options.RecoveryType,
    TotalAttempts = options.MaxAttempts,
    StartTime = DateTime.Now
};
SessionManager.SaveSession(CurrentSession);
```

### Logging Strategy
**WalletLogger writes to Documents/WalletRecoveryTool/Logs/ with timestamps:**
```csharp
_logger.LogWalletChecked(wallet);     // Every wallet (verbose)
_logger.LogWalletFound(wallet);       // Only wallets with balance
_logger.LogError(message, exception); // Errors only
```
Logging can be disabled via AppSettings.EnableLogging.

## Adding New Cryptocurrencies

**Follow this exact sequence:**

1. **Add NuGet package** to WalletRecoveryTool.csproj
2. **Extend WalletInfo** class in CryptoWalletGenerator.cs with new address property
3. **Add generation logic** in CryptoWalletGenerator.GenerateWallet() switch/case
4. **Add balance checking** method in BalanceChecker.cs (follow existing GetBitcoinBalance pattern)
5. **Update CheckAllBalances** to call new method
6. **Add UI option** in MainWindow.xaml ComboBox
7. **Test with known addresses** from blockchain explorer

Reference commit history or `CryptoWalletGenerator.cs` lines 85-95 for Dogecoin/Litecoin examples.

## Testing Approach
- **Use testnet addresses** or known empty addresses for development
- **Validate BIP39 compliance** with test vectors from official spec
- **Test cancellation tokens** - stop recovery mid-process and verify cleanup
- **Monitor API rate limits** - run for 5+ minutes to verify throttling works
- **Check Dispatcher.Invoke usage** - all UI updates from threads must use it