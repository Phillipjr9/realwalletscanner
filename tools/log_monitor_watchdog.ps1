param(
    [int]$IntervalMinutes = 5,
    [int]$Iterations = 0,
    [string]$MonitorScript = (Join-Path $PSScriptRoot "log_monitor_v4.ps1"),
    [string]$AlertsFile = (Join-Path $env:USERPROFILE "Documents\WalletRecoveryTool\Logs\scan_alerts.log"),
    [switch]$UseDesktopNotification
)

$ErrorActionPreference = 'Stop'

function Ensure-Directory([string]$Path) {
    if ($Path) {
        $dir = Split-Path -Path $Path -Parent
        if ($dir -and -not (Test-Path $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
    }
}

function Write-Alert([string]$Message) {
    Ensure-Directory -Path $AlertsFile
    $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    $line = "[$stamp] $Message"
    Add-Content -Path $AlertsFile -Value $line
    Write-Host $line -ForegroundColor Yellow

    if ($UseDesktopNotification) {
        try {
            Add-Type -AssemblyName System.Windows.Forms -ErrorAction SilentlyContinue | Out-Null
            $notify = New-Object System.Windows.Forms.NotifyIcon
            $notify.Icon = [System.Drawing.SystemIcons]::Warning
            $notify.BalloonTipTitle = 'WalletRecoveryTool scan alert'
            $notify.BalloonTipText = $Message
            $notify.Visible = $true
            $notify.ShowBalloonTip(3000)
            Start-Sleep -Milliseconds 3500
            $notify.Dispose()
        }
        catch {
            Write-Host "Desktop notification not available: $($_.Exception.Message)" -ForegroundColor DarkYellow
        }
    }
}

function Invoke-MonitorOnce {
    $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    Write-Host "[$stamp] Running monitor..." -ForegroundColor Cyan

    if (-not (Test-Path $MonitorScript)) {
        Write-Alert "Monitor script not found: $MonitorScript"
        return 1
    }

    $output = & powershell -ExecutionPolicy Bypass -File $MonitorScript 2>&1
    $exitCode = $LASTEXITCODE
    $text = ($output | Out-String).Trim()

    if ($text) {
        Write-Host $text
    }

    if ($exitCode -ne 0) {
        Write-Alert "Monitor reported issues (exit code $exitCode)."
        Ensure-Directory -Path $AlertsFile
        Add-Content -Path $AlertsFile -Value "--- monitor output ---"
        Add-Content -Path $AlertsFile -Value $text
        Add-Content -Path $AlertsFile -Value "--- end output ---"
        return $exitCode
    }

    Write-Host "[$stamp] Monitor completed without issues." -ForegroundColor Green
    return 0
}

Ensure-Directory -Path $AlertsFile
$iteration = 0

while ($true) {
    $iteration++
    Invoke-MonitorOnce | Out-Null

    if ($Iterations -gt 0 -and $iteration -ge $Iterations) {
        break
    }

    if ($IntervalMinutes -le 0) {
        break
    }

    $delaySeconds = [Math]::Max(1, $IntervalMinutes * 60)
    Write-Host "Sleeping for $IntervalMinutes minute(s) before next check..." -ForegroundColor DarkGray
    Start-Sleep -Seconds $delaySeconds
}
