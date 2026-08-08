<#
.SYNOPSIS
    Asserts the URL-space invariants from DOCS/INVARIANTS.md against a running environment.

.DESCRIPTION
    Every check here exists because the corresponding bug reached production at least once. The
    slug-collision bug in particular survived three "fixes" over three days, because each
    verification tested it signed OUT and the original report was from a signed-IN agent. So each
    bare-slug case is asserted BOTH ways, and the pair must agree.

    The signed-in case is simulated by sending a syntactically valid but meaningless portal cookie.
    That is deliberate and is the exact regression being guarded: the broken code branched on the
    mere PRESENCE of a cookie whose name started with ".AspNetCore.Cookies", never on whether it was
    valid. If a future change reintroduces any presence check, these rows diverge and this fails.

.PARAMETER AgentHost
    An agent's public host, e.g. bobtest.247advisers.com. This is where collisions happen.

.PARAMETER PlatformHost
    The portal host, default app.iproadvisers.com.

.PARAMETER AgentPageSlug
    A slug that IS a real published page on AgentHost AND collides with a portal controller name.
    "testimonials" is the default because every agent gets that page from the starter navigation.

    IMPORTANT: pass a slug the agent actually has. On 2026-08-07 this bug was declared "still open"
    after testing /articles, /forms, /documents and /newsletter on an agent whose pages were named
    nothing of the sort -- the test asserted a precondition it never checked. -VerifyPrecondition
    below refuses to run if the page isn't really there.

.EXAMPLE
    ./ops/Test-RoutingInvariants.ps1 -AgentHost bobtest.247advisers.com
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $AgentHost,
    [string] $PlatformHost  = 'app.iproadvisers.com',
    [string] $AgentPageSlug = 'testimonials',
    [switch] $SkipPrecondition,

    # Optional. When supplied, the script signs in on the AGENT host and follows the redirect.
    # This leg exists because on 2026-08-08 the routing fix sent bare paths on agent hosts to the
    # public site while the login redirect still pointed at a bare /Dashboard -- so signing in on
    # your own domain landed you on "Website not published yet". Every check in this file passed at
    # the time, because none of them logged in.
    [string] $AgentUser,
    [string] $AgentPassword
)

$ErrorActionPreference = 'Stop'
$script:Failures = 0
$script:Checks   = 0

# A syntactically plausible portal session cookie that is not a real session.
$FakePortalCookie = '.AspNetCore.Cookies=invalid-but-present-portal-session'

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# Windows PowerShell 5.1 does not load System.Net.Http into the default session.
Add-Type -AssemblyName System.Net.Http

# Deliberately HttpClient and not Invoke-WebRequest.
#
# Invoke-WebRequest under Windows PowerShell 5.1 SILENTLY DROPS a 'Cookie' header passed via
# -Headers, because Cookie is a restricted header it expects to manage through -WebSession. The
# first version of this script used -Headers and every "signed in" row reported PASS against a
# production build that was provably broken -- the requests simply weren't carrying the cookie.
#
# That is the same defect this script exists to catch: an assertion whose precondition was never
# established. A test that cannot fail is worse than no test, so the transport is explicit here.
# UseCookies=$false is what allows the header to be set by hand; AllowAutoRedirect=$false is what
# lets a 302 be observed as a result rather than thrown as an error.
function Invoke-Probe {
    param([string] $Url, [string] $Cookie)

    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $false
    $handler.UseCookies        = $false

    $client = New-Object System.Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(30)

    try {
        $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, $Url)
        if ($Cookie) {
            if (-not $req.Headers.TryAddWithoutValidation('Cookie', $Cookie)) {
                throw "Could not attach the Cookie header to $Url -- the signed-in checks would be meaningless."
            }
        }

        $resp   = $client.SendAsync($req).GetAwaiter().GetResult()
        $status = [int] $resp.StatusCode
        $body   = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    }
    finally {
        $client.Dispose()
        $handler.Dispose()
    }

    $title = ''
    if ($body -match '(?is)<title>(.*?)</title>') { $title = $Matches[1].Trim() }

    [pscustomobject]@{ Status = $status; Title = $title; Body = $body }
}

function Assert-Check {
    param([string] $Name, [bool] $Ok, [string] $Detail)

    $script:Checks++
    if ($Ok) {
        Write-Host ("  PASS  {0}" -f $Name) -ForegroundColor Green
    }
    else {
        $script:Failures++
        Write-Host ("  FAIL  {0}" -f $Name) -ForegroundColor Red
        Write-Host ("        {0}" -f $Detail) -ForegroundColor DarkGray
    }
}

Write-Host ""
Write-Host "Routing invariants (DOCS/INVARIANTS.md rule 1)" -ForegroundColor Cyan
Write-Host ("  agent host    : {0}" -f $AgentHost)
Write-Host ("  platform host : {0}" -f $PlatformHost)
Write-Host ("  colliding slug: /{0}" -f $AgentPageSlug)
Write-Host ""

