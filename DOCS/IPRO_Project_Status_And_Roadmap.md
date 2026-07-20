# IPRO Project Status and Roadmap

Last updated: July 19, 2026

## Standing Convention: Every Paid Feature Must Be Package-Gated

Any new agent-facing capability that isn't a plain bug fix or a free platform-wide improvement must be selectable per package in Super Admin, the same way **Client Invoicing** and **Client Portal** already are:

1. Add a constant to `src/IPRO.Entities/PackageFeatureCodes.cs`.
2. Add a matching `Feature(...)` row in `PackageEntitlementSeeder.BuildFeatureDefinitions()` (`src/IPRO.DataAccess/PackageEntitlementSeeder.cs`) with a sensible default per package.
3. Gate every controller action that exposes the feature with a server-side `IPackageEntitlementService.GetAccessAsync(agentId, PackageFeatureCodes.X)` check (fail closed) — never rely on hiding a button in the UI alone.

Doing this automatically surfaces the feature as a checkbox in Super Admin's **Packages → Edit** screen (`_PackageFeaturesEditor.cshtml`) for every package (Silver/Gold/Platinum/Broker), which is how Super Admin actually turns it on or off per package without a code change. This is now also captured as a checklist item in `DOCUMENTATION_STANDARD.md`. Confirmed live 2026-07-18: `client_invoicing` and `client_portal` both already appear as toggleable rows in the Platinum package's 41-function grid.

## What Is Working

### Core deployment
- Web app and admin app are deployed on Azure App Service.
- GitHub Actions build/deploy pipelines are in place for both apps.
- MySQL database connection, package data, agent data, billing data, website data, and content records are active.

### Registration and login
- Public registration flow exists at `/pub/register.aspx` and `/Account/Register`.
- Active packages load into registration.
- Email addresses are normalized to lowercase and treated as unique.
- Username is generated from first name + last name.
- Duplicate username handling exists.
- Temporary password flow exists and first-login password change is supported.
- Registration success page is polished and shows temporary website, username, and password.

### Super Admin portal
- Admin dashboard exists.
- Agents can be listed, viewed, edited, deactivated, deleted, and reset.
- Agent billing status and invoice history are visible.
- Packages can be created and edited.
- Package feature limits are editable.
- Package active/inactive status exists.
- PayPal setup, email setup, tax rates, domain automation, templates, agents, revenue, and subscription sections exist.
- Admin authentication and authorization is a real role model instead of a single hardcoded credential: multiple named admin accounts exist (created/edited/deactivated/password-reset from **Admin Users**), split across two roles (Super Admin: full access including billing/platform config; Support: day-to-day ops), and every login attempt plus admin-account change is written to an audit log. The original Azure-config credential is auto-migrated into the first Super Admin account on first startup, so nothing breaks on deploy. Fixes a prior inconsistency where some billing/platform screens (PayPal, Email, Packages, Tax Rates) were actually *less* protected than day-to-day ops screens.

### Billing and invoices
- PayPal subscription checkout works in sandbox.
- Monthly and annual subscriptions are supported.
- Upgrade/downgrade flow exists.
- Setup fee is supported as a one-time charge.
- Canadian tax rates are supported by province.
- US customers are treated as no-tax.
- Invoice records are created.
- Invoice numbers use the shorter `IPRO-YYYY-000001` style.
- Invoice PDF/print view is one-page friendly.
- Paid invoice email resend exists.
- Promotion codes exist for signup, fully integrated with PayPal billing (not just cosmetic): Super Admin creates codes with a recurring-price discount (percent/flat, for N billing cycles or permanently) and/or a setup-fee discount. A recurring discount is implemented as a real, separate PayPal plan with PayPal's native multi-cycle pricing (a discounted cycle followed by a full-price cycle) — PayPal itself reverts the price automatically, IPRO never has to. Codes support expiry, redemption limits, and package restriction; redemptions are recorded (with original/discounted amounts) for reporting once a subscription actually activates, not merely when a code is entered.
- Pending/unpaid checkout recovery exists.
- Agents can now invoice their own clients: estimates and invoices with line items, automatic tax calculated from the client's own province, a signed no-login link the client uses to view/approve/decline/pay, a "Pay Now" button driven by the agent's own payment link (set on their Profile — no PayPal/Stripe integration required, since IPRO never touches the money), manual paid-tracking (Online/Cheque/Cash/EFT/Other), recurring schedules that auto-draft (never auto-send) an invoice on a cadence, and CSV export.

