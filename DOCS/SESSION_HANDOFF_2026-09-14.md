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
| _see log_ | **489** privacy policy: clicked + how it is measured, the Google Calendar paragraph, Azure Communication Services as the deliverer; both copies, change log, nine pins | whole tree |

## Do this first tomorrow

1. **489 shipped this evening; the lawyer's review of both legal documents is still outstanding.** It was: the privacy policy -- "clicked" in the email paragraph (the
   policy already says opened); a short Google Calendar data paragraph for the OAuth verification; the
   email-provider row (still names SendGrid; production sends through Azure Communication Services).
2. **Owner-side, after the 488 deploy:** send one birthday card to test@iproadvisers.com, open it, read
   Opened on Email Activity; confirm the cPanel filter did not tag the card (one more image in it).
3. **Microsoft's answer (442):** if engagement tracking is ever enabled on the domain, follow the 488
   note in DOCS/DNS_ZONE_RUNBOOK.md -- never both at once.
4. **PayPal live cutover** -- production is still sandbox; portal-to-portal, then the webhook and a first
   live charge path verified together.
5. **Owner-side:** Google verification (DOCS/27: untick the old `calendar` scope, the video, submit);
   the DNS switch for the four live names on or before 21 September (DOCS/14 -- APPEND to
   `App__AliasHosts`, 483); the designer's items; the /insurance package when ready.
6. After launch week: 471 (Azure CLI signed out or read-only on the dev machine; branch protection on
   main), 450 manual poll check, the From display name and a per-adviser reply alias (480), the Entra
   client secret rotation before 2028-09-11 (482).

Related: `DOCS/TODO.md` 442, 486-488; `DOCS/DNS_ZONE_RUNBOOK.md`; `DOCS/27_GOOGLE_OAUTH_VERIFICATION.md`;
`DOCS/SESSION_HANDOFF_2026-09-12.md` (09-12); `DOCS/SESSION_HANDOFF_2026-09-10.md` (09-10 and 09-11).
