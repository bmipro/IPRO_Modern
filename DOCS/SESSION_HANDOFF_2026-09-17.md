# Session handoff — 2026-09-17 (4 days to launch)

## What happened

- **Google approved the OAuth verification (morning):** calendar.events, two days after submission.
  The unverified-app screen and the 100-user cap are gone for every adviser. Rule from Google's reminders:
  any change to Branding or Data access resets the verification. Recorded in DOCS/27 and TODO 486.

- **Audit reconciliation (morning):** the owner asked whether the auditors' findings had all been fixed.
  `DOCS/AUDIT_RECONCILIATION_2026-09-17.md` lists every finding from all eight review documents with
  evidence: every Critical and High closed; one launch blocker without a closing record (the PayPal live
  cutover, owner-side, Friday or Saturday); a tail of Lows.

- **492 (afternoon):** the minute-sized Lows and the owner's three small items in one batch, red-first
  (23 pins). Details in `DOCS/TODO.md` 492. JOBS-11 dropped on the owner's call.

- **The narrow audit and 493 (afternoon):** two review passes over everything since 2026-08-28
  (`DOCS/NARROW_AUDIT_2026-09-17.md`). The serious finding: 491 waited for a send slot INSIDE the
  claimed send loops, and a wait for the hour window is most of an hour -- on launch morning the
  15-minute claims would have gone stale mid-wait, recipients mailed twice, every blast retired as
  Failed, all five workers parked, and request-path mail (password reset, lead notification) hung
  to Azure's 230-second cut-off. 493 bounds the wait at 90 seconds and answers Deferred instead:
  the loops pause and resume once a minute, nothing is counted, the screen says In progress.
  Plus the small items (telemetry scrub, previous signing key, lead-notification cap, form limits
  3 per 5 min, alias path encoding, primary-calendar wording, the Google delete loop, the
  roll-up writer, replayed pixels). Details in `DOCS/TODO.md` 493.

## Pushed today

| Code | Item | Gate |
|---|---|---|
| `e692cd7` | **492** pre-launch audit remainder (eleven Lows) + the owner's three small items | 864/864 |
| `0da8190` | **493** the send gate never waits past its bound; the narrow audit's before-launch fixes | 884/884 |

## Do this first tomorrow

1. **PayPal live cutover (Friday or Saturday, owner's choice):** production is still sandbox; portal-to-portal
   (the owner enters the live client id, secret and webhook id), then the webhook and a first live charge
   path verified together, with margin before the 21st.
2. **Owner-side:** the DNS switch for the four live names on or before 21 September (DOCS/14 -- APPEND to
   `App__AliasHosts`, 483), the 19th or 20th is fine; the lawyer's review of both legal documents; the
   designer's items and the /insurance package when ready.
3. **Owner-side, five minutes, after 493 is live (H-1 of the narrow audit):** on BOTH App Services set
   `Email__TrackingSigningKey` to a new random value (32+ characters) and `Email__TrackingSigningKeyPrevious`
   to the current value of `Email__AzureEventWebhookSecret` (copied in the portal), so the click links
   sent since the 14th keep redirecting; clear `Previous` in a month. Both apps restart once.
4. **Launch-week expectation at the cap (491, re-sized by 493):** bulk mail goes at 80 an hour, so a
   500-client newsletter takes about six and a half hours and lands complete, pausing and resuming
   once a minute and showing **In progress** on Email Activity meanwhile; two advisers sending the
   same morning interleave. Transactional mail keeps 20 an hour and never waits past the minute.
5. **After launch, from the reconciliation and the narrow audit (`DOCS/NARROW_AUDIT_2026-09-17.md`, last section):** the telemetry scrubber's blind spot (L2/L3), the sanitisation
   backfill (L12), the website editor's write-path gates (WEB-L-1), the two schema authorities, browser
   coverage, staging, credit notes (M6, parked), the Phase 3 truth sweep; **.NET 8 leaves support on
   2026-11-10 -- move to .NET 10 in October.** White-label: `DOCS/WHITE_LABEL_UPGRADE.md` when a partner
   conversation starts. Microsoft: a new quota request in mid-October with 30 days of history.
6. After launch week: 471, 450, the From display name (480), the Entra client secret rotation (482).

Related: `DOCS/AUDIT_RECONCILIATION_2026-09-17.md`; `DOCS/NARROW_AUDIT_2026-09-17.md`; `DOCS/TODO.md` 486, 491, 492, 493; `DOCS/27_GOOGLE_OAUTH_VERIFICATION.md`;
`DOCS/SESSION_HANDOFF_2026-09-16.md`.