### CRM and follow-ups
- Agent client/contact management exists.
- Account types/groups exist.
- Client profile fields are expanded.
- Notes and client activity timeline exist.
- Follow-up management exists per client.
- Follow-up history pagination exists.
- Dashboard reminders exist.
- Calendar/follow-up view exists.
- Package contact limits are partially enforced.
- Client life-event reminders exist: a client's birthday (from Date of Birth) is covered automatically, and agents can add policy renewal/anniversary/custom yearly dates per client; a daily job auto-creates a real follow-up reminder ahead of each one (default 7 days), gated by a Platinum/Broker-tier package feature.

### Newsletter and campaigns
- Newsletter drafts exist.
- Preview exists.
- Send now and scheduled send exist.
- Sending to all subscribers, account type/group, or one individual exists.
- SendGrid delivery tracking exists.
- Open/failure tracking exists when SendGrid events are configured.
- Test send exists.
- Basic drip/campaign functionality exists.
- Newsletters already have a genuine rich-text editor (toolbar plus HTML-source toggle) — this was previously misreported as missing.
- Newsletter starter templates now exist: Super Admin manages a library of reusable starter content (seeded with 4 defaults on first startup), and agents can start a new newsletter from one instead of always starting blank.
- Drip campaign emails are now tracked the same way newsletter sends are (delivered/opened/clicked per recipient via SendGrid events, previously completely untracked), and each campaign's Performance section shows per-step counts and open/click rate percentages. Newsletter send history also now shows open/click rate percentages, not just raw counts. Drip campaign steps use the same rich editor as newsletters instead of a plain textarea.

### Agent profile
- Agents can edit their own profile/contact/business information from the agent portal.

### Newsletter and campaign unsubscribe
- Newsletter sends already generate a per-recipient unsubscribe link (token-based) that updates the client's newsletter-subscribed preference; this now applies consistently across all 3 audience choices (all subscribers, account type/group, and individual client) instead of only the default "all subscribers" audience.
- Drip campaign emails now also include an unsubscribe link, scoped to that specific campaign enrollment — clicking it cancels only that campaign's sequence for that client, without affecting their newsletter subscription or other campaigns.

### Website leads
- The agent-facing Website Leads inbox already supported status filtering (all/unread/new/contacted/dismissed), search across name/email/phone/message/source, real pagination, CRM linking, and Plan Follow-up/Contacted/Dismiss/Mark-all-read actions — this was already complete, not a stub.
- New this pass: a date-range filter, sort options (newest/oldest/by status), bulk actions (select individual leads or select-all, then bulk mark-contacted or bulk-dismiss), and CSV export of every lead matching the current filter/search/date range/sort (not just the current page).

