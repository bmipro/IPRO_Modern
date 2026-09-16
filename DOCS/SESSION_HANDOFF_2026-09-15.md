# Session handoff — 2026-09-15 (6 days to launch)

## What happened

- **Google OAuth verification SUBMITTED (morning-afternoon, owner-side, guided):** the demo video was
  recorded on the live site (4:15) and frame-checked here before upload (every required moment on screen;
  the owner's own calendar entries visible, his call, accepted). Data access corrected and saved with the
  justification and the YouTube link; branding verification demanded proof of the homepage domain ->
  Search Console Domain property + a TXT record in the cPanel Zone Editor (live within minutes) -> branding
  verified and published; Prepare for verification -> questionnaire -> submitted. Verification centre now
  reads Branding verified + Data access under review. Details and the working order in DOCS/27.

- **Lesson kept (memory + DOCS/27):** the desktop Browser pane is the owner's own tab -- never reload it to
  check what Google saved; read from a separate Chrome-extension tab. The Data access page does not persist
  a Save while the justification box is empty.

- **490 (afternoon):** Disconnect now removes the events copied in from Google (same SaveChanges as the
  connection; revoke failure or not; pre-490 leftovers too), the Calendar page shows copies only while a
  connection exists, and the confirm text says copies go, follow-ups stay, Google's own copies stay
  there. Follow-ups keep GoogleEventId so a reconnect does not duplicate. Red first (4). Details in
  `DOCS/TODO.md` 490.

## Pushed today

| Code | Item | Gate |
|---|---|---|
| `4775ff0` | **490** Google Calendar Disconnect removes the copied Google events; the Calendar page shows copies only while connected; confirm text says so | 816/816 |

## Close-out 2026-09-15

Close-out as asked at the end of the day, after 490. Final build on both hosts before this close-out:
`3e70ef0`. Tree clean and pushed.

| Code | Item | Gate |
|---|---|---|
| `4775ff0` | **490** Google Calendar Disconnect removes the copied Google events; the Calendar page shows copies only while connected; the confirm text says so | 816/816 |
| docs | Google OAuth verification SUBMITTED (Data access saved, branding verified via Search Console + cPanel TXT, published, questionnaire, submit); the working order in DOCS/27; last night's platform restart in DOCS/09 | -- |

Owner-side today: the demo video recorded and frame-checked, YouTube unlisted; Search Console Domain
property verified with a TXT record in the cPanel Zone Editor (keep it); branding verified and
published; Data access under review. Google's questions arrive at support@iproadvisers.com and
bahman.motamed@gmail.com; answer from the same account; do not edit Branding while the review runs.

Last night's Sev1 SMS pair (22:33 Toronto) was Azure restarting both web apps -- self-healed in 8 and 12
minutes, database untouched, nothing to fix; the owner accepts this on the current plan (DOCS/09).

Backups: `IPRO_Modern_backup_<stamp>.zip` in `C:\Users\admin\OneDrive\Codex_Code_Bkup` and
`C:\Users\admin\Documents\IPRO_Backups` (git archive of HEAD, stamped at the close-out); build servers
shut down; MySQL stays a Windows service.

## Do this first tomorrow

1. ~~490~~ shipped this afternoon.
2. **PayPal live cutover** -- production is still sandbox; portal-to-portal, then the webhook and a first
   live charge path verified together.
3. **Owner-side:** the DNS switch for the four live names on or before 21 September (DOCS/14 -- APPEND to
   `App__AliasHosts`, 483); the designer's items; the /insurance package when ready; the lawyer's review of
   both legal documents (README-review-notes, 14 September entry).
4. **Google review:** watch support@iproadvisers.com and bahman.motamed@gmail.com; answer from the same
   Google account; do not touch Branding while it runs. **Microsoft ticket (442):** wait; if engagement
   tracking is ever enabled on the domain, the 488 note in DOCS/DNS_ZONE_RUNBOOK.md.
5. Small items offered, not yet decided: the client Edit form note for an unsubscribed client; `/Admin/`
   landing on the login page; Hangfire deleting a job once its retries are exhausted.
6. After launch week: 471, 450, the From display name (480), the Entra client secret rotation (482).

Related: `DOCS/27_GOOGLE_OAUTH_VERIFICATION.md`; `DOCS/TODO.md` 486-489; `DOCS/SESSION_HANDOFF_2026-09-14.md`.
