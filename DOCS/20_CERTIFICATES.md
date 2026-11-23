# TLS Certificates

## Why this has its own document

Certificate expiry used to mean "the portal is unreachable until someone notices". Since roadmap
item 87 it means more than that: newsletter image URLs point at `app.iproadvisers.com/media/...`,
so **an expired certificate breaks images in every newsletter, including mail already delivered to
people's inboxes**, and nothing in the application reports it. The failure is silent, customer-
visible, and retroactive. That is why there is a watchdog.

## What covers what

| Domain | Issuer | Renewal | Notes |
|---|---|---|---|
| `app.iproadvisers.com` | DigiCert (App Service managed) | **Automatic** | Since 2026-09-23 (TODO 505); newsletter images resolve here on real sends |
| `admin.iproadvisers.com` | DigiCert (App Service managed) | **Automatic** | Since 2026-09-23 (TODO 505); SuperAdmin only |
| `*.247advisers.com` | Let's Encrypt (wildcard) once switched on; until then Sectigo (bought) | **Automatic** from the switch-over (675); by hand before | Every adviser's free address (`name.247advisers.com`); bound to ipro-prod-web; the bought one expires **25 January 2027**. See "The free-address certificate" below |
| `www.iproadvisers.com`, `iproadvisers.com`, `www.iproaccountants.com`, `iproaccountants.com` | DigiCert (App Service managed) | **Automatic** | All four bound 2026-09-20, valid to 20 March 2027 (`DOCS/DOMAIN_SWITCH_RUNBOOK.md`). They need the zone's `CAA 0 issue "digicert.com"` record to renew: do not delete it |
| `ipromortgages.com`, `www.ipromortgages.com` | DigiCert (App Service managed) | **Automatic** | Bound 2026-09-12 (the rehearsal) |

Every platform name carries an App Service managed certificate, which Azure renews on its own about
45 days before expiry. The one exception is the free-address wildcard `*.247advisers.com` (below): Azure's own
certificates cannot cover a wildcard, so iPro renews it itself from Let's Encrypt (675). The one thing they need from us is the zone's `CAA 0 issue "digicert.com"`
record; the watchdog below is there for the day a renewal fails anyway. Until 2026-09-23 the two
`iproadvisers.com` hosts were Let's Encrypt via [lego](https://go-acme.github.io/lego/), because
Azure's managed certificate was tried in July 2026 and never issued:

