# Audit reconciliation — 2026-08-17

## STATUS CHANGES — 2026-08-17 (later the same day)

Six local commits, **not yet deployed**. Suite 60 -> 110 tests, green against real MySQL.

| Finding | Was | Now | Commit |
|---|---|---|---|
| JOBS-4 (consent never written outside the preferences page) | open | **FIXED** | `f998d59`, `6d060e9` |
| A5-H6 (two dispatch runs can both mail a whole list) | open | **FIXED** | `d0ec9ea`, `01b9cff`, `1f6cedd` |
| A5-H7 / JOBS-3 (a send stuck on Sending forever, nobody told) | open | **FIXED** | same |
| A5-H13 (form submission answers permanently unerasable) | open | **FIXED** | `9c160d8` |
| A5-H11 / A5-H12 / A5-H14 (blob ownership + orphans) | open | **STILL OPEN — design rejected** | — |

### Three corrections to what this document said

1. **A5-H13 is a predicate-REACHABILITY bug, not a missing cleanup in `FormsController`.** Deleting
   the answers with the form is FORBIDDEN — `DOCS/17_FORMS.md` promises the agent they survive. The
   eraser had to re-anchor on the lead.
2. **The same answer text is already duplicated into `WebsiteLead.Message`,** which the eraser does
   reach. So the exposure was the structured per-field rows, not the only copy.
3. **Rows belonging to agents already erased are unattributable** — nothing left says whose they
   were. The code fix cannot reduce that count; it needs a one-time, counted manual cleanup.

### One finding this document did not contain

`RecurringInvoicesController.Delete` removed a schedule and left its line items behind. No FK, and
the navigation collection is not loaded on that path so EF's client-side cascade never fired either.
Same defect class as A5-H13, different feature area, never reported by any of the four audits. Fixed
in `9c160d8`.

### Why the blob family was not done

An adversarial pass over the proposed design found three ways it destroys or never cleans data. The
decisive one: **the orphan sweep would delete images that are live in already-delivered mail.** The
reference index sees database rows only, so a newsletter photo sitting in someone's inbox is
referenced by nothing it can see and looks unreferenced the moment its row goes. Two of the three
proposed new deletion sources would also have put agent-typed, unvalidated URLs into a delete path.

The safe subset — container registry, reference index, and keep-if-referenced at the six sites that
today delete unconditionally — is worth doing and can only ever keep MORE files than today. The
sweep must be report-only until a human has read one report. Detail in `DOCS/TODO.md`.


**Every finding from every audit, re-verified against the CURRENT code rather than against
the audit documents.** Commissioned because the owner asked, fairly, why defects he found
himself had been reported as fixed. A doc saying "Status: Fixed" was treated as a claim to
be tested, not as evidence. 183 findings checked; verdicts are FIXED / PARTIAL / NOT_FIXED /
UNCLEAR.

**This file supersedes the status markers in the four individual audit docs.** Those docs
remain the description of each finding; this one is the truth about its state. When you fix
something here, edit it HERE — the previous triage rotted precisely because its checkboxes
were never updated.

## Counts

```
TOTAL VERIFIED FINDINGS: 183 — FIXED 125 (68%), PARTIAL 13 (7%), NOT_FIXED 45 (25%). Genuinely open (PARTIAL + NOT_FIXED) = 58 raw entries, which collapse to 54 distinct defects after de-duplicating four pairs that two different audits each reported (ADMIN-10 = A5-M-REBUILDRES; ADMIN-2 = BILLING-9; A5-H7 = JOBS-3; M-8 = A2-H4).

PER AUDIT (items | fixed | partial | not fixed):
- Pre-July criticals (PayPal APPROVAL_PENDING, Hangfire dashboard): 2 | 2 | 0 | 0
- July 2026-07-24 audit (C-1, H-1..H-8, M-1..M-12, L-1..L-15): 35 | 33 | 2 | 0
- Dependency/CVE table (DEP-*): 9 | 7 | 1 | 1
- Second-opinion audit (SO-NEW / SO-M-NEW / SO-MIN): 19 | 17 | 2 | 0
- Audit 5 (A5-*): 33 | 15 | 1 | 17
- Regression pass (R-*): 16 | 12 | 1 | 3
- Audit 2 (A2-*): 10 | 5 | 2 | 3
- Billing audit (BILLING-1..12): 12 | 10 | 2 | 0
- Web audit (WEB-*): 10 | 6 | 0 | 4
- Admin audit (ADMIN-1..12): 12 | 4 | 1 | 7
- Jobs/email audit (JOBS-1..11): 11 | 1 | 1 | 9
- Data/schema audit (DATA-F1..F14): 14 | 13 | 0 | 1

PER SEVERITY, open only: CRITICAL 1 (partial) | HIGH 20 | MEDIUM/MODERATE 24 | LOW/MINOR/INFO 13.

Blunt read on the shape: the audits that were worked hardest (July, Billing, Data, Second-opinion) are 90-95% closed. The three most recent audits (A5, Admin, Jobs) are 45%, 33% and 18% closed — most of what is open was never worked, not "fixed and regressed".
```



