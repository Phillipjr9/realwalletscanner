# Log monitor v4 - use regex word boundaries for keyword matching
$logDirs = @(
    (Join-Path -Path $env:USERPROFILE -ChildPath "Documents\WalletRecoveryTool\Logs"),
    (Join-Path -Path ([Environment]::GetFolderPath('ApplicationData')) -ChildPath "WalletRecoveryTool\Logs")
)

$logFile = $null
foreach ($d in $logDirs) {
    if (Test-Path $d) {
        $f = Get-ChildItem -Path $d -Filter "debug_log_*.txt" -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($f) { $logFile = $f.FullName; break }
    }
}

if (-not $logFile) {
    Write-Host "No debug log found in expected locations:" -ForegroundColor Yellow
    $logDirs | ForEach-Object { Write-Host "  $_" }
    exit 2
}

Write-Host "Inspecting log:" $logFile

$content = Get-Content -Path $logFile -Raw -ErrorAction Stop

# Regex to match clear error/warning tokens (word-boundary, case-insensitive)
$errPattern = '\b(ERROR|EXCEPTION|LIMITREACHED|rate limit|rate-limit|BACKOFF|THROTTLE|COOLDOWN|API call failed|TIMEOUT|FAILED|WARN|RATELIMIT)\b'
$errMatches = [regex]::Matches($content, $errPattern, 'IgnoreCase')

if ($errMatches.Count -gt 0) {
    Write-Host "\nPotential issues found:" -ForegroundColor Red
    $errMatches | ForEach-Object { Write-Host ($_).Value }
} else {
    Write-Host "\nNo obvious error keywords found." -ForegroundColor Green
}

# Bloom filter counts
$bloomRegex = 'bloom filter loaded:\s*(\d+)' 
$emptyRegex = 'known-empty filter loaded:\s*(\d+)' 
$bloomMatches = [regex]::Matches($content, $bloomRegex, 'IgnoreCase')
$emptyMatches = [regex]::Matches($content, $emptyRegex, 'IgnoreCase')
if ($bloomMatches.Count -gt 0) { $bloomMatches | ForEach-Object { Write-Host "Bloom: $($_.Groups[1].Value)" } }
if ($emptyMatches.Count -gt 0) { $emptyMatches | ForEach-Object { Write-Host "Known-empty: $($_.Groups[1].Value)" } }

# Parse simple progress lines
$progRegex = '\[.*?\]\s+Checked:\s*(\d+)\s*\|\s*Speed:\s*(\d+)\/s'
$progMatches = [regex]::Matches($content, $progRegex)

if ($progMatches.Count -gt 0) {
    $speeds = @()
    foreach ($pm in $progMatches) { $speeds += [int]$pm.Groups[2].Value }
    $max = ($speeds | Measure-Object -Maximum).Maximum
    $min = ($speeds | Measure-Object -Minimum).Minimum
    $avg = [math]::Round(($speeds | Measure-Object -Average).Average,2)
    Write-Host "\nProgress speed (min/avg/max): $min/$avg/$max per sec"

    $lowStreak = 0; $maxLowStreak = 0
    foreach ($s in $speeds) {
        if ($s -le 1) { $lowStreak++ } else { if ($lowStreak -gt $maxLowStreak) { $maxLowStreak = $lowStreak }; $lowStreak = 0 }
    }
    if ($lowStreak -gt $maxLowStreak) { $maxLowStreak = $lowStreak }
    if ($maxLowStreak -ge 10) {
        Write-Host "Warning: detected a low-speed streak of $maxLowStreak samples (<=1/s)" -ForegroundColor Yellow
    }
} else { Write-Host "No progress lines detected in log." }

# Exit code: 0=clean,1=issues found,2=no log
if ($errMatches.Count -gt 0) { exit 1 } else { exit 0 }
