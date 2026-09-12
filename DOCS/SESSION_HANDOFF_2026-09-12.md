# Session handoff — 2026-09-12 (9 days to launch)

## What happened

- **480 (owner's finding, morning):** a drip test arrived with Reply-To support@iproadvisers.com, so a
  client's reply to their adviser would land at support and need relaying by hand. That was 440's
  free-webmail substitution (09-01) doing what it was built to do; the owner's call, agreed: a reply
  that reaches the adviser outweighs the partial spam score, so both providers keep the adviser's
  own address as Reply-To, webmail or not, and the classifier is gone. Details in `DOCS/TODO.md` 480.

- **481 (owner's finding, morning):** an e-letter to two clients delivered one and left the other Queued.
  ACS's metric showed one SendMail and no retry; the log showed no error; the dispatcher mails every
  Queued row it finds. The letter had been saved as due before its recipient rows existed, and the
  minutely job claimed it in that gap. The send and its rows commit together now (e-cards too), and
  startup marks rows stranded under a finished send as Failed with the reason. Details in `DOCS/TODO.md` 481.

- **482 (afternoon):** SuperAdmin is behind Microsoft Entra sign-in. The owner added the Microsoft
  identity provider on `ipro-prod-admin` in the portal (single tenant, 24-month secret, require
  authentication, 302 redirect); the two health paths were excluded from the CLI on his go, because
  the portal dialog has no field for it and the deploy workflow reads `/health/version` anonymously.
  Verified from outside: health 200 for anyone, everything else 302 to Microsoft / 401 for API callers.
  Client secret expires 2028-09-11 (TODO 482). Runbook in `DOCS/14`.

- **483 (afternoon):** the owner registered ipromortgages.com, and the 21 September runbook was
  rehearsed on it end to end the same afternoon: four records at GoDaddy, both names bound with
  managed certificates, `App__AliasHosts` set to the two mortgage entries. The name now redirects
  permanently to /mortgage. DOCS/14 step 3 says APPEND on the day. Details in `DOCS/TODO.md` 483.

- **484 (afternoon):** the owner saw ipromortgages.com redirect and asked for the brand name to
  stay in the address bar. A brand domain now serves its landing page in place (its files and the
  live preview frame too) and redirects everything else to the platform with the path kept; the
  iproadvisers.com pair still redirects to the home. Canonical stays on the platform. Details in
  `DOCS/TODO.md` 484.

## Pushed today

| Code | Item | Gate |
|---|---|---|
| `e8ed964` | **480** Reply-To is the adviser's own address on every client-facing message, webmail or not; the support address only when none is given; the freemail classifier removed | 738/738 |
| `5fbcd8b` | **481** e-letter and e-card creation commit the send and its recipient rows together (the minutely job could claim a letter before its rows existed: two recipients, one delivered, one Queued forever); startup marks rows stranded under a finished send as Failed with the reason | 742/742 |
| docs | **482** SuperAdmin behind Microsoft Entra sign-in; health paths excluded; runbook in DOCS/14 (owner-side in the portal, one CLI change on his go; no code) | -- |
| docs | **483** ipromortgages.com registered, bound, certificate issued, redirecting to /mortgage; the runbook rehearsed and corrected (append, not set) | -- |
| `59c8c78` | **484** a brand domain serves its landing page under its own name (files and the preview frame too), redirects the rest to the platform with the path kept; the landing partials link the home absolutely | 763/763 |

## Close-out 2026-09-12

Pushed and verified at `/health/version` on both hosts before each tick:

| Code | Item | Gate |
|---|---|---|
| `e8ed964` | **480** Reply-To is the adviser's own address on every client-facing message, webmail or not (440's substitution taken back on the owner's decision) | 738/738 |
| `5fbcd8b` | **481** an e-letter's or e-card's recipient rows commit with the send (the minutely job could claim a letter before its rows existed); rows stranded under a finished send are repaired at startup | 742/742 |
| docs | **482** SuperAdmin behind Microsoft Entra sign-in: portal by the owner, health paths excluded from the CLI on his go, assignment required with only the owner assigned; secret expires 2028-09-11 | -- |
| docs | **483** ipromortgages.com registered, bound with managed certificates, redirecting to /mortgage; the 21 September runbook rehearsed end to end and corrected (append the live names to the existing alias setting) | -- |
| `59c8c78` | **484** a brand domain serves its landing page under its own name (files and the live preview frame too) and redirects the rest to the platform with the path kept; the iproadvisers names still redirect to the home; the landing partials link the home absolutely | 763/763 |

Final build on both hosts before this close-out: `013a595` (second close-out of the day, after 484). Tree clean and pushed.

Owner-side today, all read back: the e-card unsubscribe / preferences / subscribe-again loop tested
end to end on a live client; the Hangfire dashboard checked (one server, minutely jobs current) and
its 771 historical failed jobs deleted; the Entra sign-in tested in two browsers (the `/Admin/` 404 in
the second was a bookmark to a path that never existed; `/` is the address); the four DNS records
at GoDaddy; the on-demand checks of `/health` and `/health/version` through the new gate.

Decisions on record: open and click tracking waits for Microsoft's answer until Monday; if it is not
positive, the platform's own pixel and click redirect get built (about a day). PayPal live cutover
and the ticket also wait until Monday.

Close-out as asked, taken twice today (13:50 after 483, and again after 484): both snapshot zips (`git archive HEAD` to `OneDrive\Codex_Code_Bkup` and
`Documents\IPRO_Backups`), build servers shut down, no test host running. Local MySQL is the
Windows service `IPROLocalMySQL` (AUTO_START): nothing to stop before a reboot.
## Do this first tomorrow

1. **Microsoft's answer on ticket 2608310040012537 (442)** -- Monday cutoff. Positive: verify engagement tracking by sending one e-letter and reading the source for a rewritten link. Otherwise: build the platform's own open pixel and click redirect (opens and clicks into the same OpenedAt/ClickedAt fields; about a day with the gate).
2. **PayPal live cutover** -- production is still sandbox; portal-to-portal (the owner enters the live client id, secret and webhook id), then the webhook and a first live charge path verified together.
3. **Owner-side:** the DNS switch for the four live names on or before 21 September (DOCS/14 -- and APPEND to `App__AliasHosts`, 483); tell the designer about the accountants package's 375 px clip and the two open confirmations (trial card, orange hex); the /insurance package when ready.
4. Small items offered, not yet decided: the client Edit form saying why the newsletter tick is ignored for an unsubscribed client; `/Admin/` landing on the login page; Hangfire deleting a job automatically once its retries are exhausted.
5. After launch week: 471 (Azure CLI signed out or read-only on the dev machine; branch protection on main), 450 manual poll check, the From display name and a per-adviser reply alias (480), the Entra client secret rotation before 2028-09-11 (482).

Related: `DOCS/TODO.md` 471-483; `DOCS/14_BACKUP_AND_RELEASE_CHECKLIST.md`; `DOCS/SESSION_HANDOFF_2026-09-10.md` (09-10 and 09-11); `DOCS/SESSION_HANDOFF_2026-09-08.md` (09-08 and 09-09).
