@echo off
setlocal EnableDelayedExpansion
title CodeHaven Wallet Recovery Workstation - One-Click Windows Installer and Launcher
color 0B

echo ============================================================================
echo   CODEHAVEN QUANTUM WALLET RECOVERY WORKSTATION v2.7 - ONE-CLICK SETUP
echo   Combined Desktop WPF Suite + Local Web Workstation + AtlanticBB SMTP
echo ============================================================================
echo.

set "REPO_DIR=%~dp0"
set "INSTALL_DIR=%LOCALAPPDATA%\WalletRecoveryTool"
set "APPDATA_CONFIG_DIR=%APPDATA%\WalletRecoveryTool"
if not exist "%APPDATA_CONFIG_DIR%" mkdir "%APPDATA_CONFIG_DIR%"

REM ----------------------------------------------------------------------------
REM STEP 0: Stop Running Instances & Clean Old Build Artifacts
REM ----------------------------------------------------------------------------
echo [0/4] Stopping running instances and cleaning old runtime files...
taskkill /F /IM WalletRecoveryTool.exe /T >nul 2>&1
timeout /t 1 /nobreak >nul

REM Preserve existing smtp.local.json before wiping stale DLLs in INSTALL_DIR
if exist "%INSTALL_DIR%\smtp.local.json" (
    copy /Y "%INSTALL_DIR%\smtp.local.json" "%APPDATA_CONFIG_DIR%\smtp.local.json" >nul 2>&1
)
if exist "%INSTALL_DIR%" rmdir /S /Q "%INSTALL_DIR%" >nul 2>&1
mkdir "%INSTALL_DIR%" >nul 2>&1
if exist "%APPDATA_CONFIG_DIR%\smtp.local.json" (
    copy /Y "%APPDATA_CONFIG_DIR%\smtp.local.json" "%INSTALL_DIR%\smtp.local.json" >nul 2>&1
)
if exist "%TEMP%\.net\WalletRecoveryTool" rmdir /S /Q "%TEMP%\.net\WalletRecoveryTool" >nul 2>&1
if exist "%REPO_DIR%bin" rmdir /S /Q "%REPO_DIR%bin" >nul 2>&1
if exist "%REPO_DIR%obj" rmdir /S /Q "%REPO_DIR%obj" >nul 2>&1

REM ----------------------------------------------------------------------------
REM STEP 1: Configure AtlanticBB SMTP Profile (smtp.atlanticbb.net:587)
REM ----------------------------------------------------------------------------
if not exist "%INSTALL_DIR%\smtp.local.json" (
    echo [1/4] Configuring AtlanticBB SMTP Profile ^(smtp.atlanticbb.net:587^)...
    set /p "USER_SMTP_PASS=Enter SMTP password for smokie1969@atlanticbb.net (or press Enter to set later in UI): "
    (
        echo {
        echo   "enabled": true,
        echo   "method": "SMTP",
        echo   "smtpHost": "smtp.atlanticbb.net",
        echo   "smtpPort": 587,
        echo   "smtpSecure": true,
        echo   "smtpUser": "smokie1969@atlanticbb.net",
        echo   "smtpPass": "!USER_SMTP_PASS!",
        echo   "recipientEmail": "smokie1969@atlanticbb.net"
        echo }
    ) > "%INSTALL_DIR%\smtp.local.json"
    copy /Y "%INSTALL_DIR%\smtp.local.json" "%APPDATA_CONFIG_DIR%\smtp.local.json" >nul 2>&1
    copy /Y "%INSTALL_DIR%\smtp.local.json" "%REPO_DIR%smtp.local.json" >nul 2>&1
    if exist "%REPO_DIR%WebWorkstation" (
        copy /Y "%INSTALL_DIR%\smtp.local.json" "%REPO_DIR%WebWorkstation\smtp_config.json" >nul 2>&1
    )
    echo       SMTP configuration saved locally.
) else (
    echo [1/4] Existing SMTP profile detected in %INSTALL_DIR%\smtp.local.json
    copy /Y "%INSTALL_DIR%\smtp.local.json" "%REPO_DIR%smtp.local.json" >nul 2>&1
    if exist "%REPO_DIR%WebWorkstation" (
        copy /Y "%INSTALL_DIR%\smtp.local.json" "%REPO_DIR%WebWorkstation\smtp_config.json" >nul 2>&1
    )
)

