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
