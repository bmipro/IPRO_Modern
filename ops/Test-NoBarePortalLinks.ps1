<#
.SYNOPSIS
    Fails if any view or controller emits a BARE link to an agent-portal route.

.DESCRIPTION
    On an agent's own domain a bare path is the PUBLIC WEBSITE (DOCS/INVARIANTS.md rule 1), so
    href="/Clients/Create" sends the agent to their own site's 404 instead of the portal. This is a
    source-level check because it is the only kind that cannot be fooled by not clicking the link:

      2026-08-08, three times in one evening --
        1. the login redirect still pointed at a bare /Dashboard;
        2. after fixing that, 253 hardcoded hrefs across 62 views were still bare, found by the owner
           clicking "Add Client";
        3. and /ClientPortalMessages, /ClientPortalDocuments and /ClientPortalAppointments 404'd for
           the agent's CLIENTS, because the never-shadowed list named only two of the seven
           ClientPortal controllers.

    Run it after any change to routing, views, or controllers. It needs no server and no credentials.

.EXAMPLE
    ./ops/Test-NoBarePortalLinks.ps1
#>
[CmdletBinding()]
param([string] $RepoRoot = (Split-Path $PSScriptRoot -Parent))

$ErrorActionPreference = 'Stop'

# Agent-portal controllers. These live under /portal and nothing may link to them bare.
# The portal controller list is DERIVED from the filesystem, not hand-maintained.
#
# It used to be a literal array, which meant a new portal controller was silently unchecked until
# someone remembered to add it -- and the script still printed PASS, which is worse than no check at
# all because green reads as coverage. Adding EmailActivity on 2026-08-08 surfaced that: the guard
# passed while knowing nothing about it.
#
# Now the default is "checked", and anything that must stay bare has to be named below. That is the
# safer direction for the failure mode: forgetting to exclude produces a loud false positive you fix
# in one line; forgetting to include produced a silent gap.

# Controllers whose routes must stay BARE -- client- or public-facing, reached from links already
# sitting in people's inboxes, or the ways in and out of the portal itself. These correspond to
# IsNeverShadowedPrefix in src/IPRO.Web/Program.cs; see DOCS/INVARIANTS.md rule 1.
$mustStayBare = @(
    'Account', 'Billing', 'Home', 'Media', 'Preview', 'PublicWebsite',
    'ClientDocument',        # [Route("invoice")]
    'PollVote',              # [Route("Poll/[action]")]
    'TestimonialRequest'     # [Route("testimonial")]
)

$controllerDir = Join-Path $RepoRoot 'src\IPRO.Web\Controllers'
$portal = Get-ChildItem -Path $controllerDir -Filter '*Controller.cs' -Recurse |
    ForEach-Object { $_.BaseName -replace 'Controller$', '' } |
    Where-Object { $_ -notlike 'ClientPortal*' -and $mustStayBare -notcontains $_ } |
    Sort-Object -Unique

if ($portal.Count -eq 0) {
    Write-Host "FAIL  found no portal controllers under $controllerDir -- is the path right?" -ForegroundColor Red
    exit 1
}

$alternation = ($portal | Sort-Object Length -Descending) -join '|'

# Matches a bare portal URL however it is written. The first version of this script only knew about
# href= and action=, so it passed clean while the E-Card live preview was still broken -- that URL is
# built in JavaScript (preview.src = '/ECards/PreviewCard?...'), which is neither attribute.
#
# So: match ANY quoted string that starts with /<PortalController>, whatever precedes it. That covers
# href, action, src, data-*, fetch(), $.get/$.post, location.href, window.open, url: '...', and
# whatever the next person invents.
$viewPattern = '[''"]/(' + $alternation + ')(?=[/''"?#])'
$codePattern = 'Redirect\("/(' + $alternation + ')(?=[/"])'

$findings = @()

foreach ($file in Get-ChildItem -Path (Join-Path $RepoRoot 'src\IPRO.Web\Views') -Recurse -Filter *.cshtml) {
    $n = 0
    foreach ($line in Get-Content -LiteralPath $file.FullName) {
        $n++
        if ($line -cmatch $viewPattern) {
            $findings += [pscustomobject]@{
                File = $file.FullName.Replace($RepoRoot, '').TrimStart('\')
                Line = $n
                Hit  = $Matches[0]
            }
        }
    }
}

foreach ($file in Get-ChildItem -Path (Join-Path $RepoRoot 'src\IPRO.Web\Controllers') -Recurse -Filter *.cs) {
    $n = 0
    foreach ($line in Get-Content -LiteralPath $file.FullName) {
        $n++
        if ($line -cmatch $codePattern) {
            $findings += [pscustomobject]@{
                File = $file.FullName.Replace($RepoRoot, '').TrimStart('\')
                Line = $n
                Hit  = $Matches[0]
            }
        }
    }
}

# An attribute route REPLACES the conventional one, so an action in a portal controller that declares
# only a bare [HttpGet("Newsletter/Foo")] is unreachable at /portal/Newsletter/Foo -- where every
# portal link now points. That is how "Use this template" 404'd on 2026-08-08 after the link sweep.
# Each such attribute needs a prefixed twin; ClientsController.FollowUpQueue is the pattern.
foreach ($file in Get-ChildItem -Path (Join-Path $RepoRoot 'src\IPRO.Web\Controllers') -Recurse -Filter *.cs) {
    $lines = Get-Content -LiteralPath $file.FullName
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -cmatch '^\s*\[(?:Http(?:Get|Post|Put|Delete)|Route)\("(' + $alternation + ')(/[^"]*)?"\)\]') {
            $bare = $Matches[0].Trim()
            # Look for a portal/-prefixed attribute on the same action (the adjacent attribute lines).
            $twin = $false
            for ($j = [Math]::Max(0, $i - 4); $j -lt [Math]::Min($lines.Count, $i + 5); $j++) {
                if ($lines[$j] -cmatch '^\s*\[(?:Http(?:Get|Post|Put|Delete)|Route)\("portal/') { $twin = $true }
            }
            if (-not $twin) {
                $findings += [pscustomobject]@{
                    File = $file.FullName.Replace($RepoRoot, '').TrimStart('\')
                    Line = $i + 1
                    Hit  = "$bare  (no portal/ twin -- unreachable from the portal)"
                }
            }
        }
    }
}

Write-Host ""
if ($findings.Count -eq 0) {
    Write-Host "PASS  no bare links to agent-portal routes" -ForegroundColor Green
    Write-Host "      (checked $($portal.Count) portal controllers across views and controllers)" -ForegroundColor DarkGray
    exit 0
}

Write-Host "FAIL  $($findings.Count) bare link(s) to agent-portal routes" -ForegroundColor Red
Write-Host "      On an agent's domain these resolve to the PUBLIC WEBSITE, not the portal." -ForegroundColor Red
Write-Host "      Prefix each with /portal -- see DOCS/INVARIANTS.md rule 2." -ForegroundColor Red
Write-Host ""
$findings | ForEach-Object { Write-Host ("  {0}:{1}  {2}" -f $_.File, $_.Line, $_.Hit) -ForegroundColor DarkGray }
exit 1
