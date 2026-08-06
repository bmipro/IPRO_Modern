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

### When an agent binds a custom domain

Binding gets as far as "Connected" and stops: App Service Managed Certificates do not issue on this
subscription, so the hostname ends up bound with no certificate. Because the app is HTTPS-only, the
site is then **completely unreachable** — visitors get a browser security warning, which is worse than
before the domain was added.

`DomainAutomationJob` detects this and emails IPRO Operations automatically. The agent's portal tells
them we've been alerted and to expect it secured within one business day, so this is an SLA, not a
backlog item. To fulfil it:

```
powershell -File C:\Users\admin\lego\New-AgentCert.ps1 -Domain www.theirdomain.ca
```

It prints one DNS TXT record to publish at **the agent's** registrar, waits, then uploads and binds the
certificate and reads it back to confirm. Afterwards add the domain to `$Domains` in
`Check-CertExpiry.ps1` *and* to `DefaultDomains` in `src/IPRO.Scheduler/CertificateExpiryJob.cs`, or the
90-day expiry will go unwatched.

This is a stopgap. The real fix is in-app ACME issuance — the app already holds the ARM credentials it
would need, and an HTTP-01 challenge needs no registrar access at all because the domain already points
at us. Tracked in the roadmap.

Full context, including why certificate expiry now breaks newsletter images retroactively, is in
[DOCS/20_CERTIFICATES.md](../DOCS/20_CERTIFICATES.md).
