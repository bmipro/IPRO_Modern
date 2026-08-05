# Renew-Certs.ps1 -- renew the Let's Encrypt certs and push them to Azure App Service.
#
# WHAT THIS REPLACES
# Renewal used to be: run run_app.ps1, watch for a TXT value, paste it into cPanel, wait, then
# separately upload the PFX to Azure and bind it -- remembering which app lives in which resource
# group. Several of those steps were only in someone's head. This does the whole sequence and
# stops with a clear message if any part of it fails.
#
# WHAT IT STILL NEEDS A HUMAN FOR
# lego is configured with --dns manual, so it prints a DNS TXT record that must be published in
# cPanel before validation can continue. This script surfaces that record prominently and waits.
# Making it fully unattended means switching to lego's cPanel DNS provider (--dns cpanel), which
# needs a cPanel API token. See DOCS/20_CERTIFICATES.md.
#
# USAGE
#   powershell -File Renew-Certs.ps1 -Preflight     # check everything without contacting Let's Encrypt
#   powershell -File Renew-Certs.ps1                # renew every domain that is within -WithinDays
#   powershell -File Renew-Certs.ps1 -Domain app.iproadvisers.com -Force
#
# TO ADD A DOMAIN: add an entry to $Targets. RunScript is the existing per-domain lego wrapper.

param(
    [string]$Domain,
    [int]$WithinDays = 30,
    [switch]$Force,
    [switch]$Preflight
)

$ErrorActionPreference = "Continue"
$LegoDir = "C:\Users\admin\lego"

$Targets = @(
    @{
        Domain        = "app.iproadvisers.com"
        WebApp        = "ipro-prod-web"
        ResourceGroup = "ipro-production"
        RunScript     = "run_app.ps1"
        ChallengeFile = "challenge.txt"
        # Newsletter image URLs point here. An expiry breaks images in already-delivered mail.
        Critical      = $true
    },
    @{
        Domain        = "admin.iproadvisers.com"
        WebApp        = "ipro-prod-admin"
        ResourceGroup = "ipro-prod-admin_group"
        RunScript     = "run_admin.ps1"
        ChallengeFile = "admin-challenge.txt"
        Critical      = $false
    }
)

function Write-Step { param([string]$Text) Write-Output "`n=== $Text ===" }

# The PFX password is read out of the existing run_*.ps1 rather than restated here, so there is
# exactly one copy of it on disk instead of three.
function Get-PfxPassword {
    param([string]$ScriptName)
    $path = Join-Path $LegoDir $ScriptName
    if (-not (Test-Path $path)) { return $null }
    $m = Select-String -Path $path -Pattern '--pfx\.password\s+"([^"]+)"' | Select-Object -First 1
    if ($null -eq $m) { return $null }
    return $m.Matches[0].Groups[1].Value
}

function Get-RemainingDays {
    param([string]$HostName)
    $client = $null; $sslStream = $null
    try {
        $client = New-Object System.Net.Sockets.TcpClient
        $c = $client.BeginConnect($HostName, 443, $null, $null)
        if (-not $c.AsyncWaitHandle.WaitOne(10000, $false)) { return $null }
        $client.EndConnect($c)
        $sslStream = New-Object System.Net.Security.SslStream($client.GetStream(), $false, { $true })
        $sslStream.AuthenticateAsClient($HostName)
        $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]$sslStream.RemoteCertificate
        return [math]::Floor(($cert.NotAfter - (Get-Date)).TotalDays)
    } catch { return $null } finally {
        if ($sslStream) { $sslStream.Dispose() }
        if ($client) { $client.Dispose() }
    }
}

# ---------------------------------------------------------------- preflight

Write-Step "Preflight"
$fail = $false

if (Test-Path (Join-Path $LegoDir "lego.exe")) { Write-Output "  ok    lego.exe present" }
else { Write-Output "  FAIL  lego.exe missing from $LegoDir"; $fail = $true }

$azOk = $false
try {
    $acct = az account show --query "name" -o tsv 2>$null
    if ($LASTEXITCODE -eq 0 -and $acct) { Write-Output "  ok    az CLI signed in ($acct)"; $azOk = $true }
    else { Write-Output "  FAIL  az CLI not signed in -- run: az login"; $fail = $true }
} catch { Write-Output "  FAIL  az CLI not available"; $fail = $true }

$selected = if ($Domain) { $Targets | Where-Object { $_.Domain -eq $Domain } } else { $Targets }
if (-not $selected) { Write-Output "  FAIL  no target matching '$Domain'"; $fail = $true }

foreach ($t in $selected) {
    $pw = Get-PfxPassword -ScriptName $t.RunScript
    if ($pw) { Write-Output "  ok    $($t.Domain): PFX password readable from $($t.RunScript)" }
    else { Write-Output "  FAIL  $($t.Domain): could not read PFX password from $($t.RunScript)"; $fail = $true }

    if (-not (Test-Path (Join-Path $LegoDir $t.RunScript))) {
        Write-Output "  FAIL  $($t.Domain): $($t.RunScript) missing"; $fail = $true
    }

    if ($azOk) {
        $hostnames = az webapp config hostname list --webapp-name $t.WebApp --resource-group $t.ResourceGroup --query "[].name" -o tsv 2>$null
        if ($LASTEXITCODE -eq 0 -and $hostnames -match [regex]::Escape($t.Domain)) {
            Write-Output "  ok    $($t.Domain): bound to $($t.WebApp) in $($t.ResourceGroup)"
        } else {
            Write-Output "  FAIL  $($t.Domain): not found on $($t.WebApp)/$($t.ResourceGroup)"; $fail = $true
        }
    }

    $days = Get-RemainingDays -HostName $t.Domain
    if ($null -ne $days) { Write-Output "  info  $($t.Domain): $days days remaining" }
    else { Write-Output "  warn  $($t.Domain): could not read current certificate" }
}

