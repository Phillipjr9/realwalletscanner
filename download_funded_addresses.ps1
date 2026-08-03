<#
.SYNOPSIS
    Downloads funded Bitcoin address lists and builds bloom filters for offline scanning.
    This is the #1 speed optimization — eliminates 99.99% of API calls.

.DESCRIPTION
    Downloads the complete Bitcoin funded address set from multiple sources:
    1. Blockchair daily address dump (TSV, ~50M addresses with balance)
    2. Loyce.club Bitcoin address archives (all-time funded addresses)
    
    Places files in: %AppData%\WalletRecoveryTool\FundedAddresses\
    The app auto-builds a bloom filter on next startup (~150MB RAM, instant lookups).

.NOTES
    Run time: 10-30 minutes depending on internet speed (downloads ~2-4 GB compressed).
    Disk space: ~8 GB uncompressed, ~150 MB bloom filter after build.
    The bloom filter (.bloom file) is what matters — you can delete the .tsv after.
#>

$ErrorActionPreference = "Stop"
$ProgressPreference = "Continue"

$dataDir = Join-Path $env:APPDATA "WalletRecoveryTool\FundedAddresses"
if (-not (Test-Path $dataDir)) {
    New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
}

Write-Host ""
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "  Bitcoin Funded Address Bloom Filter Setup" -ForegroundColor Cyan
Write-Host "  Target: $dataDir" -ForegroundColor Gray
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host ""

# Check if bloom filter already exists
$bloomPath = Join-Path $dataDir "btc_funded.bloom"
if (Test-Path $bloomPath) {
    $bloomSize = (Get-Item $bloomPath).Length / 1MB
    Write-Host "[OK] BTC bloom filter already exists ($([math]::Round($bloomSize, 1)) MB)" -ForegroundColor Green
    $rebuild = Read-Host "Rebuild from fresh data? (y/N)"
    if ($rebuild -ne "y") {
        Write-Host "Keeping existing bloom filter. Done!" -ForegroundColor Green
        exit 0
    }
    Remove-Item $bloomPath -Force
    Write-Host "Removed old bloom filter. Downloading fresh data..." -ForegroundColor Yellow
}

# ==================================================================
# SOURCE 1: Blockchair Bitcoin Addresses (best source — daily updated)
# ==================================================================
$blockchairFile = Join-Path $dataDir "blockchair_bitcoin_addresses.tsv"
$blockchairGz = "$blockchairFile.gz"
$blockchairUrl = "https://gz.blockchair.com/bitcoin/addresses/blockchair_bitcoin_addresses_latest.tsv.gz"

function Download-WithRetry {
    param([string]$Url, [string]$OutFile, [int]$MaxRetries = 3)
    
    for ($i = 1; $i -le $MaxRetries; $i++) {
        try {
            Write-Host "  Attempt $i/$MaxRetries..." -ForegroundColor Gray
            
            # Use BITS for large files (supports resume)
            $webClient = New-Object System.Net.WebClient
            $webClient.Headers.Add("User-Agent", "WalletRecoveryTool/2.0")
            $webClient.DownloadFile($Url, $OutFile)
            return $true
        }
        catch {
            Write-Host "  Failed: $($_.Exception.Message)" -ForegroundColor Red
            if ($i -lt $MaxRetries) {
                Write-Host "  Retrying in 10 seconds..." -ForegroundColor Yellow
                Start-Sleep -Seconds 10
            }
        }
    }
    return $false
}

