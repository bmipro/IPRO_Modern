# Session handoff — 2026-09-30

## What happened

- **AWS granted production access overnight** (50,000 emails a day, 14 a second, Canada Central). The owner: "So
  happy we got accepted with them."
- **531a built and deployed, switched off** (`a10f87d`, 1320/1320): Amazon SES behind a switch, for the email an
  adviser sends to their own clients; iPro's own mail to advisers stays on ACS. `DOCS/SES_GO_LIVE_RUNBOOK.md`
  has the owner's console steps and the way back.
- **The AWS setup, done with the owner in the console (runbook steps 1 to 6).** He clicked every Create; nothing
  secret passed through chat, a file or the repo.
  - Configuration sets `ipro-notify` and `ipro-news` (reputation metrics ticked: the console now leaves it off).
  - SNS topic `ipro-ses-events` (Standard, no encryption) with the statement letting SES in this account publish;
    the console's default statement came with an empty `Resource`, set to the topic's ARN.
  - Event destination `ipro-events` on both sets: rendering failures, rejects, deliveries, hard bounces,
    complaints, delivery delays.
  - IAM login `ipro-app-ses`, no console access, inline policy `ipro-ses-send` (read back: exactly the
    runbook's). Its first access key was closed before anyone copied it (the desktop app's browser pane blocks
    websites from writing to the clipboard, so Amazon's copy icons did nothing); it was deactivated and deleted
    unseen, and the second key was copied by hand.
  - Five settings on `ipro-prod-web` (checked by name and length only): `Email__Ses__AccessKeyId` (20),
    `SecretAccessKey` (40), `AccountId`, `EventTopicArn`, `EventSecret` (40, made in the owner's PowerShell).
    My own attempt to add the three non-key settings from the command line was stopped by my safety check, so
    the owner entered all five in the portal.
  - The HTTPS subscription to `https://app.iproadvisers.com/SesEmailEvents?secret=...` reads **Confirmed**: the
    app verified Amazon's signature and confirmed it itself.
- **The pilot is on, adviser 12 only** (the owner, Global Business Solution), since about 1:40 p.m. Eastern:
  `Email__Ses__PilotAgentIds=12` (added first), then `Email__Ses__Streams=notify`. INV-1003 to his own mailbox
  arrived From **"Global Business Solution via iPro" <mail@notify.iproadvisers.com>** (1:31 by ACS read "IPRO
  Advisers <support@iproadvisers.com>"), and Amazon's delivery report landed on the invoice a minute later
  ("delivered Sep 30, 1:45 PM"). SES made the tenant `adviser-12` on that first send: enabled, Standard
  reputation policy, both identities and both configuration sets assigned.
- **Left with the owner, his choice:** he is sending a few emails to different addresses himself and will report
  back; then the two simulator checks.
- **534 built and deployed after the close-out** (the owner: "there is no way that we could do mouse over
  Resources menu and be able to click anything"): on the live adviser sites the Resources panel closed in the
  12px strip between the menu row and the panel. On a desktop a submenu now stays open for 0.3 s after the
  pointer leaves its item; the agent-login arrow moved after "Powered by iPro" (his second ask).

## Pushed today

| Code | Item | Gate |
|---|---|---|
| `a10f87d` | **531a** Amazon SES for the email an adviser sends to their clients, built and switched off | 1320/1320 |
| `27e30b6` | **534** the Resources menu stays open on the way into its panel; the agent-login arrow follows "Powered by iPro" | 1322/1322 |

Docs: `9c3051f` (531a and the runbook), `3305034` (531a's check-mark), the close-out (`3157e7d`), and 534's
check-mark.

## Do this first when the owner is back

1. **His pilot sends:** each should show "delivered" (or "bounced", with Amazon's reason) in the document's email
   list and in Email Activity. A failure would say "could not be sent (Amazon SES ...)"; the web container log
   names it (`SesEmailService`, `SesTenants`, `SesEmailEventsController`).
2. **The simulator checks (runbook step 7):** two test clients, `bounce@simulator.amazonses.com` and
   `complaint@simulator.amazonses.com`, an invoice to each: the first reads "bounced" and the client is suppressed
   on every channel; the second leaves the client unsubscribed from everything. They cost no quota or reputation.
3. **Then everyone (step 8), the owner's go:** clear `Email__Ses__PilotAgentIds` (restart); watch a week (SES ->
   Reputation metrics: bounces under 2%, complaints under 0.05%); then `Email__Ses__Streams=notify,news`;
   CloudWatch alarms emailing the owner. The help guides' sender name ("<business> via iPro") changes with it.
4. **The way back, any time:** clear `Email__Ses__Streams` (restart); every email goes through ACS again.
5. **Carried from 09-29:** the builder retest when the developer's fixes arrive; 532 (Refer a Friend) after 531;
   decisions on 520, 524, 528 and 529; the open list (506, the page-view tables' retention, an adviser's icon
   and logo, the comped plans' renewal date, 519, `AsSplitQuery` on the client Details page); the calendar
   (clear `Email__TrackingSigningKeyPrevious` around 17 October; ACS closes to new customers 23 October, ours
   keeps working; .NET 10 in October; the new customer's first renewal 25 October).
6. **Optional:** HostPapa forwarders from `mail@notify.iproadvisers.com` and `mail@news.iproadvisers.com` to
   support@, for the rare reply that ignores Reply-To.
7. **534, a glance:** on saeedmasoudian.247advisers.com or bahmanmotamed.247advisers.com, hover **Resources**,
   move down to any column and click a link; at the foot, the arrow sits after "Powered by iPro".

Related: `DOCS/TODO.md` 531 and 534; `DOCS/SES_GO_LIVE_RUNBOOK.md`; `DOCS/SESSION_HANDOFF_2026-09-29.md`.
