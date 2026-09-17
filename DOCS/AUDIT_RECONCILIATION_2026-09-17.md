# Audit reconciliation — every finding, current status (2026-09-17)

The owner asked, four days before launch, whether anything from the auditors' reviews was left unfixed.
This is the full reconciliation of every finding in every review document against the TODO ledger,
the recorded decisions and the code as it is today. Rules: FIXED needs a commit/TODO row or code that
now handles it; DEFERRED/ACCEPTED needs a recorded owner decision; OPEN/PARTIAL was confirmed by reading
the code or configuration the finding names, on 2026-09-17.

Documents covered: SECURITY_AUDIT_2026-07-24 (+ second opinion), SECURITY_AUDIT_2026-08-05 (with the
R- and A2- independent sets), REVIEW_REQUEST_2026-08-07 A/B, audit-2026-08-14/ (triage, billing, webapp,
admin, jobs-email, data-layer), AUDIT_RECONCILIATION_2026-08-17, LAUNCH_BLOCKER_PLANS_2026-08-17,
AUDIT_2026-08-20_POST_SWEEP, PRODUCT_TRUTH_2026-08-28.

## Headline

- Every Critical and every High in every document is closed with a dated record, a commit and a live
  verification. LB-1 to LB-4 and the post-sweep wave-1 set are closed.
- One launch blocker has no closing record: **the PayPal sandbox-to-live cutover** (portal-to-portal,
  owner-side; verified by a first live charge).
- What remains is a tail of Lows and Partials, most of them minutes each, listed below with the file and
  line that still shows the finding. Decision on 2026-09-17: the minute-sized security and correctness
  items go into one batch (492) before launch; the rest is post-launch or accepted.

## Closed by 492 (built the same day, 2026-09-17)

M-6 residual (footer script nonce), L11 (rich editor sanitises on render, both apps), L1 (IP literals
refused; `@` stripped), WEB-L-2 (lead-magnet token bound to the agent), LB-1 sibling (newsletter test
sends use the canonical base), L8 (fresh consent read before a drip step), BILLING-12 half (profile
cannot write the promotion code), SO-MIN-7 (one base-URL helper), L5 (domain removal honours the
switch), ADMIN-11 (reset confirmation), ADMIN-12 (tax-rate before/after), plus the owner's three items
(client Edit note, /Admin landing, Hangfire deleting exhausted jobs). JOBS-11 dropped on the owner's
call (SendGrid no longer the provider). The tables below are as found on the morning of 2026-09-17.

## The narrow audit of the same afternoon (493)

After this reconciliation, two independent review passes over everything built since 2026-08-28
(`DOCS/NARROW_AUDIT_2026-09-17.md`) found one serious defect -- 491 waited for a send slot inside the
claimed send loops, which on launch morning would have duplicated recipients and retired every blast
as Failed -- and a tail of small ones. The before-launch set went out as 493 the same day; the rest
are listed there with a home. The tables below are as found on the morning of 2026-09-17.

## Still open today

