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
| `app.iproadvisers.com` | Let's Encrypt | **Manual, ours** | Newsletter images resolve here on real sends |
| `admin.iproadvisers.com` | Let's Encrypt | **Manual, ours** | SuperAdmin only |
| `*.247advisers.com` | Sectigo | Managed by the host | Agent custom domains; not our responsibility |

Azure's free managed certificate was tried for the two `iproadvisers.com` hosts in July 2026 and
never issued correctly, which is why these are Let's Encrypt via [lego](https://go-acme.github.io/lego/)
instead. Do not assume the managed option now works without re-testing it.

## The watchdog in Azure (primary)

`CertificateExpiryJob` runs daily at 07:00 UTC as the Hangfire recurring job **`certificate-expiry`**,
visible in SuperAdmin under **Job Scheduler** (`admin.iproadvisers.com/hangfire`) alongside the other
recurring jobs.

- Reads each certificate over a raw TLS handshake, so it reports even when the site is down
- Under 30 days: emails the operations address, then **deliberately throws**
- The throw is what makes a due certificate a red row on the Job Scheduler dashboard. Hangfire has no
  "warning" state, so failing the job is the only way to surface this there. `AutomaticRetry(Attempts
  = 0)` stops it re-running and re-sending
- Per-domain isolation: one unreachable host does not stop the others being checked

Configuration (all optional; defaults are in the job):

| Key | Default |
|---|---|
| `Certificates:Watch` | `app.iproadvisers.com`, `admin.iproadvisers.com` |
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

## Renewing (needs a person)

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

## Making it fully unattended

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
