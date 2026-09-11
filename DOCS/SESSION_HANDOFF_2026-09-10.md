# Session handoff — 2026-09-10 (11 days to launch)

## What happened

- **471 (owner's concern, morning):** the Anthropic agent-incident disclosures (a fourth model breach
  of third-party systems disclosed 09-10, three in July). Two zero-cost hardening steps logged for the
  owner to run after launch week: sign the Azure CLI out of the dev machine or scope it read-only, and
  GitHub branch protection on main requiring the Actions workflow. Standing rules restated.
- **Data backup (owner's concern):** "a client calls and says I accidentally deleted all my clients
  info" had no answer. Assessed: database PITR 7 days never rehearsed, blob soft delete off, every
  portal delete a hard delete. Four-step plan agreed: (1) owner raised database retention to 35 days
  and enabled blob/container soft delete (30 days) and versioning -- done and read back the same
  morning; (2) restore rehearsal this week (473); (3) a client recycle bin before launch (472, built
  today); (4) a nightly logical dump after launch (474).
- **473 restore rehearsal, done the same evening:** production restored to a throwaway server at
  22:10 UTC, Ready in 7 min 14 s, `ipro_crm` present, 27 clients and 2 agents on the copy and on
  production, the recycle-bin table present; copy deleted; runbook in `DOCS/14`. Two production
  firewall rules were left at the end (one added today for the check, one from 1 July); the owner
  removes them.

## Pushed today

| Code | Item | Gate |
|---|---|---|
| `4a3b9ca` | **472** client recycle bin: delete snapshots everything the eraser removes and keeps the files; Recently Deleted page; one-click Restore with ids remapped and history re-linked; nightly purge after 30 days; erasure covers the table; guides 02 and 14 | 733/733 |
| `9137190` | **475** Starter Articles editor: 'Add to this group' pre-fills business type, category and the next sort order; Business Type offers the verticals in use (datalist), on articles and starter pages | 736/736 |
| `161fac8` | **474** (09-11) nightly gzipped SQL dump of the database to the private db-backups container, 30-day retention; SuperAdmin Backups page with Run now; restore runbook | 739/739 |
| `ee59468` | **454 (rest) + 476** the two ops mails keep the provider's answer (warn on a refused send); Backups page shows each dump's size and the change against the previous one | 742/742 |
| `417da98` | **477** (code half) the old public names redirect permanently to the platform -- the home, or the host's own landing page (/accountants) -- once App:AliasHosts is set; launch-day runbook in DOCS/14 | 745/745 |
| `fc8fd00` | **478** (/accountants) the accountants landing page from the designer's package: platform header and footer, live package cards, live starter-site preview in the frame, register and preview links with the business type; phone-width clip fixed | 749/749 |
| `770d126` | **478** (/mortgage) the mortgage landing page from the designer's package on the same template; the corrected four-column footer and the pricing cards become partials both pages share, one stylesheet, icons under images/landing; the accountants footer's Terms and Privacy links fixed (/Home/Terms and /Home/Privacy answered 404) | 754/754 |
| `843f4be` | **479** Did You Know teasers decode entities (a literal &mdash; showed in the accountants preview); the 2 x 3 grid keeps its three columns inside the landing pages' preview frame (stack rule phone-only now) | 756/756 |

## Close-out 2026-09-10

Pushed and verified at `/health/version` on both hosts before each tick:

| Code | Item | Gate |
|---|---|---|
| `4a3b9ca` | **472** client recycle bin: snapshot of everything the eraser removes, files kept, Recently Deleted page, one-click Restore with ids remapped and history re-linked, nightly purge after 30 days | 733/733 |
| docs | **473** restore rehearsal done (22:10 UTC point, Ready in 7 min 14 s, counts matched, copy deleted); runbook in DOCS/14 | -- |
| `9137190` | **475** Starter Articles editor: 'Add to this group' pre-fills, Business Type offers the verticals in use (datalist), same on starter pages | see the tick |

Final build on both hosts: `d632b69`. Tree clean and pushed.

Owner-side today, all read back: database backup retention 35 days; blob and container soft delete
30 days; blob versioning on; production firewall back to `AllowAzureServices` only (two client-IP
rules removed on the owner's word). The owner tested the recycle bin on a client with a follow-up:
delete, Recently Deleted, Restore -- everything back.

Close-out as asked: both snapshot zips (`git archive HEAD` to `OneDrive\Codex_Code_Bkup` and
`Documents\IPRO_Backups`), build servers shut down, no test host running. Local MySQL is the
Windows service `IPROLocalMySQL` (AUTO_START): nothing to stop before a reboot.
## Do this first tomorrow

0. **474 nightly database dump to blob storage** -- the owner chose to start the day with it (3-4 h build with tests; no mysqldump on Linux App Service, so an in-app export job to a private container with a 30-day lifecycle rule).
2. **Ticket 2608310040012537 (442)** -- Microsoft's answer to the 09-08 reply.
3. **PayPal live cutover** -- production is still sandbox.
4. Owner pre-launch list: Postmaster Tools, PayPal Verified badge, "Contact us" channel, the Masoud demo (463).

Related: `DOCS/TODO.md` 471-474; `DOCS/14_BACKUP_AND_RELEASE_CHECKLIST.md`; `DOCS/SESSION_HANDOFF_2026-09-08.md` (09-08 and 09-09).
