# New-AgentCert.ps1 -- issue a Let's Encrypt certificate for ANY custom domain and bind it in Azure.
#
# WHY THIS EXISTS
# Binding a custom domain in the agent portal gets you as far as "Azure: Bound" and then stops. The
# site is HTTPS-only, Azure has no certificate for the new hostname, so the visitor is redirected to
# HTTPS and hits SSL_ERROR_BAD_CERT_DOMAIN -- the served cert is Microsoft's *.azurewebsites.net.
# Firefox blocks it outright. Observed on www.ouritems.ca, 2026-08-06.
#
# Azure's free App Service Managed Certificate is NOT an option on this subscription: it accepts the
# create request (202) and never produces a certificate. Reproduced repeatedly in July 2026 across two
# hostnames; that is why the platform domains ended up on lego in the first place. See
# DOCS/20_CERTIFICATES.md.
#
# Renew-Certs.ps1 handles the two PLATFORM domains, whose lego wrappers are hardcoded. This script is
# the general case: any domain, any app, any DNS provider -- because agent domains live wherever the
# agent's registrar is (ouritems.ca is on GoDaddy; iproadvisers.com is on cPanel).
#
# USAGE
#   powershell -File New-AgentCert.ps1 -Domain www.ouritems.ca
#   powershell -File New-AgentCert.ps1 -Domain www.example.com -WebApp ipro-prod-web -ResourceGroup ipro-production
#
# WHAT IT NEEDS FROM YOU
# One DNS TXT record, published at the agent's registrar while the script waits. It prints exactly
# what to create and then polls public DNS until it appears.

param(
    [Parameter(Mandatory = $true)][string]$Domain,
    [string]$WebApp = "ipro-prod-web",
    [string]$ResourceGroup = "ipro-production",
    [string]$Email = "bahman.motamed@gmail.com",
    [int]$DnsWaitMinutes = 30
)

$ErrorActionPreference = "Continue"
$LegoDir = "C:\Users\admin\lego"
$Domain = $Domain.Trim().Trim('.').ToLowerInvariant()

function Step { param([string]$T) Write-Output "`n=== $T ===" }

Step "Preflight"
$fail = $false

if (Test-Path (Join-Path $LegoDir "lego.exe")) { "  ok    lego.exe present" } else { "  FAIL  lego.exe missing"; $fail = $true }

try {
    $acct = az account show --query name -o tsv 2>$null
    if ($LASTEXITCODE -eq 0 -and $acct) { "  ok    az signed in ($acct)" } else { "  FAIL  az not signed in"; $fail = $true }
} catch { "  FAIL  az unavailable"; $fail = $true }

# The hostname must already be bound in Azure. Issuing a certificate for a hostname App Service does
# not serve would produce a valid cert that can never be attached to anything.
$bound = az webapp config hostname list --webapp-name $WebApp --resource-group $ResourceGroup --query "[].name" -o tsv 2>$null
if ($bound -match [regex]::Escape($Domain)) {
    "  ok    $Domain is bound to $WebApp"
} else {
    "  FAIL  $Domain is NOT bound to $WebApp/$ResourceGroup."
    "        Add and bind the domain in the agent portal first -- this script only adds the certificate."
    $fail = $true
}

# Reuse the PFX password already used by the platform certs rather than introducing a second secret.
$pfxPassword = $null
$runApp = Join-Path $LegoDir "run_app.ps1"
if (Test-Path $runApp) {
    $m = Select-String -Path $runApp -Pattern '--pfx\.password\s+"([^"]+)"' | Select-Object -First 1
    if ($m) { $pfxPassword = $m.Matches[0].Groups[1].Value }
}
if ($pfxPassword) { "  ok    PFX password read from run_app.ps1" } else { "  FAIL  could not read PFX password"; $fail = $true }

if ($fail) { Write-Output "`nPreflight FAILED -- nothing was requested."; exit 1 }

Step "Requesting certificate for $Domain"
"  lego will print a TXT record below. Publish it at the domain's registrar."
"  DNS for an agent domain is wherever THEIR registrar is -- not necessarily cPanel."

$logPath = Join-Path $LegoDir ("agentcert-" + ($Domain -replace '[^a-z0-9]', '-') + ".log")
Remove-Item $logPath -ErrorAction SilentlyContinue

$env:MANUAL_PROPAGATION_TIMEOUT = ($DnsWaitMinutes * 60).ToString()
$env:MANUAL_POLLING_INTERVAL = "15"