if ($fail) { Write-Output "`nPreflight FAILED -- fix the above before renewing."; exit 1 }
Write-Output "`nPreflight passed."
if ($Preflight) { exit 0 }

# ---------------------------------------------------------------- renew

foreach ($t in $selected) {
    $days = Get-RemainingDays -HostName $t.Domain
    if (-not $Force -and $null -ne $days -and $days -gt $WithinDays) {
        Write-Step "$($t.Domain): skipping ($days days left, threshold $WithinDays). Use -Force to override."
        continue
    }

    Write-Step "$($t.Domain): requesting certificate"
    $challengePath = Join-Path $LegoDir $t.ChallengeFile
    Remove-Item $challengePath -ErrorAction SilentlyContinue

    # The wrapper is launched as a job so this script can watch for the challenge file and show the
    # TXT record the moment lego emits it, instead of the operator tailing a log by hand.
    $job = Start-Job -ScriptBlock {
        param($dir, $script)
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $dir $script)
    } -ArgumentList $LegoDir, $t.RunScript

    Write-Output "  waiting for the DNS challenge value..."
    $shown = $false
    for ($i = 0; $i -lt 60; $i++) {
        if (Test-Path $challengePath) {
            $content = Get-Content $challengePath -Raw
            if ($content -match 'RECORD_NAME=(\S+)' ) {
                $rn = $matches[1]
                if ($content -match 'RECORD_VALUE=(\S+)') {
                    $rv = $matches[1]
                    Write-Output ""
                    Write-Output "  ############################################################"
                    Write-Output "  PUBLISH THIS TXT RECORD IN cPANEL DNS, THEN WAIT:"
                    Write-Output ""
                    Write-Output "    Name:  $rn"
                    Write-Output "    Type:  TXT"
                    Write-Output "    Value: $rv"
                    Write-Output ""
                    Write-Output "  The wrapper polls Google DNS and continues on its own once the"
                    Write-Output "  record resolves (up to 25 minutes)."
                    Write-Output "  ############################################################"
                    Write-Output ""
                    $shown = $true
                    break
                }
            }
            if ($content -match 'NO_CHALLENGE_FOUND') {
                Write-Output "  FAIL  lego did not emit a challenge -- see $($t.RunScript) log"
                break
            }
        }
        Start-Sleep -Seconds 5
    }
    if (-not $shown) { Write-Output "  warn  never saw a challenge value; letting the wrapper run on" }

    Wait-Job $job -Timeout 2400 | Out-Null
    Receive-Job $job | Out-Null
    Remove-Job $job -Force -ErrorAction SilentlyContinue

    $pfx = Join-Path $LegoDir "certs\certificates\$($t.Domain).pfx"
    if (-not (Test-Path $pfx)) {
        Write-Output "  FAIL  no PFX produced at $pfx -- renewal did not complete."
        continue
    }
    # A stale PFX from the previous renewal would upload happily and change nothing, so require
    # that the file was actually written during this run.
    $age = (Get-Date) - (Get-Item $pfx).LastWriteTime
    if ($age.TotalMinutes -gt 60) {
        Write-Output "  FAIL  $pfx is $([math]::Round($age.TotalHours,1))h old -- that is the previous cert, not a new one."
        continue
    }

    Write-Step "$($t.Domain): uploading to Azure"
    $pw = Get-PfxPassword -ScriptName $t.RunScript
    $thumb = az webapp config ssl upload `
        --certificate-file $pfx `
        --certificate-password $pw `
        --name $t.WebApp `
        --resource-group $t.ResourceGroup `
        --query thumbprint -o tsv 2>$null

    if ($LASTEXITCODE -ne 0 -or -not $thumb) {
        Write-Output "  FAIL  upload failed for $($t.Domain)"
        continue
    }
    Write-Output "  uploaded, thumbprint $thumb"

    Write-Step "$($t.Domain): binding"
    az webapp config ssl bind `
        --certificate-thumbprint $thumb `
        --ssl-type SNI `
        --name $t.WebApp `
        --resource-group $t.ResourceGroup 2>$null | Out-Null

    if ($LASTEXITCODE -ne 0) { Write-Output "  FAIL  bind failed for $($t.Domain)"; continue }

    Start-Sleep -Seconds 15
    $newDays = Get-RemainingDays -HostName $t.Domain
    if ($null -ne $newDays -and $newDays -gt 60) {
        Write-Output "  DONE  $($t.Domain) now has $newDays days remaining."
    } else {
        Write-Output "  warn  $($t.Domain) still reports $newDays days -- binding may need a moment, re-check with Check-CertExpiry.ps1"
    }
}

Write-Step "Finished"
Write-Output "Re-check any time with:  powershell -File $LegoDir\Check-CertExpiry.ps1"