## Was the July audit really fixed — including the Low findings?

Yes - with three things you should know before you accept that.

All 15 Low/Informational findings from the July audit (L-1 through L-15) were re-verified against the current code, and all 15 came back FIXED. Twelve of them were genuinely closed by real code changes: the leaked category name, the login-enumeration timing gap, the never-expiring portal invite token, the missing rate-limit rule on the client portal login, the /Billing prefix-match bypass, the unsanitized admin template HTML, the missing CSV size limit, the dead weak-password-hashing code, the broken error page, the stray backup file, the orphaned project file, and the shadow billing service. Several were fixed more thoroughly than the audit document describes - the portal token check is stricter than documented, and the /Billing fix works via a different mechanism than the doc claims but is equally correct.

The three caveats:

1. L-14 and L-15 were closed as "reviewed, no code change" and "accepted risk". The code is unchanged and the original findings still describe it accurately. For L-15 specifically, admin-authored text is still injected into every agent's page layout without encoding - the judgement that no agent, client or public visitor can reach it holds, but a malicious or compromised SuperAdmin could put working script into every agent's screen. That is an admin-to-agent escalation route that the disposition does not mention, and it is cheap to close.

2. L-12's fix is a database constraint created at startup - and the code deliberately continues booting if that creation fails. Whether the constraint actually exists in the production database cannot be determined from code. The application logic underneath was not changed, so a collision now surfaces as an error page instead of a duplicate row.

3. L-10 left inert build leftovers on disk that still name the deleted project. Cosmetic only.

So: the Low findings were real work, really done. They are not the part of this project that should worry you.

## The honest bottom line

For the July audit specifically, "everything was fixed" was very nearly true, and the exceptions matter. Of its 35 findings, 33 verify as genuinely fixed in the current code and 2 do not: M-8 (a promo code can still be redeemed one time past its cap - the code now detects the breach, logs an error, and grants the discount anyway) and M-9 (the overdue-invoice job still makes hundreds of database round trips per run using the exact inefficient pattern the finding named, with the batched replacement already written and sitting unused next to it). Neither was disclosed as incomplete. Beyond those two, roughly six findings marked FIXED closed the half of the problem the fix instruction named and left the other half of the finding's own stated impact standing without saying so - logo uploads are validated but still bypass the storage quota; per-item error isolation was added to the dispatch jobs but a send can still get stuck forever; the XSS sanitizer is wired at both ends but one editor view still renders raw stored HTML. And the dependency table shipped alongside the July audit contains one flatly false statement: Newtonsoft.Json is described as "gone entirely" and it is not - it is still resolved at the vulnerable version in three projects, and the reason nobody caught it is that the stated verification method only checked the two app projects, which is exactly where it happens to look clean. The pattern across all of it is not dishonesty and not sloppiness; it is a consistent habit of fixing the reported location, not the shape - and then verifying in a way that would not have found the miss. The wider truth is one the July claim never asserted but you may have heard it as: the July audit covered 35 items, later audits found 148 more, and 45 of those are untouched today. "Everything on the July list was fixed" was close to true. "Everything was fixed" was not, and is not.

## OPEN — 54 distinct defects, worst first

### [CRITICAL (partial)] JOBS-1

Drip campaigns: a client who opted out of your email can still be ENROLLED into a campaign. No mail actually reaches them any more (the job now cancels the enrollment at the first due send), so the legal exposure is largely closed, but the enrollment screen will still show opted-out people as enrolled. Separately, a spam complaint against a drip email is not recorded at all.

### [HIGH] A5-H6