# Driven through cmd.exe with stdin redirected, matching run_app.ps1: lego's --dns manual provider
# blocks on Enter, and MSYS pipes do not interoperate with a native Windows binary's stdin.
# --dns.propagation.disable-ans/-rns skip lego's own propagation pre-check, which is unreliable in
# this environment; Let's Encrypt still performs the real validation.
$inner = '"' + (Join-Path $LegoDir 'lego.exe') + '"' +
         " run --accept-tos --email `"$Email`" --domains `"$Domain`" --dns manual" +
         " --dns.propagation.disable-ans --dns.propagation.disable-rns" +
         " --path .\certs --pfx --pfx.password `"$pfxPassword`" 2>&1"

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "cmd.exe"
$psi.Arguments = '/c "' + $inner + '"'
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.UseShellExecute = $false
$psi.WorkingDirectory = $LegoDir

$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = $psi
$proc.Start() | Out-Null

$expected = $null
$reader = $proc.StandardOutput
while (-not $reader.EndOfStream) {
    $line = $reader.ReadLine()
    if ($null -eq $line) { break }
    Add-Content -Path $logPath -Value $line -Encoding utf8
    if ($line -match 'TXT\s+"([^"]+)"') { $expected = $matches[1] }
    if ($line -match "Press 'Enter' when you are done") { break }
}

if (-not $expected) {
    Write-Output "  FAIL  lego did not emit a DNS challenge. See $logPath"
    try { $proc.StandardInput.WriteLine(""); $proc.StandardInput.Flush() } catch {}
    $proc.WaitForExit(10000) | Out-Null
    exit 1
}

Write-Output ""
Write-Output "  ############################################################"
Write-Output "  CREATE THIS DNS RECORD NOW:"
Write-Output ""
Write-Output "    Name:  _acme-challenge.$Domain"
Write-Output "    Type:  TXT"
Write-Output "    Value: $expected"
Write-Output ""
Write-Output "  In GoDaddy the Name field is usually just the part before the"
Write-Output "  domain, e.g. _acme-challenge.www"
Write-Output "  ############################################################"
Write-Output ""

$deadline = (Get-Date).AddMinutes($DnsWaitMinutes)
$found = $false
while ((Get-Date) -lt $deadline) {
    try {
        $txt = Resolve-DnsName -Name "_acme-challenge.$Domain" -Type TXT -Server 8.8.8.8 -ErrorAction SilentlyContinue
        foreach ($r in $txt) { if ($r.Strings -contains $expected) { $found = $true; break } }
    } catch {}
    if ($found) { break }
    Start-Sleep -Seconds 15
}

if ($found) {
    Write-Output "  DNS record is visible on 8.8.8.8. Submitting to Let's Encrypt..."
    Start-Sleep -Seconds 10
} else {
    # Deliberate: this DNS host has taken 20+ minutes before while the record was already live.
    # Let's Encrypt does its own lookup at validation time and is the real authority.
    Write-Output "  Timed out waiting to SEE the record. Submitting anyway -- Let's Encrypt will judge."
}

$proc.StandardInput.WriteLine("")
$proc.StandardInput.Flush()
while (-not $reader.EndOfStream) {
    $line = $reader.ReadLine()
    if ($null -eq $line) { break }
    Add-Content -Path $logPath -Value $line -Encoding utf8
}
$proc.WaitForExit()

$pfx = Join-Path $LegoDir "certs\certificates\$Domain.pfx"
if (-not (Test-Path $pfx)) {
    Write-Output "`n  FAIL  no certificate produced. See $logPath"
    exit 1
}

Step "Uploading to Azure"
$thumb = az webapp config ssl upload --certificate-file $pfx --certificate-password $pfxPassword `
    --name $WebApp --resource-group $ResourceGroup --query thumbprint -o tsv 2>$null
if ($LASTEXITCODE -ne 0 -or -not $thumb) { Write-Output "  FAIL  upload failed"; exit 1 }
"  uploaded, thumbprint $thumb"

Step "Binding"
az webapp config ssl bind --certificate-thumbprint $thumb --ssl-type SNI `
    --name $WebApp --resource-group $ResourceGroup 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Output "  FAIL  bind failed"; exit 1 }

Step "Verifying"
Start-Sleep -Seconds 20
try {
    $client = New-Object System.Net.Sockets.TcpClient($Domain, 443)
    $ssl = New-Object System.Net.Security.SslStream($client.GetStream(), $false, { $true })
    $ssl.AuthenticateAsClient($Domain)
    $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]$ssl.RemoteCertificate
    "  subject : $($cert.Subject)"
    "  issuer  : $($cert.Issuer)"
    "  expires : $($cert.NotAfter)"
    if ($cert.Subject -match [regex]::Escape($Domain)) {
        "`n  SUCCESS - https://$Domain now serves its own certificate."
    } else {
        "`n  WARNING - still serving a certificate for something else; give Azure a minute and re-check."
    }
    $ssl.Dispose(); $client.Dispose()
} catch {
    "  could not read the certificate back: $($_.Exception.Message)"
}

Write-Output ""
Write-Output "REMEMBER: Let's Encrypt certificates last 90 days and this one does NOT auto-renew."
Write-Output "Add $Domain to the `$Domains list in Check-CertExpiry.ps1 so the watchdog covers it."
