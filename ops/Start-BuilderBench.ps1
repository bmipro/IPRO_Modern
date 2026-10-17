# Start-BuilderBench.ps1 -- 585: the new website builder (Beta) on this PC, wired to iPro's portal and
# SuperAdmin, so the whole path can be tried by hand: switch the Beta on for an adviser, open the
# builder from My Website, edit, see it on the site, send it, review it, publish it, put it back.
#
# Three apps, each in its own minimized window (ops\Stop-BuilderBench.ps1 stops them and closes the
# windows):
#   portal      http://localhost:5110   (this checkout's IPRO.Web)
#   SuperAdmin  http://localhost:5210   (this checkout's IPRO.Admin)
#   builder     http://localhost:5310   (the builder's ipro/customer-beta checkout, hosted mode)
#
# Each app runs from its own published copy under -BenchRoot\apps, never from a checkout's build
# folders, so building and testing go on while the bench runs. Run this again to pick up new code.
# The portal and SuperAdmin use the local development database (ipro_crm) as any local run does; the
# builder keeps its drafts in its own database (ipro_builder_bench) and its files under -BenchRoot\data.
# Nothing here touches the owner's own builders (ports 5087 and 5088) or anything on the internet.
#
# Usage:  powershell -ExecutionPolicy Bypass -File ops\Start-BuilderBench.ps1
param(
    [string]$BuilderRepo = "C:\Users\admin\Projects\bb",
    [string]$BenchRoot = "C:\Users\admin\Projects\bb-bench",
    [int]$WebPort = 5110,
    [int]$AdminPort = 5210,
    [int]$BuilderPort = 5310
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

function Test-Port([int]$port) {
    (Test-NetConnection -ComputerName 127.0.0.1 -Port $port -WarningAction SilentlyContinue).TcpTestSucceeded
}

foreach ($port in @($WebPort, $AdminPort, $BuilderPort)) {
    if (Test-Port $port) {
        Write-Host "Port $port is already in use. Run ops\Stop-BuilderBench.ps1 first, or choose other ports." -ForegroundColor Red
        exit 1
    }
}
if (-not (Test-Path (Join-Path $BuilderRepo "src\Builder.Web\Builder.Web.csproj"))) {
    Write-Host "The builder's checkout was not found at $BuilderRepo (give -BuilderRepo)." -ForegroundColor Red
    exit 1
}

# 1. MySQL and the storage emulator, as for any local run (MySQL is a Windows service here; it is only
#    started when nothing answers on 3306, and never stopped).
& (Join-Path $PSScriptRoot "Start-LocalEnv.ps1") 6>$null | Out-Null

# 2. The portal's and SuperAdmin's local settings are not in Git. A worktree has none of its own: they
#    are copied from the main checkout the first time (they stay on this PC, ignored by Git).
$common = (git -C $repo rev-parse --path-format=absolute --git-common-dir).Trim()
$mainCheckout = Split-Path -Parent $common
foreach ($app in @("IPRO.Web", "IPRO.Admin")) {
    $target = Join-Path $repo "src\$app\appsettings.Development.json"
    if (-not (Test-Path $target)) {
        $source = Join-Path $mainCheckout "src\$app\appsettings.Development.json"
        if (-not (Test-Path $source)) {
            Write-Host "No local settings for $app (looked in $source). See DOCS\16_LOCAL_DEV.md." -ForegroundColor Red
            exit 1
        }
        Copy-Item $source $target
        Write-Host "Copied the local settings of $app into this checkout." -ForegroundColor Yellow
    }
}

# 3. The key the portal and the builder share for this bench (iPro's erase calls). New at every start,
#    never written to a file.
$bytes = New-Object byte[] 24
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$key = -join ($bytes | ForEach-Object { $_.ToString("x2") })

# 4. A published copy of each app, run from there.
$apps = Join-Path $BenchRoot "apps"
$dataRoot = Join-Path $BenchRoot "data"
New-Item -ItemType Directory -Force $apps, $dataRoot | Out-Null
$copies = @(
    @{ Name = "portal"; Project = (Join-Path $repo "src\IPRO.Web\IPRO.Web.csproj"); Dll = "IPRO.Web.dll" },
    @{ Name = "superadmin"; Project = (Join-Path $repo "src\IPRO.Admin\IPRO.Admin.csproj"); Dll = "IPRO.Admin.dll" },
    @{ Name = "builder"; Project = (Join-Path $BuilderRepo "src\Builder.Web\Builder.Web.csproj"); Dll = "Builder.Web.dll" }
)
foreach ($copy in $copies) {
    $out = Join-Path $apps $copy.Name
    Write-Host "Publishing the $($copy.Name)..." -ForegroundColor Cyan
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    dotnet publish $copy.Project -c Debug -o $out -v q -nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host "The $($copy.Name) did not build." -ForegroundColor Red; exit 1 }
}
# A working copy's App_Data is not the bench's (the builder keeps its own under data).
$builderAppData = Join-Path $apps "builder\App_Data"
if (Test-Path $builderAppData) { Remove-Item -Recurse -Force $builderAppData }

