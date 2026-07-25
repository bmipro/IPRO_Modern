# Second-Opinion Verification — 2026-07-25

## Method

Six independent, parallel review passes, mirroring the structure of the original audit (`SECURITY_AUDIT_2026-07-24.md`), each re-reading the *actual current code* rather than trusting that doc's "Fixed" claims. Scope: every finding from the original audit, plus a fresh, open-ended look in each domain for anything missed originally or introduced by the remediation work done since. No code was changed as part of this pass — verification only.

## Executive summary

The good news first: **of the ~35 findings in the original audit, the overwhelming majority are genuinely, correctly fixed** — verified independently, not just re-read. Full confirmation list is at the bottom.

The bad news: **two fixes that were marked "Fixed" do not actually work**, and **one fix introduced a real regression**. These three are the headline of this report:

1. **H-1's rate-limiter fix does not work.** The `X-Real-IP` spoofing bypass the audit described is still fully live on both apps. **Fixed in [c165637].**
2. **The Upgrade billing path has the same bug H-7 fixed on Downgrade — except on Upgrade it was never touched.** This is the normal, everyday paid-upgrade flow, not an edge case. **Open — product decision pending, see below.**
3. **M-6's CSP fix silently disabled confirmation dialogs on destructive actions** (delete agent, reset password, void invoice) — those buttons now fire immediately with no "are you sure?" prompt. **Fixed in [7c8a069]** (all 20 affected inline handlers across 13 files, not just the security-relevant ones).

Plus a handful of moderate and minor findings below. Recommend prioritizing 1–3 above everything else in this document.

---

## Critical / High — needs prompt attention

### NEW-1. H-1's rate limiter is still bypassable via a spoofed `X-Real-IP` header

**Status: Fixed in [c165637].** `builder.Services.PostConfigure<IpRateLimitOptions>(o => o.RealIpHeader = null);` added in both apps' `Program.cs` right after the `Configure<IpRateLimitOptions>` call, forcing `RegisterResolvers()` to skip the header-based resolver and fall through to the connection-based one. Built, deployed, verified via GH Actions + real Azure container logs (clean "Site started", no unhandled exceptions) on both `ipro-prod-web` and `ipro-prod-admin`.

**This was marked "Fixed" in the original audit and is not fixed.**

The `ForwardedHeaders`/`KnownNetworks` work is real and correctly fixes every other consumer of `Connection.RemoteIpAddress` (e.g. registration IP logging). But the actual rate-limiter bypass was never closed: the `AspNetCoreRateLimit` NuGet package has a **hardcoded default of `RealIpHeader = "X-Real-IP"` inside the library itself**. Deleting the `"RealIpHeader": "X-Real-IP"` line from `appsettings.json` does nothing — config binding only overwrites keys present in the JSON, so the library's own default survives untouched. Verified by decompiling `AspNetCoreRateLimit.dll` directly: `RegisterResolvers()` unconditionally adds a header-based IP resolver ahead of the connection-based one whenever `RealIpHeader` is non-null (which it still is), and the middleware uses the first non-empty result — header wins over real connection IP every time, with zero trust/origin validation on that header.

**Effect:** anyone can still send `X-Real-IP: <anything>` and completely defeat every per-endpoint rate limit in both apps — login brute-force, registration spam, password-reset abuse, lead/testimonial spam, poll-vote stuffing. Identical to the original H-1 finding, unchanged.

**Where:** `AspNetCoreRateLimit`'s own `IpRateLimitOptions`/`RateLimitConfiguration`/`IpHeaderResolveContributor` (external package, decompiled to confirm); both apps' `Program.cs` (rate limiter registration) and `appsettings.json`.

**Fix:** force the header off explicitly in code, after options binding — e.g. `builder.Services.PostConfigure<IpRateLimitOptions>(o => o.RealIpHeader = null);` in both apps — so the library's `RegisterResolvers()` skips the header contributor entirely and falls through to the connection-based resolver (which the existing `ForwardedHeaders` hardening already makes trustworthy).

### NEW-2. The Upgrade billing path can grant free/under-billed access — same bug class as H-7, different code path, never fixed

**This is the single most consequential finding of this review.** H-7 closed the *Downgrade* path's "new Billing row Active with no real PayPal subscription" bug. The *Upgrade* path — the normal, everyday flow every paying customer uses to move to a pricier package — has the identical problem and was never touched:

- **Free-upgrade edge case:** `ApplyUpgradeWithoutPaymentAsync` (reached whenever the prorated amount due is ≤ $0, which happens briefly around every billing-cycle rollover) marks the old subscription Cancelled **in IPRO's database only** — it never calls `CancelPayPalSubscriptionAsync`. It then creates the new package's `Billing` row Active with **no `PayPalSubscriptionId` ever assigned**. An agent who upgrades right at their own billing date gets permanent free access to the pricier package, while their old, never-cancelled PayPal subscription keeps auto-billing them at the old, cheaper price.
- **Paid-upgrade case (the common case):** for a real prorated charge, the code always routes through a one-time PayPal order (capture-payment), never a new PayPal *subscription*. The old subscription is cancelled locally only (same gap as above), and nothing anywhere — no job, no webhook — ever bills the upgraded package again after that single top-up charge. Checked every scheduled job: `SubscriptionBillingJob` only processes Downgrade changes.
- **Compounding issue:** because the old subscription is never actually cancelled on PayPal's side, its next real recurring-payment webhook arrives and flips the "cancelled" row back to Active (the exact resurrection mechanism the original H-7 write-up is about) — potentially leaving an agent with two simultaneously-Active `Billing` rows.

