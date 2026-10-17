# Check-AppSettings.ps1 -- 585: what an App Service app has set, by NAME and LENGTH only. No value is
# ever printed, so the output can be pasted anywhere. Read-only: it changes nothing.
#
#   powershell -ExecutionPolicy Bypass -File ops\Check-AppSettings.ps1 -App ipro-staging-web -ResourceGroup ipro-builder-staging
#   ... -Expect App__Environment,Email__OnlyTo,PayPal__IsSandbox     (names that must be set; listed as MISSING when not)
#   ... -Forbid APPLICATIONINSIGHTS_CONNECTION_STRING,Email__Ses__SecretAccessKey   (names that must NOT be set)
#
# Needs the Azure CLI signed in (az login) with read access to the app.
param(
    [Parameter(Mandatory = $true)][string]$App,
    [Parameter(Mandatory = $true)][string]$ResourceGroup,
    [string[]]$Expect = @(),
    [string[]]$Forbid = @()
)

$json = az webapp config appsettings list --name $App --resource-group $ResourceGroup -o json 2>$null
if ($LASTEXITCODE -ne 0 -or -not $json) { Write-Host "Could not read the settings of $App (signed in? the right resource group?)." -ForegroundColor Red; exit 1 }
$settings = $json | ConvertFrom-Json
$connections = (az webapp config connection-string list --name $App --resource-group $ResourceGroup -o json 2>$null) | ConvertFrom-Json

$rows = @()
foreach ($s in $settings) { $rows += [pscustomobject]@{ Name = $s.name; Kind = "setting"; Length = ([string]$s.value).Length } }
foreach ($c in $connections) { $rows += [pscustomobject]@{ Name = $c.name; Kind = "connection string"; Length = ([string]$c.value.value).Length } }
$rows | Sort-Object Name | Format-Table -AutoSize | Out-String -Width 160 | Write-Host

$names = $rows | ForEach-Object { $_.Name }
$bad = 0
foreach ($n in $Expect) {
    $row = $rows | Where-Object { $_.Name -eq $n } | Select-Object -First 1
    if (-not $row) { Write-Host "MISSING: $n" -ForegroundColor Red; $bad++ }
    elseif ($row.Length -eq 0) { Write-Host "EMPTY:   $n" -ForegroundColor Red; $bad++ }
}
foreach ($n in $Forbid) {
    if ($names -contains $n) { Write-Host "MUST NOT BE SET: $n" -ForegroundColor Red; $bad++ }
}
if ($bad -eq 0 -and ($Expect.Count + $Forbid.Count) -gt 0) { Write-Host "Every expected name is set, and none of the forbidden ones." -ForegroundColor Green }
exit $bad
