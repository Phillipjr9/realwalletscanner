@echo off
echo Building Wallet Recovery Tool...
echo.

REM Set dotnet path
set DOTNET_PATH="C:\Program Files\dotnet\dotnet.exe"

REM Check if dotnet is installed
%DOTNET_PATH% --version >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: .NET SDK not found. Please install .NET 8.0 SDK from https://dotnet.microsoft.com/download
    pause
    exit /b 1
)

REM Restore packages
echo Restoring NuGet packages...
%DOTNET_PATH% restore WalletRecoveryTool.csproj
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: Failed to restore packages
    pause
    exit /b 1
)

REM Build application
echo Building application...
%DOTNET_PATH% build WalletRecoveryTool.csproj --configuration Release
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: Build failed
    pause
    exit /b 1
)

echo.
echo ===================================
echo BUILD SUCCESSFUL!
echo ===================================
echo.
echo To run the application:
echo %DOTNET_PATH% run --configuration Release
echo.
echo Or navigate to: bin\Release\net8.0-windows\WalletRecoveryTool.exe
echo.
pause