All four email dispatchers (newsletter, e-card, e-letter, poll) claim a send by reading its status and then writing it, not atomically. If two runs overlap - a slow send plus the next hourly tick, or a deploy restarting mid-run - both can see the same send as 'Scheduled' and the newsletter dispatcher will build a second complete recipient list and mail it. That is the mass-duplicate-email scenario.

### [HIGH] JOBS-4

Spam complaints and unsubscribes only suppress the recipient for newsletters. E-cards, e-letters, polls and Did-You-Know emails ignore them entirely - nothing outside the preferences page ever writes the opt-out date. Someone who hits 'this is spam' keeps receiving your other email types. This is the CASL/CAN-SPAM item, and it is fully open.

### [FIXED 2026-08-18, pending production buyer pass] WEB-H-1

If a prospect starts signup or an upgrade on an agent's own domain (someagent.247advisers.com or a custom domain) rather than the canonical app URL, PayPal sends them back to the canonical host where their login cookie does not exist. They get bounced to the login page and the payment capture never runs - money moves at PayPal, the subscription does not activate in IPRO.

FIXED 2026-08-18 on branch fix/audit-high-five: PortalUrlHelper is now genuinely host-aware (it was a 2-line canonical pass-through). PayPal return/cancel URLs are built for the session's host via an allowlisted GetSessionBaseUrlAsync (canonical + platform domains + *.247advisers.com + bound custom domains from AgentDomains, NOT gated on SslStatus; unknown hosts fall back to canonical so a forged Host header never reaches PayPal). Both call sites fixed: BillingController.Subscribe/ResumePayment AND AccountController's hand-rolled duplicate on the signup path. The /Billing/PayPalReturn and /Billing/Cancel literals now exist only in PortalUrlHelper, enforced by a source-walking test. GoogleCalendarController keeps its canonical bounce (Google has a pre-registered redirect-URI allowlist; PayPal does not) but now shares the one host list. 21 tests in CheckoutHostPreservationTests.

Two facts the original entry omitted, recorded so nobody relies on them: BILLING.SUBSCRIPTION.ACTIVATED webhook handling is a partial backstop (activates the row if delivered), and the login ReturnUrl replay is a fragile second one (lost on MustChangePassword, TempData lost). Neither is the fix.

REMAINING: one production sandbox buyer pass from an agent host (signup + upgrade + cancel legs) after deploy — webhook-dependent activation is only verifiable in production, and production PayPal is sandbox mode so the pass is safe.

### [HIGH] A5-H13

Deleting a website form deletes the form but not the submissions or the answers visitors typed into it. Because the eraser finds those answers by looking up the parent form, once the form is gone that visitor personal data is orphaned in the database and unreachable by the erase tool. A deletion request cannot actually be honoured for this data today.

### [HIGH] A5-H12

Erasing one agent can delete image files that OTHER agents are still using. The 'is this shared?' check only looks at three starter-content tables and never checks other agents' article images. Their pages and newsletters go to broken images and there is no undo.

### [HIGH] A5-H14

Replacing or deleting an article's image deletes the underlying file without checking whether a newsletter already copied that image. Newsletters already sitting in clients' inboxes lose their picture.

### [HIGH (same defect, two audits)] A5-H7 / JOBS-3

A send that is interrupted mid-flight - crash, deploy, restart, or just a failure on the final save - is stuck in the 'Sending' state permanently. Nothing anywhere in the system looks for stuck sends, and the jobs only pick up sends marked 'Scheduled'. It will never go out and nobody is told. Per-recipient results are also all held in memory until one save at the end, so an interruption loses the record of who already received it.

### [MOSTLY CLOSED 2026-08-17 `ee497e8`] WEB-H-2

Anyone on the internet, not logged in, can POST promo codes at /Account/ValidatePromoCode and the reply tells them the exact discount terms. There is no rate limit on that endpoint specifically, so codes can be guessed in bulk. Flagged as a top-priority item in an earlier triage. FIXED 2026-08-17: antiforgery now required (invisible to a real visitor) plus a 5m/5 IP rate limit on the bare and /portal paths, so bulk guessing is impractical. Deliberately NOT done, owner decision 2026-08-17: the discount terms are still shown for a code that is already known-valid, because that confirmation is worth more to genuine customers than the secrecy is worth against a non-threat. Residual exposure accepted.

### [CLOSED 2026-08-17 `ee497e8`] DEP-Newtonsoft.Json

