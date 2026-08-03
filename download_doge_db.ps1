# Download Dogecoin AddressDB for BTCRecover
# This script downloads the 1GB Dogecoin AddressDB for testing BTCRecover integration

param(
    [string]$OutputPath = ".\btcrecover"
)

Write-Host "🐕 BTCRecover Dogecoin AddressDB Downloader" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host ""

# Create output directory if it doesn't exist
if (!(Test-Path $OutputPath)) {
    New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
    Write-Host "Created directory: $OutputPath" -ForegroundColor Green
}

# Dogecoin AddressDB URL (you may need to update this if the URL changes)
$downloadUrl = "https://cryptoguide.tips/btcrecover-addressdbs/addresses-DOGE.db"

$outputFile = Join-Path $OutputPath "addresses-DOGE.db"

Write-Host "Downloading Dogecoin AddressDB..." -ForegroundColor Yellow
Write-Host "From: $downloadUrl" -ForegroundColor Gray
Write-Host "To: $outputFile" -ForegroundColor Gray
Write-Host ""

try {
    # Check if file already exists
    if (Test-Path $outputFile) {
        $fileSize = (Get-Item $outputFile).Length
        Write-Host "File already exists: $outputFile" -ForegroundColor Yellow
        Write-Host "Size: $([math]::Round($fileSize / 1GB, 2)) GB" -ForegroundColor Yellow

        $overwrite = Read-Host "Do you want to re-download? (y/n)"
        if ($overwrite -ne 'y') {
            Write-Host "Download cancelled." -ForegroundColor Red
            exit
        }
    }

    # Start download with progress
    Write-Host "Starting download (this may take several minutes)..." -ForegroundColor Green

    $webClient = New-Object System.Net.WebClient
    $webClient.DownloadFile($downloadUrl, $outputFile)

    # Verify download
    if (Test-Path $outputFile) {
        $fileSize = (Get-Item $outputFile).Length
        Write-Host "" -ForegroundColor Green
        Write-Host "✅ Download completed successfully!" -ForegroundColor Green
        Write-Host "📁 File: $outputFile" -ForegroundColor Green
        Write-Host "📊 Size: $([math]::Round($fileSize / 1GB, 2)) GB" -ForegroundColor Green
        Write-Host "" -ForegroundColor Green
        Write-Host "🎉 Your Wallet Recovery Tool can now use BTCRecover with AddressDB!" -ForegroundColor Green
        Write-Host "   Run the app and click 'ADVANCED SEED RECOVERY' to test it." -ForegroundColor Cyan
    } else {
        throw "Download failed - file not found after download"
    }

} catch {
    Write-Host "" -ForegroundColor Red
    Write-Host "❌ Download failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "" -ForegroundColor Red
    Write-Host "Manual download instructions:" -ForegroundColor Yellow
    Write-Host "1. Open browser: https://cryptoguide.tips/btcrecover-addressdbs/" -ForegroundColor Yellow
    Write-Host "2. Download: addresses-DOGE.db" -ForegroundColor Yellow
    Write-Host "3. Extract to: $OutputPath" -ForegroundColor Yellow
    Write-Host "" -ForegroundColor Yellow
}

Write-Host "Press any key to continue..." -ForegroundColor Gray
$null = $host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")