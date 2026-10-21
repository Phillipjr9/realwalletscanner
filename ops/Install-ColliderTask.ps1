<#
.SYNOPSIS
    Installs theCollider pool worker as a Windows scheduled task that starts at
    boot and restarts itself if it dies. Collision Protocol / JLP v3 pool.

.DESCRIPTION
    Registers a SYSTEM-level task (so it survives logoff and needs no stored
    password) with restart-on-failure, no execution time limit, and
    start-when-available. Optionally stops the machine from sleeping.

    Nothing is downloaded. Point -ColliderPath at a binary you extracted from
    the official release and hash-checked yourself.

.PARAMETER Worker
    Bitcoin payout address. Every DP is credited to this string and payouts are
    sent here. The pool does NOT validate it - a typo is unrecoverable.

.PARAMETER ColliderPath
    Full path to collider.exe.

.PARAMETER Gpus
    Optional GPU ids, e.g. "0" or "0,1". Omit to auto-detect all.

.PARAMETER PreventSleep
    Sets AC standby/hibernate timeouts to Never so the hunt keeps running.

.EXAMPLE
    .\Install-ColliderTask.ps1 -Worker 36vjpBDd2yT6XwuBJPXgr7ksPBxhyCk4aP `
        -ColliderPath C:\collider\collider.exe -PreventSleep
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Worker,
    [Parameter(Mandatory = $true)][string]$ColliderPath,
    [string]$Gpus = "",
    [switch]$PreventSleep,
    [string]$PoolUrl = "jlps://pool.collisionprotocol.com:17403",
    [string]$TaskName = "Collider Pool Worker"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $ColliderPath)) { throw "collider.exe not found: $ColliderPath" }
$ColliderPath = (Resolve-Path $ColliderPath).Path

# ---- Address sanity -------------------------------------------------------
# The pool accepts any [A-Za-z0-9_.-]{1,64} string, so check the checksum here.
$validator = Join-Path $PSScriptRoot "validate-worker.py"
$py = Get-Command python, python3, py -ErrorAction SilentlyContinue | Select-Object -First 1
if ($py -and (Test-Path $validator)) {
    & $py.Source $validator $Worker
    if ($LASTEXITCODE -ne 0) { throw "Address failed checksum validation. Refusing to install." }
} else {
    if ($Worker -notmatch '^(1|3)[1-9A-HJ-NP-Za-km-z]{25,34}$' -and
        $Worker -notmatch '^bc1[02-9ac-hj-np-z]{8,87}$') {
        throw "Address does not look like a Bitcoin mainnet address: $Worker"
    }
    Write-Warning "Checksum not verified (python3 + ops/validate-worker.py not found)."
    Write-Warning "A typo here silently sends your pool share to a dead address."
}

# ---- Task action ----------------------------------------------------------
$argsList = @("--pool", $PoolUrl, "--worker", $Worker, "--no-tui", "--verbose")
if ($Gpus) { $argsList += @("--gpus", $Gpus) }
$argString = ($argsList | ForEach-Object { if ($_ -match '\s') { '"{0}"' -f $_ } else { $_ } }) -join " "

$action = New-ScheduledTaskAction -Execute $ColliderPath `
    -Argument $argString `
    -WorkingDirectory (Split-Path $ColliderPath)

$triggers = @(
    New-ScheduledTaskTrigger -AtStartup
    New-ScheduledTaskTrigger -AtLogOn
)

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -MultipleInstances IgnoreNew `
    -RestartCount 999 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit ([TimeSpan]::Zero)

$principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest

Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $triggers `
    -Settings $settings -Principal $principal -Description "theCollider pool worker (Collision Protocol)" | Out-Null

Start-ScheduledTask -TaskName $TaskName
Write-Host "`nRegistered and started task '$TaskName'." -ForegroundColor Green

if ($PreventSleep) {
    powercfg /change standby-timeout-ac 0    | Out-Null
    powercfg /change hibernate-timeout-ac 0  | Out-Null
    powercfg /change monitor-timeout-ac 15   | Out-Null
    Write-Host "Sleep/hibernate disabled on AC power (monitor still blanks at 15m)." -ForegroundColor Green
}

Write-Host ""
Write-Host "Log:       Event Viewer > Applications and Services Logs (or run the task manually to watch stdout)" -ForegroundColor Cyan
Write-Host "Status:    Get-ScheduledTask -TaskName '$TaskName' | Get-ScheduledTaskInfo"
Write-Host "Stop:      Stop-ScheduledTask -TaskName '$TaskName'"
Write-Host "Dashboard: https://collisionprotocol.com/dashboard?worker=$Worker"
Write-Host ""
Write-Warning "Windows Update reboots will interrupt the hunt. Set active hours to cover your working day."
Write-Warning "Running as SYSTEM means the client has admin rights it does not need. Use a spare/local account if you prefer."