REM ----------------------------------------------------------------------------
REM STEP 2: Self-Contained Release Publish (win-x64, No Single-File Cache)
REM ----------------------------------------------------------------------------
echo.
echo [2/4] Building self-contained v2.7 Windows application executable...

REM Check for dotnet CLI
where dotnet >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    if exist "C:\Program Files\dotnet\dotnet.exe" (
        set "PATH=%PATH%;C:\Program Files\dotnet"
    ) else (
        echo       .NET 8 SDK not found. Installing .NET 8 SDK automatically via winget...
        winget install Microsoft.DotNet.SDK.8 --accept-package-agreements --accept-source-agreements
        set "PATH=%PATH%;C:\Program Files\dotnet"
    )
)

dotnet publish "%REPO_DIR%WalletRecoveryTool.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=false -o "%INSTALL_DIR%"
if %ERRORLEVEL% NEQ 0 (
    echo       Fallback to standard Release build...
    dotnet build "%REPO_DIR%WalletRecoveryTool.csproj" -c Release -o "%INSTALL_DIR%"
)

if exist "%REPO_DIR%wallet.ico" copy /Y "%REPO_DIR%wallet.ico" "%INSTALL_DIR%\wallet.ico" >nul 2>&1
if exist "%REPO_DIR%WebWorkstation" (
    if not exist "%INSTALL_DIR%\WebWorkstation" mkdir "%INSTALL_DIR%\WebWorkstation"
    xcopy /E /I /Y "%REPO_DIR%WebWorkstation\*" "%INSTALL_DIR%\WebWorkstation\" >nul 2>&1
)

REM ----------------------------------------------------------------------------
REM STEP 3: Create Desktop and Start Menu One-Click Shortcuts
REM ----------------------------------------------------------------------------
echo.
echo [3/4] Updating Desktop and Start Menu shortcuts...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ws = New-Object -ComObject WScript.Shell; foreach ($dir in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) { $lnk = $ws.CreateShortcut((Join-Path $dir 'Wallet Recovery Workstation.lnk')); $lnk.TargetPath = '%INSTALL_DIR%\WalletRecoveryTool.exe'; $lnk.WorkingDirectory = '%INSTALL_DIR%'; if (Test-Path '%INSTALL_DIR%\wallet.ico') { $lnk.IconLocation = '%INSTALL_DIR%\wallet.ico'; }; $lnk.Description = 'CodeHaven Quantum Wallet Recovery Workstation v2.7'; $lnk.Save(); }"
echo       Desktop shortcut updated: "Wallet Recovery Workstation"

REM ----------------------------------------------------------------------------
REM STEP 4: Launch Combined Workstation (WPF App)
REM ----------------------------------------------------------------------------
echo.
echo [4/4] Launching Wallet Recovery Workstation v2.7...

if exist "%INSTALL_DIR%\WalletRecoveryTool.exe" (
    start "" /D "%INSTALL_DIR%" "%INSTALL_DIR%\WalletRecoveryTool.exe"
    echo.
    echo ============================================================================
    echo   INSTALLATION COMPLETE (v2.7)!
    echo   - Desktop Shortcut: "Wallet Recovery Workstation"
    echo   - Installed Path:   %INSTALL_DIR%\WalletRecoveryTool.exe
    echo   - Local Workstation: http://127.0.0.1:3000
    echo   - AtlanticBB SMTP:  smtp.atlanticbb.net:587 ^(smokie1969@atlanticbb.net^)
    echo ============================================================================
    timeout /t 4
    exit /b 0
) else (
    echo.
    echo ERROR: WalletRecoveryTool.exe was not produced. Check compiler output above.
    pause
    exit /b 1
)
