# BTCRecover Integration for Wallet Recovery Tool

This document explains how to set up and use BTCRecover's advanced seed recovery features within the Wallet Recovery Tool.

## What is BTCRecover?

BTCRecover is an open-source tool for recovering Bitcoin wallet passwords and seeds. It excels at:
- **Seed Recovery**: Finding correct seeds when you have typos or missing words
- **Password Recovery**: Cracking wallet passwords with various techniques
- **GPU Acceleration**: Fast processing using OpenCL
- **Address Database**: Checking against pre-computed funded addresses

## Address Database (AddressDB)

The AddressDB is a crucial component for seed recovery without known addresses. It contains a database of all addresses that have ever had balance on the blockchain.

### Why Use AddressDB?
- **No API Limits**: Checks millions of addresses instantly
- **No Known Addresses Required**: Works even if you don't know your wallet addresses
- **Fast Recovery**: Much faster than API-based checking

### Downloading Pre-made AddressDB Files

**Visit: https://cryptoguide.tips/btcrecover-addressdbs/**

Download the appropriate database for your needs:

| Coin | File Size | Use Case |
|------|-----------|----------|
| Bitcoin | ~16GB | Most comprehensive, works for all BIP39 wallets |
| Ethereum | ~4GB | Good for ETH-based recovery |
| Litecoin | ~4GB | Smaller blockchain, faster processing |
| Dogecoin | ~1GB | Very fast, good for testing |

### File Locations

Place downloaded `.db` files in the `btcrecover/` folder:
```
WalletRecoveryTool/
├── btcrecover/
│   ├── addresses-BTC.db    # Bitcoin AddressDB
│   ├── addresses-ETH.db    # Ethereum AddressDB
│   └── seedrecover.py      # BTCRecover script
```

## Setup Instructions

### 1. Install Python 3.10+
```powershell
# Using winget (recommended)
winget install Python.Python.3.12

# Or download from https://python.org
```

### 2. Install BTCRecover Dependencies
```powershell
cd btcrecover
pip install -r requirements.txt
```

### 3. Download AddressDB (Optional but Recommended)
- Visit https://cryptoguide.tips/btcrecover-addressdbs/
- Download appropriate database files
- Extract to `btcrecover/` folder

## Using BTCRecover in Your App

### From the Mnemonic Tab

1. **Enter your mnemonic** in the text box (even if incorrect)
2. **Click "ADVANCED SEED RECOVERY"**
3. **Choose mode**:
   - **With AddressDB**: Automatic mode, no manual input needed
   - **Interactive Mode**: Manual setup required

### What Happens

**With AddressDB:**
- BTCRecover runs with `--addressdb` flag
- Automatically tries typo corrections
- Checks against funded addresses
- No user interaction required

**Without AddressDB:**
- Interactive setup prompts
- Must provide known addresses
- Slower but still powerful

## Advanced Usage

### Command Line Options Used

```bash
# With AddressDB
python seedrecover.py --addressdb addresses-BTC.db --wallet-type bip39 --bip39 --no-dupchecks --big-typos 2 --typos 2 --typos-swap --dsw --no-eta

# Interactive Mode
python seedrecover.py --dsw --no-eta
```

### Creating Custom AddressDB

For advanced users who want to create their own databases:

```bash
# From blockchain data (requires full node)
python create-address-db.py --dblength 31  # For Bitcoin

# From address list (CSV/TSV)
python create-address-db.py --inputlist addresses.txt --dblength 29
```

## Troubleshooting

### Python Not Found
- Ensure Python 3.10+ is installed
- Add Python to PATH environment variable
- Restart the application

### BTCRecover Not Found
- Ensure `btcrecover/` folder exists
- Check that `seedrecover.py` is present
- Run `pip install -r requirements.txt`

### AddressDB Issues
- Verify database file is not corrupted
- Check file permissions
- Ensure sufficient RAM (database size + 2GB)

### Performance Tips
- Use SSD storage for AddressDB
- Close other memory-intensive applications
- Consider smaller blockchains for testing (Litecoin, Dogecoin)

## Integration Benefits

✅ **Simultaneous Operation**: Your app and BTCRecover run together
✅ **Complementary Features**: Your app for quick checks, BTCRecover for deep recovery
✅ **GPU Acceleration**: BTCRecover supports OpenCL for faster processing
✅ **Multiple Cryptos**: Works with Bitcoin, Ethereum, Litecoin, and more
✅ **Typo Correction**: Automatically fixes common seed mistakes

## Support

For BTCRecover-specific issues:
- GitHub: https://github.com/3rdIteration/btcrecover
- Documentation: https://btcrecover.readthedocs.io/
- YouTube Tutorials: https://www.youtube.com/playlist?list=PL7rfJxwogDzmd1IanPrmlTg3ewAIq-BZJ