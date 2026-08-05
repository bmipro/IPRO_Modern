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
```

Neither script contains a secret. The PFX password is read at runtime out of `run_app.ps1` /
`run_admin.ps1` in the lego folder, which is deliberately not in this repository.

## Contents

| Script | Purpose |
|---|---|
| `Check-CertExpiry.ps1` | Daily TLS expiry watchdog. Run by the "IPRO Certificate Expiry Watch" scheduled task. |
| `Renew-Certs.ps1` | Renews the Let's Encrypt certs and pushes them to Azure App Service. Needs a person for the DNS TXT step. |

Full context, including why certificate expiry now breaks newsletter images retroactively, is in
[DOCS/20_CERTIFICATES.md](../DOCS/20_CERTIFICATES.md).
