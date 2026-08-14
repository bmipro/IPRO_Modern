# IPRO.Web Security Audit — Public Web App
[Agent 2 of 5. Verbatim; findings pending my own verification before fixing.]

## CRITICAL

### C-1. Unauthenticated client-portal account takeover: an omitted `token` matches every client whose invite token is NULL
`ClientPortalAccountController.cs:96-97` (GET) and `:106-108` (POST)

`token` bound with no null guard; `SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true` (Program.cs:140) → missing value binds null without failing validation. EF null-semantics rewrites `column == @nullParam` to `column IS NULL`, so predicate becomes `WHERE PortalInviteToken IS NULL AND (expiry IS NULL OR > NOW())`. NULL is the NORMAL state: every never-invited contact (incl. all public-lead-form auto-created), every client after activation (:125 nulls token), every client after RevokePortal (ClientsController.cs:230) — latter two leave ExpiresAt untouched.

Exploit: anon GET /ClientPortalAccount/Activate (no token) for antiforgery pair, then POST with password + NO token field → sets PortalPasswordHash on an arbitrary client of an arbitrary agent + SignInClientAsync → valid IPRO.ClientPortal cookie. Cross-tenant by construction, repeatable. No rate-limit rule on the POST.

Fix: `if (string.IsNullOrWhiteSpace(token)) return NotFound();` in both actions (the exact guard+comment already exists in EmailPreferencesController.FindByTokenAsync:133-141); also require ExpiresAt != null and clear ExpiresAt alongside token at Activate:125 and RevokePortal:230.

## HIGH

### H-1. PayPal capture silently lost when signup/upgrade starts on a non-canonical host — customer pays and stays gated
`AccountController.cs:426-437` · `BillingController.cs:31,119-121` · `Program.cs:105-107,530`

Auth cookie host-only (no Cookie.Domain). account/billing are IsNeverShadowedPrefix so resolve on every host incl. *.247advisers.com temp sites and custom domains. PayPal return URL always canonical base (PortalUrlHelper). Prospect completes signup on someagent.247advisers.com → cookie set on THAT host → PayPal returns to canonical /Billing/PayPalReturn where there is no cookie → [Authorize] 302s to login → CapturePaymentAsync never runs. Subscription approved at PayPal, local row never activated, agent stays gated; pressing Subscribe again risks a second charge. Cancel fails same way.

Fix: bounce session to canonical host before checkout.ApprovalUrl (one-time signed hand-off), or carry originating host into return URL and re-establish session there. (Overlaps memory: "bounce to canonical host first".)

### H-2. Unauthenticated promo-code oracle, with a 100%-off code granting a free paid subscription
`AccountController.cs:497-538`

