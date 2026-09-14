# Session handoff — 2026-09-14 (7 days to launch)

## What happened

- **Ticket 2608310040012537 (442), morning:** read from Azure with the CLI (read-only): declined 04 Sept
  on volume ("28 emails in 30 days"), the owner's reframe 08 Sept, Harsh Raj's acknowledgement 10 Sept
  (under review, no date), the owner's split request 11 Sept, nothing since. Microsoft's own pages still
  say engagement tracking cannot be enabled on a custom domain with default sending limits (limits page
  updated March 2026; the quota page says a support ticket is the only route and approval is never
  automatic). The domain's tracking flag reads Enabled at the resource level and the engagement metric
  has no data at all; Event Grid, the webhook and the screen were already wired. On the owner's go a
  one-line reply asking for a decision date on the tracking half was posted on the case through the
  CLI at 13:18 Toronto time, under his name; it is the thread's newest entry. Harsh Raj's hours are
  Tuesday to Saturday, so nothing was expected the same day.

- **488 (afternoon):** the platform tracks opens and clicks on its own: a one-pixel image and a signed
  redirect on app.iproadvisers.com, one random token per recipient row, minted by each of the six
  marketing dispatchers just before the send, landing in the same OpenedAt/ClickedAt columns through
  the same recorders the provider events use. Invoice mail stays "not tracked". Off switch
  `Email__PlatformTrackingEnabled=false` (both apps) if Microsoft ever enables the provider's tracking.
  Details in `DOCS/TODO.md` 488; the runbook note in DOCS/DNS_ZONE_RUNBOOK.md.

- **DOCS/27, morning:** the "known trap" section had been inserted twice by the 487 docs script (its
  idempotency guard tested a string its own insert did not contain); removed and deployed (`03f77ce`).
  The Google verification console steps (untick the old calendar scope, record the video, submit) moved
  to later this week at the owner's request.

- **Also this morning:** the owner asked whether PHP could go on the dev machine; the answer was yes
  with the one caution (bundles that carry their own MySQL on 3306); he chose another server instead.

- **489 (evening):** the privacy policy caught up with three facts -- clicks are measured by the
  platform itself (488), a Google Calendar paragraph in the shape Google's OAuth reviewer looks for,
  and Azure Communication Services named as the deliverer (SendGrid as standby). Both copies, the
  reviewer's change log, nine pins. The lawyer's review is still outstanding. The owner also got a
  step-by-step Google verification guide as a file outside the repo
  (`C:\Users\admin\Documents\IPRO_Google_Verification_Steps.html`, generated from DOCS/27).

## Pushed today

| Code | Item | Gate |
|---|---|---|
| `03f77ce` | docs: DOCS/27 duplicated 487 section removed | -- |
| `fa58c71` | **488** platform-owned open and click tracking for newsletters, drip steps, e-cards, e-letters, polls and Did You Know: pixel + signed redirect on the platform host, per-recipient token, the same recorders and columns as the provider path; invoice mail stays "not tracked" | 803/803 |
| `bbd5b6a` | **489** privacy policy: clicked + how it is measured, the Google Calendar paragraph, Azure Communication Services as the deliverer; both copies, change log, nine pins | 812/812 |

## Close-out 2026-09-14

Close-out as asked at the end of the evening, after 489. Final build on both hosts before this
close-out: `62a1a12`. Tree clean and pushed.

| Code | Item | Gate |
|---|---|---|
| `03f77ce` | docs: DOCS/27 duplicated 487 section removed | -- |
| `fa58c71` | **488** platform-owned open and click tracking: pixel + signed redirect on the platform host, per-recipient token, six channels, the same recorders and columns as the provider path; invoice mail stays "not tracked" | 803/803 |
| `bbd5b6a` | **489** privacy policy: clicked + how it is measured, the Google Calendar paragraph, Azure Communication Services as the deliverer; both copies, the reviewer's change log, nine pins | 812/812 |

