$ErrorActionPreference = 'Continue'
Write-Host "============================================================================" -ForegroundColor Cyan
Write-Host "  CODEHAVEN QUANTUM WALLET RECOVERY WORKSTATION v2.7 - DIRECT BUILD & RUN" -ForegroundColor Cyan
Write-Host "============================================================================" -ForegroundColor Cyan

# 1. Stop any running instance
Stop-Process -Name WalletRecoveryTool -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

# 2. Locate repo folder
$candidates = @(
    "C:\Users\progressive\Downloads\realwalletscanner-master\realwalletscanner-master",
    (Join-Path $env:USERPROFILE "Downloads\realwalletscanner-master\realwalletscanner-master"),
    (Join-Path $env:USERPROFILE "Downloads\realwalletscanner-master"),
    (Get-Location).Path
)

$dir = $null
foreach ($c in $candidates) {
    if (Test-Path (Join-Path $c "WalletRecoveryTool.csproj")) {
        $dir = $c
        break
    }
}

if (-not $dir) {
    $dir = "C:\Users\progressive\Downloads\realwalletscanner-master\realwalletscanner-master"
}

Write-Host "[1/4] Syncing latest v2.7 files into: $dir" -ForegroundColor Yellow
$ref = "e07cf12fd95a14f5fba2c44be42fc3c762a7bf0b"
try {
    $commitInfo = Invoke-RestMethod -UseBasicParsing -Uri "https://api.github.com/repos/Phillipjr9/realwalletscanner/commits/master" -UserAgent "WRT-Updater" -ErrorAction Stop
    if ($commitInfo -and $commitInfo.sha) {
        $ref = $commitInfo.sha
        Write-Host "      Resolved latest master commit: $($ref.Substring(0, 7))" -ForegroundColor Cyan
    }
} catch {
    Write-Host "      Using verified commit ref: $($ref.Substring(0, 7))" -ForegroundColor Cyan
}
$base = "https://raw.githubusercontent.com/Phillipjr9/realwalletscanner/$ref"
$files = @(
    "OneClick-Install-And-Run.bat",
    "WalletRecoveryTool.csproj",
    "App.xaml.cs",
    "Windows/MainWindow.xaml",
    "Windows/MainWindow.xaml.cs",
    "Core/StringShield.cs",
    "Core/EmbeddedWorkstationServer.cs",
    "Core/ApiUsageTracker.cs",
    "Core/MnemonicTracker.cs",
    "Core/SmartWalletHunter.cs",
    "Core/WalletRecoveryEngine.cs",
    "Core/CryptoWalletGenerator.cs",
    "Core/BalanceChecker.cs",
    "WebWorkstation/index.html",
    "WebWorkstation/server.js"
)