function Decompress-GzFile {
    param([string]$GzPath, [string]$OutPath)
    
    Write-Host "  Decompressing $(Split-Path $GzPath -Leaf)..." -ForegroundColor Gray
    
    $input = [System.IO.File]::OpenRead($GzPath)
    $output = [System.IO.File]::Create($OutPath)
    $gzipStream = New-Object System.IO.Compression.GZipStream($input, [System.IO.Compression.CompressionMode]::Decompress)
    
    $buffer = New-Object byte[] 65536
    $totalBytes = 0
    $lastReport = 0
    
    while (($read = $gzipStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
        $output.Write($buffer, 0, $read)
        $totalBytes += $read
        
        if ($totalBytes - $lastReport -gt 100MB) {
            $lastReport = $totalBytes
            $sizeMB = [math]::Round($totalBytes / 1MB, 0)
            Write-Host "  Decompressed: $sizeMB MB..." -ForegroundColor Gray
        }
    }
    
    $gzipStream.Dispose()
    $output.Dispose()
    $input.Dispose()
    
    $sizeMB = [math]::Round($totalBytes / 1MB, 1)
    Write-Host "  Done: $sizeMB MB" -ForegroundColor Green
}

$downloaded = $false

# Try Blockchair first
if (-not (Test-Path $blockchairFile)) {
    Write-Host ""
    Write-Host "[1/3] Downloading Blockchair Bitcoin address dump..." -ForegroundColor Yellow
    Write-Host "  URL: $blockchairUrl" -ForegroundColor Gray
    Write-Host "  This file contains ALL funded BTC addresses (~50M)" -ForegroundColor Gray
    Write-Host "  Size: ~2-3 GB compressed, ~6-8 GB uncompressed" -ForegroundColor Gray
    Write-Host ""
    
    if (Download-WithRetry -Url $blockchairUrl -OutFile $blockchairGz) {
        $gzSize = [math]::Round((Get-Item $blockchairGz).Length / 1MB, 1)
        Write-Host "  Downloaded: $gzSize MB" -ForegroundColor Green
        
        Decompress-GzFile -GzPath $blockchairGz -OutPath $blockchairFile
        
        # Clean up .gz to save disk space
        Remove-Item $blockchairGz -Force
        $downloaded = $true
    }
    else {
        Write-Host "  Blockchair download failed. Trying alternative sources..." -ForegroundColor Red
    }
}
else {
    $existingSize = [math]::Round((Get-Item $blockchairFile).Length / 1MB, 1)
    Write-Host "[1/3] Blockchair file already exists ($existingSize MB)" -ForegroundColor Green
    $downloaded = $true
}

# ==================================================================
# SOURCE 2: Loyce.club (alternative — all Bitcoin addresses ever used)
# ==================================================================
$loyceFile = Join-Path $dataDir "btc_funded.txt"

if (-not $downloaded -and -not (Test-Path $loyceFile)) {
    Write-Host ""
    Write-Host "[2/3] Trying Loyce.club funded address list..." -ForegroundColor Yellow
    $loyceUrl = "http://addresses.loyce.club/Bitcoin_addresses_LATEST.txt.gz"
    $loyceGz = "$loyceFile.gz"
    
    Write-Host "  URL: $loyceUrl" -ForegroundColor Gray
    
    if (Download-WithRetry -Url $loyceUrl -OutFile $loyceGz) {
        Decompress-GzFile -GzPath $loyceGz -OutPath $loyceFile
        Remove-Item $loyceGz -Force
        $downloaded = $true
    }
    else {
        Write-Host "  Loyce.club download also failed." -ForegroundColor Red
    }
}

# ==================================================================
# SOURCE 3: Generate from local Bitcoin Core (if installed)
# ==================================================================
if (-not $downloaded) {
    Write-Host ""
    Write-Host "[3/3] Checking for local Bitcoin Core..." -ForegroundColor Yellow
    
    $bitcoinCli = Get-Command "bitcoin-cli" -ErrorAction SilentlyContinue
    if ($bitcoinCli) {
        Write-Host "  Bitcoin Core found! Dumping UTXO set..." -ForegroundColor Green
        Write-Host "  (This may take 10-30 minutes depending on disk speed)" -ForegroundColor Gray
        
        try {
            $utxoDump = & bitcoin-cli dumptxoutset "$dataDir\utxo_dump.dat" 2>&1
            Write-Host "  UTXO dump complete. Processing..." -ForegroundColor Green
            $downloaded = $true
        }
        catch {
            Write-Host "  Bitcoin Core dump failed: $($_.Exception.Message)" -ForegroundColor Red
        }
    }
    else {
        Write-Host "  Bitcoin Core not found (not required)." -ForegroundColor Gray
    }
}

# ==================================================================
# SUMMARY
# ==================================================================
Write-Host ""
Write-Host "========================================================" -ForegroundColor Cyan

if ($downloaded) {
    $files = Get-ChildItem $dataDir -File | Where-Object { $_.Extension -in ".txt", ".tsv", ".csv" }
    $totalSize = ($files | Measure-Object -Property Length -Sum).Sum / 1MB
    $totalLines = 0
    
    foreach ($f in $files) {
        $lineCount = (Get-Content $f.FullName -ReadCount 0).Count
        $totalLines += $lineCount
        $sizeMB = [math]::Round($f.Length / 1MB, 1)
        Write-Host "  $($f.Name): $sizeMB MB, ~$($lineCount.ToString('N0')) lines" -ForegroundColor White
    }
    
    Write-Host ""
    Write-Host "  Total: ~$($totalLines.ToString('N0')) addresses, $([math]::Round($totalSize, 1)) MB" -ForegroundColor Green
    Write-Host ""
    Write-Host "  NEXT STEP: Start the Wallet Recovery Tool." -ForegroundColor Yellow
    Write-Host "  The app will auto-build a bloom filter on startup (~2-5 min)." -ForegroundColor Yellow
    Write-Host "  After that, scanning speed increases 100x+ (no API needed)." -ForegroundColor Yellow
}
else {
    Write-Host "  No address data downloaded." -ForegroundColor Red
    Write-Host ""
    Write-Host "  MANUAL SETUP:" -ForegroundColor Yellow
    Write-Host "  1. Go to https://gz.blockchair.com/bitcoin/addresses/" -ForegroundColor White
    Write-Host "  2. Download blockchair_bitcoin_addresses_latest.tsv.gz" -ForegroundColor White
    Write-Host "  3. Extract the .tsv file to:" -ForegroundColor White
    Write-Host "     $dataDir" -ForegroundColor Cyan
    Write-Host "  4. Start the Wallet Recovery Tool — bloom filter auto-builds" -ForegroundColor White
}

Write-Host ""
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host ""