### Website, domains, and templates
- Agent website settings exist.
- Temporary domain pattern exists, such as `FirstnameLastname.247Advisers.com`.
- Custom domain manager exists.
- Multiple custom domains are supported based on package allowance.
- DNS check, Azure domain binding, and SSL status are tracked.
- Azure custom domain binding automation works when Azure permissions/settings are correct.
- Wildcard SSL for `*.247advisers.com` was installed.
- Custom domain tests such as `www.4ipro.com` were successfully bound.
- Page manager basics exist.
- Navigation manager basics exist.
- Shared starter banners and agent image uploads have started.
- Template selection and theme color exist.
- Agent-editable footer exists (copyright, phone, email, address, social links, legal links, optional disclaimer) and renders consistently across all templates, matching the selected template's style.
- Website page image selection (uploaded images and shared starter banners) saves immediately and reliably, without discarding other unsaved edits on the page.
- Super Admin sets default font family, heading font size, and body font size per template; agents can override all three from My Website. The hero/banner title keeps its own template-authored display size regardless of this setting.
- Public contact/lead forms are hardened: each lead records whether the agent notification email actually delivered (visible to agents and Super Admin), blocked honeypot/timing/captcha attempts are logged (reason, domain, IP only, never submitted content) and reviewable cross-agent in a new Super Admin Website Leads screen, and the public lead endpoint has its own dedicated rate limit.
- 2026-07-17: fixed a critical regression in that same hardening work that had silently blocked essentially every real public contact/newsletter submission platform-wide since it shipped (2026-07-16), with zero trace in the product because the failure happened before a lead row was ever created. Root cause was ASP.NET Core's implicit-required validation on a non-nullable honeypot field (`<Nullable>enable</Nullable>` makes any non-nullable `string` property implicitly required even with no `[Required]` attribute), compounded by two related bugs in the custom-domain routing middleware that broke the post-submit redirect path and silently stripped the success-confirmation query string. All three are fixed and verified live end-to-end; see the "Public Contact/Newsletter Leads Silently Not Saving" incident in `09_TROUBLESHOOTING.md` for full detail.
- Fixed (2026-07-17): after submitting a public contact/newsletter form, visitors were silently redirected to the site's home page instead of back to the page they were on, with no success confirmation ever shown — caused by the custom-domain/temp-domain routing middleware rewriting the internal request path before the lead form captured it as its post-submit return path. Also renamed the hidden honeypot field away from "Website" (a common browser-autofill target that could cause a real visitor's legitimate submission to be silently treated as spam) and fixed its redirect to show the same confirmation a real submission would.
- Template output is more differentiated: Classic Sidebar's Call to Action, Contact, and generic Text blocks now use distinct grid/card/band layouts instead of one flat stack. Hero Style (Gradient/Clean/Classic) now has a real, visible effect on all 3 templates (it was previously a no-op). Agents can now also override Background Color, Button Style, and Section Spacing per site, in addition to Theme Color and Font. Design customizations are kept when switching templates instead of resetting automatically; a "Reset to Template Defaults" action is available when a full reset is wanted. Curated color palette swatches (in both Super Admin templates and agent My Website) set a coordinated theme/background color pair in one click.
- Services, Testimonials, and Call to Action blocks each support an independent per-block **Layout** choice (Services: Cards/List/Icons; Testimonials: Grid/Featured Quote/List; Call to Action: Banner/Card/Split) on top of whichever template family the site uses, giving agents block-level mix-and-match beyond template and color choices.
- Template governance is complete: the Super Admin template list shows per-template agent/package usage, defaults are set per business type (not just one global default), duplicate creates a safe versioned draft, deletion is blocked while a template is in use, and retiring a template emails every affected agent (in addition to the existing in-app notice on their My Website page) while their site stays online unchanged.
- Domain automation is hardened: failed domains back off and eventually pause automatic retries instead of retrying forever; agents can self-service retry a domain; agents see plain-language errors while Super Admin still sees the raw Azure/DNS detail; a missing Azure setting is named specifically; removing a domain requires typed confirmation and best-effort cleans up the Azure hostname binding/certificate; the root/apex domain's forwarding status is now tracked and shown (informational only); and the DNS/Azure-binding check logic is now one shared implementation instead of two drifted copies.
- Website analytics exists and is fully functional: agents review 7/30/90-day page views, estimated unique visitors, leads, conversion rate, popular pages, traffic sources, and a per-domain breakdown from the Agent Portal's Analytics screen. Real-time page-view recording excludes common bots, social-preview fetchers, template previews, and visitors sending Do Not Track, and stores a one-way monthly-rotating visitor hash instead of a raw IP address. Gated by the package's "Detailed visitor/hits tracking system" feature.
- Modern Professional and Editorial Visual's generic Text block now falls back to a full-width layout when a block has no image, instead of always reserving space for an image that isn't there. Modern's default Testimonials layout is now a simple quote list instead of reusing the same card-grid component as its own Services section, so the two sections no longer look identical on a Modern Professional site.
- Agents already had a genuine, non-destructive, real-content template preview before applying a template — this was previously misreported as missing. The actual gap (Super Admin's template editor only had a fake-content mock preview) is now closed too: Super Admin can preview a candidate template rendered against any real agent's actual pages/content via a signed, time-limited link, without saving or changing anything.

### Agent support
- A Support center exists in the Agent Portal: curated help documentation (rendered from the same manuals in `DOCS/`) plus a real two-way ticket system — agents open a ticket, Super Admin/Support replies in the same thread, and either side can reply again after a ticket is marked Resolved/Closed to reopen it automatically. Super Admin gets a cross-agent ticket queue (filter by status, search by subject/agent) mirroring the Website Leads screen's pattern. Agents are always emailed when Support replies; Support can optionally be emailed on new tickets/agent replies via a config setting.

### Documentation
- `DOCS` exists as the project documentation area.
- Some user-facing manuals have been started.

## Not Done or Still Fragile

### Important missing product pieces
- Document upload/storage (done — see item 17: a scoped version already existed inside the Client Portal; a general-purpose agent-side document library outside the portal context now exists too).
- Appointment booking (done — see item 15: scheduling a request now creates a real Calendar follow-up and emails the client the confirmed time).
- SMS reminders.
- Social media posting/management (done — see item 18: a content composer/tracker exists; live auto-publishing to specific platforms is a separate, larger future item — see "Reputation and social media" below).
- Formal backup/release checklist (done — see `DOCS/14_BACKUP_AND_RELEASE_CHECKLIST.md`: documents the actual current backup process — git + a dated OneDrive snapshot + folding decisions into existing docs — and the release process, including honestly-flagged gaps like no staging slot and no scripted rollback).
- Broader admin audit logging (done — see item 19: now covers every meaningful mutating action across the Super Admin portal, plus a real Audit Log viewer with filters).

### Proposed agent-value features (not scoped yet)
- **Testimonial module.** Today the website's Testimonials block is just manually typed text. A real module would let an agent send a client a short request link, the client submits a quote (and optionally a star rating/photo) through a public form, the agent approves/rejects it in the portal, and approved testimonials feed the existing Testimonials block automatically instead of copy-paste. A natural first slice of the "Reputation and social media" idea below.
- **Poll/survey system.** Let an agent build a short poll (one or a few questions), send it to their client list the same way a newsletter goes out, and view results in the portal, with a public voting page for recipients. Reuses the existing client-list segmentation and email-send infrastructure rather than building new plumbing.
- **Lead-magnet download block** (recommended). Let an agent attach a downloadable resource (PDF guide, checklist) to a page, gated behind the existing public lead-capture form, so a visitor trades contact info for the download. Reuses the WebsiteLead pipeline and file-upload infrastructure end to end.
- **External review widget** (recommended). A much smaller first step than a full review-request system: let an agent paste their Google/Facebook review page link and show an embeddable ratings badge on their site. Immediate trust-signal value without building a full request-and-moderate pipeline.

## Recommended Next Tasks

### 1. Stabilize the page builder image flow (done)
- Uploaded and shared-banner image selection saves to the destination block immediately.
- Applying an image to one block no longer discards unsaved edits elsewhere on the page (the prior root cause: an unconditional full-page reload after every image apply).
- A failed save rolls the selection back and shows a clear error instead of silently reverting on a later reload.

### 2. Add the footer system (done)
- Agent-editable footer settings exist.
- Footer renders across every template.
- Includes copyright, contact information, social links, legal links, and optional disclaimer.
- Footer style follows the selected template.

### 3. Harden public contact forms (done)
- CAPTCHA, honeypot, and timing checks already existed and are unchanged for visitors.
- Notification delivery is now tracked per lead (visible to agents and Super Admin) instead of silently failing.
- Blocked honeypot/timing/captcha attempts are now logged (reason, domain, IP only) instead of leaving no trace.
- A new Super Admin Website Leads screen gives cross-agent visibility into leads and blocked attempts.
- The public lead endpoint has a dedicated rate limit separate from ordinary page browsing.

### 4. Improve template governance (done)
- Template list shows agent/package usage per template.
- Default template is set per business type, not just one global default.
- Duplicate creates a versioned draft for safe editing before activation.
- Deleting a template in use is blocked with the affected agent/package names.
- Retiring a template emails every affected agent and shows an in-app notice on their My Website page; their site stays online unchanged.

### 5. Harden domain automation (done)
- Failed domains no longer retry forever: automatic background checking backs off over time and eventually pauses after repeated failures, tracked per domain.
- Agents can click **Retry** to recheck a domain themselves (roughly every 2 minutes), instead of only Super Admin being able to force a recheck.
- Agents see plain-language error messages instead of raw Azure error text; Super Admin still sees the real underlying error for diagnosis. A missing Azure configuration setting is named specifically instead of a vague "settings incomplete" message.
- Removing a domain requires typing the exact domain name to confirm, and best-effort removes the corresponding Azure hostname binding/certificate instead of leaving it orphaned.
- The root/apex domain's DNS and www-forwarding status is now tracked and shown (informational only, never blocks the site) in both the agent and Super Admin views.
- The DNS/Azure-binding check logic that the background job and Super Admin's Recheck action used to implement separately (and had drifted) is now a single shared service both call.

### 6. Build website analytics (done)
- Page views, estimated unique visitors, leads, and conversion rate are tracked and shown for 7/30/90-day periods.
- Popular pages and traffic sources (referrers) are shown.
- Domain source is tracked and comparable across an agent's temporary and custom domains.
- Bots, social-preview fetchers, template previews, and Do Not Track visitors are excluded from tracking.
- Gated by the package's visitor-tracking feature; shown in the agent portal's Analytics screen.

### 7. Finish template visual differentiation (done)
- Modern Professional and Editorial Visual's generic Text block now falls back to full-width when there's no image, instead of always reserving unused space for one.
- Modern Professional's default Testimonials layout no longer reuses the same card-grid component as its own Services section.

### 8. Close remaining unsubscribe gaps (done)
- Account type/group and individual-client newsletter sends now respect the client's newsletter-subscribed preference, matching the "all subscribers" audience (previously only that default audience checked it).
- Drip campaign emails now include a real unsubscribe link, scoped per campaign enrollment (clicking it cancels only that enrollment, leaving newsletter subscription and other campaigns untouched).

### 9. Polish the website lead inbox (done)
- The core inbox (status filters, unread tracking, search, pagination, CRM linking, follow-up actions) was already complete — this pass added a date-range filter, sort options, bulk mark-contacted/dismiss actions, and CSV export of every filtered/searched lead.

### 10. Close the roadmap's "named gaps" (done)
- Verified all 4 items against actual code first: template preview and the rich HTML editor turned out to already be built (roadmap was stale); newsletter templates, campaign reporting, and the admin role model were real gaps.
- Newsletter starter templates: Super Admin manages a seeded library; agents can start a newsletter from one.
- Drip campaign emails are now tracked (delivered/opened/clicked) the same way newsletter sends are; campaigns show a per-step Performance breakdown with rates; newsletter send history now shows rate percentages too; drip steps use the same rich editor as newsletters.
- Admin role/security model: multiple named admin accounts (Super Admin / Support roles) replace the single hardcoded credential, gating was corrected where it was previously backwards, and login/admin-account changes are audit-logged.
- Super Admin can now preview a candidate template against a real agent's actual site via a signed, time-limited link (previously only a fake-content mock).

### 11. Add an agent support center (done)
- New Support area in the Agent Portal: help documentation (curated from the existing `DOCS/` manuals) plus a real two-way ticket system, not just a contact form.
- Agents open a ticket and reply within it; Super Admin/Support sees a cross-agent queue (status filter, search) and replies in the same thread; replying to a Resolved/Closed ticket reopens it automatically regardless of which side replies.
- Agents are always emailed when Support replies; Support can optionally be emailed on new tickets/replies via `Support:NotificationEmail`, which degrades gracefully (ticket still saves) if unset.
- Also added an always-visible **Agent Login** link to every page of every public website template, so agents (or anyone) can reach the portal sign-in page from a live site.

### 12. Add promotion codes for signup, integrated with PayPal (done)
- Super Admin creates discount codes with a recurring-price discount (percent/flat, permanent or for a set number of billing cycles) and/or a setup-fee discount, plus expiry, redemption limits, and package restriction.
- Genuinely integrated with PayPal, not cosmetic: a recurring discount creates a real, separate PayPal plan using PayPal's native multi-cycle pricing, so PayPal itself reverts a temporary discount to full price automatically.
- Codes are validated at registration (with a live price preview) and re-validated again when the agent actually subscribes, since the PayPal subscription isn't created until then; an expired/maxed-out code degrades to the normal price instead of blocking signup.
- Redemptions are recorded only once a subscription actually activates (not merely when a code is entered), with original/discounted amounts for revenue-impact reporting.

### 13. Add agent-to-client invoicing (estimates, invoices, recurring, export) (done)
- Agents create estimates and invoices for their own clients with line items; tax is computed from the client's own province (not the agent's), reusing the same Canadian provincial tax table as platform billing.
- No PayPal/Stripe integration needed: a document shows a "Pay Now" button pointing at the agent's own payment link (set on Profile), and the agent always confirms payment manually (Online/Cheque/Cash/EFT/Other) — money never flows through IPRO.
- Clients view, and for estimates approve/decline, a document through a signed no-login link — no client-portal login required.
- Approved estimates convert to invoices in place with a new invoice number.
- Recurring schedules (Monthly/Quarterly/Annually) auto-generate a Draft invoice on a cadence; generated invoices are never auto-sent, so the agent always reviews before it reaches the client.
- CSV export of documents matching the current filter.
- Gated by a new Platinum/Broker-tier package feature ("Client invoicing and estimates"), same pattern as other premium features.