**Why it never issued, found 2026-09-20:** the `iproadvisers.com` zone carries CAA records (the legacy
host's, for its AutoSSL) that named Sectigo, Google, GlobalSign and Let's Encrypt only. App Service's
managed certificates come from DigiCert, which had to refuse; Let's Encrypt was on the list, which is
why lego worked. The owner added `CAA 0 issue "digicert.com"` that day and a managed certificate for
`www.iproadvisers.com` issued in about 13 minutes. So `app.` and `admin.` CAN now move to managed
certificates and stop needing a hand renewal -- TODO 505, before 5 October.

**Done 2026-09-23:** both moved to managed certificates (issued in about four minutes each, bound SNI,
proved from outside: DigiCert, expiry 2027-03-23). No host needs the hand renewal any more; the
section below is kept for the day a managed certificate fails to renew. The lego certificate
resources stay in Azure, unbound, until they lapse on 19 October.

## The watchdog in Azure (primary)

`CertificateExpiryJob` runs daily at 07:00 UTC as the Hangfire recurring job **`certificate-expiry`**,
visible in SuperAdmin under **Job Scheduler** (`admin.iproadvisers.com/hangfire`) alongside the other
recurring jobs.

- Reads each certificate over a raw TLS handshake, so it reports even when the site is down
- Inside 30 days, or unreadable: emails the operations address, then **deliberately throws**. Since
  521 (2026-09-25) the red row and the email say what to check (the CAA record, the binding; see
  "When a managed certificate does not renew" below), not to run the retired lego script
- The throw is what makes a due certificate a red row on the Job Scheduler dashboard. Hangfire has no
  "warning" state, so failing the job is the only way to surface this there. `AutomaticRetry(Attempts
  = 0)` stops it re-running and re-sending
- Per-domain isolation: one unreachable host does not stop the others being checked

Configuration (all optional; defaults are in the job):

| Key | Default |
|---|---|
| `Certificates:Watch` | every platform name (since 521): `app.` and `admin.iproadvisers.com`, `www.` and bare `iproadvisers.com`, `iproaccountants.com`, `ipromortgages.com` |
| `Certificates:WarnDays` | `30` |
| `Certificates:AlertEmail` | falls back to `Email:NotificationEmail`, then `Email:FromEmail` |

The recipient fallback exists because `Email:NotificationEmail` still ships as a `CHANGE_THIS_`
placeholder and is not set in Azure. Any candidate containing `CHANGE_THIS` or lacking an `@` is
skipped, so alerts land on `support@iproadvisers.com` rather than disappearing.

## The watchdog on the maintenance machine (secondary)

`C:\Users\admin\lego\Check-CertExpiry.ps1`, run daily at 09:00 by the scheduled task
**"IPRO Certificate Expiry Watch"**. This duplicates the Azure job above and is kept deliberately: it
is the signal that still works if the Azure app itself is down, which is exactly when you would not
get the other one.

- Reads the live certificate over a raw TLS handshake, so it still reports when the site is down or
  mid-deploy
- Appends every check to `C:\Users\admin\lego\cert-status.log`
- Under 30 days: writes `CERT-RENEWAL-DUE.txt` to the Desktop, logs an Application event
  (source `IPROCertWatch`, id 4001), and exits 2
- Clears the alert file automatically once the certificate is healthy again

Check it by hand any time:

```bash
powershell -File C:\Users\admin\lego\Check-CertExpiry.ps1
```

**Known limitation:** the task is registered with `Interactive` logon, because registering it as
`S4U` needed elevation. It therefore **only runs while the user is logged in**. `StartWhenAvailable`
is set so a missed run catches up at next logon. If this machine starts spending long periods
logged out, re-register the task elevated with `-LogonType S4U`.

**Adding a domain:** append it to `$Domains` at the top of the script. Nothing else changes.

## The free-address certificate

Found 2026-11-20 (TODO 638): until then this table said the wildcard was "managed by the host, not our
responsibility". It is ours. Every adviser's free address resolves to `ipro-prod-web` (a wildcard DNS record), and
the app's `*.247advisers.com` hostname binding serves a **Sectigo wildcard certificate that was bought and
uploaded by hand** (subject `CN=*.247advisers.com`, issuer Sectigo Public Server Authentication CA DV R36, valid to
**2027-01-25**). Azure does not renew an uploaded certificate. If it lapses, every free address shows a
certificate error at once.

The watchdog checks it through `certificate-check.247advisers.com` (a name no adviser has; the wildcard answers
for any name under it; the root comes from `App:TemporarySiteRootDomain`).

### It renews itself (675, 2026-11-23)

A bought certificate now lasts at most 200 days (the industry limit since 2026-03-15), 100 days from 2027-03-15 and
47 days from 2029-03-15, and the seller (SSLs.com) renews only in the last 30 days. So iPro gets the certificate from
**Let's Encrypt** itself, proving the domain by a DNS record (the dns-01 check):

- `FreeAddressCertificateJob` (Job Scheduler row `free-address-certificate`) runs at 06:30 UTC every day. It renews
  when the certificate bound to `*.247advisers.com` expires within 30 days, or was not uploaded by this job (so its
  first run with the switch On replaces the bought one). Let's Encrypt certificates last 90 days today (45 by 2028).
- Let's Encrypt reads `_acme-challenge.247advisers.com`. At GoDaddy that name is a CNAME to
  `_acme-challenge.acme-dns.247advisers.com`, and `acme-dns.247advisers.com` is a small Azure DNS zone in
  `ipro-production`, delegated from GoDaddy. The job writes the TXT record there (with the IPRO Domain Automation
  identity, which is Contributor on the resource group), waits until Azure's name servers show it, lets Let's
  Encrypt check, and removes it. Nothing else in the domain's DNS, its mail included, is ever touched.
- The certificate and its new key (RSA 2048) never leave the process except as a password-protected PFX sent to
  Azure; the password is made for the upload and kept nowhere. It is uploaded as `ipro-free-address-<UTC time>` and
  bound to `*.247advisers.com` (SNI); Azure starts serving it within minutes. The job removes its own uploads once they
  have expired. The bought certificate is never touched: remove it in the portal after it expires, if wanted.
- The app setting `FreeAddressCertificate__Mode` on `ipro-prod-web`: `Off` (or missing: nothing happens), `Test` (the
  whole path against Let's Encrypt's test service, including an upload and its removal, switching nothing), `On`. Only
  `ipro-prod-web` itself acts on it (`WEBSITE_SITE_NAME`), so a copy of production's settings elsewhere does nothing.
- Switching it On is agreeing, for iPro, to Let's Encrypt's Subscriber Agreement (https://letsencrypt.org/repository/).
  No account is kept: each run makes a new one with no contact address.
- With the switch On the watchdog warns **21 days** ahead: by then the job has failed for nine mornings in a row. With
  it Off or Test it warns **60 days** ahead, for a hand renewal.

**Setting it up (the owner's steps, once):**

1. Azure portal > Create a resource > **DNS zone**: subscription "Azure subscription 1", resource group
   `ipro-production`, name `acme-dns.247advisers.com`. When it is created, its Overview lists four name servers.
2. GoDaddy > 247advisers.com > DNS > Add: four **NS** records, name `acme-dns`, one per Azure name server (without
   the final dot). Then one **CNAME**, name `_acme-challenge`, value `_acme-challenge.acme-dns.247advisers.com`.
   Leave every other record as it is (the `*` wildcard, the mail records). See DNS_ZONE_RUNBOOK.md.
3. `ipro-prod-web` > Settings > Environment variables > add `FreeAddressCertificate__Mode` = `Test`, Apply (the app
   restarts). Then SuperAdmin > Job Scheduler > `free-address-certificate` > Trigger now. Its run should say
   "Test passed".
4. Change the setting to `On`, Apply, and trigger the job again (or wait for 06:30 UTC). The run says which
   certificate it bound; `https://certificate-check.247advisers.com` then shows a Let's Encrypt certificate good for
   90 days, and the next watchdog run is green.

**When the job's row is red** it says why, in Let's Encrypt's or Azure's words: most likely the CNAME or the NS records
at GoDaddy (step 2), the zone (step 1), or the IPRO Domain Automation identity's secret (it ends 2028-07-10). The zone
has no CAA record today, so any authority may issue; if one is ever added to 247advisers.com it must include
`0 issue "letsencrypt.org"` (the same trap as DigiCert's above). There are
21 days or more to fix it. To renew by hand meanwhile (the way it was done before 675; a purchase and a certificate
change, so the owner's step):

1. Renew or buy the `*.247advisers.com` wildcard with the seller (SSLs.com, a Sectigo reseller). Domain validation is
   by a DNS record at GoDaddy.
2. Export the issued certificate with its private key as a PFX with a password.
3. In the Azure portal: `ipro-prod-web` > Certificates > Bring your own certificates (.pfx) > Add, then
   Custom domains > `*.247advisers.com` > Add binding with the new certificate (SNI SSL).
4. Check: `https://certificate-check.247advisers.com/health` answers 200 and the browser shows the new expiry
   date; the next watchdog run is green.

With the switch On, the job replaces a hand-uploaded certificate on its next good run; set it to `Off` to keep one.

## When a managed certificate does not renew

Azure renews an App Service managed certificate on its own, about 45 days before it expires, and
needs nothing from us as long as two things hold. When the watchdog fires (a red `certificate-expiry`
row on the Job Scheduler and an email to the operations address), check them in this order:

1. **The zone's CAA record still allows DigiCert** -- `0 issue "digicert.com"` on `iproadvisers.com`,
   `iproaccountants.com` and `ipromortgages.com` (`bash ops/domain-switch/dns-check.sh <zone>` prints
   it). This is the record the July 2026 attempt lacked: without it DigiCert must refuse, and Azure
   reports nothing.
2. **The certificate is still bound to the app and its resource is healthy** -- in the Azure portal,
   the web app's Custom domains page (each name and its binding) and its Certificates page (each
   managed certificate's status and expiry); `az webapp config ssl show` reads the same, read-only.
   Re-issuing is `az webapp config ssl create`, then `bind`, exactly as on 2026-09-23 -- on the
   owner's go, since it touches the production bindings.
3. **A host that could not be read at all** is a different problem: the site itself is down or
   mid-deploy. Check `/health/version` first.

The hand renewal below is retired; it is the last resort only if DigiCert itself could not issue.

## Renewing by hand with lego (retired 2026-09-23; kept for history)

Not part of any routine since TODO 505. The lego account, the scripts and the PFX password are still
on the maintenance machine, and the two unbound Let's Encrypt certificate resources lapse in Azure on
19 October 2026 with nothing to do.

```bash
powershell -File C:\Users\admin\lego\Renew-Certs.ps1
```

Sequence per domain: preflight, request the certificate via lego, upload the resulting PFX to the
right App Service, bind it SNI, then re-read the live certificate to confirm the new expiry.

`-Preflight` runs every check without contacting Let's Encrypt -- lego present, `az` signed in, PFX
password readable, and the domain genuinely bound to the expected web app and resource group. Use it
before a real run; it costs nothing and catches the boring failures.

Other switches: `-Domain <name>` for one host, `-WithinDays N` to change the 30-day threshold,
`-Force` to renew regardless of remaining time.

**The manual step:** lego is configured `--dns manual`, so it prints a DNS TXT record that must be
published in cPanel before validation continues. The script surfaces the record prominently and then
waits; the underlying wrapper polls Google DNS and proceeds on its own once the record resolves (up
to 25 minutes). Nothing else needs a human.

**Two guards worth knowing about**, because both failure modes are quiet:

- The PFX password is read out of `run_app.ps1` / `run_admin.ps1` at runtime rather than restated,
  so there is one copy of it on disk instead of three.
- A PFX older than an hour is rejected. Uploading a stale file from the previous renewal would
  succeed and change nothing, leaving a cert that looks renewed and is not.

## Making the lego route unattended (moot since 2026-09-23)

DNS for `iproadvisers.com` is on `ns1/ns2.websiteservername.com` -- the cPanel host, not GoDaddy's
nameservers, so GoDaddy's API is not the route. lego has a cPanel DNS provider; switching
`--dns manual` to `--dns cpanel` with `CPANEL_USERNAME`, `CPANEL_TOKEN` and `CPANEL_BASE_URL` would
remove the last manual step and let renewal run from the scheduled task directly.

That needs a cPanel API token, which has to be created and stored by a person -- so it is a
deliberate follow-up, not something to bolt on mid-incident.

## History

- **2026-07-21** -- Both certs issued via lego after Azure's managed certificate failed to issue.
- **2026-08-05** -- Watchdog and renewal script added, after the newsletter media proxy made expiry
  a mail-breaking event rather than a portal-only one. Testing the alert path immediately found two
  bugs: the hardcoded `C:\Users\admin\Desktop` does not exist on this machine (the Desktop is
  OneDrive-redirected), and the script printed "ALERT WRITTEN" without checking whether the write
  had succeeded. A watchdog that reports having warned you when it did not is worse than no
  watchdog, and neither bug would have surfaced until October.
- **2026-09-23** -- `app.` and `admin.` moved to App Service managed certificates (TODO 505, on the
  owner's go); the lego renewal is no longer needed for any host.
- **2026-09-25** -- The watchdog's red row and email rewritten for the managed world (TODO 521): they
  now say what to check (the CAA record, the binding) instead of pointing at the lego script, and
  every platform name is watched, not only the two once renewed by hand.
- **2026-11-20** -- The `*.247advisers.com` wildcard found to be ours, bought and uploaded by hand, and watched from
  then on, 60 days ahead (TODO 638, 672).
- **2026-11-23** -- It renews itself from Let's Encrypt once switched on (TODO 675): SSLs.com would renew it only from
  2026-12-25, a bought certificate lasts 199 days now and 100 days from March 2027. The watchdog's email about it no
  longer gives the managed-certificate checklist (CAA, DigiCert), which never applied to it.
