# Security & Code Quality Audit — 2026-07-24

## Method

Read-only static audit of the full codebase (`IPRO.Web`, `IPRO.Admin`, `IPRO.Entities`, `IPRO.DataAccess`, `IPRO.Business`, `IPRO.Billing`, `IPRO.Scheduler`, `IPRO.Email`, `IPRO.Utility`), run as six parallel, independently-scoped passes:

1. Authentication, sessions, password handling, CSRF
2. Authorization / IDOR / multi-tenancy boundaries
3. Injection (SQL), XSS, file upload security
4. Secrets exposure, dependency vulnerabilities, infrastructure/transport hardening
5. Billing/payment integrity, promo/trial code abuse
6. Code quality and bugs (including a re-audit of the schema-repair connection-handling bug class that has caused real outages before)

This was **not** a live penetration test — no exploitation was attempted against production. The one exception was a plain, read-only `GET /hangfire` request to confirm whether that endpoint was actually reachable (see Fixed #2); nothing was clicked, triggered, or changed on any live system as part of the audit itself. Every finding below was verified by reading the actual code, not inferred from patterns alone.

## Executive summary

The codebase is in better shape than the finding count below might suggest. The multi-tenant data-isolation discipline (the single most important property for a SaaS like this) is consistently well-applied, PayPal webhook signature verification is implemented correctly, password hashing uses a proper modern algorithm throughout, CSRF protection covers essentially every state-changing action, and document/photo uploads have real content-signature validation.

**Every Critical and High finding is now fixed, including H-7** (its remaining half needed a product decision rather than a unilateral implementation choice — resolved 2026-07-25). **All 12 Medium findings are now fixed**, including the two initially deferred pending further scoping or a product decision (M-6's CSP nonce migration across 28 view files, and M-7's per-agent redemption check).

**All 15 Low/Informational findings and the dependency-vulnerability table are also now resolved** (13 fixed, 2 reviewed and confirmed to need no code change) — see their sections below for commit links. One dependency (AngleSharp, Moderate) is deliberately left unpatched due to a hard version pin from `HtmlSanitizer`; see the dependency table for why.

**Every finding from this audit is now closed, including the one-time billing-data check.** A direct query of production data (2026-07-25) confirmed zero downgrades have ever been recorded and zero accounts are sitting in the old bug's free-access state — the H-7 bug never actually fired for any agent. See its entry below.

### Fixed — Critical

| # | Issue | Commit |
|---|---|---|
| PayPal `APPROVAL_PENDING` | Any authenticated agent could activate a paid subscription without ever paying | [`10a48d2`](https://github.com/bmipro/IPRO_Modern/commit/10a48d2) |
| C-1 | Temporary/reset passwords were derived from the agent's own (publicly-visible) last name | [`2997851`](https://github.com/bmipro/IPRO_Modern/commit/2997851) |

### Fixed — High

| # | Issue | Commit |
|---|---|---|
| Hangfire dashboard | IPRO.Web's `/hangfire` had no explicit authorization filter | [`9508c81`](https://github.com/bmipro/IPRO_Modern/commit/9508c81) (corrected — see incident note below) |
| H-6 | Password-reset link trusted the request's Host header | [`e61d6a8`](https://github.com/bmipro/IPRO_Modern/commit/e61d6a8) |
| H-5 | Website logo upload had no validation at all (size/extension/content-type/signature) | [`26c3de6`](https://github.com/bmipro/IPRO_Modern/commit/26c3de6) |
| H-8 | Newsletter/poll dispatch jobs had no per-item error isolation | [`af5c6dd`](https://github.com/bmipro/IPRO_Modern/commit/af5c6dd) |
| H-2 | Unauthenticated SendGrid webhook could mutate any agent's data | [`aa60478`](https://github.com/bmipro/IPRO_Modern/commit/aa60478) — **needs a manual step, see below** |
| H-3 / H-4 | Stored XSS in newsletter and drip-campaign HTML bodies | [`fdbb1d0`](https://github.com/bmipro/IPRO_Modern/commit/fdbb1d0) |
| H-7 | Downgrading didn't cancel the old PayPal subscription, and re-activated the new package for free | [`12275ad`](https://github.com/bmipro/IPRO_Modern/commit/12275ad) + [`40eac39`](https://github.com/bmipro/IPRO_Modern/commit/40eac39) |
| H-1 | Rate limiter trusted a spoofable `X-Real-IP` header instead of the real client IP | [`f356a13`](https://github.com/bmipro/IPRO_Modern/commit/f356a13) |

**Note on the Hangfire fix:** the first attempt (`f3601d9`) caused a brief production outage on IPRO.Web — it wrapped the dashboard mapping in an `IsDevelopment()` check, which turned out to also skip initialization that Hangfire's static `RecurringJob.AddOrUpdate` calls depend on, crashing the app on startup. Diagnosed via Azure's container logs and corrected in `9508c81`. Full incident detail in `09_TROUBLESHOOTING.md`.

**H-2 needs a manual step to actually take effect**: the code now correctly rejects every SendGrid event with a 401 until "Signed Event Webhook Requests" is enabled in the SendGrid dashboard and the resulting public key is set as `Email__SendGridEventWebhookPublicKey` on `ipro-prod-web`'s Azure App Settings. That's the safe, correct default (fail closed, not open) — but newsletter/drip campaign open/click/bounce/unsubscribe tracking won't update until that's done.

**H-7 is now fully fixed**: the old PayPal subscription is genuinely cancelled when a downgrade takes effect, closing the double-billing/entitlement-resurrection bug. The new, downgraded package is deliberately not auto-activated (that would've just moved the free-access bug to the lower tier) — instead the agent is gated to `/Billing` like any other lapsed-billing agent and emailed a prompt to re-approve the new subscription on PayPal there, per your direction.

**H-1 was the highest-risk of the eight** — it touches the same middleware category that caused the Hangfire incident, and Microsoft's own docs warn this specific change can cause an infinite HTTPS redirect loop on Azure Linux App Service if misconfigured. Verified `ASPNETCORE_FORWARDEDHEADERS_ENABLED` wasn't already set before assuming it was needed, used Microsoft's documented Azure-specific `KnownNetworks` ranges, and confirmed after deploy (via real container logs, not just a status code) that there's no redirect loop and no unhandled exception.

### Fixed — Medium

| # | Issue | Commit |
|---|---|---|
| M-4 | Admin login form was missing CSRF protection | [`42ce429`](https://github.com/bmipro/IPRO_Modern/commit/42ce429) |
| M-2 | Agent password reset only required `AdminAccess`, not `SuperAdmin` | [`a1db7ef`](https://github.com/bmipro/IPRO_Modern/commit/a1db7ef) |
| M-3 | Password change never verified the current password | [`2f03b59`](https://github.com/bmipro/IPRO_Modern/commit/2f03b59) |
| M-1 | Billing gate misread a ClientPortal user's ID as an AgentId | [`9f24da7`](https://github.com/bmipro/IPRO_Modern/commit/9f24da7) |
| M-10 | Exact-equality day math skipped notifications on a missed cron run | [`8d930b4`](https://github.com/bmipro/IPRO_Modern/commit/8d930b4) |
| M-11 | Calendar reminder job had no per-item isolation, logged false successes | [`d3771df`](https://github.com/bmipro/IPRO_Modern/commit/d3771df) |
| M-12 | Domain automation job had no per-item isolation or logger | [`0ab8ba6`](https://github.com/bmipro/IPRO_Modern/commit/0ab8ba6) |
| M-8 | Redemption-count increments had a race condition | [`5c9234b`](https://github.com/bmipro/IPRO_Modern/commit/5c9234b) |
| M-5 | IPRO.Admin had none of IPRO.Web's security response headers | [`3e376e8`](https://github.com/bmipro/IPRO_Modern/commit/3e376e8) |
| M-6 | CSP allowed `'unsafe-inline'` scripts (28 view files) | [`48c6251`](https://github.com/bmipro/IPRO_Modern/commit/48c6251) |
| M-7 | Promo/trial codes had no per-agent redemption uniqueness | [`ddc3ccc`](https://github.com/bmipro/IPRO_Modern/commit/ddc3ccc) |
| M-9 | N+1 queries and unbounded-`Take` starvation in scheduled jobs | [`eda2ae9`](https://github.com/bmipro/IPRO_Modern/commit/eda2ae9) |

---

## Critical

### C-1. Temporary and admin-reset passwords are guessable from public information

- **Status: Fixed in [`2997851`](https://github.com/bmipro/IPRO_Modern/commit/2997851).**
- **Where:** `src/IPRO.Web/Controllers/AccountController.cs:754-763` (`GenerateTemporaryPassword`, used at registration) and `src/IPRO.Admin/Controllers/AgentsController.cs:323-327` (`BuildTemporaryPassword`, used by the admin `ResetPassword` action)
- **What:** both generate the temporary password from the agent's own last name (stripped to alphanumerics), falling back to `firstName+lastName` or a fixed pattern — never a random value.
- **Why it's exploitable:** every agent runs a public marketing website through this same product, so an agent's full name is normally public. Anyone who knows an agent's registration email and last name can log in directly with the last name as the password during the window before the real user's first login, then use `MustChangePassword` to lock them out and take over billing/client data. The admin-triggered reset path is reachable by any admin under the `AdminAccess` policy, not just `SuperAdmin` (see M-2), which lowers the bar further via social engineering of support staff.
- **Fix:** generate with a CSPRNG — `EncryptionService.GenerateToken` already exists in this codebase and is used correctly elsewhere (e.g. `AgentsController.ProvisionHosting`); reuse it here instead of deriving from a name.

---

## High

### H-1. Rate limiting can likely be bypassed by spoofing the `X-Real-IP` header

- **Status: Fixed in [`f356a13`](https://github.com/bmipro/IPRO_Modern/commit/f356a13).**
- **Where:** `src/IPRO.Web/appsettings.json:71`, `src/IPRO.Admin/appsettings.json:8` (`"RealIpHeader": "X-Real-IP"`); no `UseForwardedHeaders`/`KnownProxies` configuration exists in either `Program.cs`.
- **What:** both apps have well-designed per-endpoint rate limits (login 10/5min, register 5/hour, forgot/reset password 5/5min, public lead/testimonial forms 10/5min, poll vote 20/5min, plus a 120/min global catch-all) — genuinely good baseline protection. But they key on `X-Real-IP`, a header Azure App Service does not set and nothing strips a client-supplied value from. The app's own code elsewhere reads `X-Forwarded-For` instead (`PublicWebsiteController.cs:602`), confirming the mismatch.
- **Why it matters:** if the rate limiter trusts an arbitrary client-supplied header, an attacker can send a different value on every request and never be throttled — defeating every login/registration/password-reset rate limit in both apps. It also likely means `AccountController.cs:257`'s `RegistrationIpAddress` (used for fraud/abuse investigation) records Azure's internal proxy IP instead of the real signup source.
- **Fix:** add `app.UseForwardedHeaders(...)` trusting Azure's actual `X-Forwarded-For`, and point the rate limiter at the now-correctly-populated `RemoteIpAddress` instead of a spoofable custom header.

### H-2. Unauthenticated webhook can mutate any agent's newsletter data

- **Status: Fixed in [`aa60478`](https://github.com/bmipro/IPRO_Modern/commit/aa60478), using the official `SendGrid` package's ECDSA verification (not Ed25519 as first guessed here) rather than hand-rolled crypto. Needs a manual step to actually start working — see the executive summary above.**
- **Where:** `src/IPRO.Web/Controllers/NewsletterController.cs:415-451` (`[AllowAnonymous] SendGridEvents`) → `src/IPRO.Business/Services/NewsLetterService.cs`, `RecordRecipientEventAsync` (154-265) and `RecordDripStepEventAsync` (267+).
- **What:** this endpoint has no SendGrid signature verification (confirmed via repo-wide search — none exists), and the sink loads `NewsLetterRecipients`/drip-step-sends by a plain sequential integer ID with no ownership check at all.
- **Why it's exploitable:** anyone on the internet, with no login, can `POST /Newsletter/SendGridEvents` a crafted event body with an incrementing `newsletter_recipient_id` and silently unsubscribe any agent's clients from newsletters, or forge bounce/open/delivered stats to corrupt any agent's campaign analytics — at platform scale, not just one account.
- **Fix:** implement SendGrid's Event Webhook Ed25519 signature verification before processing any event (SendGrid publishes the scheme specifically for this).

### H-3 / H-4. Stored XSS in Newsletter and Drip Campaign HTML bodies

- **Status: Fixed in [`fdbb1d0`](https://github.com/bmipro/IPRO_Modern/commit/fdbb1d0) — HtmlSanitizer (Ganss.Xss), wired in at both render time and save time so already-stored content is cleaned going forward too.**
- **Where:** `src/IPRO.Business/Services/NewsletterHtmlComposer.cs:57,71` (the only two fields not `HtmlEncode`d, out of every field in that composer), `src/IPRO.Web/Views/Newsletter/Preview.cshtml:66` (`@Html.Raw`), `src/IPRO.Web/Views/Campaigns/Details.cshtml:43` (a regex tag-stripper, not real sanitization), and the shared authoring surface `src/IPRO.Web/Views/Shared/_RichEditor.cshtml:34`.
- **What:** `Newsletter.HtmlBody`, newsletter article content, and drip-campaign step `HtmlBody` are saved with zero sanitization and rendered via `Html.Raw` — both in the agent's own authenticated `/Newsletter/Preview` session and in the actual HTML email sent to real subscribed clients (`NewsLetterDispatcher.cs`).
- **Why it's exploitable:** an agent account (including a compromised one) can save a payload like `<img src=x onerror="...">` as newsletter or drip content; it executes immediately in the preview and is delivered verbatim to every real client on that campaign. No sanitizer library exists anywhere in the solution today, and the app's CSP allows `'unsafe-inline'` scripts (M-6), so it provides no backstop here.
- **Fix:** run `HtmlBody`/article content through an allow-list HTML sanitizer (e.g. `HtmlSanitizer`/Ganss.Xss) at persistence time — one shared fix point covers both newsletters and drip campaigns.

### H-5. Website logo upload has no validation at all

- **Status: Fixed in [`26c3de6`](https://github.com/bmipro/IPRO_Modern/commit/26c3de6).**
- **Where:** `src/IPRO.Web/Controllers/WebsiteController.cs`, `Save` action, lines 69-73.
- **What:** unlike every other upload endpoint in this codebase, this one has no size limit, no extension allowlist, no content-type cross-check, and no magic-byte signature check. The upload form's `accept="image/png,image/jpeg,image/svg+xml"` (client-side only, trivially ignored) explicitly invites SVG, which the other, properly-validated image endpoints deliberately exclude.
- **Why it matters:** any authenticated agent can upload an SVG containing `<svg onload=...>` (or effectively any file) as their "logo," stored publicly and served back with whatever content-type was declared. Realistic impact is hosting XSS/phishing content on a legitimate Azure Blob Storage URL, plus unmetered storage abuse (this path isn't counted against the storage quota other uploads respect).
- **Fix:** apply the same extension allowlist + content-type cross-check + magic-byte signature check already implemented in `AccountController.UploadPhoto`/`WebsitePagesController.UploadImage` — worth factoring into one shared helper next to `PortalDocumentValidator` and reusing it here.

### H-6. Password-reset link trusts the request's Host header

- **Status: Fixed in [`e61d6a8`](https://github.com/bmipro/IPRO_Modern/commit/e61d6a8), reusing `PortalUrlHelper.GetAgentPortalBaseUrl` rather than a new `App:BaseUrl` read.**
- **Where:** `src/IPRO.Web/Controllers/AccountController.cs:85` (`Url.Action(..., Request.Scheme)`, no explicit host); `AllowedHosts: "*"` in both apps' `appsettings.json`; no `UseForwardedHeaders`/host allowlist anywhere.
- **Why it matters:** Azure App Service is known to largely forward a client-supplied `Host` header as sent. An attacker can plausibly trigger a password-reset email for a victim with a spoofed `Host` header, so the emailed link points at an attacker-controlled domain, harvest the token when the victim clicks it, then replay it against the real app. The codebase already has the correct pattern elsewhere — `NewsLetterDispatcher.cs:131-137`'s `GetBaseUrl()` builds links from the configured `App:BaseUrl` setting rather than trusting the current request, precisely because it's a background job with no request to trust.
- **Fix:** build the reset link from `App:BaseUrl` the same way, instead of `Request.Scheme`/implicit host.

### H-7. Downgrading a subscription never cancels the old PayPal subscription

- **Status: Fixed in [`12275ad`](https://github.com/bmipro/IPRO_Modern/commit/12275ad) (old-subscription cancellation) and [`40eac39`](https://github.com/bmipro/IPRO_Modern/commit/40eac39) (finished, per your direction: prompt to re-approve at downgrade time).**
- **Where:** `src/IPRO.Billing/PayPalBillingService.cs`, `ApplyDuePendingChangesAsync`, contrasted with the correct pattern in `ActivateSubscriptionBillingAsync` (303-315).
- **What it was:** when a scheduled downgrade became due, the old `Billing` row was marked `Cancelled` only in IPRO's own database — `CancelPayPalSubscriptionAsync` was never called for it, and the new lower-tier `Billing` row was created `Active` with no PayPal linkage at all (free, permanent access to the lower tier, never actually re-billed).
- **Why it mattered:** the old, higher-priced PayPal subscription kept auto-billing the agent, and when PayPal's normal recurring-payment webhook later arrived for that still-active subscription, the handler (no status filter on the lookup) flipped the "cancelled" row back to `Active` — so the agent could end up back on the old tier's access while being charged the old higher price the whole time, unbeknownst to IPRO's own records.
- **Fix:** the old subscription is now genuinely cancelled on PayPal's side (`12275ad`). The new package is deliberately **not** auto-activated (`40eac39`) — this codebase's PayPal integration has no way to create a new subscription without the buyer re-approving on PayPal's own page, so silently activating it would just move the same free-access bug to the lower tier. Instead, once the scheduled downgrade is actually applied (`ApplyDuePendingChangesAsync`, triggered by the hourly `SubscriptionBillingJob` or by the agent hitting the Billing/Dashboard pages — not instantaneously on every request), the agent is left with no active subscription: the existing entitlement gate (`IsAccessGatedAsync`) then redirects every *subsequent* request to `/Billing`, identical treatment to any other lapsed-billing agent, and an email prompts them to finish subscribing to the new package there — reusing the exact same PayPal approval flow (`CreateSubscriptionAsync`) a brand-new signup already goes through, so no new payment/approval code was needed. Correctly, the agent keeps full access on the old package (and keeps being billed for it) right up until that handoff happens.
- **One-time billing-data check: done (2026-07-25).** `SELECT ChangeType, Status, COUNT(*) FROM SubscriptionChanges WHERE ChangeType = 'Downgrade' GROUP BY ChangeType, Status` returned zero rows — no agent has ever reached a scheduled downgrade's effective date, so the bug never had a chance to fire. Confirmed with a second query for defense in depth: `SELECT Id, AgentUserId, BillingRuleId, CreatedAt FROM Billings WHERE Status = 'Active' AND (PayPalSubscriptionId IS NULL OR PayPalSubscriptionId = '')` also returned zero rows — no account is currently sitting in the old bug's free-access state either. No data cleanup needed.

### H-8. Newsletter and Poll dispatch jobs have no per-item error isolation

- **Status: Fixed in [`af5c6dd`](https://github.com/bmipro/IPRO_Modern/commit/af5c6dd).**
- **Where:** `src/IPRO.Scheduler/NewsLetterDispatchJob.cs:25-29` + `src/IPRO.Email/NewsLetterDispatcher.cs` (no try/catch anywhere in the chain); `src/IPRO.Scheduler/PollDispatchJob.cs:26-30` + `src/IPRO.Email/PollDispatcher.cs:59-87` (identical gap).
- **Why it matters:** every other scheduled job in this codebase (trial reminders, overdue invoices, life-event reminders, AI digest, recurring invoices, calendar sync, drip campaigns) correctly wraps each loop iteration so one bad record doesn't stop the rest — these two don't. A send is marked `Sending` before the risky work, and if anything throws afterward (most plausibly the batched save after emails are already physically sent), the send is left permanently stuck with no retry, and — because the outer loop also has no try/catch — every *other* due send in that run gets aborted too. Since this job runs every minute and re-fetches the same due list, a stuck send can block everything queued behind it indefinitely.
- **Fix:** wrap the outer per-send loop and the per-recipient loop in try/catch with logging, matching the pattern already used correctly by every other job in this codebase.

---

## Medium

### M-1. Shared gating middleware can misread a ClientPortal user's ID as an AgentId

- **Status: Fixed in [`9f24da7`](https://github.com/bmipro/IPRO_Modern/commit/9f24da7).**
- **Where:** `src/IPRO.Web/Program.cs:189-201`.
- **What:** the billing-access-gate middleware reads `ClaimTypes.NameIdentifier` from `HttpContext.User` without checking which authentication scheme it came from. For a logged-in ClientPortal user, that ID is a `Client.Id`, but the code feeds it straight into an agent-billing entitlement check.
- **Impact:** not a cross-tenant data leak — but a client whose numeric ID happens to exceed the highest real `AgentUser.Id` gets incorrectly treated as "gated" and redirected to a page requiring the agent login scheme, effectively locking legitimate clients out of the portal. Availability bug, not confidentiality/integrity.
- **Fix:** check `User.Identity?.AuthenticationType` (or a claim only agents have) before treating `NameIdentifier` as an agent ID.

### M-2. Admin's agent-password-reset action only requires `AdminAccess`, not `SuperAdmin`

- **Status: Fixed in [`a1db7ef`](https://github.com/bmipro/IPRO_Modern/commit/a1db7ef).**
- **Where:** `src/IPRO.Admin/Controllers/AgentsController.cs:11` (class-level policy), `ResetPassword` action at 162-179.
- **Why it matters:** every other credential/financial-config action in Admin requires the stronger `SuperAdmin` policy; this one — resetting any agent's login password — doesn't. Combined with C-1's guessable password, this meaningfully lowers the bar for account takeover via social engineering of lower-privileged support staff.
- **Fix:** require `SuperAdmin` specifically for this action (or at minimum, fix C-1 first so this gap doesn't compound with a guessable credential).

### M-3. Changing your password never asks for your current one

- **Status: Fixed in [`2f03b59`](https://github.com/bmipro/IPRO_Modern/commit/2f03b59).**
- **Where:** `src/IPRO.Web/Controllers/AccountController.cs:623-651` (`ChangePassword(string newPassword, string confirmPassword)` — no current-password parameter at all).
- **Why it matters:** this same action serves both the forced first-login flow (fine, no real password exists yet) and voluntary later changes (should re-verify). Anyone who gains a valid session — stolen cookie, an unattended logged-in browser — can silently change the password and lock the real owner out with no re-authentication step in the way.
- **Fix:** require and verify the current password for voluntary changes; skip only for the forced first-time flow.

### M-4. Admin login form is missing CSRF protection

- **Status: Fixed in [`42ce429`](https://github.com/bmipro/IPRO_Modern/commit/42ce429).**
- **Where:** `src/IPRO.Admin/Controllers/AdminController.cs:29-30`.
- **What:** the one gap in an otherwise near-universal `[ValidateAntiForgeryToken]` pattern (140+ POST actions checked across both apps) — agent login and client login both have it, admin login doesn't.
- **Fix:** add `[ValidateAntiForgeryToken]` to match the other two login actions.

### M-5. IPRO.Admin has none of IPRO.Web's security response headers

- **Status: Fixed in [`3e376e8`](https://github.com/bmipro/IPRO_Modern/commit/3e376e8) — a dedicated middleware scoped to Admin's actual CDN/frame footprint, not a shared one with Web's broader CSP.**
- **Where:** `src/IPRO.Web/Middleware/SecurityHeadersMiddleware.cs` (X-Frame-Options, X-Content-Type-Options, Referrer-Policy, Permissions-Policy, CSP) is wired up in `IPRO.Web/Program.cs:124`; no equivalent call exists anywhere in `IPRO.Admin/Program.cs`.
- **Why it matters:** Admin is the SuperAdmin panel — billing rules, admin-user management, audit log. Missing clickjacking/MIME-sniffing protection there is a narrower but real gap.
- **Fix:** share `SecurityHeadersMiddleware` between both projects and call `UseSecurityHeaders()` in Admin too.

### M-6. Content-Security-Policy allows `'unsafe-inline'` scripts

- **Status: Fixed in [`48c6251`](https://github.com/bmipro/IPRO_Modern/commit/48c6251).** Per-request cryptographic nonce generated in `SecurityHeadersMiddleware`, exposed via a `GetCspNonce()` extension method, interpolated into `script-src`, and applied via `nonce="@Context.GetCspNonce()"` on all 28 inline `<script>` tags across both apps. `'unsafe-inline'` dropped from `script-src` (kept on `style-src`, out of scope for this finding). Verified via curl CSP-header inspection and a live Browser-tool console-error check on the public Register page — zero CSP violations.
- **Where:** `src/IPRO.Web/Middleware/SecurityHeadersMiddleware.cs:43-51`.
- **What:** the rest of the policy is reasonably scoped (named CDN allowlist, `connect-src 'self'`, `frame-ancestors 'self'`) but `'unsafe-inline'` on `script-src`/`style-src` substantially weakens CSP as an XSS backstop, since inline `<script>` is the most common injection payload shape. Also missing (informational): `base-uri 'self'`, `form-action 'self'`.
- **Fix:** move inline scripts/styles to external files or nonces/hashes and drop `'unsafe-inline'` from `script-src` at minimum.

### M-7. Promo and trial invite codes have no per-agent redemption uniqueness

- **Status: Fixed in [`ddc3ccc`](https://github.com/bmipro/IPRO_Modern/commit/ddc3ccc) — per-agent redemption check (the cheaper of the two options), your call.** `ValidatePromotionCodeAsync` now takes an optional `agentId` and rejects a code already redeemed by that agent, checked against `PromotionCodeRedemptions`. Only applies to an existing agent re-subscribing (`PayPalBillingService.CreateSubscriptionAsync`, which now passes the agent's own `userId`) — a brand-new registration has no prior redemption history to check, so `AccountController`'s two call sites (registration POST, the anonymous `ValidatePromoCode` AJAX action) are correctly unaffected via the parameter's `null` default. Trial invite codes have no equivalent gap: a trial can only ever be redeemed once, at registration, for an account that doesn't exist yet.
- **Where:** `src/IPRO.Billing/PayPalBillingService.cs`, `ValidatePromotionCodeAsync` (1801-1813); `src/IPRO.Web/Controllers/AccountController.cs`, `ResolveTrialInviteAsync` (314-329).
- **What:** neither checks redemption history for the *requesting agent* — only whether the code overall is still active/unexpired/under its max-redemption count. An agent can cancel and resubscribe with the same promo code repeatedly (bounded only by `MaxRedemptions`, which can be unlimited), and the registration "verification code" is a visible anti-bot check, not a proof of email ownership — so the same person can register multiple accounts with different email strings and redeem a capped trial code more times than intended.
- **Fix:** check for an existing redemption by the requesting agent before allowing another, and/or require a verified email step before a redemption counts.

### M-8. Redemption-count increments have a race condition

- **Status: Fixed in [`5c9234b`](https://github.com/bmipro/IPRO_Modern/commit/5c9234b) — EF Core's `ExecuteUpdateAsync` (an atomic conditional UPDATE evaluated by the database at write time) rather than a schema-adding concurrency token.**
- **Where:** `PayPalBillingService.cs:1937` (`promo.RedemptionCount++`) and `AccountController.cs:280` (`trialInvite.RedemptionCount++`) — both plain read-modify-write with no concurrency token on either entity.
- **Why it matters:** two concurrent redemptions near the last available slot can both pass the `RedemptionCount < MaxRedemptions` check before either write lands, allowing one more redemption than configured. Narrow, bounded impact (a handful of extra free redemptions at most), but a real gap independently flagged from two different angles during this audit.
- **Fix:** use an atomic conditional update (`UPDATE ... SET RedemptionCount = RedemptionCount + 1 WHERE RedemptionCount < MaxRedemptions`, checking rows-affected) or add an EF concurrency token.

### M-9. N+1 query patterns in scheduled jobs and entitlement checks

- **Status: Fixed in [`eda2ae9`](https://github.com/bmipro/IPRO_Modern/commit/eda2ae9) — real fix for the starvation risk, not a cosmetic stand-in, plus the N+1 batching.** `ClientLifeEventReminderJob`'s starvation risk specifically needed the schema change to actually fix (ordering by `Id` alone would only make the same starvation deterministic, not solve it) — added `ClientLifeEvent.LastCheckedAt` and `Client.BirthdayReminderLastCheckedAt`, ordered both queries oldest-checked-first, and stamp the marker on every evaluated row regardless of outcome, mirroring `DomainAutomationJob`'s already-correct pattern. The other three jobs using unbounded `Take` (`OverdueInvoiceReminderJob`, `DripCampaignJob`, `RecurringClientInvoiceJob`) already self-rotate via a state-changing field, so they only needed a matching `OrderBy` added for explicitness. N+1 batching: added `IPackageEntitlementService.HasAccessBulkAsync`, resolving billing/trial access for many agents in a handful of fixed queries instead of 2-4 per agent — used by `AiDailyDigestJob` and `ClientLifeEventReminderJob`. `AiDailyDigestJob`'s other 5-6 per-agent queries are now precomputed once and grouped in memory. `AgentsController.DeleteAgentOwnedDataAsync` and `TaxRatesController` now batch their per-row lookups with `Where(x => ids.Contains(...))`.
- **Where:** `AiDailyDigestJob.cs`, `PackageEntitlementService.cs`, `ClientLifeEventReminderJob.cs`, `OverdueInvoiceReminderJob.cs`, `DripCampaignJob.cs`, `RecurringClientInvoiceJob.cs`, `AgentsController.cs` (Admin), `TaxRatesController.cs` (Admin).
- **Why it mattered:** cost scaled linearly with agent/client count; fine at the time, would have gotten slower as the platform grew. The unbounded-`Take` jobs with no `OrderBy` could have permanently starved rows past the cutoff once active-row counts exceeded the cap.

### M-10. Exact-equality day math means a missed cron run permanently skips a notification

- **Status: Fixed in [`8d930b4`](https://github.com/bmipro/IPRO_Modern/commit/8d930b4).**
- **Where:** `TrialReminderJob.cs:65,69` (one-time "trial ended"/"grace ended" emails, `daysRemaining == 0` / `-daysRemaining == graceDays`); `ClientLifeEventReminderJob.cs:38,72` (life-event and birthday reminders, `!= today`).
- **Why it matters:** unlike the offset-based trial reminders just above them (which correctly use `<=` plus a persisted sent-count, so a missed run self-heals), these use exact equality with no catch-up. A deploy window, transient crash, or Hangfire downtime on the exact day silently and permanently skips that one notification — no retry, nothing logged as unusual. Confirmed this does **not** affect actual trial access enforcement (`IsAccessGatedAsync` recomputes the real cutoff on every request independently) — this is a support-ticket risk, not a billing-bypass.
- **Fix:** replace exact-equality checks with a `<=`/range check plus a persisted "already notified this cycle" marker, matching the pattern already used correctly for the offset-based reminders.

### M-11. Calendar reminder job has no per-item isolation and logs success even when a send fails

- **Status: Fixed in [`d3771df`](https://github.com/bmipro/IPRO_Modern/commit/d3771df).**
- **Where:** `src/IPRO.Scheduler/CalendarReminderJob.cs:24-32`.
- **What:** no try/catch around the per-event loop (and because the query window is `StartDate` between now and +1 hour, a crash partway through *permanently* drops the remaining reminders in that batch, not just delays them — by the next hourly run they've already fallen out of the window). Separately, the loop discards `_email.SendAsync`'s boolean result and unconditionally logs "Reminder sent" regardless of whether it actually was.
- **Fix:** add try/catch per event; check the send result before logging success.

### M-12. Domain automation job has no per-item isolation and no logger at all

- **Status: Fixed in [`0ab8ba6`](https://github.com/bmipro/IPRO_Modern/commit/0ab8ba6).**

- **Where:** `src/IPRO.Scheduler/DomainAutomationJob.cs:32-35`.
- **What:** no try/catch around the per-domain loop, and the class has no `ILogger` injected. Partly mitigated because the underlying `DomainCheckService` is itself defensively coded internally, but any exception path not already covered there would silently starve every other agent's domain/SSL automation behind the failing one, every 5 minutes, with no diagnostic trail.
- **Fix:** add try/catch per domain and inject a logger, matching every other job.

---

## Low / Informational

All fixed in three commits on 2026-07-25: [`15fbe8b`](https://github.com/bmipro/IPRO_Modern/commit/15fbe8b) (L-1–L-9, L-12), [`0cb0f6d`](https://github.com/bmipro/IPRO_Modern/commit/0cb0f6d) (L-8, L-10, L-11, L-13), except L-14/L-15 which were reviewed and needed no code change (see their entries).

- **L-1.** ~~`PollsController.cs:221-225` — sending a poll can display another agent's `ClientCategory` *name* in the audience-label banner if the category ID is edited in the request (the recipient list itself stays correctly tenant-scoped — only the category name string leaks). The sibling Newsletter feature does this correctly (`NewsLetterService.cs:352-361`); Polls is missing the same `category.AgentUserId == agentId` check.~~ **Fixed in [`15fbe8b`](https://github.com/bmipro/IPRO_Modern/commit/15fbe8b)** — added the same check.
- **L-2.** ~~`AccountController.cs:76-107` — `ForgotPassword` awaits the outbound email send only on the "account exists" branch, creating a measurable timing difference that enables user enumeration despite an identical response body.~~ **Fixed in [`15fbe8b`](https://github.com/bmipro/IPRO_Modern/commit/15fbe8b)** — the send now runs on a background task (its own DI scope, via `IServiceScopeFactory`) so response time no longer depends on whether the account exists.
- **L-3.** ~~`Client.PortalInviteToken` (`ClientsController.cs:129`) has no expiry, unlike the agent password-reset token (1-hour expiry).~~ **Fixed in [`15fbe8b`](https://github.com/bmipro/IPRO_Modern/commit/15fbe8b)** — added `PortalInviteTokenExpiresAt` (7-day default on new invites; `null` treated as non-expiring so already-issued invites aren't retroactively broken).
- **L-4.** ~~`POST /ClientPortalAccount/Login` has no dedicated rate-limit rule, unlike agent and admin login.~~ **Fixed in [`15fbe8b`](https://github.com/bmipro/IPRO_Modern/commit/15fbe8b)** — added a matching 10/5min rule.
- **L-5.** ~~The trial-lockout gate's path check (`Program.cs:187`) is a prefix match (`path.StartsWith("/Billing")`), not a segment match.~~ **Fixed in [`15fbe8b`](https://github.com/bmipro/IPRO_Modern/commit/15fbe8b)** — switched to `Path.StartsWithSegments`.
- **L-6.** ~~Admin's newsletter *template* HTML body (`NewsletterTemplates/Edit.cshtml:43`) is unsanitized like H-3/H-4.~~ **Fixed in [`15fbe8b`](https://github.com/bmipro/IPRO_Modern/commit/15fbe8b)** — same `HtmlContentSanitizer` call H-3/H-4 already uses.
- **L-7.** ~~`ClientsController.ImportCsv` (697-711) has no server-side file-size limit.~~ **Fixed in [`15fbe8b`](https://github.com/bmipro/IPRO_Modern/commit/15fbe8b)** — added a 20MB cap, matching the limit already used elsewhere in the same controller.
- **L-8.** ~~`src/IPRO.Utility/EncryptionService.cs:8-16` — a dead `HashPassword`/`VerifyPassword` pair using plain SHA-256 with one hardcoded global salt.~~ **Fixed in [`0cb0f6d`](https://github.com/bmipro/IPRO_Modern/commit/0cb0f6d)** — deleted.
- **L-9.** ~~`IPRO.Web/Program.cs:120` points its production exception handler at `/Home/Error`, but no `HomeController` or matching view exists anywhere in the project.~~ **Fixed in [`15fbe8b`](https://github.com/bmipro/IPRO_Modern/commit/15fbe8b)** — added a minimal, layout-independent `HomeController.Error()` + view. Deliberately never shows exception details (unlike Admin's equivalent): every authenticated Web user is a paying agent or client, not IPRO staff, so there's no elevated role to gate diagnostics behind.
- **L-10.** ~~A root-level `IPRO_Modern.csproj` (not referenced by `IPRO.sln` at all) and an unused `EntityFrameworkCore.SqlServer` package reference on `IPRO.DataAccess` appear to be leftover scaffolding.~~ **Fixed in [`0cb0f6d`](https://github.com/bmipro/IPRO_Modern/commit/0cb0f6d)** — both deleted; this also removed the Microsoft.Data.SqlClient/Newtonsoft.Json/Azure.Identity/IdentityModel advisories below entirely.
- **L-11.** ~~`src/IPRO.Web/Controllers/BillingController.cs.bkup` — a stray backup of an old controller version whose webhook handler had no signature verification at all.~~ **Fixed in [`0cb0f6d`](https://github.com/bmipro/IPRO_Modern/commit/0cb0f6d)** — deleted.
- **L-12.** ~~`ClientInvoiceService.GenerateDocumentNumberAsync` (45-64) picks the next invoice number with no DB-level unique constraint on `(AgentUserId, DocumentNumber)`.~~ **Fixed in [`15fbe8b`](https://github.com/bmipro/IPRO_Modern/commit/15fbe8b)** — added a unique index in both apps' schema repair. Creation is skipped (not failed) if pre-existing duplicate data would violate it, so a bad historical row can't crash app startup.
- **L-13.** ~~`src/IPRO.Billing/BillingService.cs` — a complete, entirely unused class that shares a confusing name with the real `PayPalBillingService`/`IBillingService`.~~ **Fixed in [`0cb0f6d`](https://github.com/bmipro/IPRO_Modern/commit/0cb0f6d)** — deleted.
- **L-14.** **Reviewed, no code change.** Two EF migrations (`20260711143000_RepairWebsiteTemplateColumns.cs:52`, `20260711010200_AddWebsiteTemplateManagement.cs:99`) build DDL via C# string interpolation with manual quote-escaping instead of parameterization. Every call site today passes a hardcoded literal, so it's not currently exploitable, and these are already-applied historical migrations — editing them retroactively fixes nothing real. Flagged only as a pattern to avoid copy-pasting into a future call site with real input.
- **L-15.** **Reviewed, no code change.** A few `.cshtml` files interpolate admin-configured package/billing copy directly into HTML attributes (`data-upgrade-title="..."`) before `Html.Raw`-ing the containing element. Source is always admin-authored text, not agent/public input — no realistic exploitation path found.

---

## Dependency vulnerabilities

- **Status: Fixed in [`538f777`](https://github.com/bmipro/IPRO_Modern/commit/538f777)**, except AngleSharp (see below).

Original live scan via `dotnet list package --vulnerable --include-transitive` against the NuGet Advisory Database. All were **transitive** (none direct references), and several traced back to the orphaned SqlServer reference in L-10.

| Package (resolved) | Severity | Notes | Resolution |
|---|---|---|---|
| System.Text.Json 8.0.0 | High ×2 | Directly and heavily used (PayPal/Anthropic API response parsing) | Pinned to 8.0.6 |
| System.Security.Cryptography.Xml 8.0.0/4.5.0 | High ×6, Moderate | Pulled in transitively; app doesn't process untrusted XML | Pinned to 8.0.4 |
| Microsoft.Data.SqlClient 5.1.1 | High | Traced back to the unused `EntityFrameworkCore.SqlServer` reference | Gone entirely once L-10 removed that reference |
| System.Formats.Asn1 8.0.0/5.0.0 | High | ASN.1 parsing DoS, transitive | Pinned to 8.0.2 |
| Newtonsoft.Json 11.0.1 | High | Quite outdated (current major is 13.x) | Gone entirely once L-10 removed the SqlServer reference |
| Azure.Identity 1.7.0 | High, Moderate ×2 | Credential-handling advisories | Gone entirely once L-10 removed the SqlServer reference |
| Microsoft.Extensions.Caching.Memory 8.0.0 | High | DoS via unbounded cache growth; `AddMemoryCache()` and rate-limit counters are actively used | Pinned to 8.0.1 (pulled in a matching bump to `Microsoft.Extensions.Logging.Abstractions` 8.0.3 and `Microsoft.Extensions.Options` 8.0.2 to satisfy its own updated dependency range) |
| Microsoft.IdentityModel.JsonWebTokens / System.IdentityModel.Tokens.Jwt 6.24.0 | Moderate | Via Azure SDK; app doesn't accept externally-supplied JWTs | Gone entirely once L-10 removed the SqlServer reference |
| AngleSharp 0.17.1 | Moderate | New since the original scan — pulled in by `HtmlSanitizer` (H-3/H-4's fix) | **Not fixed, deliberately.** `HtmlSanitizer` 9.0.967 (its own latest release) exact-pins AngleSharp to `[0.17.1]`; forcing a newer AngleSharp via central transitive pinning would break the H-3/H-4 sanitizer fix. Revisit once HtmlSanitizer ships a version compatible with a patched AngleSharp. |

Verified via a fresh `dotnet list package --vulnerable --include-transitive` against both apps post-fix — only the AngleSharp entry remains.

**Also noted:** all projects target `net8.0`, which is in Maintenance support until **November 10, 2026** — not urgent, but worth planning a move to .NET 10 (current LTS) before then.

---

## Verified clean (representative, not exhaustive)

Noted briefly so the findings above aren't mistaken for the whole picture:

- **Multi-tenancy / IDOR** — nearly every controller action across both apps consistently filters by the authenticated user's `AgentUserId`/`ClientId`, including every file/document download action, bulk array-of-IDs actions, and token-based public flows (invoice links, testimonial requests, poll votes, lead-magnet downloads — all GUID/signed-token based, not sequential IDs).
- **PayPal webhook signature verification** — `VerifyWebhookSignatureAsync` performs a genuine server-to-server call to PayPal's verify-webhook-signature endpoint and fails closed if misconfigured. No forgeable-webhook path found.
- **Server-side price enforcement** — every subscription/order amount is read from the DB-stored `BillingRule` by ID; the client never submits a price.
- **Password hashing** — ASP.NET Core Identity's `PasswordHasher<T>` (PBKDF2-HMAC-SHA256, proper per-user salt) used consistently for agents, clients, and admins.
- **CSRF coverage** — 140+ POST/PUT/DELETE actions checked across both apps; essentially all carry `[ValidateAntiForgeryToken]` (see M-4 for the one gap).
- **Document/photo uploads** — real magic-byte signature validation (not just extension/content-type) for every upload path except the one flagged in H-5.
- **SQL injection** — no raw SQL anywhere is built by concatenating untrusted input; every schema-repair call site passes hardcoded DDL.
- **Public website content rendering** — agent block content, testimonials, and lead-form submissions (including anonymous, public-facing input) are consistently auto-encoded, not `Html.Raw`'d.
- **Admin role separation** — `SuperAdmin` vs. `Support` is a genuine, consistently-enforced policy distinction, not just "authenticated," and Admin's own `AdminUser` credential store is fully separate from Web's `AgentUser`/`Client` stores.
- **Secrets hygiene** — no live credentials found anywhere in the current tree or in full git history (spot-checked across the repo's history, not just the latest commit).

---

## Open questions for discussion

- **H-7** — resolved. Code path fixed, and the one-time production billing-data check confirmed the bug never actually fired for any agent (zero downgrades ever recorded). Nothing left from this audit.

---

## Production reliability: intermittent crash-loop (both apps, 07-22 onward) — under investigation

Discovered during M-7/M-9 deploy verification on 2026-07-25, not part of the original audit's scope. Documenting here since it's an active production issue, not a code-review finding.

- **What:** both `ipro-prod-web` and `ipro-prod-admin` have been crash-restarting every ~5-15 minutes since 07-22 (near-zero before that date). Confirmed via Azure container logs as a genuine native crash — `Segmentation fault (core dumped) dotnet "IPRO.Web.dll"` — not an Azure platform artifact, exit codes 139 (SIGSEGV) / 134 (SIGABRT), no managed exception logged (consistent with a native-level crash killing the process before .NET's own exception handling runs).
- **Ruled out:** the runtime container image (unchanged the whole time), circular DI dependencies (checked every `IPRO.Business` service constructor), entity-graph JSON serialization cycles (every `JsonSerializer.Serialize` call site is a plain DTO, never a raw EF entity), the schema-repair connection-wrapper bug class that caused two earlier incidents this session (every `OpenConnectionAsync`/`CloseConnectionAsync` pair in both `Program.cs` files balances correctly), any `unsafe`/native interop code (none exists in the solution), and the MySQL server itself (`ipro-mysql-prod`, Burstable B1ms — checked CPU, connections, storage IO, memory, and CPU credit balance directly via Azure Monitor; all healthy with heavy headroom throughout).
- **Significant, since it rules out job-specific bugs:** `ipro-prod-admin` runs zero background jobs (dashboard-only view of Web's Hangfire storage, no `AddHangfireServer`) yet shows the identical crash escalation as Web — so the cause is something both apps share, not a bug inside a specific scheduled job.
- **Leading hypothesis:** CPU/thread-pool starvation on the Basic B1 App Service plan (1 vCPU each). Application Insights (added 2026-07-25 specifically to get real telemetry instead of log archaeology) captured real `System.Net.Sockets.SocketException` ("Connect Timeout expired" / "Command Timeout expired" against MySQL) clustered right around a crash window — client-side timeouts with a demonstrably idle database point at the App Service's own single vCPU being unable to keep up with the burst of concurrent async work at startup (schema-repair checks, EF Core init, Hangfire job registration), not a code defect.
- **Current status:** both apps have been stable since Application Insights was wired up (which required a restart) — no recurrence yet, longer than any stretch on 07-25 prior. Being monitored; not yet resolved with certainty.
- **Infrastructure added for this investigation:** `ipro-prod-web-insights` / `ipro-prod-admin-insights` (Application Insights, workspace-based, `canadaeast`) plus their backing Log Analytics workspaces, wired to both apps via codeless auto-instrumentation app settings (no code change). Free-tier eligible; no code deployed.
- **Next step if it recurs:** scale the affected plan(s) to a tier with a dedicated vCPU (e.g. Standard S1) — a cost-affecting infrastructure change, needs your go-ahead when the time comes.
