# Session Log — 2026-08-05

Discussions and decisions, including the ones that did not become code. Shipped work is in the
roadmap (items 85–88); this records *why* those shapes were chosen and what was rejected.

## Thread 1 — Email deliverability

Context: a newsletter had landed in Junk, and a mail-tester run scored 3.3 where an earlier one
scored 8.6.

**Decisions:**

| Decision | Outcome |
|---|---|
| Fix SPF | Done earlier; verified published. DKIM was already valid. |
| Use an agent `@247advisers.com` address as Reply-To, to clear `FREEMAIL_FORGED_REPLYTO` (−1.0) | **Rejected by the user, mid-build.** It would require creating a forwarder per agent on `247advisers.com` or `iproadvisers.com` — a per-signup manual step and a new external dependency. Correctly judged not worth 1 point. Work was stopped and reverted rather than finished. |
| Serve newsletter images from our own domain to clear `URI_IMG_CWINDOWSNET` (−1.0) | **Chosen.** No per-agent setup, no new dependency. Shipped as roadmap item 87. |
| Buy a SendGrid dedicated IP | **Advised against** at current volume — dedicated IPs need consistent sending volume to warm up and can be worse than shared at low volume. |

**Corrections made during this thread**, both prompted by the user pushing back on the analysis:

- Initial diagnosis over-weighted SPF (worth +0.001) and had not asked for the score breakdown at all.
- The 8.6 and 3.3 tests were described as "the same message from a different IP". Wrong: the
  SpamAssassin totals (−0.9 vs −4.7) prove they were different messages.

**Scope correction after shipping:** `URI_IMG_CWINDOWSNET` was presented as a −1.0 hit on newsletters
generally. It is only a hit on newsletters *containing a blob-hosted image*. Starter banners are static
assets under `wwwroot` and were never affected. The fix is real but narrower than first stated.

## Thread 2 — Newsletter media proxy (roadmap item 87)

`GET /media/{container}/{*blobPath}` on IPRO.Web, with `NewsletterHtmlComposer` rewriting blob URLs at
compose time so existing newsletters benefit with no data migration.

**Decisions:**

- **Rewrite at compose time, not save time** — old newsletters pick it up on next send.
- **Allowlist of five already-public containers is the entire security boundary**, because the endpoint
  must be anonymous (mail clients carry no session). `agent-documents` and `portal-documents` are
  `isPrivate:true` and deliberately excluded.
- **Rejected: Azure CDN / Front Door with a `media.iproadvisers.com` CNAME.** More moving parts and
  another DNS record for the same benefit.

**Near-miss:** the allowlist checked `container` but `{*blobPath}` could carry `../agent-documents/…`,
which the blob SDK normalises into the private container. Found by chasing a wrong assertion in a
throwaway test, not by review. Guard added before deploy. Written up in `09_TROUBLESHOOTING.md`.

**Verified live:** real blob 200 through `/media`, private containers 404, traversal 404, on both
`app.iproadvisers.com` and agent custom domains.

**Behaviour worth remembering:** preview and test-send use `Request.Host` (so an agent's own custom
domain), real sends use `App:BaseUrl` (`app.iproadvisers.com`). Both work; they just look different.

## Thread 3 — Certificates (roadmap item 88)

Item 87 made a manually-renewed cert load-bearing for delivered mail. That changed the risk from
"portal unreachable" to "images break in inboxes, silently, retroactively".

**Decisions:**

- **Monitor in two places, renew in one.** `CertificateExpiryJob` runs in Azure (survives the laptop
  being off, visible on the Job Scheduler dashboard); the Windows scheduled task is kept because it
  survives the Azure app being down. Renewal stays local — it needs lego, the ACME account key, and a
  hand-published DNS TXT record.
- **The job deliberately throws when a cert is due.** Hangfire has no warning state, so failing is the
  only way to make it a red row on the dashboard. `AutomaticRetry(0)` prevents retry spam.
- **Rejected: pointing `App:BaseUrl` at `*.azurewebsites.net`** to inherit Microsoft's self-renewing
  cert. It would remove the risk but throw away the branded domain that was the point of item 87.
- **Deferred: full unattended renewal** via lego's cPanel DNS provider. DNS is on the cPanel host, not
  GoDaddy's nameservers, so that is the route — but it needs a cPanel API token a person must create.

**Bugs found by testing the alert path rather than the happy path:** the hardcoded
`C:\Users\admin\Desktop` does not exist (Desktop is OneDrive-redirected), and the script printed
"ALERT WRITTEN" without checking the write succeeded. Neither would have surfaced before October.

## Thread 4 — "Can SuperAdmin see the scheduled tasks?"

**The most useful question of the session, because the answer contradicted what had just been asserted.**

Answered: no dashboard, it was removed in the July audit, and a read-only SuperAdmin page should be
built. All wrong. The dashboard was removed from **IPRO.Web** and *moved to* **IPRO.Admin**, where it
has been the **Job Scheduler** nav item ever since, gated on the SuperAdmin claim
(`SuperAdminDashboardAuthorizationFilter`). The grep that misled searched for `UseHangfireDashboard`;
the real call is `MapHangfireDashboard`.

**Decision: do not build the jobs tab.** It would have duplicated shipped functionality. Only the cert
job was built, and it appears on the existing dashboard automatically — confirmed live, 16 recurring
jobs where there were 15.

**Transferable lesson:** a negative grep result is not evidence of absence. Checking the product's own
navigation would have settled it in seconds.

## Thread 5 — Storage and quota (roadmap items 85–86)

- **Documents now count toward `FileUploadCapacity`** alongside gallery photos — one shared pool.
- **Delete-on-replace** added for website logos and article images; agent photos already did it.
- **User overruled a proposed delete-on-replace for documents**, on the grounds that quota + explicit
  delete + account deletion already cover it. Verifying proved them right: `DocumentsController.Delete`
  already deletes the blob, and replacement isn't even possible on that path. The proposal was
  pattern-matching, not analysis.
- **Orphan-file scanner: still not built,** deliberately. With the leak closed its value is reporting,
  not reclamation. The ~8 orphans from the 2026-08-04 crash are a few MB and nothing references them.

## Open items

| Item | State |
|---|---|
| `certificate-expiry` first manual run | Triggered; dashboard badge was still grey (not yet Succeeded) at end of session. **Confirm it went green.** A red result means Azure cannot open outbound 443 to those hosts, which would make it false-alarm daily. |
| Logo delete-on-replace | Unverified. `agent-logos` baseline is **12**; a swap through the agent portal should keep it at 12, not 13. |
| Cert renewal automation | Manual DNS step remains. Needs a cPanel API token. |
| mail-tester re-run | Would confirm `URI_IMG_CWINDOWSNET` is actually gone and give a new total. Diagnostics only — a real send was delivered and opened. |
| Scheduled task logon type | Registered `Interactive` (S4U needed elevation), so it only runs while logged in. |

## Process notes

- **IPRO.Web takes ~3 minutes to cold start** after any deploy, because of its schema-repair and seeder
  chain. A 503 in that window was mistaken for an outage caused by the deploy. It was not; the app
  recovered on its own. Documented in `09_TROUBLESHOOTING.md`.
- A docs-only commit still triggers a full deploy and restart of both apps.
