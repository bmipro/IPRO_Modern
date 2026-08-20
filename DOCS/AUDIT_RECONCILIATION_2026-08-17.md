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

### [FIXED 2026-08-18] JOBS-1 (was CRITICAL partial)

Drip campaigns: a client who opted out of your email can still be ENROLLED into a campaign. No mail actually reaches them any more (the job now cancels the enrollment at the first due send), so the legal exposure is largely closed, but the enrollment screen will still show opted-out people as enrolled. Separately, a spam complaint against a drip email is not recorded at all.

FIXED 2026-08-18 on branch fix/audit-high-five, in three parts matching the finding's three halves:

1. Enrollment gate: CampaignsController.EnrollClientsAsync now filters out suppressed clients (EmailConsentService.IsSuppressed, EmailChannel.DripCampaign — rule 7's single decision point) before enrolling, on BOTH paths (category bulk-enroll and single-client). The agent is told how many were skipped and why, and the generic "already active" fallback messages no longer overwrite that warning.
2. Truth sweep: EmailConsentService.CancelSuppressedDripEnrollmentsAsync cancels Active enrollments whose client is already suppressed — the legacy rows created BEFORE LB-2 taught SuppressAllAsync to cancel enrollments at opt-out time. DripCampaignJob runs the sweep at the top of every hourly tick, so a stale "enrolled" row now survives at most an hour instead of until its next due send (up to the step's full delay, weeks). SQL narrows on EmailOptOutAt, IsSuppressed makes the decision.
3. The spam-complaint half was already closed by LB-2 (f998d59/6d060e9): RecordDripStepEventAsync handles spamreport/unsubscribe/group_unsubscribe on drip sends → SuppressAllAsync, which records the opt-out AND cancels the client's active enrollments. Verified in code this session, not assumed.

The per-due-send IsSuppressed check in DripCampaignJob stays as the last line of defence. 5 tests in DripEnrollmentConsentTests (gate refuses + agent told, clean client still enrolls, new opt-out cancels immediately, sweep cancels legacy rows and spares subscribed ones, sweep no-op).

### [FIXED 2026-08-17 `1fLB`] A5-H6

**STATUS CORRECTED 2026-08-20:** this heading said HIGH/open until today even though the STATUS CHANGES table at the top of this file recorded it FIXED on 2026-08-17 (`d0ec9ea`, `01b9cff`, `1f6cedd` — the four dispatchers adopted the atomic claim primitive). Commits verified present. Original finding below.

All four email dispatchers (newsletter, e-card, e-letter, poll) claim a send by reading its status and then writing it, not atomically. If two runs overlap - a slow send plus the next hourly tick, or a deploy restarting mid-run - both can see the same send as 'Scheduled' and the newsletter dispatcher will build a second complete recipient list and mail it. That is the mass-duplicate-email scenario.

### [FIXED 2026-08-17] JOBS-4

**STATUS CORRECTED 2026-08-20:** fixed by `f998d59` + `6d060e9` (LB-2 stage 1/1b — every unsubscribe path writes consent; EmailChannel now covers DidYouKnow, DripCampaign and the rest). The heading below was never updated. Commits verified present. Original finding below.

Spam complaints and unsubscribes only suppress the recipient for newsletters. E-cards, e-letters, polls and Did-You-Know emails ignore them entirely - nothing outside the preferences page ever writes the opt-out date. Someone who hits 'this is spam' keeps receiving your other email types. This is the CASL/CAN-SPAM item, and it is fully open.

### [FIXED 2026-08-18, pending production buyer pass] WEB-H-1

If a prospect starts signup or an upgrade on an agent's own domain (someagent.247advisers.com or a custom domain) rather than the canonical app URL, PayPal sends them back to the canonical host where their login cookie does not exist. They get bounced to the login page and the payment capture never runs - money moves at PayPal, the subscription does not activate in IPRO.

FIXED 2026-08-18 on branch fix/audit-high-five: PortalUrlHelper is now genuinely host-aware (it was a 2-line canonical pass-through). PayPal return/cancel URLs are built for the session's host via an allowlisted GetSessionBaseUrlAsync (canonical + platform domains + *.247advisers.com + bound custom domains from AgentDomains, NOT gated on SslStatus; unknown hosts fall back to canonical so a forged Host header never reaches PayPal). Both call sites fixed: BillingController.Subscribe/ResumePayment AND AccountController's hand-rolled duplicate on the signup path. The /Billing/PayPalReturn and /Billing/Cancel literals now exist only in PortalUrlHelper, enforced by a source-walking test. GoogleCalendarController keeps its canonical bounce (Google has a pre-registered redirect-URI allowlist; PayPal does not) but now shares the one host list. 21 tests in CheckoutHostPreservationTests.

Two facts the original entry omitted, recorded so nobody relies on them: BILLING.SUBSCRIPTION.ACTIVATED webhook handling is a partial backstop (activates the row if delivered), and the login ReturnUrl replay is a fragile second one (lost on MustChangePassword, TempData lost). Neither is the fix.

RUN 2026-08-20 — SIGNUP LEG PASSED, with a caveat worth stating plainly. Agent BobyMot #35 signed up on QA Silver (Daily); subscription I-VG1A3CKSK6DX went Active, invoice IPRO-2026-000010 ($214.70) was minted, marked Paid and the receipt emailed, all inside the same minute as registration. That means the return-and-capture leg RAN — the precise thing WEB-H-1 broke, whose failure mode is a bounce to a login page where capture never runs. Money reconciles exactly: two PayPal charges, $169.50 (setup + HST) and $45.20 (first cycle + HST), totalling the invoice.

CAVEAT: it is NOT proven that the run started on an agent host. The owner recalls starting on bahmanmotamed.247advisers.com and returning there but is not certain, and it cannot be reconstructed after the fact — Azure retains no HTTP logs for this app. The portal screenshot showing bobymot.247advisers.com is NOT evidence either way: PayPalReturn ends in a relative redirect (host-preserving), so that screenshot is a separate later login to the new agent's own portal, corroborated by "Last Login: Never" on the admin page at 12:51. And BILLING.SUBSCRIPTION.ACTIVATED remains a documented partial backstop that can activate the row on its own, so activation alone does not isolate the return leg. Treat the signup leg as strong evidence, not proof.

REMAINING: the upgrade and cancel legs, run from bobymot.247advisers.com — the upgrade goes through the same BuildBillingActionUrlAsync producer and, started from an agent host, is the definitive WEB-H-1 proof the signup leg could not supply. It is also required QA work (day 3 of items 367-369), so it costs nothing extra. Original remaining note: one production sandbox buyer pass from an agent host (signup + upgrade + cancel legs) after deploy — webhook-dependent activation is only verifiable in production, and production PayPal is sandbox mode so the pass is safe.

### [FIXED 2026-08-17] A5-H13

**STATUS CORRECTED 2026-08-20:** fixed by `9c160d8` (LB-4 — the eraser re-anchors on the lead, so answers stay reachable after the form is deleted). Commit verified present. Original finding below.

Deleting a website form deletes the form but not the submissions or the answers visitors typed into it. Because the eraser finds those answers by looking up the parent form, once the form is gone that visitor personal data is orphaned in the database and unreachable by the erase tool. A deletion request cannot actually be honoured for this data today.

### [FIXED 2026-08-18] A5-H12

Erasing one agent can delete image files that OTHER agents are still using. The 'is this shared?' check only looks at three starter-content tables and never checks other agents' article images. Their pages and newsletters go to broken images and there is no undo.

FIXED 2026-08-18 (branch fix/audit-high-five): after the shred, every candidate blob is re-checked against BlobReferences — a live query over EVERY URL-bearing column and stored HTML body (15 table/column pairs, not three). Any file another agent still points at moves to the kept list. Test: Erasing_one_agent_keeps_a_file_another_agent_still_uses, plus the inverse (a file only the erased agent used still goes).

### [FIXED 2026-08-18] A5-H14

Replacing or deleting an article's image deletes the underlying file without checking whether a newsletter already copied that image. Newsletters already sitting in clients' inboxes lose their picture.

FIXED 2026-08-18: all six image-delete sites (article image, agent photo replace + remove, website logo replace, media-asset delete, gallery-image delete) now ask BlobReferences first and KEEP the file when anything still references it — including newsletter/drip/e-letter HTML and block SettingsJson, which the old two-table checks never saw. Two of the six also deleted the file BEFORE saving the row (a failed save left rows pointing at destroyed files); both reordered to row-first. The property the tests pin: the guards can only ever keep MORE files than the unconditional deletes they replaced.

### [FIXED 2026-08-17] A5-H7 / JOBS-3

**STATUS CORRECTED 2026-08-20:** fixed by `1f6cedd` alongside A5-H6 (stuck-send detection came with the claim primitive). Commit verified present. Original finding below.

A send that is interrupted mid-flight - crash, deploy, restart, or just a failure on the final save - is stuck in the 'Sending' state permanently. Nothing anywhere in the system looks for stuck sends, and the jobs only pick up sends marked 'Scheduled'. It will never go out and nobody is told. Per-recipient results are also all held in memory until one save at the end, so an interruption loses the record of who already received it.

### [MOSTLY CLOSED 2026-08-17 `ee497e8`] WEB-H-2

Anyone on the internet, not logged in, can POST promo codes at /Account/ValidatePromoCode and the reply tells them the exact discount terms. There is no rate limit on that endpoint specifically, so codes can be guessed in bulk. Flagged as a top-priority item in an earlier triage. FIXED 2026-08-17: antiforgery now required (invisible to a real visitor) plus a 5m/5 IP rate limit on the bare and /portal paths, so bulk guessing is impractical. Deliberately NOT done, owner decision 2026-08-17: the discount terms are still shown for a code that is already known-valid, because that confirmation is worth more to genuine customers than the secrecy is worth against a non-threat. Residual exposure accepted.

### [CLOSED 2026-08-17 `ee497e8`] DEP-Newtonsoft.Json

A vulnerable library version (Newtonsoft.Json 11.0.1) is still resolved in three internal projects. The earlier 'gone entirely' claim was wrong about the cause, and the check that produced it only looked at the two app projects, which hid it. In practice what ships to production is the safe 13.0.2, but only by luck - nothing pins it, and any change to two unrelated packages silently drops both apps back to the vulnerable version. Any CI security scan reports HIGH today. One line in one file fixes it.

### [FIXED 2026-08-18] ADMIN-2 / BILLING-9

Editing a package price in Admin does not update the corresponding PayPal plan. PayPal keeps charging the old frozen price while the invoice IPRO issues shows the new one. A warning banner was added so an admin can see the divergence, but nothing blocks checkout or reconciles the invoice - if the admin ignores the banner, customers get invoices that do not match what was charged.

FIXED 2026-08-18 on branch fix/audit-high-five: checkout now FAILS CLOSED on divergence. CreateSubscriptionAsync refuses (HasDivergentPlanPrice, next to IsPeriodOfferable) whenever the package's editable price differs from the price snapshot the sync stamped for that period's plan -- covering both subscribe and upgrade, which share the method. A null snapshot (plan synced before the 422b columns existed) is "divergence unknown": allowed, banner keeps nagging -- blocking there would brick every legacy package on no evidence. ResumePayment deliberately not guarded: it resumes an already-minted invoice at the already-agreed amount. 4 tests in PlanPriceDivergenceGuardTests.

### [FIXED 2026-08-18] ADMIN-9

Related to the above: re-running the PayPal plan sync after zeroing a price wipes the live plan ID for that billing period, and if the second plan creation fails PayPal is left with an orphaned plan the system has no record of.

FIXED 2026-08-18, both halves. SyncPayPalPlansAsync now persists each plan id THE MOMENT it is created (monthly saved before annual is attempted), so a failure partway leaves the created plan recorded instead of discarded-but-live-at-PayPal. And a replaced or zeroed plan id is no longer silently overwritten: the old plan is deactivated at PayPal best-effort (existing subscribers keep billing; only NEW subscriptions are blocked) and the old->new transition is written to OperateLogs (Action=PayPalPlanReplaced) either way, so the worst case is a logged, findable orphan rather than an untraceable one.

### [MOSTLY CLOSED 2026-08-18 — report-only by design] A5-H11

Deleting a Gallery block or a website page leaves its uploaded image files in blob storage forever, invisible to the cleanup tool and not credited back against the agent's storage quota. Silent, permanent storage cost growth.

MOSTLY CLOSED 2026-08-18: orphans are no longer invisible — Admin -> Blob Storage walks every registered container (BlobReferences.Containers is the registry) and lists each file no database row references, with a banner stating why nothing on that page deletes: "unreferenced in the database" is not proof the file is absent from already-delivered mail, which is exactly how the original sweep design would have destroyed live images. Deletion stays a manual, per-file human decision. Residual accepted: storage is not auto-reclaimed (the whole account holds ~17 MB; the trade is deliberate) and quota is not credited back.

### [FIXED 2026-08-20] DEP-AngleSharp

FIXED on branch fix/audit-medium-seven: HtmlSanitizer 9.0.967 -> 9.2.995, which depends on AngleSharp 1.7.1 (the old release exact-pinned the vulnerable [0.17.1]). The "no compatible upgrade exists" deferral was verified obsolete against NuGet before bumping. Four sanitizer regression tests guard the parser swap (script/onerror still die, formatting survives). Original finding below.

The HTML-parsing library underneath your XSS sanitizer has a known sanitizer-bypass bug. This was deliberately deferred because no compatible upgrade existed - that is no longer true. Newer HtmlSanitizer releases (up to 9.2.995) use a patched version. Because this is the exact library implementing the stored-XSS fix, leaving it partially weakens a HIGH fix you have already paid for.

### [FIXED 2026-08-20] ADMIN-7

FIXED on branch fix/audit-medium-seven: AdminCookieRevalidator (CookieAuthenticationEvents.ValidatePrincipal) re-checks the database on EVERY authenticated request -- account missing, deactivated, or role differing from the cookie's claim (either direction) rejects the principal and signs it out. One PK lookup per request for a handful of admins. VERIFIED LIVE locally: logged in as superadmin, demoted the row to Support in the DB, next request bounced to the login page; restored, logged back in fine. 3 tests on the decision matrix. Original finding below.

An admin's role and active/inactive status live only in their 4-hour login cookie. Demoting or deactivating an admin has no effect until that cookie expires - up to four hours of continued full access after you revoke it.

### [FIXED 2026-08-20] ADMIN-10 / A5-M-REBUILDRES

FIXED on branch fix/audit-medium-seven: RebuildResources is now [Authorize(Policy = "SuperAdmin")], the confirm text says plainly that customised content blocks are DELETED and unrecoverable (it previously only mentioned what was kept), and for support-role admins the button renders disabled with a tooltip rather than hidden (the owner's standing rule: disable, don't hide). Reflection test pins the policy attribute. Original finding below.

The 'Rebuild Resources' button hard-deletes an agent's Resources pages and all their customised content blocks, and ANY admin (including a support-role admin) can press it. The confirmation text does not warn that customised content is destroyed - it says articles are kept, which is only half the story.

### [FIXED 2026-08-20] A5-M-STARTER

FIXED on fix/medium-sweep: all three starter-library controllers are SuperAdmin-only and their nav links joined the SuperAdmin block, matching Templates. Reflection test pins the policy. Original finding below.

Support-level admins can write the starter-content libraries (starter articles, starter blocks, starter forms) even though the comparable template and e-card libraries are SuperAdmin-only. Content written there propagates into every future agent's site.

### [FIXED 2026-08-20] A5-M-SSRF

FIXED on branch fix/audit-medium-seven: new PublicHostGuard screens every hostname the domain checker touches -- IP-literal "domains" are refused outright, and any name resolving to loopback / RFC1918 / link-local (incl. 169.254.169.254) / CGNAT / ULA / unspecified space is refused AFTER resolution, so an internal name pointing at internal space is caught the same as a raw IP. Applied at all three fetch points in DomainCheckService (www check, Azure-binding probe, root-domain check), and the binding probe no longer follows redirects, closing the 302-to-internal variant. 15 address-matrix tests. Original finding below.

The custom-domain checker will resolve and fetch whatever hostname an agent types, with no filter for internal or loopback addresses, and reports back whether it was reachable. That turns your server into a probe an agent can point at your internal network.

### [FIXED 2026-08-20] A5-M-SANITIZER

FIXED on branch fix/audit-medium-seven: HtmlContentSanitizer no longer runs stock defaults. Removed: every form control tag (form/input/button/select/textarea/option/label/fieldset/...) and the overlay CSS properties (position, z-index, inset offsets, pointer-events). Kept deliberately: the rest of the inline-style whitelist, because newsletters and articles are built from inline formatting and stripping style wholesale would visibly break existing content. Tests pin both directions (phishing vectors die, newsletter table/color/padding survive). Original finding below.

The HTML sanitizer runs on stock defaults, which permit <form>, <input>, <button> and inline style. An agent's article or drip email can therefore contain a working form or a visually convincing overlay. Not script execution, but enough for a credential-harvest lookalike.

### [FIXED 2026-08-20] SO-M-NEW-6

FIXED on branch fix/audit-medium-seven: the telemetry scrubber now also redacts PATH-carried tokens -- /invoice/{token} and /testimonial/{token}, the two links the finding named -- in request.Url, request.Name and Operation.Name, alongside the existing query-string scrub. The Admin app's copy carries the identical logic so the two files cannot drift. 3 tests. Original finding below.

Live access tokens are still written to Application Insights logs. The scrubber was added but only inspects the query string, and the two links that carry tokens (client invoice and testimonial links) put the token in the URL path instead. The code comment claims those are covered; they are not.

### [FIXED 2026-08-20] A5-M-ERASEATOMIC

FIXED on fix/medium-sweep: the account is deactivated FIRST (AuthenticateAsync refuses inactive agents), then the whole row shred runs in one transaction -- a partial failure now rolls back to locked-out-but-intact instead of a half-erased account with a working login. Preview still touches nothing (tested). Original finding below.

Agent erasure is not a transaction and the account is not locked out first. If it fails partway, the agent still has a working login to an account whose files have already been deleted.

### [FIXED 2026-08-20] ADMIN-6

FIXED on fix/medium-sweep: agent deletion unbinds each custom domain (www + apex) from Azure via RemoveDomainAsync BEFORE the rows are shredded; a failed unbind is logged loudly and never blocks the deletion. Original finding below.

Deleting an agent never unbinds their custom domain from Azure. The hostname binding and its managed SSL certificate survive the deletion, attached to your Azure resources, with no owner.

### [FIXED 2026-08-20] A5-M-RESEND

FIXED on fix/medium-sweep: re-sending a PAID client invoice keeps it Paid (the client just gets a copy); only Draft/Sent invoices flip to Sent. Pinned by a real-controller test. Original finding below.

Re-sending a client invoice that is already PAID silently flips it back to unpaid and puts it back into the overdue-reminder queue. Your client gets dunned for a bill they already settled.

### [FIXED 2026-08-20] JOBS-7

FIXED on fix/medium-sweep: DispatchDripStepAsync now returns the real send outcome, and the job refuses to advance past a failed step -- the error stays on the enrollment instead of being blanked. Original finding below.

When a drip campaign step fails to send, the failure is recorded on a sub-record and then the enrollment advances anyway and the error message is blanked. The client silently misses that step and you cannot see it happened.

### [FIXED 2026-08-20] JOBS-8

FIXED on fix/medium-sweep: transient failures (timeout/429/5xx/exceptions) retry on later ticks with a SendAttempts counter, failing honestly at 5 with a 'gave up' summary; answered rejections fail immediately. State machine unit-tested across the whole matrix. Original finding below.

A single transient error (one timeout) marks a drip enrollment 'Failed' permanently. There is no retry and no screen that lists failed enrollments - that client's campaign just stops forever.

### [FIXED 2026-08-20] JOBS-5

FIXED on fix/medium-sweep: EmailSendResult now distinguishes transient from final (SendGridEmailService classifies 429/5xx/exceptions as transient), and the DYK job leaves transient failures claimed for the 15-minute stale-claim retry instead of retiring them. Original finding below.

The Did-You-Know mailer treats any send failure as final and retires the email, including cases where SendGrid never actually answered (rate limit, 5xx, socket timeout). Those emails are dropped and never retried.

### [FIXED 2026-08-20] JOBS-6

FIXED on fix/medium-sweep: each event in a SendGrid webhook batch is processed in its own try/catch -- one malformed event is logged and skipped, the rest of the batch survives, and the endpoint answers 200 so SendGrid neither re-duplicates nor drops the batch. Original finding below.

The SendGrid event webhook processes a batch of events with no per-event error handling. One malformed event makes the whole request fail, so every later event in that batch is lost and SendGrid retries into the same poison event - open/click/bounce data silently stops updating.

### [VERIFIED ALREADY FIXED] JOBS-9

VERIFIED 2026-08-20: closed by the LB-2 consent work -- PollDispatcher deliberately does NOT filter on IsNewsletterSubscribed (its own comment says so) and suppresses via EmailConsentService.IsSuppressed(Poll) with an honest skipped-count log. No change needed. Original finding below.

Poll sends use their own stricter consent rule than the rest of the system, so clients who never opted into the newsletter are dropped from poll audiences before the count is taken. Your poll reports show them as neither sent nor suppressed - they just vanish.

### [FIXED 2026-08-20] JOBS-10

FIXED on fix/medium-sweep: testimonial requests now refuse suppressed clients (the unused TestimonialRequest channel finally used) with an agent-visible message, and the email carries the standard List-Unsubscribe header. Original finding below.

Testimonial request emails skip the consent check entirely and carry no unsubscribe header. An opted-out client can still receive one. A consent category for it was added to the code and is never actually used.

### [LOW] JOBS-11

The SendGrid webhook checks the signature but never checks the timestamp, so a captured genuine payload can be replayed against you indefinitely.

### [FIXED 2026-08-20] A5-M-JOBISOLATION

FIXED on fix/medium-sweep: the due-changes loop wraps each agent in its own try/catch -- one agent's PayPal error is logged and the rest of the hour's run continues. Original finding below.

The hourly subscription-billing job still has no per-agent error isolation in its main loop. One agent's PayPal error aborts that hour's run for every remaining agent - scheduled plan changes and billing-issue notices silently do not happen.

### [FIXED 2026-08-18 `79480ad`] A2-H6

**STATUS CORRECTED 2026-08-20:** genuinely fixed on 2026-08-18 but this entry was never updated — verified today in the workflow files: BOTH `main_ipro-prod-web.yml` and `main_ipro-prod-admin.yml` now declare `group: deploy-ipro-production` with `cancel-in-progress: false`, so the deploys serialize and the documentation is finally true. Original finding below.

The audit states that the Web and Admin deploys now share one concurrency group and fully serialize. They do not - they use two different groups, so one push to main still starts both deploys at once and both run schema changes against the same database simultaneously. The specific crash this caused is now caught and handled, so the risk is reduced, but the written claim is untrue.

### [HIGH (accepted by prior decision)] A2-H5

If a uniqueness constraint fails to be created at startup (because the data already has duplicates), the app logs one line to the error stream and keeps serving without it. You would not notice. This was an accepted decision, not an oversight - but it is why nobody can tell you from code alone which constraints are actually live in production.

### [HIGH (half closed 2026-08-18; unification still scheduled)] A2-H8

Two competing systems still define your database schema: EF migrations and hand-written startup repair code. Known, documented, and scheduled - but until it is done, schema drift between what the code expects and what the database has stays possible.

HALF CLOSED 2026-08-18 (branch fix/audit-high-five): the WORSE drift axis is gone. The ~30 repair functions existed as two hand-maintained copies, one per Program.cs (~1,200 duplicated lines) -- and a mechanical diff during extraction found the copies had ALREADY drifted in 9 of 32 functions. All 9 drifts were comments/ordering, never SQL, but nothing guaranteed that. They now live once in IPRO.DataAccess.StartupSchemaRepair, called by both apps; Web's Program.cs shrank 2,019 -> 718 lines, Admin's 1,776 -> 519. Admin-only pieces (EnsureAdminUserSchemaAsync, recovery reset) stay in Admin by design. Verified: both apps boot clean on the shared repair against the existing dev DB, concurrently; and the F3 disaster path re-proven -- Web booted against a COMPLETELY EMPTY database, shared repair created all 98 tables, zero errors.

STILL OPEN: the EF-migrations-vs-repair duality itself (the snapshot covers 28 of 85 tables). That unification remains the separate scheduled initiative; this extraction makes it easier, not done.

### [HIGH (partial, open cost decision)] R-H9

Each deploy workflow can no longer overlap itself, but there is still no staging slot - deploys go straight to the live site. This is a cost decision you have not made yet, not a missed fix.

### [HIGH (deferred by decision)] R-H4

Client invoice numbers are still generated by reading the highest existing number and adding one, with no lock. Two invoices created at the same instant can collide. A retry was added elsewhere so it now self-heals rather than erroring, but the race itself is still in the code.

### [FIXED 2026-08-20] M-8 / A2-H4

FIXED on fix/medium-sweep: the cap slot is claimed ATOMICALLY AT CHECKOUT CREATION, before any discount is priced -- the race's loser is refused while the discount is still just a number on a screen, instead of redeeming one past the cap after money moved. Failed/abandoned checkouts release their slot (every post-claim exit covered; leak found and closed during testing). Two integration tests. Original finding below.

A promo code with a redemption cap can still be redeemed one time past its cap. The system now detects the breach and logs an error - and then grants the discount anyway rather than refusing. The window is actually wider than when first reported, because the cap is checked at subscribe time but claimed minutes later at activation. The equivalent trial-code path WAS fixed properly; this one was not given the same treatment.

### [FIXED 2026-08-20] A5-M-QUOTA

FIXED on fix/medium-sweep: article images now count against the shared pool (size captured at upload; pre-existing rows contribute 0, documented), and a package with NO storage limit value defaults to 1024 MB instead of unlimited. Agent photo + logo stay deliberately excluded: one bounded file each. Original finding below.

Storage quota now counts documents, website media, portal documents and gallery images - but still not article images, agent photos or logos. And if an agent's package has no storage limit value set, the check is skipped entirely rather than defaulting to deny.

### [FIXED 2026-08-20] A5-M-DOCUSAGE

FIXED on fix/medium-sweep: the Documents page now shows the SAME shared-pool figure the upload check enforces. Original finding below.

The Documents page shows a storage figure that counts only documents, while uploads are actually blocked against a larger total. Agents will see 'plenty of space left' and get rejected. The gap is now bigger than when it was first reported.

### [FIXED 2026-08-20] A5-M-PARENT

FIXED on fix/medium-sweep: a rejected parent-page choice still falls back to top-level (established behaviour) but now says so plainly in the flash instead of claiming success, on both save paths. Original finding below.

If an agent sets an invalid parent page (nonexistent, circular, or too deep), the system silently makes it a top-level page and reports 'Navigation settings saved'. The agent is told it worked; it did something else.

### [FIXED 2026-08-20] A5-M-CACHE404

FIXED on fix/medium-sweep: the [ResponseCache] attribute that stamped max-age=31536000 on every response is gone; responses start no-store and only the success path sets the immutable year header. Source-walk test pins it. Original finding below.

Media 'not found' responses are stamped with a one-year cache header. A browser or CDN that hits a missing image once will keep showing it as missing for a year even after you upload the file.

### [FIXED 2026-08-20] A5-M-EMPTYHOST

FIXED on fix/medium-sweep: FindWebsiteForHostAsync refuses an empty host outright -- previously an empty Host header matched any AgentDomains row with an empty RootDomain and handed robots/sitemap/form submissions to an arbitrary agent's site. One guard covers every caller. Original finding below.

Public form-submission, robots.txt and sitemap endpoints do not guard against an empty Host header the way page rendering does - the same request that returns 'not found' for a page is handled for a submission.

### [FIXED 2026-08-20] M-9

FIXED on branch fix/audit-medium-seven: OverdueInvoiceReminderJob resolves entitlements ONCE per run via HasAccessBulkAsync over the batch's distinct agents -- the same pattern AiDailyDigestJob and ClientLifeEventReminderJob already used -- instead of a per-invoice HasAccessAsync inside the loop. The per-invoice SaveChangesAsync stays: that one is deliberate (idempotency marker, 2026-08-14). Original finding below.

Most of the database-query inefficiency was fixed, but the overdue-invoice reminder job still queries per invoice inside its loop - roughly 600-1600 database round trips per run instead of a handful, with the batched alternative already written and sitting unused.

### [FIXED 2026-08-20] ADMIN-8

FIXED on fix/medium-sweep: the revenue chart buckets by the SAME agent-local clock the ledger rows print (AgentLocalTime per issuing agent), so month-boundary invoices land in the month their own row shows. Original finding below.

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