### Security
| Finding | Where it still shows | Size |
|---|---|---|
| 07-24 M-6 residual: one inline script without a CSP nonce, the legal-link picker is dead in CSP-enforcing browsers | `src/IPRO.Web/Views/WebsitePages/Footer.cshtml:86` | minutes |
| 08-20 L11: the rich editor renders stored HTML raw, both apps | `Views/Shared/_RichEditor.cshtml:34` in IPRO.Web and IPRO.Admin | minutes |
| 08-20 L12: sanitisation is write-only; content saved before 2026-08-20 keeps live `<form>` blocks and overlay CSS until re-saved; no backfill | no backfill exists | hours |
| 08-20 L1: public IP literals accepted as custom domains (the file's own comment says any IP literal is refused); NormalizeDomain does not strip `@` | `src/IPRO.Utility/PublicHostGuard.cs:87` | minutes |
| 08-20 L2/L3: the telemetry scrubber returns early for anything but RequestTelemetry; two hand-copies, Admin's untested | `SensitiveDataTelemetryInitializer.cs:30` (Web and Admin) | hours |
| 08-14 WEB-L-2: lead-magnet download loads an AgentDocument by id with no tenant scope; the signed token is the only guard | `PublicWebsiteController.cs:483` | minutes |
| 08-14 WEB-L-1: website write paths carry no entitlement check; Duplicate clones gated blocks (latent while InstantWebsite is in every package) | `WebsitePagesController` (gates only at :31, :68, :174) | hours |
| 08-14 JOBS-11: the SendGrid webhook verifies the signature, never the timestamp (dormant under ACS; endpoint live, fails closed only on the key) | `NewsletterController.cs:508-551` | minutes |
| 07-24 note: all nine projects target net8.0; Maintenance support ends 2026-11-10; no upgrade plan recorded | every csproj | day+ |

### Correctness
| Finding | Where it still shows | Size |
|---|---|---|
| 08-14 BILLING-12 (partial): a PENDING PayPal capture reads as paid (`IsSuccessStatusCode`); an agent can write any string into their own PromotionCode | `PayPalBillingService.cs:4148`; `AccountController.cs:655` | hours (the order path is only reachable when no subscription starts) |
| LB-1 sibling: newsletter TEST sends bake the request host into the mail | `NewsletterController.cs:235` via `:674` | minutes |
| 08-20 L8: drip consent reads the Client snapshot loaded at batch start; a mid-batch unsubscribe still receives the step | `DripCampaignJob.cs:90,123` | minutes |
| 08-20 L7: a fresh DripCampaignStepSend row per retry attempt inflates per-campaign failure stats | `NewsLetterDispatcher.cs` (drip step insert) | minutes |
| 08-20 L4: `Articles.ImageSizeBytes` created inside `EnsureDripCampaignEnrollmentSchemaAsync` | `StartupSchemaRepair.cs:246` | minutes |
| SO-MIN-7: two files still hardcode the base-URL fallback instead of `WebAppUrlHelper` | `PollDispatcher.cs:325`, `TrialReminderJob.cs:157` | minutes |
| A2-H8 / DATA-F2 (partial, deferred post-launch): migrations and StartupSchemaRepair are two schema authorities; the snapshot covers 28 of 85 tables | `IPRODbContextModelSnapshot.cs` | day+ |
| 08-20 L10 (partial): the deferred start_time window is bounded by the 48-hour sweep; the PayPal timing itself was never verified | billing | hours |
| 08-20 L6 (partial): the dead `_azureDomains == null` branch survives; the warning not re-confirmed by a build | `AgentsController.cs:542` | minutes |
| Flaky test seen once on 2026-08-28, name lost, undiagnosed | suite | hours |
| DYK attempt counter computed client-side then written, not a server-side increment | `DidYouKnowEmailDispatchJob.cs:260-276` | minutes |

### Operations
| Finding | Where it still shows | Size |
|---|---|---|
| A2-H5 (accepted, verification open): the one-time `[SCHEMA] ... NOT CREATED` log search to learn which unique indexes are live in production was never run | container log | minutes |
| R-H9 (deferred past launch by the 2026-08-27 staging round): no staging slot; the snapshot + restore rehearsal is unscheduled | -- | day |
| One-time cleanup of unattributable form-answer rows from agents erased before LB-4; manual by design, no completion record | database | hours |
| LB-1 follow-up: no reconcile sweep over Pending Billings with a PayPal subscription id; a lost activation is logged, not healed | billing sweep | hours |
| A2-L2 (partial): browser coverage for /portal aliases, rate limits and gates never landed; e2e holds two smoke specs | `e2e/` | day |
| 08-20 L5: RemoveDomainAsync ignores `_options.Enabled` | `AzureDomainAutomationService.cs:145-160` | minutes |
| 07-24 crash-loop investigation never formally closed (infra moved as recommended: 64-bit workers, B2 plan; no recurrence recorded) | write-up only | minutes |
| Blob-damage reconciliation ("were image files destroyed by past deletions?") never settled | storage vs database | hours |

### Product
| Finding | Where it still shows | Size |
|---|---|---|
| PT Phase 3 truth sweep: /Preview, Register and the help docs never audited against the brief; the Azure region confirmation for the data-location claim had no record (settled 2026-09-17: App Service canadaeast, MySQL Canada East, ACS data location Canada, see 489) | TODO 412 | days |
| 08-20 M6 (owner decision, parked): no credit-note mechanism; `RefundStatus.ConvertedToCredit` unreachable; the CRA tax-by-region figure over-reports after a refund | `SubscriptionChange.cs:4` | day |
| 08-14 ADMIN-11: the admin confirm still says "Reset ... password to their last name"; the action generates a random password | `Agents/Details.cshtml:17`, `Agents/Index.cshtml:73` | minutes |
| 08-14 ADMIN-12: tax-rate edits are logged as "Bulk-updated N ..." with no before/after | `TaxRatesController.cs:55` | minutes |
| 08-05 H-11 / A5-H11 (accepted): orphan blobs reported, never reclaimed, quota never credited | Admin, Blob Storage | accepted |
| Client-erasure residual (accepted, owner's product call): WebsiteLead keeps name, email, phone, IP and answers after a client erasure | eraser | accepted |

## Closed, with evidence (one line per finding)

### SECURITY_AUDIT_2026-07-24 and its second opinion
PayPal APPROVAL_PENDING activation FIXED 10a48d2 · Hangfire dashboard unauthorised FIXED 9508c81 (Admin, SuperAdmin filter) · C-1 last-name passwords FIXED 2997851 · H-1 spoofable X-Real-IP FIXED f356a13 + c165637 · H-2 unauthenticated SendGrid webhook FIXED aa60478 (ECDSA, fails closed; dormant since ACS) · H-3/H-4 stored XSS FIXED fdbb1d0 + sanitizer waves · H-5 logo upload FIXED 26c3de6 · H-6 Host-trusting reset link FIXED e61d6a8 + ac1b58e · H-7 downgrade never cancelled PayPal FIXED 12275ad + 40eac39 · H-8 dispatch isolation FIXED af5c6dd · M-1..M-5, M-7, M-10..M-12 FIXED (9f24da7, a1db7ef, 2f03b59, 42ce429, 3e376e8, ddc3ccc, 8d930b4, d3771df, 0ab8ba6) · M-6 CSP nonces FIXED 48c6251 except the Footer residual above · M-8 redemption race FIXED 2026-08-20 · M-9 N+1/Take FIXED 2026-08-20 · L-1..L-13 FIXED 15fbe8b + 0cb0f6d · L-14, L-15 ACCEPTED (reviewed, no change) · vulnerable packages FIXED 538f777 + ee497e8 (Newtonsoft pin) · AngleSharp FIXED 2026-08-20 (HtmlSanitizer 9.2.995) · SO NEW-1..NEW-3, M-NEW-1..M-NEW-5 FIXED (c165637, 41b5468/1a4cb02, 7c8a069, cfd7483/f6c6ad5, e3a6004, ac1b58e, c9eea50, 71bcf59) · M-NEW-6 telemetry PARTIAL (above) · ten minor bullets FIXED (344ab9d, b8d15ad, 9dc7d3a, 61589e0, 619a0e2) · MIN-7 PARTIAL (above).

### SECURITY_AUDIT_2026-08-05, R- and A2- sets
Media traversal FIXED 14fff55 · C-1 website hijack FIXED 5695a17 · C-2 upgrade stops billing FIXED 1a4cb02 · C-3 DYK duplicates FIXED 4bc6ad6 · H-1..H-10, H-12..H-14 FIXED (f6c6ad5, eae2575/f13218d, 23b94ec, 5c3b85b, d0ec9ea/01b9cff/1f6cedd, 0a2b3d8, f6c6ad5 then TODO 418, F-sweep, wave 1 2026-08-24, 9c160d8, BlobReferences) · H-11 PARTIAL/accepted (above) · 17 MOD bullets FIXED across the 2026-08-20 and 2026-08-26 waves · R-H1..R-H3, R-H5..R-H8, R-L3/L4/L6 FIXED (4a3e365, f13218d, 82a972a, ecdc1d6) · R-H4 FIXED TODO 418 (455ee57) · R-H9 DEFERRED (staging round) · R-L1, R-L5 ACCEPTED · R-L2 FIXED 2026-08-08 · A2-H1..A2-H4, A2-H6, A2-H7, A2-L1 FIXED · A2-H5 ACCEPTED (verification open) · A2-H8, A2-L2 PARTIAL (above).

### REVIEW_REQUEST_2026-08-07 A/B
Question lists; their answers became the R- and A2- sets. The /portal compensating machinery was deleted 2026-08-08; the five unexercised fixes were exercised; the 2026-08-07 outage cause is unknowable (no HTTP logs retained) and the instrumentation half was done; local MySQL is the Windows service.

### audit-2026-08-14
BILLING #1..#11 FIXED (TODO 421/422, 14460ea, ae16eb6, e76600f, fix/audit-high-five) · BILLING #12 PARTIAL (above) · WEB C-1 FIXED 5013486 · WEB H-1 FIXED (LB-1; production buyer pass 2026-08-20, lifecycle 2026-08-26) · WEB H-2 FIXED ee497e8 (residual accepted 2026-08-17) · WEB M-1..M-5 FIXED · WEB L-1, L-2 OPEN (above) · ADMIN #1..#10 FIXED (dcd89c5, 2e9ee95, d7c65c9, medium sweep, AdminCookieRevalidator, fix/audit-medium-seven) · ADMIN #11, #12 OPEN (above) · JOBS #1..#10 FIXED · JOBS #11 OPEN (above) · DATA F1 FIXED 6a6d8cb · F2 DEFERRED · F3..F14 FIXED (F-sweep, TODO 418/425/426/427).

### AUDIT_RECONCILIATION_2026-08-17, LAUNCH_BLOCKER_PLANS_2026-08-17
Everything it listed as open was closed across fix/audit-high-five (08-18), fix/medium-sweep and fix/audit-medium-seven (08-20), re-verified by the post-sweep auditor, except the items carried into the tables above. LB-1..LB-4 FIXED (51b5f24; f998d59/6d060e9/d0ec9ea/01b9cff/1f6cedd; ee497e8; 9c160d8). LB-1 sibling and LB-1 follow-up remain (above).

### AUDIT_2026-08-20_POST_SWEEP
C1, C2, H1..H15, M1..M5, M7..M20 FIXED across wave 1 (08-24), the billing wave (08-25), the security+drip and erasure waves (08-26) and waves A/B/C plus the resolver split, drip recovery and consent-session wave (08-27). M6 parked by the owner. L9 FIXED. L1..L8, L10..L12 as in the tables above. The eight documentation corrections applied.

### PRODUCT_TRUTH_2026-08-28
ManagedBlog FIXED (a real Blog block, 2026-08-28) · ManagedSeo, DesignatedSupport, MailMerge, PrintableLabelCreator, Newsboard, RotatingBanner, FramedLinkManager WITHDRAWN by the owner 2026-08-28 (rows deleted plus a startup repair) · MultilingualEditor and CustomHomeButtons renamed · SmsReminder accepted as honest · Phase 3 truth sweep and Phase 4 PayPal cutover as in the tables above.
