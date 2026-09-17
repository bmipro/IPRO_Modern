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

## Pushed today

| Code | Item | Gate |
|---|---|---|
| _see log_ | **492** pre-launch audit remainder (eleven Lows) + the owner's three small items | whole tree |

## Do this first tomorrow

1. **PayPal live cutover (Friday or Saturday, owner's choice):** production is still sandbox; portal-to-portal
   (the owner enters the live client id, secret and webhook id), then the webhook and a first live charge
   path verified together, with margin before the 21st.
2. **Owner-side:** the DNS switch for the four live names on or before 21 September (DOCS/14 -- APPEND to
   `App__AliasHosts`, 483), the 19th or 20th is fine; the lawyer's review of both legal documents; the
   designer's items and the /insurance package when ready.
3. **Launch-week expectation at the cap (491):** a 500-client newsletter takes about five hours and lands
   complete; two advisers sending the same morning queue behind each other.
4. **After launch, from the reconciliation:** the telemetry scrubber's blind spot (L2/L3), the sanitisation
   backfill (L12), the website editor's write-path gates (WEB-L-1), the two schema authorities, browser
   coverage, staging, credit notes (M6, parked), the Phase 3 truth sweep; **.NET 8 leaves support on
   2026-11-10 -- move to .NET 10 in October.** White-label: `DOCS/WHITE_LABEL_UPGRADE.md` when a partner
   conversation starts. Microsoft: a new quota request in mid-October with 30 days of history.
5. After launch week: 471, 450, the From display name (480), the Entra client secret rotation (482).

Related: `DOCS/AUDIT_RECONCILIATION_2026-09-17.md`; `DOCS/TODO.md` 486, 491, 492; `DOCS/27_GOOGLE_OAUTH_VERIFICATION.md`;
`DOCS/SESSION_HANDOFF_2026-09-16.md`.
