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
| _see log_ | **475** Starter Articles editor: 'Add to this group' pre-fills business type, category and the next sort order; Business Type offers the verticals in use (datalist), on articles and starter pages | whole tree |

## Do this first tomorrow

2. **Ticket 2608310040012537 (442)** -- Microsoft's answer to the 09-08 reply.
3. **PayPal live cutover** -- production is still sandbox.
4. Owner pre-launch list: Postmaster Tools, PayPal Verified badge, "Contact us" channel, the Masoud demo (463).

Related: `DOCS/TODO.md` 471-474; `DOCS/14_BACKUP_AND_RELEASE_CHECKLIST.md`; `DOCS/SESSION_HANDOFF_2026-09-08.md` (09-08 and 09-09).