ValidatePromoCode: anonymous, [IgnoreAntiforgeryToken], no rate-limit rule (only * 120/min). Clean 3-way oracle, discloses discount terms. packageId small enumerable. Brute ~7,200/hr/IP → register with accepted code → fully-comped code hits isFullyComped (PayPalBillingService.cs:132-138) → free Platinum, redemption counted. [OVERLAPS billing agent finding #1/#12 and confirms severity.]

Fix: require auth or fold into antiforgery-protected registration POST only; dedicated tight rate limit on both /Account/ValidatePromoCode and /portal/ twin; uniform message not describing the discount.

## MEDIUM

### M-1. Client Portal (Platinum/Broker feature) enforced in exactly one action
`PortalMessagesController.cs` · `PortalRequestsController.cs` · `ClientsController.cs:158-215,225-236`
PackageFeatureCodes.ClientPortal = no,no,all,all. Only ClientsController.InvitePortal checks it (:117). PortalMessages/PortalRequests controllers have ZERO _entitlements refs; UploadPortalDocument/Download/Delete/RevokePortal check ownership but not entitlement. Silver/Gold agent runs full client-portal surface by direct nav/POST; downgraded agent keeps it. Correct pattern = ClientInvoicesController.RequireClientInvoicingAccessAsync (gates all 13 actions).

### M-2. SyncPrimaryDomainAsync re-parents an arbitrary AgentDomains row with no ownership filter
`WebsiteController.cs:539,561-562`
`FirstOrDefaultAsync(d => d.DomainName == customDomain)` then `domain.AgentUserId = AgentId`. Owning that row = serving victim's domain + capturing their leads. Exact CRITICAL closed 2026-08-05 (post-mortem at :586-603); currently closed only by DescribeDomainClaimAsync called 400 lines earlier (:136-144) — TOCTOU-racy, and a refactor reopens it. Fix: add `&& d.AgentUserId == AgentId` at :539.

### M-3. Storage quota bypassed on two of four upload paths
`WebsitePagesController.cs:412-462 (UploadImage)` · `ClientsController.cs:158-195 (UploadPortalDocument)` · `AgentStorageUsage.cs:16-23`
TotalBytesAsync sums only AgentDocuments + gallery JSON. UploadImage (website-media) and UploadPortalDocument (portal-documents) check no quota and aren't counted. UploadGalleryImage + DocumentsController.Upload DO enforce. Silver (50MB) pushes unbounded images/files; the enforcing paths under-report usage.

### M-4. Public form pipeline: replayable captcha + unrated SubmitCustomForm + anonymous mail-to-arbitrary-address
`PublicWebsiteController.cs:948-974,426-613,258-265,280-353` · `appsettings.json:68-95`
(a) IsCaptchaValid validates data-protected expected|issuedAt with 1800s window, NO single-use tracking → one solve replays 30 min. (b) POST /PublicWebsite/SubmitCustomForm has no rate rule (siblings SubmitLead/SubmitTestimonial 10/5min) → 60× gap; each rejected attempt writes a WebsiteSpamAttempts row before validation. (c) Via SubmitLead with SubmissionType=DidYouKnow, QueueDidYouKnowArticleEmailsAsync queues mail to the unverified typed address (:337-343) — SendGrid reputation used to mail a third party. Fix: single-use captcha nonce; rate-limit SubmitCustomForm + /portal twin; confirm address / shorter cap before DYK mail.

### M-5. Client IP read from raw X-Forwarded-For, bypassing validated forwarded-headers pipeline
`PublicWebsiteController.cs:942-946`
Re-reads raw XFF leftmost (client-controlled) though UseForwardedHeaders already set Connection.RemoteIpAddress from trusted list (Program.cs:150-173). Feeds WebsiteLead.IpAddress, WebsiteSpamAttempt.IpAddress, and analytics visitorHash (:829-832) — forged header inflates unique-visitor counts and makes abuse untraceable. Same class as rate-limiter RealIpHeader bug already fixed. Fix: use Connection.RemoteIpAddress directly.

## LOW

### L-1. Website write-paths carry no InstantWebsite entitlement check; Duplicate clones gated block types
`WebsitePagesController.cs` — RequireWebsiteAccessAsync only on 3 read screens (Index:31, Navigation:68, Footer:165); all mutating actions unguarded. Not exploitable today (InstantWebsite = all,all,all,all) — latent. Also Duplicate (:656, CloneBlock:1170) copies every block type unconditionally, bypassing AddBlock's per-type gates; downgraded agent keeps+multiplies gated blocks.

### L-2. DownloadLeadMagnet fetches an agent document by id with no tenant scope
`PublicWebsiteController.cs:401` — `FirstOrDefaultAsync(d => d.Id == documentId)`, private agent-documents container. Safe today only because documentId arrives in a data-protected token that verified ownership at mint; protector purpose is global. Add `&& d.AgentUserId == website.AgentUserId`.

## Overall assessment (agent's words)
Tenant isolation inside the agent portal is genuinely strong — swept every controller, found no id-only lookup of agent-owned data without an AgentUserId/ClientId predicate. MediaController path-traversal defence exemplary; uploads validate magic bytes; video block YouTube-only; article/campaign HTML sanitized; /portal URL-space rule centralized. Remaining defects cluster in two places: (1) token/parameter handling on anonymous entry points — the null-param hole in Activate (C-1) is an unauth cross-tenant takeover to fix today, made sharper because the correct guard+comment already exists in EmailPreferencesController; (2) seams from signup v2 + multi-host serving — host-only cookie vs canonical-host PayPal callback (H-1) and the anonymous promo oracle (H-2), both costing money not data. Mediums are consistency failures against patterns the codebase already gets right elsewhere → highest-leverage follow-up is a checklist pass applying each established pattern uniformly.
