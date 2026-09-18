# Start-LocalEnv.ps1 -- brings up the local development environment.
#
# Prereqs (one-time, already done 2026-08-07, see DOCS/16_LOCAL_DEV.md):
#   - Portable MySQL 8.0.44 in C:\Users\admin\ipro-local\  (matches prod 8.0.21 major version)
#   - Azurite installed globally via npm (blob storage emulator)
#   - src/IPRO.Web/appsettings.Development.json + src/IPRO.Admin/appsettings.Development.json
#     (gitignored; template in DOCS/16_LOCAL_DEV.md)
#
# Usage:  powershell -ExecutionPolicy Bypass -File ops\Start-LocalEnv.ps1

$mysqlBase = "C:\Users\admin\ipro-local"
$mysqld = Join-Path $mysqlBase "mysql-8.0.44-winx64\bin\mysqld.exe"

function Test-Port([int]$port) {
    (Test-NetConnection -ComputerName 127.0.0.1 -Port $port -WarningAction SilentlyContinue).TcpTestSucceeded
}

if (Test-Port 3306) {
    Write-Host "MySQL already running on 3306" -ForegroundColor Green
} else {
    Write-Host "Starting MySQL..." -ForegroundColor Yellow
    Start-Process -FilePath $mysqld -ArgumentList "--defaults-file=$mysqlBase\my.ini", "--console" -WindowStyle Minimized
}

if (Test-Port 10000) {
    Write-Host "Azurite already running on 10000" -ForegroundColor Green
} else {
    Write-Host "Starting Azurite..." -ForegroundColor Yellow
    # "azurite.cmd", not "azurite": npm drops an extensionless shell shim beside the .cmd, and
    # Start-Process resolves the bare name to THAT file, which Windows opens in Notepad instead of
    # running. The emulator never started, and IPRO.Web then sat for minutes retrying
    # 127.0.0.1:10000 in two start-up steps before it would listen (found 2026-09-18).
    Start-Process -FilePath "azurite.cmd" -ArgumentList "--silent", "--location", "$mysqlBase\azurite", "--blobHost", "127.0.0.1", "--queueHost", "127.0.0.1", "--tableHost", "127.0.0.1" -WindowStyle Minimized
    $up = $false
    foreach ($i in 1..15) { Start-Sleep -Seconds 1; if (Test-Port 10000) { $up = $true; break } }
    if ($up) { Write-Host "Azurite is listening on 10000" -ForegroundColor Green }
    else { Write-Host "Azurite did NOT come up on 10000 -- IPRO.Web will stall at start-up until it does." -ForegroundColor Red }
}

Write-Host ""
Write-Host "Apps (run each in its own terminal, from the repo root):" -ForegroundColor Cyan
Write-Host '  dotnet run --project src/IPRO.Web/IPRO.Web.csproj   --no-launch-profile -- --environment Development --urls http://localhost:5100'
Write-Host '  dotnet run --project src/IPRO.Admin/IPRO.Admin.csproj --no-launch-profile -- --environment Development --urls http://localhost:5200'
