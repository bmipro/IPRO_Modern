# Stop-BuilderBench.ps1 -- 585: stops the three apps ops\Start-BuilderBench.ps1 started, by their ports,
# and only a dotnet process listening there, then closes their "Bench ..." windows. MySQL, the storage
# emulator and the owner's own builders (5087, 5088) are left alone. The builder's drafts stay in their
# database and folder for next time.
#
# Usage:  powershell -ExecutionPolicy Bypass -File ops\Stop-BuilderBench.ps1
param(
    [int]$WebPort = 5110,
    [int]$AdminPort = 5210,
    [int]$BuilderPort = 5310
)

foreach ($port in @($WebPort, $AdminPort, $BuilderPort)) {
    $owners = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique
    if (-not $owners) { Write-Host "Nothing is listening on $port."; continue }
    foreach ($id in $owners) {
        $process = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($process -and $process.ProcessName -match '^(dotnet|IPRO\.Web|IPRO\.Admin|Builder\.Web)$') {
            Stop-Process -Id $id -Confirm:$false
            Write-Host "Stopped $($process.ProcessName) on $port."
        } else {
            Write-Host "Port $port belongs to $($process.ProcessName) ($id), not to the bench: left running." -ForegroundColor Yellow
        }
    }
}

# The windows the bench opened, by their titles.
Get-Process powershell -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowTitle -match "^Bench (portal|SuperAdmin|builder) ($WebPort|$AdminPort|$BuilderPort)$" } |
    ForEach-Object { Stop-Process -Id $_.Id -Confirm:$false; Write-Host "Closed the window ""$($_.MainWindowTitle)""." }