Owner's confirmations on record: the first live card after 488 (a Halloween card, 17:51) delivered
17:52, opened 17:52, clicked 17:53, status Clicked on Email Activity; it arrived in the inbox with no
spam tag; the mail client asked to approve image loading, as it does for any sender not in the address
book (the footnote's caveat). The 489 wording was read and called accurate before the deploy.

Google console this evening (the desktop app's browser pane, signed in as the project owner): the Data
access list held only the wide `calendar` scope -- neither `calendar.events` nor `userinfo.email` had
ever been saved, so Friday's "added" never persisted. Two attempts to save the corrected list did not
persist either: the page showed the new rows, the justification box was empty, and Save left no message
the owner noticed. Hypothesis for tomorrow: with a sensitive scope on the list this page validates
"How will the scopes be used?" before it saves. Learned from the page itself: the justification and the
YouTube link are entered on Data access (the Verification centre only submits), and Google's note says
the unverified-app screen must appear in the video. The owner's guide file
(`C:\Users\admin\Documents\IPRO_Google_Verification_Steps.html`) was corrected for all three. Lesson
recorded: reloading the shared browser pane discards the owner's unsaved form; read the server state
from a separate tab (the Chrome extension) instead.

Microsoft ticket: the 13:18 reply asking for a decision date on the tracking half is the thread's newest
entry; no answer expected before Tuesday afternoon (Harsh Raj's hours). Tracking no longer depends on it.

Backups: `IPRO_Modern_backup_<stamp>.zip` in `C:\Users\admin\OneDrive\Codex_Code_Bkup` and
`C:\Users\admin\Documents\IPRO_Backups` (git archive of HEAD, stamped at the close-out); build servers
shut down; MySQL stays a Windows service.

## Do this first tomorrow

1. **Google console, from step 2 of the owner's guide (fresh, in the morning):** Add or remove scopes --
   untick `.../auth/calendar`, tick `.../auth/calendar.events` (filter `events`) and `.../auth/userinfo.email`,
   Update; paste the justification (876 characters) into "How will the scopes be used?"; Save and read
   the message. Then the screen recording (unverified-app screen, consent box ticked, an appointment both
   ways, Disconnect, end on the privacy paragraph), YouTube Unlisted, the link on Data access, Save,
   Verification centre -> submit. Assistant checks the server state from a separate tab, never by
   reloading the owner's pane.
2. **Microsoft's answer (442):** nothing to do but wait; if engagement tracking is ever enabled on the
   domain, the 488 note in DOCS/DNS_ZONE_RUNBOOK.md -- never both at once.
3. **PayPal live cutover** -- production is still sandbox; portal-to-portal, then the webhook and a first
   live charge path verified together.
4. **Owner-side:** the DNS switch for the four live names on or before 21 September (DOCS/14 -- APPEND to
   `App__AliasHosts`, 483); the designer's items; the /insurance package when ready; the lawyer's review
   of both legal documents (README-review-notes, 14 September entry).
5. Small items offered, not yet decided: the client Edit form saying why the newsletter tick is ignored for
   an unsubscribed client; `/Admin/` landing on the login page; Hangfire deleting a job once its retries
   are exhausted.
6. After launch week: 471 (Azure CLI signed out or read-only on the dev machine; branch protection on
   main), 450 manual poll check, the From display name and a per-adviser reply alias (480), the Entra
   client secret rotation before 2028-09-11 (482).

Related: `DOCS/TODO.md` 442, 486-489; `DOCS/DNS_ZONE_RUNBOOK.md`; `DOCS/27_GOOGLE_OAUTH_VERIFICATION.md`;
`DOCS/legal/README-review-notes.md`; `DOCS/SESSION_HANDOFF_2026-09-12.md` (09-12);
`DOCS/SESSION_HANDOFF_2026-09-10.md` (09-10 and 09-11).