A vulnerable library version (Newtonsoft.Json 11.0.1) is still resolved in three internal projects. The earlier 'gone entirely' claim was wrong about the cause, and the check that produced it only looked at the two app projects, which hid it. In practice what ships to production is the safe 13.0.2, but only by luck - nothing pins it, and any change to two unrelated packages silently drops both apps back to the vulnerable version. Any CI security scan reports HIGH today. One line in one file fixes it.

### [HIGH (partial)] ADMIN-2 / BILLING-9

Editing a package price in Admin does not update the corresponding PayPal plan. PayPal keeps charging the old frozen price while the invoice IPRO issues shows the new one. A warning banner was added so an admin can see the divergence, but nothing blocks checkout or reconciles the invoice - if the admin ignores the banner, customers get invoices that do not match what was charged.

### [MEDIUM] ADMIN-9

Related to the above: re-running the PayPal plan sync after zeroing a price wipes the live plan ID for that billing period, and if the second plan creation fails PayPal is left with an orphaned plan the system has no record of.

### [HIGH] A5-H11

Deleting a Gallery block or a website page leaves its uploaded image files in blob storage forever, invisible to the cleanup tool and not credited back against the agent's storage quota. Silent, permanent storage cost growth.

### [MODERATE, and understated] DEP-AngleSharp

The HTML-parsing library underneath your XSS sanitizer has a known sanitizer-bypass bug. This was deliberately deferred because no compatible upgrade existed - that is no longer true. Newer HtmlSanitizer releases (up to 9.2.995) use a patched version. Because this is the exact library implementing the stored-XSS fix, leaving it partially weakens a HIGH fix you have already paid for.

### [MEDIUM] ADMIN-7

An admin's role and active/inactive status live only in their 4-hour login cookie. Demoting or deactivating an admin has no effect until that cookie expires - up to four hours of continued full access after you revoke it.

### [MEDIUM (same defect, two audits)] ADMIN-10 / A5-M-REBUILDRES

The 'Rebuild Resources' button hard-deletes an agent's Resources pages and all their customised content blocks, and ANY admin (including a support-role admin) can press it. The confirmation text does not warn that customised content is destroyed - it says articles are kept, which is only half the story.

### [MEDIUM] A5-M-STARTER

Support-level admins can write the starter-content libraries (starter articles, starter blocks, starter forms) even though the comparable template and e-card libraries are SuperAdmin-only. Content written there propagates into every future agent's site.

### [MEDIUM] A5-M-SSRF

The custom-domain checker will resolve and fetch whatever hostname an agent types, with no filter for internal or loopback addresses, and reports back whether it was reachable. That turns your server into a probe an agent can point at your internal network.

### [MEDIUM] A5-M-SANITIZER

The HTML sanitizer runs on stock defaults, which permit <form>, <input>, <button> and inline style. An agent's article or drip email can therefore contain a working form or a visually convincing overlay. Not script execution, but enough for a credential-harvest lookalike.

### [MODERATE (partial)] SO-M-NEW-6

Live access tokens are still written to Application Insights logs. The scrubber was added but only inspects the query string, and the two links that carry tokens (client invoice and testimonial links) put the token in the URL path instead. The code comment claims those are covered; they are not.

### [MEDIUM] A5-M-ERASEATOMIC

Agent erasure is not a transaction and the account is not locked out first. If it fails partway, the agent still has a working login to an account whose files have already been deleted.

### [MEDIUM] ADMIN-6

Deleting an agent never unbinds their custom domain from Azure. The hostname binding and its managed SSL certificate survive the deletion, attached to your Azure resources, with no owner.

### [MEDIUM] A5-M-RESEND

Re-sending a client invoice that is already PAID silently flips it back to unpaid and puts it back into the overdue-reminder queue. Your client gets dunned for a bill they already settled.

### [MEDIUM] JOBS-7

When a drip campaign step fails to send, the failure is recorded on a sub-record and then the enrollment advances anyway and the error message is blanked. The client silently misses that step and you cannot see it happened.

### [MEDIUM] JOBS-8

A single transient error (one timeout) marks a drip enrollment 'Failed' permanently. There is no retry and no screen that lists failed enrollments - that client's campaign just stops forever.

### [MEDIUM] JOBS-5

The Did-You-Know mailer treats any send failure as final and retires the email, including cases where SendGrid never actually answered (rate limit, 5xx, socket timeout). Those emails are dropped and never retried.

### [MEDIUM] JOBS-6

The SendGrid event webhook processes a batch of events with no per-event error handling. One malformed event makes the whole request fail, so every later event in that batch is lost and SendGrid retries into the same poison event - open/click/bounce data silently stops updating.

