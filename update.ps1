$ErrorActionPreference = 'Continue'
Write-Host "============================================================================" -ForegroundColor Cyan
Write-Host "  CODEHAVEN QUANTUM WALLET RECOVERY WORKSTATION v2.7 - AUTO UPDATER" -ForegroundColor Cyan
Write-Host "============================================================================" -ForegroundColor Cyan

Stop-Process -Name WalletRecoveryTool -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

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

Write-Host "[1/2] Syncing latest v2.7 files into: $dir" -ForegroundColor Yellow
$base = "https://raw.githubusercontent.com/Phillipjr9/realwalletscanner/master"
$files = @(
    "OneClick-Install-And-Run.bat",
    "WalletRecoveryTool.csproj",
    "App.xaml.cs",
    "Windows/MainWindow.xaml",
    "Windows/MainWindow.xaml.cs",
    "Core/StringShield.cs",
    "Core/EmbeddedWorkstationServer.cs",
    "Core/ApiUsageTracker.cs",
    "WebWorkstation/index.html",
    "WebWorkstation/server.js"
)

foreach ($f in $files) {
    $target = Join-Path $dir ($f -replace '/', '\')
    $parent = Split-Path -Parent $target
    if (-not (Test-Path $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    Invoke-WebRequest -UseBasicParsing -Uri "$base/$f" -OutFile $target
    Write-Host "      Updated $f" -ForegroundColor Green
}

Write-Host "[2/2] Launching OneClick-Install-And-Run.bat..." -ForegroundColor Cyan
$batPath = Join-Path $dir "OneClick-Install-And-Run.bat"
Start-Process -FilePath "cmd.exe" -ArgumentList "/c `"`"$batPath`"`"" -WorkingDirectory $dir
