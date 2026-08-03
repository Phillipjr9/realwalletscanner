# Simple log monitor for WalletRecoveryTool debug logs
# Usage: powershell -ExecutionPolicy Bypass -File tools\log_monitor.ps1

$logDirs = @(
    Join-Path $env:USERPROFILE "Documents\WalletRecoveryTool\Logs",
    Join-Path ([Environment]::GetFolderPath('ApplicationData')) "WalletRecoveryTool\Logs"
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

# Keywords indicating errors or warnings
$errKeywords = @('ERROR','Exception','LimitReached','rate limit','backoff','Throttle reconfigured','cooldown','Api call failed','Timeout','failed','WARN','RateLimit','RATE LIMIT')
$matches = @()
foreach ($k in $errKeywords) {
    $m = Select-String -InputObject $content -Pattern $k -SimpleMatch -AllMatches
    if ($m) { $matches += $m }
}

if ($matches.Count -gt 0) {
    Write-Host "\nPotential issues found:" -ForegroundColor Red
    $matches | Select-Object -Unique | ForEach-Object { Write-Host $_.Line.Trim() }
} else {
    Write-Host "\nNo obvious error keywords found." -ForegroundColor Green
}

# Check bloom filter load counts
$bloomRegex = 'bloom filter loaded:\s*(\d+)' 
$emptyRegex = 'known-empty filter loaded:\s*(\d+)' 
$bloom = Select-String -InputObject $content -Pattern $bloomRegex -AllMatches -CaseSensitive:$false
$empty = Select-String -InputObject $content -Pattern $emptyRegex -AllMatches -CaseSensitive:$false
if ($bloom) { $bloom | ForEach-Object { Write-Host "Bloom: " $_.Matches[0].Groups[1].Value } }
if ($empty) { $empty | ForEach-Object { Write-Host "Known-empty: " $_.Matches[0].Groups[1].Value } }

# Parse simple progress lines like: [00:00:01]  Checked:    123  |  Speed:   45/s  |  Found: 0
$progRegex = '\[.*?\]\s+Checked:\s*(\d+)\s*\|\s*Speed:\s*(\d+)\/s'
$progMatches = [regex]::Matches($content, $progRegex)

if ($progMatches.Count -gt 0) {
    $speeds = @()
    foreach ($pm in $progMatches) { $speeds += [int]$pm.Groups[2].Value }
    $max = ($speeds | Measure-Object -Maximum).Maximum
    $min = ($speeds | Measure-Object -Minimum).Minimum
    $avg = [math]::Round(($speeds | Measure-Object -Average).Average,2)
    Write-Host "\nProgress speed (min/avg/max): $min/$avg/$max per sec"

    # Detect long stretches of very low speed
    $lowStreak = 0; $maxLowStreak = 0
    foreach ($s in $speeds) {
        if ($s -le 1) { $lowStreak++ } else { if ($lowStreak -gt $maxLowStreak) { $maxLowStreak = $lowStreak }; $lowStreak = 0 }
    }
    if ($lowStreak -gt $maxLowStreak) { $maxLowStreak = $lowStreak }
    if ($maxLowStreak -ge 10) {
        Write-Host "Warning: detected a low-speed streak of $maxLowStreak samples (<=1/s)" -ForegroundColor Yellow
    }
}
else { Write-Host "No progress lines detected in log." }

# Final exit code: 0=clean,1=warnings,2=errors/missing
if ($matches.Count -gt 0) { exit 1 } else { exit 0 }
