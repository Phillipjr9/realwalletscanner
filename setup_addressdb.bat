@echo off
echo ========================================
echo BTCRecover AddressDB Setup Helper
echo ========================================
echo.
echo This script helps you download and set up AddressDB files for BTCRecover.
echo.
echo AddressDB files allow BTCRecover to work without known wallet addresses.
echo.

set "BTCR_DIR=%~dp0btcrecover"
set "DOWNLOAD_URL=https://cryptoguide.tips/btcrecover-addressdbs/"

echo Checking for BTCRecover directory...
if not exist "%BTCR_DIR%" (
    echo ERROR: btcrecover directory not found!
    echo Please ensure BTCRecover is properly cloned.
    pause
    exit /b 1
)

echo.
echo Available AddressDB files:
echo ===============================
echo 1. Bitcoin (addresses-BTC.db) - 16GB - Most comprehensive
echo 2. Ethereum (addresses-ETH.db) - 4GB - Good for ETH recovery
echo 3. Litecoin (addresses-LTC.db) - 4GB - Smaller, faster
echo 4. Dogecoin (addresses-DOGE.db) - 1GB - Fastest, good for testing
echo 5. Bitcoin Cash (addresses-BCH.db) - 4GB - For BCH recovery
echo.
echo Download from: %DOWNLOAD_URL%
echo.
echo Instructions:
echo 1. Open your browser and visit the URL above
echo 2. Download the desired .db file(s)
echo 3. Extract/unzip the downloaded file
echo 4. Copy the .db file to: %BTCR_DIR%\
echo.
echo Example: Copy addresses-BTC.db to %BTCR_DIR%\addresses-BTC.db
echo.

:check_existing
echo Checking for existing AddressDB files...
set "FOUND_DB="
if exist "%BTCR_DIR%\*.db" (
    echo Found existing database file(s):
    for %%f in ("%BTCR_DIR%\*.db") do echo   - %%~nxf
    set "FOUND_DB=1"
) else (
    echo No AddressDB files found.
)

echo.
if defined FOUND_DB (
    echo Great! You have AddressDB file(s) ready.
    echo The Wallet Recovery Tool will automatically use them.
) else (
    echo No AddressDB files detected.
    echo Download one from %DOWNLOAD_URL% for best results.
)

echo.
echo Press any key to open the download page in your browser...
pause >nul

start "" "%DOWNLOAD_URL%"

echo.
echo After downloading and extracting:
echo 1. Copy the .db file to %BTCR_DIR%
echo 2. Restart the Wallet Recovery Tool
echo 3. Use "ADVANCED SEED RECOVERY" button
echo.
pause