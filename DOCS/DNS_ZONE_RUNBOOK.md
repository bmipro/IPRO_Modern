# DNS Zone Runbook — everything the app needs to resolve

*Captured 2026-08-30 by querying public DNS (8.8.8.8). This is the reconstruction of every record
the product depends on; the AUTHORITATIVE zones live in the two DNS panels below. If a DNS host
is ever lost, this file rebuilds the app's DNS from scratch. Re-capture after any DNS change.*

**Where the zones are managed (two different providers — easy to forget):**

| Zone | DNS host | Panel |
|---|---|---|
| `iproadvisers.com` | `ns1/ns2.websiteservername.com` (legacy web host) | cPanel Zone Editor |
| `247advisers.com` | `ns69/ns70.domaincontrol.com` (GoDaddy) | GoDaddy DNS Management |

> Owner action at next review: export/screenshot the raw zone from each panel and attach beside
> this file — public queries cannot discover records nobody has asked about.

---

## Zone: iproadvisers.com (SOA serial 2026083003 at capture)

### CRITICAL — the app breaks without these

| Name | Type | Value | Purpose |
|---|---|---|---|
| `app` | CNAME | `ipro-prod-web.azurewebsites.net` | The agent portal + public homepage host |
| `admin` | CNAME | `ipro-prod-admin-fhaydtemgeetbycm.canadaeast-01.azurewebsites.net` | The SuperAdmin host |
| `asuid.app` | TXT | `C6D5FC2CBD50987D0FF7FE648BC710D4B2454FA211AE9094BB2816A6505E8572` | Azure App Service custom-domain verification |
| `asuid.admin` | TXT | (same value) | Azure App Service custom-domain verification |

### CRITICAL for email (Azure Communication Services, added 2026-08-30)

| Name | Type | Value | Purpose |
|---|---|---|---|
| `@` | TXT | `ms-domain-verification=0b709488-0d86-4c8e-9639-60abb536810e` | ACS domain ownership |
| `@` | TXT (the ONE SPF record) | `v=spf1 +mx +a +ip4:66.102.128.65 +include:spf.websiteservername.com +include:relay.mailchannels.net include:spf.protection.outlook.com ~all` | SPF — legacy host + mailchannels + ACS. **`include:sendgrid.net` REMOVED 2026-08-31** now that nothing sends through SendGrid; this also closes the SendGrid rollback path, which the account suspension had already closed in practice |
| `selector1-azurecomm-prod-net._domainkey` | CNAME | `selector1-azurecomm-prod-net._domainkey.azurecomm.net` | ACS DKIM 1 |
| `selector2-azurecomm-prod-net._domainkey` | CNAME | `selector2-azurecomm-prod-net._domainkey.azurecomm.net` | ACS DKIM 2 |

**ACS SPF verification quirk (cost us 3 failed attempts on 2026-08-30):** Azure's checker
rejects merged multi-include SPF records AND the `~all` softfail form. The Microsoft-support-
confirmed procedure: temporarily set the SPF record to exactly
`v=spf1 include:spf.protection.outlook.com -all`, initiate verification (passes in seconds),
then restore the full merged record. Verification is one-time and does not re-run. Keep the
swap window short: the temporary record hard-fails every non-Azure sender.

**Engagement tracking was DISABLED by default** on the ACS domain and was enabled 2026-08-31 after a live
test showed Delivered arriving but never Opened/Clicked. ACS ships `userEngagementTracking: Disabled`, so it
emitted no engagement events at all -- the code was right, there was nothing to receive. Enabling it makes ACS
inject a tracking pixel and rewrite links in outgoing mail. Already-sent mail does not backfill.

Sender usernames configured in ACS: `DoNotReply` (auto), `no-reply`, `support` — production
`Email__FromEmail` is `support@iproadvisers.com`; any new FROM address needs its username
created in ACS first (error otherwise: InvalidSenderUserName).

A domain may carry only ONE SPF TXT record — always merge, never add a second. The pre-ACS value
(for history / rollback): `v=spf1 +mx +a +ip4:66.102.128.65 +include:spf.websiteservername.com
+include:relay.mailchannels.net +include:sendgrid.net ~all`. The sendgrid include was dropped 2026-08-31. Old value may linger in resolver
caches up to the 14400s TTL after edits.

### The rest of the zone (legacy site + mailboxes — not the app's, but don't break them)

| Name | Type | Value | Purpose |
|---|---|---|---|
| `@` | A | `66.102.128.65` | Legacy marketing site at the old host |
| `www` | CNAME | `iproadvisers.com` | Legacy site |
| `mail` | CNAME | `iproadvisers.com` | Owner mailboxes at the legacy host |
| `@` | MX 0 | `iproadvisers.com` | Mail delivery to the legacy host |
| `_dmarc` | TXT | `v=DMARC1; p=none; rua=mailto:dmarc@iproadvisers.com` | DMARC. Reporting address added 2026-08-31 so aggregate reports start arriving — **the mailbox must exist or the reports are silently lost**. Enforcement deliberately left at `p=none`: this domain ALSO sends ordinary business mail through the legacy host (MX -> 66.102.128.65), and nobody has confirmed that mail is DKIM-aligned. Tightening to `p=quarantine` before checking would quarantine the owner's own email. Revisit after ~2 weeks of reports, once ACS deliverability is proven |

---

## Zone: 247advisers.com (GoDaddy; SOA serial 2026071003 at capture)

### CRITICAL — every agent website depends on this one record

| Name | Type | Value | Purpose |
|---|---|---|---|
| `*` (wildcard) | CNAME | `ipro-prod-web.azurewebsites.net` | ALL agent subdomains (`michaeltran.247advisers.com`, etc.) resolve through this single wildcard — there are no per-agent records. Verified: `michaeltran` and `bahmanmotamed` both resolve via it, and even undefined names (e.g. `_dmarc`) fall through to it, which is how we know it is a wildcard |

### The rest

| Name | Type | Value | Purpose |
|---|---|---|---|
| `@` | A | `199.68.177.133` | Root parks at a legacy/GoDaddy target (not the app) |
| `www` | CNAME | `247advisers.com` | — |
| `@` | MX 0 / 10 | `smtp.secureserver.net` / `mailstore1.secureserver.net` | GoDaddy email routing (legacy) |

Note: no SPF/DMARC TXT on this zone — acceptable while nothing sends FROM `@247advisers.com`.
If that ever changes, add them first.

---

## Azure resources these records point at (for the DR picture)

- App Service: `ipro-prod-web`, `ipro-prod-admin` (rg `ipro-production`, canadaeast)
- Email: ACS `ipro-prod-comms` + Email service `ipro-prod-email` (data location Canada),
  sending domain `iproadvisers.com`, sender `no-reply@iproadvisers.com`
- Storage: `iprostorageprod` (blob media)

Related: `DOCS/18_AZURE_INFRASTRUCTURE.md`, `DOCS/14_BACKUP_AND_RELEASE_CHECKLIST.md`, TODO item 431.