foreach ($f in $files) {
    $target = Join-Path $dir ($f -replace '/', '\')
    $parent = Split-Path -Parent $target
    if (-not (Test-Path $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $url = "$base/$f"
    $resp = Invoke-WebRequest -UseBasicParsing -Uri $url -ErrorAction Stop
    if ($f.EndsWith(".bat")) {
        $text = ($resp.Content -replace "`r`n", "`n") -replace "`n", "`r`n"
        [System.IO.File]::WriteAllText($target, $text, [System.Text.Encoding]::ASCII)
    } else {
        $bytes = $resp.RawContentStream.ToArray()
        if ($bytes.Length -gt 0) {
            [System.IO.File]::WriteAllBytes($target, $bytes)
        }
    }
    Write-Host "      Updated $f" -ForegroundColor Green
}

# 3. Clean install directory while preserving smtp.local.json
$installDir = Join-Path $env:LOCALAPPDATA "WalletRecoveryTool"
$appDataConfigDir = Join-Path $env:APPDATA "WalletRecoveryTool"
if (-not (Test-Path $appDataConfigDir)) {
    New-Item -ItemType Directory -Path $appDataConfigDir -Force | Out-Null
}
$staleClusterCookie = Join-Path $appDataConfigDir "cluster_cookie.json"
if (Test-Path $staleClusterCookie) {
    Remove-Item $staleClusterCookie -Force -ErrorAction SilentlyContinue
    Write-Host "      Removed legacy cluster_cookie.json (restored 100% single-node scanning)" -ForegroundColor Green
}

$smtpInstallPath = Join-Path $installDir "smtp.local.json"
$smtpBackupPath = Join-Path $appDataConfigDir "smtp.local.json"
if (Test-Path $smtpInstallPath) {
    Copy-Item $smtpInstallPath $smtpBackupPath -Force -ErrorAction SilentlyContinue
}

Write-Host "[2/4] Cleaning old build artifacts in $installDir ..." -ForegroundColor Yellow
if (Test-Path $installDir) {
    Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $installDir -Force | Out-Null

if (Test-Path $smtpBackupPath) {
    Copy-Item $smtpBackupPath $smtpInstallPath -Force -ErrorAction SilentlyContinue
}

$binDir = Join-Path $dir "bin"
$objDir = Join-Path $dir "obj"
if (Test-Path $binDir) { Remove-Item $binDir -Recurse -Force -ErrorAction SilentlyContinue }
if (Test-Path $objDir) { Remove-Item $objDir -Recurse -Force -ErrorAction SilentlyContinue }

# 4. Build directly in PowerShell (visible output)
Write-Host "[3/4] Compiling & publishing self-contained v2.7 executable..." -ForegroundColor Cyan
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    if (Test-Path "C:\Program Files\dotnet\dotnet.exe") {
        $env:PATH += ";C:\Program Files\dotnet"
    }
}

$csproj = Join-Path $dir "WalletRecoveryTool.csproj"
& dotnet publish $csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=false -o $installDir
if ($LASTEXITCODE -ne 0) {
    Write-Host "      Fallback to standard Release build..." -ForegroundColor Yellow
    & dotnet build $csproj -c Release -o $installDir
}

# Copy icon and WebWorkstation assets
$icoPath = Join-Path $dir "wallet.ico"
if (Test-Path $icoPath) {
    Copy-Item $icoPath (Join-Path $installDir "wallet.ico") -Force -ErrorAction SilentlyContinue
}
$webSrc = Join-Path $dir "WebWorkstation"
$webDst = Join-Path $installDir "WebWorkstation"
if (Test-Path $webSrc) {
    Copy-Item $webSrc $webDst -Recurse -Force -ErrorAction SilentlyContinue
}

# Update Desktop shortcut
try {
    $ws = New-Object -ComObject WScript.Shell
    $desktop = [Environment]::GetFolderPath('Desktop')
    $lnk = $ws.CreateShortcut((Join-Path $desktop 'Wallet Recovery Workstation.lnk'))
    $lnk.TargetPath = Join-Path $installDir 'WalletRecoveryTool.exe'
    $lnk.WorkingDirectory = $installDir
    if (Test-Path (Join-Path $installDir 'wallet.ico')) {
        $lnk.IconLocation = Join-Path $installDir 'wallet.ico'
    }
    $lnk.Description = 'CodeHaven Quantum Wallet Recovery Workstation v2.7'
    $lnk.Save()
} catch {}

# 5. Launch WalletRecoveryTool.exe directly and verify
$exePath = Join-Path $installDir "WalletRecoveryTool.exe"
if (Test-Path $exePath) {
    Write-Host "[4/4] Launching $exePath ..." -ForegroundColor Green
    $proc = Start-Process -FilePath $exePath -WorkingDirectory $installDir -PassThru
    Start-Sleep -Seconds 4
    $running = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
    $startupLog = Join-Path $env:APPDATA "WalletRecoveryTool\Logs\app_startup.log"
    if (Test-Path $startupLog) {
        Write-Host "--- Startup Log ($startupLog) ---" -ForegroundColor Gray
        Get-Content $startupLog | Select-Object -Last 15 | ForEach-Object { Write-Host "  $_" -ForegroundColor Gray }
    }
    if ($running) {
        Write-Host "============================================================================" -ForegroundColor Green
        Write-Host "  SUCCESS: WalletRecoveryTool.exe is running (PID: $($running.Id))!" -ForegroundColor Green
        Write-Host "============================================================================" -ForegroundColor Green
    } else {
        Write-Host "WARNING: Process exited (ExitCode: $($proc.ExitCode)). Checking Windows Event Log..." -ForegroundColor Red
        Get-WinEvent -FilterHashtable @{LogName='Application'; Level=2} -MaxEvents 3 -ErrorAction SilentlyContinue |
            Where-Object { $_.ProviderName -match '\.NET|Application Error' } |
            ForEach-Object { Write-Host $_.Message -ForegroundColor Red }
    }
} else {
    Write-Host "ERROR: $exePath was not created by dotnet build/publish!" -ForegroundColor Red
}
