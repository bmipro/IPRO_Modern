# ops/

Operational scripts that run on the maintenance machine rather than inside either app.

## Important: these are copies, not the live files

The scheduled task and any manual run execute the copies in **`C:\Users\admin\lego\`**, alongside
`lego.exe`, the account key, and the issued certificates -- none of which belong in git.

The copies here exist so the scripts are versioned, reviewable, and covered by the repo backups.
**If you edit one, copy it to the other**, or they will drift and the version you are reading will
not be the version that runs.

```bash
copy C:\Users\admin\lego\Check-CertExpiry.ps1 ops\Check-CertExpiry.ps1
copy C:\Users\admin\lego\Renew-Certs.ps1      ops\Renew-Certs.ps1
copy C:\Users\admin\lego\New-AgentCert.ps1    ops\New-AgentCert.ps1
```

Neither script contains a secret. The PFX password is read at runtime out of `run_app.ps1` /
`run_admin.ps1` in the lego folder, which is deliberately not in this repository.

## Contents

| Script | Purpose |
|---|---|
| `Check-CertExpiry.ps1` | Daily TLS expiry watchdog. Run by the "IPRO Certificate Expiry Watch" scheduled task. |
| `Renew-Certs.ps1` | Renews the two **platform** certs and pushes them to Azure App Service. Needs a person for the DNS TXT step. |
| `New-AgentCert.ps1` | Issues a first certificate for **any** custom domain — an agent's, on their own registrar. |

### When an agent binds a custom domain: do nothing

Agent domains are fully automatic. The agent adds one CNAME, the domain check binds the hostname and
requests an Azure App Service Managed Certificate, and that certificate issues within minutes, binds
itself, and **renews itself thereafter**. Proven on `www.4ipro.com` and `www.drhug.ca` (July 2026) and
`www.ouritems.ca` (August 2026).

`New-AgentCert.ps1` is a **fallback for when that genuinely fails**, not the standard path. Reach for it
only after the 3-hour alert from `DomainAutomationJob` fires, and only after checking that the managed
certificate does not already exist:

```
az resource show --ids /subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.Web/certificates/managed-www-their-domain-ca --query properties.thumbprint
```

If that returns a thumbprint, **bind it — do not issue a new certificate.**

> On 2026-08-06 this check was skipped. A managed certificate for `www.ouritems.ca` was issuing normally;
> a Let's Encrypt certificate was issued by hand and bound over the top, replacing a self-renewing
> certificate with a 90-day manual chore. It was rebound to the managed certificate the same day.

If you do use it, the Let's Encrypt certificate does not auto-renew, so add the domain to `$Domains` in
`Check-CertExpiry.ps1` *and* `DefaultDomains` in `src/IPRO.Scheduler/CertificateExpiryJob.cs`.

Full context, including why certificate expiry now breaks newsletter images retroactively, is in
[DOCS/20_CERTIFICATES.md](../DOCS/20_CERTIFICATES.md).
