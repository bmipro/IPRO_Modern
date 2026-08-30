# Session handoff — 2026-08-30 (22 days to launch)

Production is on **`9abae93`**, verified live on BOTH hosts. Working tree clean, no open branches.
448/448 tests. Everything below shipped today under the usual gates (red test observed first, full
suite before commit, both-host `/health/version` after deploy).

## What shipped (5 waves)

| SHA | Wave |
|---|---|
| `f3c0794` | Homepage hero shows **real portal screenshots**; the hand-built mock panels and 66 lines of dead CSS retired. Phase 3 closed. |
| `eba1ef7` | **Azure Communication Services email provider** behind an `Email:Provider` config switch, plus `DOCS/DNS_ZONE_RUNBOOK.md`. |
| `65b9114` | The **honest** leads screenshot (the shipped one had its delivery-warning banner edited out; email works now, so the real capture replaced it). |
| `9abae93` | **All eight pre-launch audit findings** fixed (TODO 435). |

Also: contract **v0.3** pushed to the builder developer's repo (`logoRef`/`logoAltText`, approved
after confirming the host renders `AgentWebsite.LogoUrl` in all three shells).

## The day's two big events

**1. SendGrid died, and we left it.** Twilio suspended the account with no explanation (login
`ERR_USER_FORBIDDEN_ACCESS`, API 401 "Maximum credits exceeded"). Root cause is almost certainly
months of automated QA sends to nonexistent test mailboxes — the Aug 28 log shows an invoice email
to `bmotamed@ywahoo.com`, a typo'd domain. Production email now runs on ACS (Canada data location,
~7x cheaper), verified end to end: fresh lead -> warning-free card -> notification delivered from
`support@iproadvisers.com`. Four failure modes stood between us and that inbox: the suspension, a
worker-restart race, the ACS SPF-checker quirk, and a missing sender username. All documented in
`DOCS/DNS_ZONE_RUNBOOK.md`.

**2. The audit found a regression I had shipped that morning.** `SendBulkAsync` put every recipient
in one `To` header — a cross-tenant disclosure of the adviser list. **My own test pinned the broken
shape** (`Assert.Equal(2, To.Count)`), so the green suite proved nothing. Corrected in the same
commit, because leaving it would have turned the fix red and invited a revert.

## Do this first tomorrow

`DOCS/TODO.md` -> **NEXT SESSION** has the full detail. Summary: **mobile fixes 1–5** (~3–4 hrs) —
the nav drawer cannot be closed (tapping "close" navigates away and discards the page), you cannot
log out on a phone (`100vh` under Safari's toolbar), public inputs are 15px so iOS zooms on focus,
the hero widget's tab rail is `display:none` below 421px with the one visible screenshot at 39%
scale, and the marketing header has no mobile Sign-in link. The owner should eyeball these on a
real phone as they land.

**Do NOT build a mobile version.** The responsive layer exists and mostly works; these are bugs in
it. Admin has no mobile mode at all (77px content column at 375px) and is deliberately out of scope
before launch — one user, owns a desktop.

## Owner-only items still outstanding

- **PayPal live cutover** — still sandbox. Every package must be re-synced against live credentials.
  (Audit finding 4 is now closed in code too, so the ledger no longer depends on that checklist
  item, but the re-sync is still required.)
- **ACS sending quota** — new resources start conservative; check before a real newsletter goes out.
- **SendGrid endgame** — decide whether to keep appealing or walk away. If walking: remove
  `include:sendgrid.net` from SPF and drop the SendGrid keys from App Service config.
- **PayPal "Verified" badge** on Register.cshtml:426 — verify at cutover or remove.
- Open a Codex project on `C:\Users\admin\Projects\IPRO.TemplateBuilder` so the builder developer's
  task can write there.

## Demo agent (for screenshots and live testing)

Michael Tran / `michaeltran@alladvisers.com`, Platinum monthly (sandbox subscription), Insurance /
Financial, site published at `michaeltran.247advisers.com`. Staged: 18 clients, 13 open follow-ups
spanning Aug 28 – Sep 25, 3 newsletters, 8 leads. Owner holds the password; Claude never signs in
itself. Screenshots live in `C:\Users\admin\Pictures\ipro-shots\` and in the OneDrive backup folder.

## Known-open, honestly

- **TODO 433**: ACS delivery/bounce tracking (Event Grid) is NOT built. Sending works; bounces and
  complaints are not recorded, so CASL suppression currently rests only on our own unsubscribe
  links (which are provider-independent and do work).
- **8 lower-severity audit candidates were dropped without adversarial verification** (email 3,
  jobs 3, guards 1, money 1). Dropped is not cleared. That is where more audit budget should go.
- **TODO 434**: website leads have no hard-delete, and "Dismiss" reads like a failed delete.
