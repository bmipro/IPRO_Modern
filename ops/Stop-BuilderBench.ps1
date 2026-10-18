# Stop-BuilderBench.ps1 -- 585: stops the three apps ops\Start-BuilderBench.ps1 started, by their ports,
# and only a dotnet process listening there, then closes the windows they ran in. MySQL, the storage
# emulator and the owner's own builders (5087, 5088) are left alone. The builder's drafts stay in their
# database and folder for next time.
#
# Usage:  powershell -ExecutionPolicy Bypass -File ops\Stop-BuilderBench.ps1
param(
    [int]$WebPort = 5110,
    [int]$AdminPort = 5210,
    [int]$BuilderPort = 5310
)

# The window each app ran in: the powershell that started it (Start-BuilderBench.ps1), found by the
# process rather than by its title, which a window inside Windows Terminal does not show.
$windows = @()
foreach ($port in @($WebPort, $AdminPort, $BuilderPort)) {
    $owners = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique
    if (-not $owners) { Write-Host "Nothing is listening on $port."; continue }
    foreach ($id in $owners) {
        $process = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($process -and $process.ProcessName -match '^(dotnet|IPRO\.Web|IPRO\.Admin|Builder\.Web)$') {
            $parent = (Get-CimInstance Win32_Process -Filter "ProcessId=$id" -ErrorAction SilentlyContinue).ParentProcessId
            if ($parent) {
                $shell = Get-CimInstance Win32_Process -Filter "ProcessId=$parent" -ErrorAction SilentlyContinue
                if ($shell -and $shell.Name -eq 'powershell.exe' -and $shell.CommandLine -match '-NoExit' -and $shell.CommandLine -match '(IPRO\.Web|IPRO\.Admin|Builder\.Web)\.dll') { $windows += $shell.ProcessId }
            }
            Stop-Process -Id $id -Confirm:$false
            Write-Host "Stopped $($process.ProcessName) on $port."
        } else {
            Write-Host "Port $port belongs to $($process.ProcessName) ($id), not to the bench: left running." -ForegroundColor Yellow
        }
    }
}

foreach ($shell in ($windows | Select-Object -Unique)) {
    Stop-Process -Id $shell -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host "Closed the window the app ran in (process $shell)."
}
