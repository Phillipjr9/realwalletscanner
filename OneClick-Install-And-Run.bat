@echo off
setlocal EnableDelayedExpansion
title CodeHaven Wallet Recovery Workstation - One-Click Windows Installer and Launcher
color 0B

echo ============================================================================
echo   CODEHAVEN QUANTUM WALLET RECOVERY WORKSTATION - ONE-CLICK WINDOWS SETUP
echo   Combined Desktop WPF Suite + Local Web Workstation + AtlanticBB SMTP
echo ============================================================================
echo.

set "REPO_DIR=%~dp0"
set "INSTALL_DIR=%LOCALAPPDATA%\WalletRecoveryTool"
set "APPDATA_CONFIG_DIR=%APPDATA%\WalletRecoveryTool"
if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"
if not exist "%APPDATA_CONFIG_DIR%" mkdir "%APPDATA_CONFIG_DIR%"

REM Close any running instance so Windows does not lock WalletRecoveryTool.exe during build
taskkill /F /IM WalletRecoveryTool.exe >nul 2>&1

REM Automatically pull latest updates from master if git is available
where git >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    pushd "%REPO_DIR%"
    git pull origin master >nul 2>&1
    popd
)

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
REM STEP 2: Locate or Build Single-File Windows Executable (WalletRecoveryTool.exe)
REM ----------------------------------------------------------------------------
echo.
echo [2/4] Preparing combined Windows application executable...

if exist "%REPO_DIR%WalletRecoveryTool.exe" (
    echo       Found pre-compiled WalletRecoveryTool.exe! Copying to %INSTALL_DIR%...
    copy /Y "%REPO_DIR%WalletRecoveryTool.exe" "%INSTALL_DIR%\WalletRecoveryTool.exe" >nul
    goto :CREATE_SHORTCUTS
)

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

echo       Building self-contained single-file Windows executable ^(win-x64^)...
dotnet publish "%REPO_DIR%WalletRecoveryTool.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%INSTALL_DIR%"
if %ERRORLEVEL% NEQ 0 (
    echo       Single-file publish fallback: running standard Release build...
    dotnet build "%REPO_DIR%WalletRecoveryTool.csproj" -c Release -o "%INSTALL_DIR%"
)

:CREATE_SHORTCUTS
if exist "%REPO_DIR%wallet.ico" copy /Y "%REPO_DIR%wallet.ico" "%INSTALL_DIR%\wallet.ico" >nul 2>&1
if exist "%REPO_DIR%WebWorkstation" (
    if not exist "%INSTALL_DIR%\WebWorkstation" mkdir "%INSTALL_DIR%\WebWorkstation"
    xcopy /E /I /Y "%REPO_DIR%WebWorkstation\*" "%INSTALL_DIR%\WebWorkstation\" >nul 2>&1
)

REM ----------------------------------------------------------------------------
REM STEP 3: Create Desktop and Start Menu One-Click Shortcuts
REM ----------------------------------------------------------------------------
echo.
echo [3/4] Creating Desktop and Start Menu shortcuts...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ws = New-Object -ComObject WScript.Shell; foreach ($dir in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) { $lnk = $ws.CreateShortcut((Join-Path $dir 'Wallet Recovery Workstation.lnk')); $lnk.TargetPath = '%INSTALL_DIR%\WalletRecoveryTool.exe'; $lnk.WorkingDirectory = '%INSTALL_DIR%'; if (Test-Path '%INSTALL_DIR%\wallet.ico') { $lnk.IconLocation = '%INSTALL_DIR%\wallet.ico'; }; $lnk.Description = 'CodeHaven Quantum Wallet Recovery Workstation'; $lnk.Save(); }"
echo       Desktop shortcut created: "Wallet Recovery Workstation"

REM ----------------------------------------------------------------------------
REM STEP 4: Launch Combined Workstation (WPF App + Optional Local Web Dashboard)
REM ----------------------------------------------------------------------------
echo.
echo [4/4] Launching Wallet Recovery Workstation...

where node >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    if exist "%INSTALL_DIR%\WebWorkstation\server.js" (
        echo       Starting background Local Web Workstation on http://localhost:3000 ...
        pushd "%INSTALL_DIR%\WebWorkstation"
        if not exist "node_modules\bip39" call npm install --silent >nul 2>&1
        start "Wallet Recovery Web Workstation" /MIN cmd /c "node server.js"
        popd
        timeout /t 1 >nul
        start "" msedge --app=http://localhost:3000 --window-size=1580,920 >nul 2>&1
    )
)

if exist "%INSTALL_DIR%\WalletRecoveryTool.exe" (
    start "" "%INSTALL_DIR%\WalletRecoveryTool.exe"
    echo.
    echo ============================================================================
    echo   INSTALLATION COMPLETE!
    echo   - Desktop Shortcut: "Wallet Recovery Workstation"
    echo   - Installed Path:   %INSTALL_DIR%\WalletRecoveryTool.exe
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