$portal = "http://localhost:$WebPort"
$admin = "http://localhost:$AdminPort"
$builder = "http://localhost:$BuilderPort"

function Start-App([string]$title, [string]$folder, [string]$dll, [hashtable]$environment, [string]$arguments) {
    $sets = ($environment.GetEnumerator() | ForEach-Object { "`$env:$($_.Key) = '$($_.Value)'" }) -join "; "
    $command = "`$Host.UI.RawUI.WindowTitle = '$title'; Set-Location '$folder'; $sets; dotnet '$dll' $arguments"
    Start-Process powershell -WindowStyle Minimized -ArgumentList "-NoExit", "-Command", $command | Out-Null
}

Start-App "Bench portal $WebPort" (Join-Path $apps "portal") "IPRO.Web.dll" @{
    "ASPNETCORE_ENVIRONMENT" = "Development"
    "App__BaseUrl" = $portal
    "Builder__BaseUrl" = $builder
    "Builder__ServiceKey" = $key
} "--urls $portal"

Start-App "Bench SuperAdmin $AdminPort" (Join-Path $apps "superadmin") "IPRO.Admin.dll" @{
    "ASPNETCORE_ENVIRONMENT" = "Development"
    "App__BaseUrl" = $portal
    "App__WebBaseUrl" = $portal
} "--urls $admin"

Start-App "Bench builder $BuilderPort" (Join-Path $apps "builder") "Builder.Web.dll" @{
    "ASPNETCORE_ENVIRONMENT" = "Development"
    "Hosting__Mode" = "Hosted"
    "Hosting__DataRoot" = $dataRoot
    "Hosting__Database" = "MySql"
    "ConnectionStrings__Builder" = "server=127.0.0.1;port=3306;database=ipro_builder_bench;user=root;SslMode=None;AllowPublicKeyRetrieval=True"
    "Hosting__IproBaseUrl" = $portal
    "Hosting__ServiceKey" = $key
    "AllowedHosts" = "localhost"
} "--urls $builder"

Write-Host "Starting (this takes a minute)..." -ForegroundColor Cyan
foreach ($port in @($BuilderPort, $AdminPort, $WebPort)) {
    $up = $false
    foreach ($i in 1..120) { Start-Sleep -Seconds 1; if (Test-Port $port) { $up = $true; break } }
    if (-not $up) { Write-Host "Nothing answers on $port yet: open its window to see why." -ForegroundColor Red }
}

Write-Host ""
Write-Host "The bench is running:" -ForegroundColor Green
Write-Host "  portal      $portal/portal"
Write-Host "  SuperAdmin  $admin"
Write-Host "  builder     $builder  (it opens from My Website, not on its own)"
Write-Host ""
Write-Host "What to do next: DOCS\BUILDER_BETA_BENCH.md. Stop it with ops\Stop-BuilderBench.ps1."
