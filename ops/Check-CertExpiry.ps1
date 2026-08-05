# Check-CertExpiry.ps1 -- daily TLS expiry monitor for the IPRO domains.
#
# WHY THIS EXISTS
# Newsletter images are served from https://app.iproadvisers.com/media/... (see roadmap item 87).
# They used to come from *.blob.core.windows.net, whose cert Microsoft renews itself. Now, if the
# app.iproadvisers.com cert lapses, mail clients refuse the image fetch and images break in every
# newsletter -- INCLUDING mail already sitting in people's inboxes -- with nothing in the app to
# signal it. That is a silent failure with a customer-visible blast radius, so it gets a watchdog.
#
# This script only OBSERVES. It never renews. Renewal is Renew-Certs.ps1, which needs a human to
# publish a DNS TXT record (lego is configured with --dns manual). Keeping the watchdog separate
# from the renewer is deliberate: the watchdog must be able to run unattended forever and never
# fail for a reason that needs a person.
#
# TO ADD A DOMAIN: add it to $Domains below. Nothing else needs changing.

param(
    [int]$WarnDays = 30,
    [switch]$Quiet
)

$ErrorActionPreference = "Continue"

$Domains = @(
    "app.iproadvisers.com"      # real newsletter sends point image URLs here -- highest impact
    "admin.iproadvisers.com"
)

$LogPath = "C:\Users\admin\lego\cert-status.log"
$Stamp   = Get-Date -Format "yyyy-MM-dd HH:mm:ss"

# The Desktop is OneDrive-redirected on this machine, so a hardcoded C:\Users\admin\Desktop does
# not exist and Set-Content fails there. Resolve it properly and fall back to the lego folder if
# the shell API ever returns something unusable -- an alert written somewhere is worth more than
# an alert lost because a path moved.
$desktop = [Environment]::GetFolderPath("Desktop")
if ([string]::IsNullOrWhiteSpace($desktop) -or -not (Test-Path $desktop)) {
    $desktop = "C:\Users\admin\lego"
}
$AlertPath = Join-Path $desktop "CERT-RENEWAL-DUE.txt"

function Get-CertExpiry {
    param([string]$HostName)

    # Raw TLS handshake rather than Invoke-WebRequest: this must report on a cert even when the
    # site itself is down, mid-deploy, or returning 5xx. The callback accepts any cert because we
    # are inspecting it, not trusting it -- an already-expired cert still has to be readable here.
    $client = $null
    $sslStream = $null
    try {
        $client = New-Object System.Net.Sockets.TcpClient
        $connect = $client.BeginConnect($HostName, 443, $null, $null)
        if (-not $connect.AsyncWaitHandle.WaitOne(10000, $false)) {
            return [pscustomobject]@{ Host = $HostName; Error = "connect timeout" }
        }
        $client.EndConnect($connect)

        $sslStream = New-Object System.Net.Security.SslStream($client.GetStream(), $false, { $true })
        $sslStream.AuthenticateAsClient($HostName)

        $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]$sslStream.RemoteCertificate
        return [pscustomobject]@{
            Host      = $HostName
            NotAfter  = $cert.NotAfter
            DaysLeft  = [math]::Floor(($cert.NotAfter - (Get-Date)).TotalDays)
            Issuer    = $cert.Issuer
            Error     = $null
        }
    } catch {
        return [pscustomobject]@{ Host = $HostName; Error = $_.Exception.Message }
    } finally {
        if ($sslStream) { $sslStream.Dispose() }
        if ($client)    { $client.Dispose() }
    }
}

$results = foreach ($d in $Domains) { Get-CertExpiry -HostName $d }

$problems = @()
foreach ($r in $results) {
    if ($r.Error) {
        # An unreachable host is itself worth flagging -- it may BE the expiry, or a dead site.
        $line = "$Stamp  $($r.Host)  ERROR: $($r.Error)"
        $problems += "$($r.Host): could not read certificate -- $($r.Error)"
    } elseif ($r.DaysLeft -le $WarnDays) {
        $line = "$Stamp  $($r.Host)  DUE: $($r.DaysLeft) days left (expires $($r.NotAfter))"
        $problems += "$($r.Host): $($r.DaysLeft) days left, expires $($r.NotAfter)"
    } else {
        $line = "$Stamp  $($r.Host)  ok: $($r.DaysLeft) days left (expires $($r.NotAfter))"
    }
    Add-Content -Path $LogPath -Value $line -Encoding utf8
    if (-not $Quiet) { Write-Output $line }
}

if ($problems.Count -gt 0) {
    $body = @()
    $body += "CERTIFICATE RENEWAL DUE"
    $body += "Checked: $Stamp"
    $body += ""
    $body += $problems
    $body += ""
    $body += "Renew with:"
    $body += "    powershell -File C:\Users\admin\lego\Renew-Certs.ps1"
    $body += ""
    $body += "This needs you present: lego runs with --dns manual, so it will print a TXT record"
    $body += "that must be published in cPanel DNS before it can continue."
    $body += ""
    $body += "Why this matters beyond the portal: newsletter image URLs point at"
    $body += "app.iproadvisers.com. An expired cert breaks images in already-delivered mail."
    $body += ""
    $body += "Delete this file once renewed; the next check recreates it if still due."

    # Confirm the write instead of assuming it. The first version of this script announced
    # "ALERT WRITTEN" while Set-Content was failing on a Desktop path that did not exist -- a
    # watchdog that lies about having warned you is worse than no watchdog.
    $alertWritten = $false
    try {
        Set-Content -Path $AlertPath -Value ($body -join [Environment]::NewLine) -Encoding utf8 -ErrorAction Stop
        $alertWritten = Test-Path $AlertPath
    } catch {
        Add-Content -Path $LogPath -Value "$Stamp  ALERT FILE WRITE FAILED at ${AlertPath}: $($_.Exception.Message)" -Encoding utf8
    }

    # Event log too, so the warning survives someone tidying their Desktop.
    try {
        if (-not [System.Diagnostics.EventLog]::SourceExists("IPROCertWatch")) {
            New-EventLog -LogName Application -Source "IPROCertWatch" -ErrorAction Stop
        }
        Write-EventLog -LogName Application -Source "IPROCertWatch" -EntryType Warning `
            -EventId 4001 -Message ($problems -join "; ") -ErrorAction Stop
    } catch { }

    if (-not $Quiet) {
        if ($alertWritten) { Write-Output "`nALERT WRITTEN: $AlertPath" }
        else { Write-Output "`nALERT COULD NOT BE WRITTEN to $AlertPath -- see $LogPath" }
    }
    exit 2
}

# Cleared: remove a stale alert so the Desktop file always reflects current state.
if (Test-Path $AlertPath) { Remove-Item $AlertPath -Force -ErrorAction SilentlyContinue }
exit 0