### 14. Add a Client Portal (login, messages, documents, appointment requests, My Info, invoices) (done)
- A separate, secure login for an agent's own clients (not the same auth as the agent/admin logins) — the agent invites a client by email, the client sets their own password via an activation link, and logs in at a distinct URL going forward.
- Because `Client.Email` has no uniqueness constraint (the same email can exist under different agents), login is scoped to the agent relationship rather than a single global identity; the rare case of one email matching multiple agents' clients is handled with a "choose your advisor" picker instead of failing.
- One continuous message thread per client, visible to both sides, with unread tracking on each side independently.
- Two-way document upload/download (agent and client both upload into a shared per-client folder); downloads always go through an authenticated action, never a raw storage URL.
- Clients see their own upcoming follow-ups (read-only) and can submit a lightweight appointment request with optional preferred date and notes; the agent has a dedicated Portal Requests queue (Pending/Scheduled/Declined).
- Clients can self-service edit their own contact information; changes save directly to the same record visible in the agent's CRM.
- Clients see all of their own estimates/invoices, linking to the same pages used by the existing signed-link invoicing feature.
- Gated by a new Platinum/Broker-tier package feature ("Client portal"), same pattern as other premium features.

### 15. Wire appointment requests into a real Calendar entry (done)
- Scheduling a pending Portal Request now lets the agent confirm/adjust the exact date and time (prefilled from the client's preferred date when given) rather than just flipping a status flag.
- Scheduling creates a real `ClientFollowUp`, so the appointment actually shows up on the agent's Calendar and Dashboard counts, not just in the Portal Requests queue.
- The client is emailed automatically when a request is scheduled (with the confirmed date/time) or declined, and sees the confirmed time on their own Appointments page.
- Rescheduling/cancelling an already-scheduled appointment reuses the existing follow-up edit/delete tools on the client's Details page — no separate reschedule flow.
- Next up: an optional two-way Google Calendar sync per agent (see "Google Calendar sync" under Bigger Product Ideas — in progress).

### 16. Add client life-event reminders (done)
- `Client.DateOfBirth` (already collected on every client) now covers birthdays automatically — no setup needed — with a reminder follow-up created 7 days ahead each year.
- Agents can add any number of additional yearly events per client (policy renewal, anniversary, or custom) with their own label and reminder lead time, from a new Life Events card on the client record.
- A daily background job creates a real `ClientFollowUp` when a reminder window is reached, so it shows up on the Follow-up queue, Calendar, Dashboard, and Google Calendar sync exactly like any manually-added follow-up — no changes needed to any of those.
- Each event/birthday reminds once per year (tracked per event and per client) so the daily job never duplicates a reminder.
- Gated by a new Platinum/Broker-tier package feature ("Client life-event reminders"), same pattern as other premium features; the job fails closed if an agent's package no longer includes it.

### 17. Add a general-purpose agent document library (done)
- New **Documents** area in the Agent Portal for an agent's own business documents (marketing materials, compliance forms, templates), separate from the per-client Portal Documents inside a client's record.
- Search by file name, filter by an optional free-text category, download through an authenticated action (never a raw storage URL), and delete.
- Makes the legacy `PackageFeatureCodes.FileUploadCapacity` checkbox genuinely functional for the first time: it was already seeded with real per-package storage limits (Silver 50 MB / Gold 500 MB / Platinum 1000 MB / Broker 1000 MB per user) but had zero enforcement anywhere in the code. Uploads are now checked against the agent's package limit, with a usage bar and a clear message if an upload would exceed it.
- Reuses the existing file-validation (`PortalDocumentValidator`) and blob storage infrastructure built for Portal Documents, in a new private `agent-documents` container.

### 18. Add a social post composer/tracker (done)
- New **Social Posts** area in the Agent Portal: write a post once, see a live character count against X (280), Instagram (2,200), LinkedIn (~3,000), and Facebook's effectively-unlimited caption length, then copy the finished text to paste into each platform's own app.
- Track status per post (Draft/Posted, self-reported) so agents can see what's still pending vs. already gone out.
- Makes the legacy `PackageFeatureCodes.SocialMediaIntegration` checkbox genuinely functional for the first time — it was already seeded as included for every package but had zero enforcement anywhere in the code.
- Deliberately does **not** connect to any real platform: no OAuth, no auto-posting, no vendor review process, no ongoing API cost. Chosen over live auto-publishing because each platform (Meta, LinkedIn, X) requires its own separate developer app, business verification, and review — LinkedIn's posting API specifically needs a hard-to-get partnership, and X's now requires a paid developer tier. Live per-platform publishing remains a distinct, larger future item under "Reputation and social media" below, to pursue only once a specific platform's setup cost is worth it.

### 19. Broaden admin audit logging + add a real viewer (done)
- Before this pass, only login attempts and admin-account changes were written to the audit log, and there was no screen to view it at all — entries were only visible via a direct database query.
- A new shared `IAdminAuditLogService` consolidates two previously-inconsistent private logging helpers, and now every meaningful mutating action across 9 Super Admin controllers writes an entry: Agents (edit, delete, password reset, activate/deactivate, hosting provisioning, Plesk login, invoice resend), Packages (create/edit/toggle/PayPal plan sync), Promotion Codes, Tax Rates, Domains, Newsletter Templates, Website Templates, Starter Content, and Support Tickets.
- A new **Audit Log** screen (Super Admin only) shows every entry, filterable by acting admin, action/detail text search, and date range, paginated.
- The pre-existing, separate per-agent `OperateLogs` history (a different, older mechanism) is untouched — both now run side by side.

## Bigger Product Ideas

### Agent invoicing and billing system (v1 done — see item 13 below)
The core of this shipped: estimates/invoices with line items, per-client tax, a no-login client view/approve/pay link, manual paid-tracking, recurring auto-drafted invoices, and CSV export. Still open for a future pass:
- Send automatic payment reminders for overdue invoices.
- Track failed/declined payments (today, payment itself happens outside IPRO, so there's nothing to detect automatically).
- QuickBooks export (CSV export exists; QuickBooks-specific format does not).
- Let clients view/pay through a real logged-in client portal instead of a one-off signed link (see "Client portal" below).

### Vertical starter packs
Create business-type starter packs for:
- Insurance and financial advisors.
- Mortgage agents.
- Accountants.
- Real estate professionals.
- Legal services.
- Health/wellness providers.
- Trades such as plumbers, electricians, contractors.
- Consultants and coaches.

Each starter pack should include:
- Website pages.
- Recommended template.
- Starter banners/images.
- Newsletter templates.
- Drip campaigns.
- Account types/groups.
- Follow-up workflows.
- Lead/contact forms.

### Real estate vertical: IDX listings (not scoped yet)
Real estate agents specifically need to display MLS listings on their site (IDX), which none of the other verticals require. Researched 2026-07-19; not yet designed or built. Costs show up at two separate layers:
- **Raw MLS data feed (platform/vendor side)**: no single national feed exists — 500+ regional MLSs each control their own data. **MLS Grid** is the most practical aggregator for a multi-region SaaS (~180+ MLSs under one vendor contract, standardized RESO Web API), priced at ~$250/month per feed + $20/month per participating agent license. **Bridge Interactive (Zillow)** charges vendors nothing directly, but the underlying MLS can still charge its own fee, and IPRO would still bear RESO certification, legal review, and field-mapping engineering either way.
- **Turnkey IDX widgets (agent side)**: providers like iHomefinder ($85–$500+/month) or IDX Broker sell a ready-made search widget an individual agent already embeds on their own site today, with zero MLS compliance burden on IPRO.

**Recommendation**: build a "Listings" content block (same pattern as the existing Services/Testimonials blocks) that lets an agent paste in their existing iHomefinder/IDX Broker embed code — fast, low-risk MVP with no vendor agreement or RESO certification needed. Only pursue a native MLS Grid integration later, once real estate signups justify the vendor contract and compliance overhead; that path would let IDX listings live inside IPRO's own package tiers instead of requiring a separate per-agent third-party subscription.

### AI-assisted business tools
- Generate website copy by vertical.
- Generate newsletter drafts.
- Generate drip campaigns.
- Generate social posts.
- Suggest follow-ups.
- Summarize client activity.
- Recommend next best action.

### Client portal (v1 done — see item 14 above; real appointment scheduling — see item 15)
Secure login, messages, two-way documents, self-service "My Information," a real appointment-scheduling flow (not just a request queue), and invoices all shipped. Still open for a future pass:
- Campaign preferences and unsubscribe controls surfaced inside the portal itself (today, unsubscribe still happens via the per-send/per-enrollment email links).
- Payments taken directly inside the portal (today's Pay Now button still links out to the agent's own external payment link, same as the standalone invoicing feature).

### Google Calendar sync (done — verified live end-to-end 2026-07-19)
Full two-way sync between an agent's own Google Calendar and the Agent Portal Calendar, opt-in per agent:
- Connect/Disconnect flow (OAuth) from a new Calendar Source settings panel on My Profile; gated by a new togglable `GoogleCalendarSync` package feature Super Admin can enable on any package (defaults to off everywhere).
- IPRO follow-ups push to the agent's connected Google Calendar automatically; events created or edited directly in Google sync back into the linked follow-up (a Hangfire job polling every ~15 minutes, not realtime push). Deleting a follow-up in IPRO removes the Google event immediately rather than waiting for the next sync cycle.
- If a linked Google event is deleted directly in Google, IPRO unlinks the follow-up rather than deleting it — a follow-up is CRM history, not just a calendar block.
- Non-client Google events (personal appointments, other meetings) show on the Agent Portal Calendar for context, distinguished from client follow-ups with a Google icon, but aren't tied to any client record and can't be marked complete.
- Live setup is done: Google Cloud OAuth client provisioned, Calendar API enabled, the `.../auth/calendar` scope registered on the OAuth consent screen's Data Access page (a separate step from creating the OAuth client — missing this caused a "not syncing" incident where the sync job ran cleanly but every Google API call failed; see `09_TROUBLESHOOTING.md`), and both directions (IPRO → Google, Google → IPRO) confirmed working live.
- Still open: the OAuth consent screen is in Testing mode (max 100 manually-added test users) and the Google Cloud project is still owned by a personal account rather than a dedicated one — see the production-readiness notes for the path to full public availability (Google app verification/review) and account ownership handoff.

### Reputation and social media
- Request reviews from clients.
- Track review status.
- Publish social posts.
- Reuse newsletter/page content as social content.
- Campaign calendar for email and social.

### Broker/team/white-label model
- Broker package management.
- Team member accounts.
- Shared templates and campaigns.
- Broker-level reporting.
- White-label branding.

## Product Direction

The strongest path is not just "website builder" or "CRM". The winning position is:

> A vertical-ready business growth platform that gives small businesses a website, CRM, email campaigns, follow-ups, billing, client portal, and automation in one place.

The website builder is dependable, website leads connect into CRM, and agent-to-client invoicing (v1) now exists. The next phase should focus on the testimonial/poll/engagement backlog and a real client portal to build on top of the invoicing foundation.