**Where:** `src/IPRO.Billing/PayPalBillingService.cs` — `ApplyUpgradeWithoutPaymentAsync`, and the Upgrade branch of `BeginPaidChangeAsync`/`CapturePaymentAsync`.

**Fix:** the Upgrade path needs the same treatment H-7 gave Downgrade, but going the other direction — either revise the existing PayPal subscription in place (PayPal's Subscriptions API has a `revise` endpoint for exactly this), or genuinely cancel the old PayPal subscription and create a real new one via the same flow `CreateSubscriptionAsync`'s Subscribe branch already uses (with real PayPal approval). This needs your input on which approach fits, similar to how we handled H-7 — happy to discuss options.

### NEW-3. M-6's CSP fix silently broke confirmation dialogs on destructive actions

**Status: Fixed in [7c8a069].** The scope turned out to be 20 broken inline handlers across 13 files, not just the 3 confirm-dialog files originally flagged — everything the M-6 nonce migration silently broke shared the same root cause, so all of it was fixed together: confirm-before-submit (delete/deactivate/reset/void/remove-photo/reset-template), copy-to-clipboard, select-on-click, auto-submit selects, the banner slider's prev/next/dot controls, two standalone print buttons, and the website-leads select-all checkbox. Shared delegated listeners (`js-confirm-submit`, `js-copy-to-clipboard`, `js-select-on-click`, `js-auto-submit`) added to both apps' `_Layout.cshtml`; standalone/page-specific cases got their own scoped nonced `<script>` blocks. Built, deployed, verified via GH Actions + real Azure container logs on both apps.

Dropping `'unsafe-inline'` from `script-src` (correctly, for the XSS-hardening goal) has a side effect nonces don't cover: **inline event-handler attributes** (`onclick="..."`, `onchange="..."`) are governed by `script-src` too, but nonces only apply to `<script>` elements, not attributes. No `'unsafe-hashes'` was added to compensate. Several `onclick="return confirm('...')"` guards were never migrated and are now silently dropped by any CSP-enforcing browser:

- Admin: delete-agent, reset-agent-password, deactivate-agent confirmations (`Agents/Details.cshtml`, `Agents/Index.cshtml`, `AdminDashboard/Index.cshtml`)
- Web: void-invoice confirmation (`ClientInvoices/_ClientInvoiceDocument.cshtml`), destructive website-template-reset confirmation (`Website/Index.cshtml`)

**Effect: fail-open, not fail-closed.** The blocked `onclick` doesn't block the underlying `<button type="submit">` — it just means `confirm()` never runs, so the destructive action fires immediately on a single click, with no "are you sure?" prompt at all, for any admin/agent using a CSP-enforcing browser (i.e. all modern browsers).

**Fix:** move these into small nonced inline `<script>` blocks that attach the confirm-guard via `addEventListener` (matching the pattern already used everywhere else in the M-6 migration), rather than inline `onclick=` attributes.

---

## Moderate

### M-NEW-1. PayPal cancellation failures are silently swallowed
`CancelPayPalSubscriptionAsync` has a bare `catch` with no status-code check and no logging — if a PayPal cancellation call ever actually fails, the local `Billing` row is still marked Cancelled regardless, while the real subscription keeps billing, with no diagnostic trail. Same file/area as NEW-2; worth fixing together.

### M-NEW-2. `GoogleCalendarSyncJob.cs` has the same missing-per-item-isolation gap H-8/M-11/M-12 fixed elsewhere, but this job was never touched
A failure partway through syncing a batch of follow-ups to Google Calendar can create a live Google Calendar event without persisting that event's ID locally (the `SaveChangesAsync` is deferred to the end of the loop) — the next run re-selects the same follow-up and creates a **duplicate** event. Also missing an `OrderBy` on its `Take(100)`, inconsistent with every sibling job fixed today.

### M-NEW-3. H-6's Host-header-trust pattern was fixed in one place, not generalized — independently found by two separate review agents
The exact anti-pattern H-6 fixed in `ForgotPassword` (building an emailed link from the ambient, spoofable `Host` header) still exists in: the client-portal invite email (`ClientsController.cs`), invoice/estimate view links (`ClientInvoicesController.cs`), testimonial request emails (`TestimonialsController.cs`), and PayPal return/cancel URLs (`BillingController.cs`). Lower severity than the original H-6 (these all require an already-authenticated agent acting on their own data, not a pre-auth attack against an arbitrary victim), but a compromised or malicious agent session could send a genuinely-signed IPRO email whose link points at a look-alike domain — a more convincing phishing vector than a spoofed-from-scratch email, since it rides on IPRO's real sending reputation and carries a real token.

### M-NEW-4. Login timing side-channel enables user/email enumeration (Web agent login + ClientPortal login)
Both `AgentService.AuthenticateAsync` and `ClientPortalAccountController`'s login skip password-hash verification entirely when the account doesn't exist, creating a measurable timing difference. Admin's login has a mitigating flat 1.5s delay on any failed attempt; Web and ClientPortal logins don't. This is exactly the kind of gap the original audit's own "Authentication, sessions, password handling, CSRF" pass should have caught.

### M-NEW-5. New stored (self-)XSS via uploaded filename on the Website Pages editor
An uploaded image's filename is stored verbatim (`Path.GetFileName` only strips directory components, not HTML metacharacters) and later concatenated into `innerHTML` client-side when the editor shows a "saving to block" status message. Scoped to the uploading agent's own browser session only — no cross-tenant or admin impact found — and classic `onerror=`/`onclick=` payloads are already blocked by M-6's CSP `script-src` hardening, but a CSS-based exfiltration vector remains via `style-src`'s still-permitted `'unsafe-inline'`. Pre-existing, not introduced by today's fixes.

### M-NEW-6. Application Insights will log live password-reset tokens by default
Application Insights' default request-telemetry captures full URLs including query strings. The existing password-reset link design puts a real, valid, single-use token directly in the URL (`?token=...`), and there's no `ITelemetryInitializer` scrubbing sensitive query parameters. That token will be visible in App Insights telemetry (queryable by anyone with read access to that Azure resource) for its ~1-hour validity window and retained in logs beyond that. Lower-sensitivity tokens (admin template preview, lead-magnet download) have the same exposure.

---

## Minor / hygiene

- `DashboardController.cs`'s cached-insight staleness check queries by ID with no `AgentUserId` filter, inconsistent with the fallback branch three lines below it. Not currently exploitable (only affects whether the *current* agent's own cached text is kept or cleared).
- `BillingIssueViewComponent.cs` has the same "NameIdentifier as AgentId, no auth-scheme check" shape as the original M-1 bug, currently safe only because it's exclusively invoked from the agent layout. No test/guard prevents a future change from reintroducing M-1 here.
- `EnsureUniqueIndexAsync`'s bare `catch {}` (added for L-12) will also silently swallow any *unrelated* failure (typo'd SQL, missing privilege), not just the documented duplicate-data case.
- `Views/Website/Index.cshtml.bak` (a stray, non-routable backup file) still has un-nonced inline `<script>` blocks — should have been caught by the L-10/11/13 dead-file cleanup.
- 160 tracked files under `src/IPRO.Web/publish/` — a committed build-output folder despite `.gitignore` excluding it (gitignore doesn't retroactively untrack already-tracked files) — including a stale copy of `appsettings.json` with the pre-H-1-fix `X-Real-IP` config. Not deployed (CI does a fresh publish), no real secrets, but should be `git rm --cached -r`'d for hygiene.
- A stray root-level `output` file (a UTF-16 directory listing dump from an old local path) is tracked in git; harmless, should be removed.
- The "read a base-URL config key, detect a placeholder, fall back to a hardcoded azurewebsites.net URL" pattern now exists in three independent copies (`PortalUrlHelper`, `NewsLetterDispatcher.GetBaseUrl`, and today's new `PayPalBillingService.BuildBillingPageUrl`). All three are correctly configured today, but this exact class of two-key config drift has caused two real incidents in this project's history — worth consolidating into one shared helper at some point.
- L-2's fire-and-forget email fix has a small residual timing gap (the DB write for a real password-reset token only happens on the account-exists branch) and does its `GetRequiredService` calls outside its own try/catch — both very low-severity, noted for completeness.
- H-7's downgrade-to-billing-gate handoff isn't literally "every request" as described — it's actually bounded by the hourly `SubscriptionBillingJob` or a hit on the Billing/Dashboard pages specifically. Functionally fine (the old subscription is still being correctly billed during that window), just an imprecise description.
- The Billing page doesn't communicate "you were just downgraded to X, finish it here" after H-7's flow — it looks identical to any other lapsed-billing state. The agent must rely on the email to know which package to pick. UX gap, not a security issue.

---

## Confirmed genuinely fixed (independently re-verified, not just re-read)

C-1, M-1, M-2, M-3, M-4, M-5, M-7, M-8, M-9 (both the starvation fix and the N+1 batching — logic traced line-by-line against the pre-fix version), M-10, M-11, M-12, H-3/H-4, H-5, H-6 (narrowly — see M-NEW-3 above for the ungeneralized part), H-7's specific downgrade mechanism (see NEW-2 above for what H-7 didn't cover), L-1, L-3, L-4, L-6, L-7, L-8, L-9, L-10, L-11, L-12, L-13, all four bumped dependency versions (verified via a fresh `dotnet list package --vulnerable` run), and the AngleSharp deferral (confirmed still the only remaining advisory, for the documented reason).
