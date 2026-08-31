# Session handoff — 2026-08-31 (21 days to launch)

## What shipped

**No production code.** Today was diagnosis, and it is worth being blunt about that: five
commits, all documentation. The value delivered was finding a launch blocker nobody knew
about and killing a false alarm that would have burned the rest of the week.

| commit | what |
|---|---|
| `fed8f52` | DNS runbook: SPF/DMARC edits, engagement-tracking discovery |
| `4cb16b8` | TODO 439 root cause; split out 440 |
| `c961221` | 439 "confirmed" (this conclusion was WRONG — see below) |
| `1fc122e` | 439 RESOLVED — there is no Gmail problem |
| `043857d` | 442 (the real blocker), 443, 444 |

## The day in one paragraph

We spent most of the day believing Gmail was silently discarding all ACS mail from
`iproadvisers.com`, and built an escalating theory on it — freemail Reply-To, then
image-heavy content, then domain reputation damaged by the SendGrid bounce history, with
a marketing subdomain and a warm-up ramp as the remedy. **All of it was wrong.** Every
"drop" was observed in ONE recipient mailbox, `bmot1966@gmail.com`. The owner suggested
testing a second Gmail address; one e-letter sent to three recipients at once settled it
in a minute — `bahmanmotamed@gmail.com` received it in the Gmail inbox in four seconds,
`test@iproadvisers.com` received it, and only `bmot1966@` did not. Deliverability is
healthy. While chasing that, we found the thing that actually matters: the ACS sending
quota is 30/minute and 100/hour.

## Do this first tomorrow

1. **Watch for Azure support request `2608310040012537`** (quota increase to 500/min,
   10,000/hour + enable user engagement tracking). Evaluation takes up to 72 hours, so
   expect a reply Wed–Thu. This is TODO 442 and it is the top launch risk.
2. **Until it is granted, no test sends to addresses that cannot receive.** Microsoft
   requires delivery failure rates below 2% and weighs domain reputation, and
   `iproadvisers.com` still carries the SendGrid bounce history from item 431. Junk sends
   between now and approval can cost us the quota. `test@iproadvisers.com` is the only
   permitted QA target.
3. **Build 440 and 441** — both confirmed, both well specified, neither blocked on Azure.
   Then 443 and 444. All four have full repro and fix notes on their TODO rows.

## Owner-only items still outstanding

- 🔴 **PayPal live cutover** — production is still sandbox; every package must be re-synced.
- 🟠 **Google Postmaster Tools** — register `iproadvisers.com`, verify by TXT. No longer
  urgent now that 439 is resolved, but it is the only window into Gmail's view of the
  domain and worth having before launch. Setup steps are in `DOCS/DNS_ZONE_RUNBOOK.md`.
- 🟠 **8 unverified audit candidates** (email 3, jobs 3, guards 1, money 1) — dropped
  without adversarial verification.
- 🟠 Unsubscribe link never clicked end-to-end; PayPal "Verified" badge decision;
  "Contact us for pricing" channel.
- ⚪ Azure Support Plan Standard is active (~$100 USD/month). Useful this week — worth
  reviewing after launch, not before.

## Known-open, honestly

- **`bmot1966@gmail.com` still receives nothing.** Now a one-mailbox curiosity, not a
  platform problem. Leading explanation: every Gmail screenshot today was
  `mail.google.com/mail/u/0`, so mail may have been sent to one address and searched in
  another all day. Alternatives: a filter or block rule from months of QA, or per-user
  spam learning. Not worth more time unless it recurs on a real client address.
- **Opens and clicks will stay empty until 442 lands**, and when it does they will
  populate for NEW sends only — historical rows stay blank forever. That will read as a
  bug unless 444 ships first.
- **433 delivery tracking is confirmed live in production** — Sent 4:34 → Delivered 4:36,
  correlating correctly through `ProviderMessageId`. First time it has worked since the
  provider swap.

## The method lesson, recorded because it cost a day

Every "Gmail drops our mail" conclusion rested on a single recipient mailbox, and one
mailbox cannot distinguish a sender-reputation problem from one poisoned inbox. The
controlled test that settled it — one send, three recipients, everything else identical —
was available from the first hour and was not run. **Before concluding that a provider is
filtering, send to at least two independent mailboxes at that provider in the same
message.** The corresponding trap on the other side is equally live: the owner's own agent
notifications go to a Yahoo address, so from inside the product email looked healthy the
whole time.

Related: `DOCS/TODO.md` items 431, 433, 439–444; `DOCS/DNS_ZONE_RUNBOOK.md`.
