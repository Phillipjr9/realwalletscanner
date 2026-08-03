@echo off
REM CLI mode launcher for WalletRecoveryTool
REM Usage: cli.bat [--mode smart|random|mnemonic] [--coins btc,eth,sol] [--threads N] [--limit N]
REM Example: cli.bat --mode smart --coins btc,eth --threads 8

set EXE=bin\Release\net8.0-windows\WalletRecoveryTool.exe

if not exist "%EXE%" (
    echo Building...
    dotnet build WalletRecoveryTool.csproj --configuration Release -v q
)

"%EXE%" --cli %*
