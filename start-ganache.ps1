$ErrorActionPreference = 'Stop'
Write-Host 'Starting Ganache Ethereum RPC on http://127.0.0.1:8545...'
npx --yes ganache --server.host 127.0.0.1 --server.port 8545 --wallet.deterministic --wallet.totalAccounts 10