### [MEDIUM] JOBS-9

Poll sends use their own stricter consent rule than the rest of the system, so clients who never opted into the newsletter are dropped from poll audiences before the count is taken. Your poll reports show them as neither sent nor suppressed - they just vanish.

### [MEDIUM] JOBS-10

Testimonial request emails skip the consent check entirely and carry no unsubscribe header. An opted-out client can still receive one. A consent category for it was added to the code and is never actually used.

### [LOW] JOBS-11

The SendGrid webhook checks the signature but never checks the timestamp, so a captured genuine payload can be replayed against you indefinitely.

### [MODERATE] A5-M-JOBISOLATION

The hourly subscription-billing job still has no per-agent error isolation in its main loop. One agent's PayPal error aborts that hour's run for every remaining agent - scheduled plan changes and billing-issue notices silently do not happen.

### [HIGH (documentation was false)] A2-H6

The audit states that the Web and Admin deploys now share one concurrency group and fully serialize. They do not - they use two different groups, so one push to main still starts both deploys at once and both run schema changes against the same database simultaneously. The specific crash this caused is now caught and handled, so the risk is reduced, but the written claim is untrue.

### [HIGH (accepted by prior decision)] A2-H5

If a uniqueness constraint fails to be created at startup (because the data already has duplicates), the app logs one line to the error stream and keeps serving without it. You would not notice. This was an accepted decision, not an oversight - but it is why nobody can tell you from code alone which constraints are actually live in production.

### [HIGH (scheduled initiative)] A2-H8

Two competing systems still define your database schema: EF migrations and hand-written startup repair code. Known, documented, and scheduled - but until it is done, schema drift between what the code expects and what the database has stays possible.

### [HIGH (partial, open cost decision)] R-H9

Each deploy workflow can no longer overlap itself, but there is still no staging slot - deploys go straight to the live site. This is a cost decision you have not made yet, not a missed fix.

### [HIGH (deferred by decision)] R-H4

Client invoice numbers are still generated by reading the highest existing number and adding one, with no lock. Two invoices created at the same instant can collide. A retry was added elsewhere so it now self-heals rather than erroring, but the race itself is still in the code.

### [MEDIUM (partial, same defect)] M-8 / A2-H4

A promo code with a redemption cap can still be redeemed one time past its cap. The system now detects the breach and logs an error - and then grants the discount anyway rather than refusing. The window is actually wider than when first reported, because the cap is checked at subscribe time but claimed minutes later at activation. The equivalent trial-code path WAS fixed properly; this one was not given the same treatment.

### [MODERATE (partial)] A5-M-QUOTA

Storage quota now counts documents, website media, portal documents and gallery images - but still not article images, agent photos or logos. And if an agent's package has no storage limit value set, the check is skipped entirely rather than defaulting to deny.

### [MODERATE] A5-M-DOCUSAGE

The Documents page shows a storage figure that counts only documents, while uploads are actually blocked against a larger total. Agents will see 'plenty of space left' and get rejected. The gap is now bigger than when it was first reported.

### [MODERATE] A5-M-PARENT

If an agent sets an invalid parent page (nonexistent, circular, or too deep), the system silently makes it a top-level page and reports 'Navigation settings saved'. The agent is told it worked; it did something else.

### [MODERATE] A5-M-CACHE404

Media 'not found' responses are stamped with a one-year cache header. A browser or CDN that hits a missing image once will keep showing it as missing for a year even after you upload the file.

### [MODERATE] A5-M-EMPTYHOST

Public form-submission, robots.txt and sitemap endpoints do not guard against an empty Host header the way page rendering does - the same request that returns 'not found' for a page is handled for a submission.

### [MEDIUM (partial)] M-9

Most of the database-query inefficiency was fixed, but the overdue-invoice reminder job still queries per invoice inside its loop - roughly 600-1600 database round trips per run instead of a handful, with the batched alternative already written and sitting unused.

### [MEDIUM] ADMIN-8

The revenue chart buckets by UTC month while the ledger rows underneath it print agent-local dates. Two clocks on one screen - month-boundary invoices will appear in a different month than the row below says.

### [LOW (latent)] WEB-L-1

Website editing actions have no package-entitlement check on any of the write paths - only three read screens are gated - and Duplicate copies block types that Add would refuse. Harmless today because Instant Website is included in every package; it becomes a real hole the moment you gate it.