# ---------------------------------------------------------------------------
# Precondition. The test is meaningless if this page does not exist -- that is
# precisely how a working fix got reported as broken.
# ---------------------------------------------------------------------------
if (-not $SkipPrecondition) {
    $pre = Invoke-Probe -Url ("https://{0}/{1}" -f $AgentHost, $AgentPageSlug)
    if ($pre.Status -ne 200 -or $pre.Title -match 'Not Published') {
        Write-Host "PRECONDITION FAILED" -ForegroundColor Yellow
        Write-Host ("  /{0} on {1} is not a real published page (status {2}, title '{3}')." `
                    -f $AgentPageSlug, $AgentHost, $pre.Status, $pre.Title) -ForegroundColor Yellow
        Write-Host "  Pass -AgentPageSlug with a page this agent actually has, or the results below" -ForegroundColor Yellow
        Write-Host "  would tell you nothing. Re-run with -SkipPrecondition to override." -ForegroundColor Yellow
        exit 2
    }
    Write-Host ("  precondition  : /{0} is a real published page ('{1}')" -f $AgentPageSlug, $pre.Title) -ForegroundColor DarkGray
    Write-Host ""
}

# ---------------------------------------------------------------------------
# 1. Bare paths on an agent host are the public site -- signed out AND signed in.
# ---------------------------------------------------------------------------
Write-Host "Agent host: bare paths belong to the public website" -ForegroundColor Cyan

# The real page. Must render, both ways.
$out = Invoke-Probe -Url ("https://{0}/{1}" -f $AgentHost, $AgentPageSlug)
$inn = Invoke-Probe -Url ("https://{0}/{1}" -f $AgentHost, $AgentPageSlug) -Cookie $FakePortalCookie

Assert-Check "/$AgentPageSlug renders the agent's page when signed out" `
    ($out.Status -eq 200 -and $out.Title -notmatch 'Agent Portal|Agent Login') `
    ("got {0} '{1}'" -f $out.Status, $out.Title)

Assert-Check "/$AgentPageSlug renders the agent's page when signed in  <-- the 3x-regressed case" `
    ($inn.Status -eq 200 -and $inn.Title -notmatch 'Agent Portal|Agent Login') `
    ("got {0} '{1}' -- a portal cookie must not change which site answers" -f $inn.Status, $inn.Title)

Assert-Check "/$AgentPageSlug is identical signed in and signed out" `
    ($out.Status -eq $inn.Status -and $out.Title -eq $inn.Title) `
    ("signed out {0} '{1}' vs signed in {2} '{3}'" -f $out.Status, $out.Title, $inn.Status, $inn.Title)

# Portal controller names that are NOT pages on this site. Must not leak the portal to a visitor.
foreach ($slug in @('clients', 'newsletter', 'campaigns', 'ecards')) {
    $a = Invoke-Probe -Url ("https://{0}/{1}" -f $AgentHost, $slug)
    $b = Invoke-Probe -Url ("https://{0}/{1}" -f $AgentHost, $slug) -Cookie $FakePortalCookie

    Assert-Check "/$slug never shows the portal on an agent host" `
        ($a.Status -eq 200 -and $b.Status -eq 200 -and
         $a.Title -notmatch 'Agent Portal|Agent Login' -and $b.Title -notmatch 'Agent Portal|Agent Login') `
        ("signed out {0} '{1}' | signed in {2} '{3}'" -f $a.Status, $a.Title, $b.Status, $b.Title)
}

# ---------------------------------------------------------------------------
# 2. The portal is still reachable on the agent host, under /portal.
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "Agent host: the portal still exists under /portal" -ForegroundColor Cyan

$p = Invoke-Probe -Url ("https://{0}/portal/Dashboard" -f $AgentHost)
Assert-Check "/portal/Dashboard reaches the portal (302 to login when signed out)" `
    ($p.Status -in 200, 302) ("got {0}" -f $p.Status)

$pt = Invoke-Probe -Url ("https://{0}/portal/{1}" -f $AgentHost, $AgentPageSlug)
Assert-Check "/portal/$AgentPageSlug reaches the PORTAL, not the public page" `
    ($pt.Status -in 200, 302 -and $pt.Title -notmatch 'Not Published') `
    ("got {0} '{1}'" -f $pt.Status, $pt.Title)

# ---------------------------------------------------------------------------
# 3. Never-shadowed prefixes still answer on the agent host.
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "Agent host: recovery paths are never shadowed" -ForegroundColor Cyan

$login = Invoke-Probe -Url ("https://{0}/Account/Login" -f $AgentHost)
Assert-Check "/Account/Login serves the login form" `
    ($login.Status -eq 200 -and $login.Title -match 'Login') ("got {0} '{1}'" -f $login.Status, $login.Title)

$health = Invoke-Probe -Url ("https://{0}/health" -f $AgentHost)
Assert-Check "/health answers the probe (not the agent's site)" `
    ($health.Status -eq 200 -and $health.Body.Trim() -eq 'Healthy') `
    ("got {0} '{1}'" -f $health.Status, $health.Body.Trim())

$billing = Invoke-Probe -Url ("https://{0}/Billing" -f $AgentHost)
Assert-Check "/Billing stays with the portal" `
    ($billing.Status -in 200, 302 -and $billing.Title -notmatch 'Not Published') `
    ("got {0} '{1}'" -f $billing.Status, $billing.Title)

# ---------------------------------------------------------------------------
# 3b. Signing in ON THE AGENT HOST lands in the portal, not on the public site.
#     Skipped without credentials, but this is the leg that catches the whole class of
#     "the portal emitted a bare URL" bugs -- see the note on -AgentUser above.
# ---------------------------------------------------------------------------
Write-Host ""
if ($AgentUser -and $AgentPassword) {
    Write-Host "Agent host: signing in actually reaches the portal" -ForegroundColor Cyan

    $loginUrl = "https://{0}/Account/Login" -f $AgentHost
    $jar      = New-Object System.Net.CookieContainer
    $h        = New-Object System.Net.Http.HttpClientHandler
    $h.AllowAutoRedirect = $false
    $h.CookieContainer   = $jar
    $c        = New-Object System.Net.Http.HttpClient($h)

    try {
        # antiforgery token from the login form
        $form  = $c.GetStringAsync($loginUrl).GetAwaiter().GetResult()
        $token = ''
        if ($form -match 'name="__RequestVerificationToken"[^>]*value="([^"]+)"') { $token = $Matches[1] }

        $fields = New-Object 'System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string,string]]'
        $fields.Add([System.Collections.Generic.KeyValuePair[string,string]]::new('Username', $AgentUser))
        $fields.Add([System.Collections.Generic.KeyValuePair[string,string]]::new('Password', $AgentPassword))
        $fields.Add([System.Collections.Generic.KeyValuePair[string,string]]::new('__RequestVerificationToken', $token))

        $post = $c.PostAsync($loginUrl, (New-Object System.Net.Http.FormUrlEncodedContent($fields))).GetAwaiter().GetResult()
        $dest = if ($post.Headers.Location) { $post.Headers.Location.ToString() } else { '' }

        Assert-Check "sign-in redirects into /portal, not a bare path" `
            ($post.StatusCode -eq 302 -and $dest -match '^(https?://[^/]+)?/portal/') `
            ("got {0} -> '{1}' (a bare path here lands the agent on their own public site)" -f [int]$post.StatusCode, $dest)

        if ($dest) {
            $follow = if ($dest -match '^https?://') { $dest } else { "https://{0}{1}" -f $AgentHost, $dest }
            $page   = $c.GetAsync($follow).GetAwaiter().GetResult()
            $html   = $page.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $t      = ''
            if ($html -match '(?is)<title>(.*?)</title>') { $t = $Matches[1].Trim() }

            Assert-Check "the page after sign-in is the portal" `
                ([int]$page.StatusCode -eq 200 -and $t -match 'Agent Portal' -and $t -notmatch 'Not Published') `
                ("got {0} '{1}'" -f [int]$page.StatusCode, $t)
        }
    }
    finally { $c.Dispose(); $h.Dispose() }
}
else {
    Write-Host "Agent host: sign-in check SKIPPED (pass -AgentUser and -AgentPassword to run it)" -ForegroundColor Yellow
    Write-Host "  This is the leg that would have caught the 2026-08-08 login regression." -ForegroundColor DarkGray
}

# ---------------------------------------------------------------------------
# 4. The platform host is unaffected.
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "Platform host: unchanged" -ForegroundColor Cyan

$ph = Invoke-Probe -Url ("https://{0}/health" -f $PlatformHost)
Assert-Check "platform /health is Healthy" `
    ($ph.Status -eq 200 -and $ph.Body.Trim() -eq 'Healthy') ("got {0}" -f $ph.Status)

$pm = Invoke-Probe -Url ("https://{0}/" -f $PlatformHost)
Assert-Check "platform / serves the marketing site" `
    ($pm.Status -eq 200 -and $pm.Title -notmatch 'Not Published') `
    ("got {0} '{1}'" -f $pm.Status, $pm.Title)

$pp = Invoke-Probe -Url ("https://{0}/portal/Dashboard" -f $PlatformHost)
Assert-Check "platform /portal/Dashboard reaches the portal" `
    ($pp.Status -in 200, 302) ("got {0}" -f $pp.Status)

# ---------------------------------------------------------------------------
Write-Host ""
if ($script:Failures -eq 0) {
    Write-Host ("{0}/{0} checks passed." -f $script:Checks) -ForegroundColor Green
    exit 0
}

Write-Host ("{0} of {1} checks FAILED." -f $script:Failures, $script:Checks) -ForegroundColor Red
Write-Host "See DOCS/INVARIANTS.md rule 1 before changing routing to make these pass." -ForegroundColor Red
exit 1
