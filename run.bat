@echo off
echo Starting Wallet Recovery Tool...
echo.

REM Set dotnet path
set DOTNET_PATH="C:\Program Files\dotnet\dotnet.exe"

REM Check if the application exists
if not exist "bin\Release\net8.0-windows\WalletRecoveryTool.exe" (
    echo Application not found. Building first...
    call build.bat
    if %ERRORLEVEL% NEQ 0 exit /b 1
)

REM Run the application
echo Launching Wallet Recovery Tool...
%DOTNET_PATH% run --project WalletRecoveryTool.csproj --configuration Release

pause