### [LOW] WEB-L-2

The public lead-magnet download fetches a document by ID with no check that it belongs to the website serving it. A signed token is the only thing standing between a guessed ID and another agent's file.

### [LOW (partial)] BILLING-12

Three loose ends on the one-time-order payment path: a PENDING capture reads as paid, and any logged-in agent can write an arbitrary string into their own promo-code field. Mostly defused because that payment path is now dead code, but the code is still there.

### [LOW] ADMIN-11

The Admin confirmation prompt still says it will reset the agent's password 'to their last name'. It actually generates a random one. The dangerous behaviour was fixed; the misleading text was not.

### [LOW] ADMIN-12

Tax-rate edits are audit-logged as 'Bulk-updated N rates' with no record of what changed from what. If a rate is wrong you cannot reconstruct who set it or what it was.

### [LOW (kept deliberately)] R-L1

Resuming a stalled payment voids the old attempt before the replacement exists. Documented as an intentional choice, not an oversight.

### [LOW (kept deliberately)] R-L5

Password-reset emails are sent fire-and-forget so that response timing cannot reveal whether an account exists. Intentional; failures are at least logged now.

### [LOW (knowingly deferred)] DATA-F2

The EF model snapshot covers 28 of 85 tables. Only bites if someone scaffolds a migration with the standard tooling, which this project does not do.

### [LOW (partial)] A2-L2

A .NET test project with real coverage now exists, but the specific promise - browser tests for the /portal URLs, rate limits and access gates - never landed. The browser suite is still two smoke tests.

### [MINOR (partial)] SO-MIN-7

The 'build a URL' logic was consolidated into one helper for most callers, but two files still carry their own copy. The same 'fixed the reported spots, missed the siblings' pattern that shows up elsewhere.

## UNCLEAR — cannot be settled by reading code; each names the check that would settle it

- Whether the unique index on client invoice numbers (L-12) actually exists in the production database. The startup code silently skips creating it if duplicate data is present. SETTLED BY: running SHOW INDEX FROM ClientInvoices against ipro-mysql-prod.
- Whether ANY uniqueness constraint failed to be created at startup (A2-H5, R-H8, DATA-F9, DATA-F10). Failures print one line to the error stream and boot continues. SETTLED BY: searching the App Service log stream for both apps for '[SCHEMA]' and 'NOT CREATED'.
- ~~Whether SendGrid event tracking is working at all (H-2).~~ **RESOLVED 2026-08-17 — WORKING.** Owner ran a real send: newsletter 11 to bmot1966@gmail.com shows Delivered / Opened / Clicked timestamps at recipient level, Open Rate 100%%, Click Rate 100%%. Those statuses can only come from a signature-verified SendGrid event webhook callback, so the Azure key is correct, SendGrid is pointed at /Newsletter/SendGridEvents, and events are being attributed to the right recipient. This also verifies the H-2 signature-verification fix end to end with a real key, which no code review could establish.
- Whether the upgrade-to-a-higher-package path actually creates and charges a real recurring subscription end to end (A5-C2, BILLING-2). The code reads correctly but the change was never exercised against PayPal. SETTLED BY: one sandbox upgrade run, watching that a subscription (not a one-time order) appears at PayPal with the right next-billing date.
- Whether any live package price currently diverges from its PayPal plan price (ADMIN-2 / BILLING-9). The banner will tell you, but only if someone looks. SETTLED BY: opening each package in Admin > Packages > Edit and checking for the red divergence banner.
- Whether image files have already been destroyed by past agent deletions or image replacements (A5-H12, A5-H14, A5-H11). Code review shows the bug exists; it cannot show what damage has already been done. SETTLED BY: reconciling blob storage contents against the image URLs referenced in the database.
- Whether a website footer feature is currently broken in visitors' browsers (M-6 residual). One script block added after the CSP change has no nonce, so browsers silently refuse to run it. SETTLED BY: loading a live agent site with the footer and checking the browser console for a Content-Security-Policy violation - then deciding whether the missing behaviour matters.
- Whether the deployed applications actually ship the safe version of the Newtonsoft.Json library (DEP-Newtonsoft.Json). The build graph says yes; nothing pins it. SETTLED BY: inspecting the published output on ipro-prod-web and ipro-prod-admin for the Newtonsoft.Json DLL version - though the correct action (add one pin line to Directory.Packages.props) is the same either way.
