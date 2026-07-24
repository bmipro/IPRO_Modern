# IPRO Project Status and Roadmap

Last updated: July 20, 2026

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
- Each admin account can pick its own accent color (a 6-swatch picker in the sidebar, mirroring the same feature on the Agent Portal) that re-themes the whole portal's buttons/links/active nav — a personal preference, not a shared setting.
- A SuperAdmin-only **AI Usage** page (`/AiUsage`) tracks estimated Anthropic API spend from the AI Daily Assistant's LLM calls, with a top-up form and a low-balance reminder banner on the Dashboard (see item 26 below).

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
- SMS reminders (not built — vendor pricing researched 2026-07-20, see "SMS reminders" under Bigger Product Ideas below).
- Social media posting/management (done — see item 18: a content composer/tracker exists; live auto-publishing to specific platforms is a separate, larger future item — see "Reputation and social media" below).
- Formal backup/release checklist (done — see `DOCS/14_BACKUP_AND_RELEASE_CHECKLIST.md`: documents the actual current backup process — git + a dated OneDrive snapshot + folding decisions into existing docs — and the release process, including honestly-flagged gaps like no staging slot and no scripted rollback).
- Broader admin audit logging (done — see item 19: now covers every meaningful mutating action across the Super Admin portal, plus a real Audit Log viewer with filters).
- Testimonial module (done 2026-07-20 — see `DOCS/15_TESTIMONIALS.md`: a new **Testimonial Submission Form** website block lets any visitor submit a testimonial through an open public form (reusing the hardened honeypot/timing/CAPTCHA anti-spam pipeline); agents review, edit, approve, or reject submissions in a new **Testimonials** queue in the Agent Portal; approved ones display below the form on the same page. The old static Testimonials block — manually-typed quotes, no approval concept — was retired the same day after confirming zero real usage, rather than left as dead, confusable functionality alongside the new one).
- Poll/survey system (done 2026-07-20 — see `DOCS/16_POLLS_AND_SURVEYS.md` and item 21 below).
- Lead-magnet download block (done 2026-07-20 — see `DOCS/05_DOMAINS_AND_LEADS.md` and item 22 below).

### Proposed agent-value features (not scoped yet)
- External review widget (done 2026-07-22 — see item 27 below).

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

### 20. Add a testimonial collection module (done)
- New **Testimonial Submission Form** website block: an open public form where any visitor submits a testimonial, protected by the same hardened honeypot/timing/math-CAPTCHA anti-spam pipeline already used on the contact form.
- The old, unrelated static **Testimonials** block (manually-typed quotes, no approval concept) was retired 2026-07-20 after confirming it had zero real usage — the whole project is still pre-launch/test-data-only. Removed the block type, its layout-variant options, and its rendering in all 3 templates rather than leaving dead functionality behind. The new block's dropdown label was also changed to **Testimonial Submission Form** (previously auto-generated as "Testimonial Form", which was too easy to confuse with the old block by name).
- New **Testimonials** area in the Agent Portal: a Pending/Approved/Rejected/All queue, an Edit screen to adjust wording before publishing, and Approve/Reject/Delete actions. Nothing shows publicly until an agent approves it.
- Approved testimonials render automatically below the form on the same public page, across all three template families (Modern Professional, Classic Sidebar, Editorial Visual).
- Makes the legacy `PackageFeatureCodes.TestimonialManager` checkbox genuinely functional for the first time — it was already seeded as included for every package but had zero enforcement anywhere in the code.
- Built as a new `TestimonialSubmission` entity/table rather than repurposing the pre-existing, unused `Testimonial` entity discovered mid-build: that old table's columns are `NOT NULL` with no default in the live schema, so a straight rename risked INSERT failures against unknown historical row state. Safer to add a fresh table and leave the dormant one alone.

### 21. Add a poll/survey system (done)
- New **Polls** area in the Agent Portal: build a poll with one or more single-choice questions (2+ answer options each), send it to the same subscriber base and audience picker (all subscribers / account type / individual client) newsletters already use, as Send Now or scheduled — dispatched by a new Hangfire job mirroring the newsletter dispatcher exactly.
- Each recipient gets a one-time link to a public, no-login voting page; submitting an answer is blocked from a second submission on the same link.
- A **Results** view shows per-question, per-option response counts and percentages, plus overall sent/responded totals.
- Deliberately scoped tight for v1: single-choice questions only (no free-text or multi-select), no SendGrid open/click webhook tracking (only Sent/Failed at send time and Responded on actual submission — the shared newsletter webhook handler is untouched), and a poll can only be deleted while still a Draft (never sent).
- Gated by a new `PackageFeatureCodes.PollSurveys` feature code, included in every package (Silver/Gold/Platinum/Broker) — same tier scoping as `Newsletters` (the exact infra it reuses) and `TestimonialManager`, so this checkbox is real and enforced from day one rather than a dead placeholder.
- After the user's first live test, added: a post-vote redirect back to the agent's own published website; a fix for the Results link being wrongly hidden whenever a send didn't register a successful SendGrid delivery; and a new **Poll Results** website block agents can add to any page, showing live results for a chosen poll once it clears 10 responses (to protect anonymity). Also identified and fixed that `App:BaseUrl` was never configured in Azure, so poll and newsletter links fell back to the raw `azurewebsites.net` hostname — bound a real `app.iproadvisers.com` domain (same as was done for the admin app) and set `App__BaseUrl` accordingly; SSL was provisioning as of this writing.
- See `DOCS/16_POLLS_AND_SURVEYS.md`.

### 22. Add a lead-magnet download block (done)
- New **Lead Magnet Download** website block: an agent picks an already-uploaded document from their [Documents](DOCS/12_AGENT_DOCUMENT_LIBRARY.md) library, and a visitor must submit the standard name/email lead-capture form before a "Download Now" link unlocks — no separate upload pipeline, reuses the existing document library and `WebsiteLead` pipeline end to end, matching the roadmap note's explicit intent.
- The submission becomes a real `WebsiteLead` (`SubmissionType = "LeadMagnet"`), so it shows up in the agent's Website Leads inbox and triggers the same agent-notification email as any Contact or Newsletter submission — not a parallel, separate system.
- The unlock link is a signed, time-boxed (30-minute) token minted via the same `IDataProtector` pattern already used for the public site's math CAPTCHA — not single-use, since re-downloading within the window isn't a scarcity concern the way re-voting on a poll is.
- Gated by a new `PackageFeatureCodes.LeadMagnet` feature code, included in every package, same pattern as `PollSurveys`/`TestimonialManager`.
- See `DOCS/05_DOMAINS_AND_LEADS.md` ("Add a Lead Magnet Download Block").

### 23. Three small follow-ups on shipped features (done)
- **Automatic overdue-invoice reminders**: once a sent invoice's due date passes, IPRO emails the client a reminder roughly weekly (a new daily `OverdueInvoiceReminderJob`, same dedup/gating pattern as `ClientLifeEventReminderJob`) until it's marked Paid/Void — no agent setup needed. See `DOCS/10_CLIENT_INVOICING.md` ("Automatic Overdue Reminders").
- **Campaign preferences inside the Client Portal**: a new **Preferences** page lets a logged-in client toggle their newsletter subscription and opt out of individual drip campaigns, alongside (not replacing) the existing anonymous per-send/per-enrollment unsubscribe email links. See `DOCS/11_CLIENT_PORTAL.md` ("Preferences").
- **Targeted testimonial requests**: a new **Request Testimonial** button on the Client Details page emails one specific client a personal feedback link (`TestimonialSubmission.RequestToken`, following the same signed-token pattern as `ClientInvoice.ViewToken`), landing in the same Pending review queue as open-form submissions once they respond. See `DOCS/15_TESTIMONIALS.md` ("Request a Testimonial from a Specific Client").
- In-portal payment processing (a fourth item from the same backlog note) was explicitly deferred — it requires picking a real payment processor and live merchant credentials, a bigger decision kept separate from this batch.

### 24. Add an AI Daily Assistant dashboard widget (done)
- New card at the top of the Agent Portal Dashboard — the first thing an agent sees — showing new lead count, leads older than 24 hours, clients with no follow-up scheduled, and one ranked "suggested next action" line (e.g. "Call Jane Doe first — her renewal follow-up is 3 days overdue").
- **Despite the name, v1 has zero AI/LLM API calls.** A new daily `AiDailyDigestJob` (same Hangfire pattern as `ClientLifeEventReminderJob`/`OverdueInvoiceReminderJob`) computes the counts and a deterministic priority ranking (overdue follow-up beats stale lead beats a "nothing scheduled" nudge), caches the result in a new `AgentDailyInsights` table (one row per agent, upserted daily), and the Dashboard just displays it instantly — no live computation, no API cost, no new configuration or NuGet dependency.
- Gated by a new `PackageFeatureCodes.AiDailyAssistant` feature code, Platinum/Broker tier — same tier as `ClientPortal`/`ClientInvoicing`/`LifeEventReminders` (a productivity/intelligence feature, not a marketing content tool like `PollSurveys`/`LeadMagnet`).
- This was scoped down deliberately from the broader "AI-assisted business tools" idea below after costing out the alternatives: most of the visible value here is plain dashboard engineering against data already in the schema, not an LLM call — so it ships with no ongoing cost and no external dependency, and the "AI" part is reserved as an optional future layer (see below) rather than the foundation.

### 25. Add a SuperAdmin portal accent-color picker (done)
- The Agent Portal has always had a 6-swatch accent-color picker in its sidebar (`AgentUser.PortalAccentColor`) that re-themes the whole app's buttons/links/active-nav via a `--portal-accent` CSS variable — the SuperAdmin portal never had the equivalent, which surfaced as a confusing "the color palette is gone" bug report before it was clarified as a missing feature, not a regression.
- Mirrored the identical pattern onto `AdminUser.PortalAccentColor`: a `SetPortalAccentColor` action in `AdminController`, the same claims-refresh-on-change pattern (the accent color is baked into the login cookie, refreshed via a full re-sign-in when changed), and the same swatch row + CSS overrides in `IPRO.Admin/Views/Shared/_Layout.cshtml`.
- One schema-repair gotcha worth remembering: `EnsureTableColumnAsync` (the shared helper used to add a column to an existing table) reads the raw ADO.NET connection directly and needs `db.Database.OpenConnectionAsync()`/`CloseConnectionAsync()` wrapped around it — unlike `ExecuteSqlRawAsync` (used for `CREATE TABLE`), which manages its own connection. `EnsureAdminUserSchemaAsync` had never needed this before and threw `Connection must be Open` on a fresh database until fixed. See `09_TROUBLESHOOTING.md`.

### 26. Add LLM-composed reason line + AI usage/cost tracking (done)
- **The "why" line**: `AiDailyDigestJob` now calls Claude Haiku 4.5 once per agent per day (only when there's an actual suggestion — skipped entirely for "you're all caught up") to populate `AgentDailyInsight.SuggestedActionReason`, a one-sentence explanation of *why* the suggested action matters, shown under the suggestion on the Agent Dashboard. New `IAiSuggestionService`/`AnthropicAiSuggestionService` in `IPRO.Business` — a plain `HttpClient` call to `api.anthropic.com/v1/messages`, no SDK dependency. Fails soft everywhere: unconfigured key, network error, or bad response all just leave the reason blank and never break the rest of the digest (counts/action text still save normally).
- **Anthropic API key**: stored as the `Ai__AnthropicApiKey` Azure App Service setting on `ipro-prod-web` only (that's the only app with a Hangfire server) — never committed to git; `appsettings.json` only carries the `YOUR_ANTHROPIC_API_KEY` placeholder.
- **Usage/cost tracking**: every call's real `input_tokens`/`output_tokens` (returned by Anthropic's API) are accumulated per job run and upserted into a new `AiUsageDailyLogs` table (one row per calendar day), with estimated cost computed from Haiku 4.5's published rate — **$1/MTok input, $5/MTok output** (confirmed live from `platform.claude.com/docs/en/about-claude/pricing` on 2026-07-21, not assumed from memory).
- **New SuperAdmin page** `/AiUsage` (`AiUsageController`, SuperAdmin-only): total funded, estimated spend to date, estimated remaining, a 30-day usage table, a "record a top-up" form (adds to the running total — funding is cumulative, not a reset), and an adjustable low-balance threshold (default 20%). Backed by a single-row `AiBillingSettings` table.
- **Top-up reminder**: a warning banner on the Admin Dashboard itself (the first page a SuperAdmin sees) once estimated remaining balance drops to the threshold, linking straight to `/AiUsage`.
- This whole feature is **self-tracked, not synced with Anthropic's real account balance** — there's no read access to Anthropic's actual ledger without a separate Admin API key, so "remaining balance" is always an estimate the SuperAdmin keeps accurate by recording top-ups as they happen.

### 27. Add an external review widget block + AI-drafted social posts (done)
- **Review Badge** website block: agent enters a platform (Google/Facebook), review page URL, current rating, and review count; renders a star rating + "Read Reviews" button on all 3 public templates. Settings live in `WebsiteReviewSettings` (`SettingsJson`, same pattern as `WebsiteLeadMagnetSettings`) — no new table. Not gated behind any package tier; a low-friction trust signal available to every agent. No live sync with Google/Facebook — an agent updates the numbers here manually whenever they change.
- **Draft with AI** in the Social Posts composer: a topic in, a short on-brand post out (professional tone, at most one emoji, at most two hashtags, sized to fit X's 280-character limit), which the agent edits before saving. Gated by the same `PackageFeatureCodes.AiDailyAssistant` entitlement as the daily digest — renamed that feature's display name from "AI daily assistant digest" to "AI Assistant features" now that it gates more than one thing (the already-seeded production row needs a one-time manual update since the seeder only sets `FeatureName` when it was previously empty — see `09_TROUBLESHOOTING.md`).
- Extracted `AiUsageRecorder` (`IPRO.Business`) as a shared static helper so cost tracking (item 26) stays accurate across both Anthropic call sites instead of duplicating the upsert logic.
- Both features documented in the agent-facing Support articles (`04_WEBSITE_BUILDER.md`, `13_SOCIAL_MEDIA_POSTS.md`), not just internal docs — these are embedded resources compiled into `IPRO.Web`, so a redeploy is required for help-article text changes to go live.

### 28. Add a branded newsletter wrapper (done)
- Newsletters previously sent as raw agent content plus an unsubscribe line — no header, no banner, no footer, confirmed by reading `NewsLetterDispatcher` directly. New `NewsletterHtmlComposer.Wrap(NewsLetter, AgentUser, baseUrl)` in `IPRO.Business` (shared by the dispatcher and the Preview/Test Send actions, so what an agent previews is exactly what sends) composes a single-column branded shell around the unchanged rich-text content: an optional banner picked from the existing starter-banner gallery (same one used for website Hero blocks), a colored title bar showing the newsletter's Edition and the agent's site link — using the agent's own `PortalAccentColor` — and a footer with their name/company/phone/email.
- New nullable `NewsLetter.BannerUrl`/`NewsLetter.Edition` columns. Banner picker and Edition field added to Create/Edit as a plain hidden-input-plus-click-to-select pattern (no separate AJAX save endpoint needed, unlike the website builder's version — a newsletter only ever has one banner, so it just submits with the rest of the form).
- Deliberately scoped to real newsletters only — drip campaign steps (`DispatchDripStepAsync`) are a separate feature surface with no `NewsLetter`/banner/edition of their own, left untouched.
- **Real bug caught via an actual test send, not just in-app preview**: the starter-banner catalog returns root-relative URLs (`/images/starter-banners/x.jpg`) — these resolve fine in a browser (so Preview looked perfect) but have no base to resolve against inside an actual email, so the banner silently vanished from the first real test send while everything else rendered correctly. Fixed by making `Wrap` take a `baseUrl` and convert any relative `BannerUrl` to absolute before embedding it — the dispatcher reuses the same `App:BaseUrl`-with-fallback logic already used for unsubscribe links, and the web controller uses the current request's own origin. **Prevention rule**: an in-app HTML preview is not sufficient to validate email HTML — anything served from a relative path needs a real test send to a real inbox before calling an email-composition feature done.
- Deferred to a later pass (not phase 1): the legacy two-column sidebar layout (agent photo/services/CTA buttons — needs those destinations to exist and be configurable first), style toggles, structured multi-article authoring (a `NewsLetterArticle` entity already exists but isn't wired into the current Create/Edit flow or dispatcher at all — worth investigating separately), and a default `BannerUrl` on admin-curated `NewsLetterTemplate` starter content.

### 33. Wire up multi-article authoring + add sidebar CTA buttons (done)
- **Investigated the "worth investigating separately" note from item 28**: `NewsLetterArticle` was not actually dead at the data layer — it had a real table, a full service (`GetArticlesAsync`/`AddArticleAsync`/`RemoveArticleAsync`), and a working "Add Article" form on `Edit.cshtml` that successfully saved articles. It was dead specifically at the *rendering* layer: `NewsletterHtmlComposer.Wrap` (the one function shared by send, Preview, and Test Send) never took an `articles` parameter, so anything an agent added there silently never appeared in the real wrapped email — Preview.cshtml worked around this by listing articles separately, unstyled, below the actual wrapped-email preview, which is also why the gap wasn't obvious from using the feature normally.
- `Wrap` now accepts `IEnumerable<NewsLetterArticle>? articles` and `IEnumerable<NewsLetterCta>? sidebarCtas` (both optional, default null/empty — zero-CTA newsletters render byte-identical to before). Each article renders as its own card (optional image, title, rich-text body) below the newsletter body, in `SortOrder`. All three `Wrap` call sites (`NewsLetterDispatcher.DispatchSendAsync`, `NewsletterController.Preview`, `NewsletterController.SendTest`) updated to fetch and pass both.
- New **Sidebar CTAs**: a small `List<NewsLetterCta>` (`Id`/`Label`/`Url`/`SortOrder`), JSON-serialized on a new nullable `NewsLetter.SidebarCtasJson` column (schema-repaired both apps, same `EnsureTableColumnAsync` mechanism as every other additive column this session) — same list-of-links shape and Add/Delete-with-full-postback UX as `WebsiteHeaderSettings.CustomLinks`, not a new pattern. When a newsletter has at least one CTA, `Wrap` renders a genuine two-column email layout (nested table, ~380px main column + ~150px sidebar, Outlook-safe) with the CTAs as accent-colored stacked buttons; with zero CTAs the layout is untouched single-column.
- **Two real gaps found and fixed while wiring this up, not left in place**: (1) `AddArticle` bound `NewsLetterId` directly from the POST body with no check that it belonged to the calling agent — a cross-tenant IDOR letting any agent attach an article to another agent's newsletter. Fixed with the same ownership-check pattern used everywhere else in the controller. (2) Newly-added articles always saved with `SortOrder = 0` (the form never set it), making display order effectively arbitrary once more than one existed — fixed by auto-assigning `SortOrder` from the current article count, matching the `CustomLinks.Count` pattern already used for nav links. Also added the missing `DeleteArticle` action (the service method existed; nothing called it) and an `ImageUrl` field to the Add Article form (the entity had it; the form never exposed it).
- Verified end-to-end in the local dev browser, not by reading code: added a real article (with image URL) and a real sidebar CTA to a dev newsletter through the actual Edit UI, confirmed both rendered correctly in the wrapped Preview (article card below body, two-column layout with the CTA button), deleted the CTA and confirmed the layout gracefully reverted to single-column, deleted the article via the real `DeleteArticle` endpoint (bypassing only the browser's native `confirm()` dialog, not the server logic) and confirmed removal in the database each step.

### 34. Add a Maps website block (done)
- New `WebsiteBlockTypes.Maps` block, addable to any page like any other block (reorder, show/hide, all for free from the existing block system) — the user specifically asked for it to work well on Contact Us, so `Maps` was also added to the `"contact"` starter-page preset alongside Hero/ContactForm.
- **Map source decision**: asked the user rather than assuming — chose Google's plain no-API-key embed URL (`maps.google.com/maps?q=...&output=embed`) over the officially-documented Maps Embed API, since the latter requires IPRO to create and manage a Google Cloud API key with a metered free tier, a real ongoing-cost/setup tradeoff for a feature meant to work with zero friction for every agent.
- **Address**: new `WebsiteMapSettings` (`Address`, `Height`) — defaults to the agent's own business address (`AgentUser.GetSingleLineAddress()`, a new single-line join added alongside the existing multi-line `GetFormattedAddressLines()` already used by the Agent Info block) with an optional per-block override for showing a different location. Confirmed live that an agent with no address at all doesn't produce a broken block — `Country` defaults to `"Canada"`, so the embed gracefully degrades to a country-level map rather than erroring or vanishing.
- **Size**: `Height` — Compact/Standard/Tall (260/380/520px), same three-tier convention as the Hero block's existing Banner Height field. **Position**: reused the existing `LayoutVariant` mechanism (`WebsiteBlockLayoutVariants.Maps`) for Full width vs. Narrow (centered, max 640px) — placement within the page (which page, which order) was already fully solved by the pre-existing add-anywhere/reorder-anywhere block system, so no new mechanism was needed for that half of the ask.
- One shared `WebsiteMapSettings.BuildEmbedUrl()` used identically by all 3 public templates to avoid triplicating URL-building logic (later changed from a static free-text-address method to an instance method over cached coordinates — see correction below).
- Verified end-to-end in the local dev browser: added a real Maps block to a Contact page, confirmed the default (profile-address) embed URL and Tall height, confirmed a custom address override and Narrow layout both take effect (checked the actual rendered `<iframe src>` and wrapper style, not just that the form saved), spot-checked the Classic template's structurally different markup separately since it has no shared `-inner` wrapper class to lean on, and confirmed the no-address edge case degrades gracefully. All temporary test data (test block, temporarily-blanked agent address fields, temporarily-switched template) reverted after.

**Correction, same day**: the user reported the map wasn't showing on a real production site. The verification above had a real gap — it checked that the `<iframe src>` was well-formed, never that Google actually rendered visible content inside it. Real testing (navigating to the exact embed URL directly) showed Google's no-key `output=embed` URL now redirects to their real Embed API endpoint and returns a blank frame without a valid API key — it doesn't reliably work today, contrary to the assumption behind the original decision. Asked the user again given this new information; chose OpenStreetMap (genuinely free, no account, confirmed working via direct navigation showing a real rendered map) over paying the Google API-key setup cost.

**This required a real rework, not a one-line swap**: unlike Google's URL, OSM's embed takes a bounding box + marker coordinates, not a free-text address — it does not geocode server-side per request. New `IGeocodingService`/`NominatimGeocodingService` (`IPRO.Business`) calls OpenStreetMap's free Nominatim API to resolve an address into lat/lon + a bounding box **once, at block-save time**, cached on `WebsiteMapSettings` (`Latitude`/`Longitude`/`BboxSouth`/`BboxNorth`/`BboxWest`/`BboxEast`/`GeocodedAddress`) — deliberately never re-geocoded on a public page view (Nominatim's usage policy caps requests at ~1/sec and expects non-automated use; a live per-visitor geocode call would both violate that and tie every visitor's page load to a third party's rate limit). Re-geocoding on save is itself skipped when the effective address hasn't changed since the last successful geocode, comparing against the newly-added `GeocodedAddress` field. `WebsiteMapSettings.BuildEmbedUrl()` became an instance method returning `null` when ungeocoded, so a failed or pending geocode gracefully omits the block rather than rendering a broken frame.
- **Existing Maps blocks saved before this fix** (i.e., the one on the real site that surfaced the bug) have no cached coordinates and won't render until the agent re-opens that block in the editor and clicks Save Block once (no changes needed) — this re-triggers geocoding under the new system. Not backfilled automatically; at the time of this fix only one real block existed.
- Verified the URL-construction and geocoding pipeline is genuinely correct by direct navigation (not just an in-app check): the exact generated OSM embed URL, opened directly, renders a real interactive map at the correct location. Could not fully confirm the embedded `<iframe>` case renders visually inside this session's own browser automation tool — a cloned iframe with a debug border didn't appear in screenshots despite correct in-DOM position and no console/frame-blocking errors, pointing at a cross-origin-iframe screenshot-capture limitation in the tool itself rather than the feature. Told the user this directly rather than claiming full visual confirmation; asked them to check the real production page as the authoritative test, same as how the original bug was actually caught.

**Second correction, same day — the real root cause**: the OSM embed *also* didn't render for the user on the real production page, with the identical blank-box symptom. Rather than assume a fourth theory, asked the user to check their browser's own DevTools console — which showed Microsoft Edge's own message: *"This content is blocked. Contact the site owner to fix the issue."* This is Edge's **Tracking Prevention** feature (set to Strict) blocking the third-party embedded map iframe as a tracker — not a bug in the URL, the key, or the geocoding, all three of which had already been independently confirmed correct by direct navigation. Edge's default level is Balanced, not Strict, so this doesn't affect most visitors; it's client-side browser behavior outside any website's control, and would affect any third-party map/video/social embed identically, not something specific to this feature.
- Given free/no-key approaches were already shown independently unreliable (Google's no-key trick returns blank without a valid key; OSM's policy blocks heavy third-party commercial embedding) — switched to the officially documented **Google Maps Embed API with a real key**, the one approach actually designed and supported for exactly this use case. Removed all the OSM-driven complexity added in the correction above (`IGeocodingService`/`NominatimGeocodingService`, cached `Latitude`/`Longitude`/bounding-box fields) — Google's API takes a free-text address directly (`.../maps/embed/v1/place?key=...&q=address`) and geocodes it live on their end per request, so none of that pre-geocoding machinery is needed; `WebsiteMapSettings` is back to just `Address`/`Height`, resolved at render time exactly like the very first version.
- **Key storage**: the real key lives only in Azure App Settings (`GoogleMaps__EmbedApiKey` on `ipro-prod-web`) — never committed to the repo. `appsettings.json` carries only a `YOUR_GOOGLE_MAPS_EMBED_API_KEY` placeholder, same convention as every other secret in this app (SendGrid, Anthropic, PayPal). Read into the 3 public templates via `@inject IConfiguration`.
- **Deliberately did not restrict the key to specific HTTP referrers** — the user caught that a referrer allowlist (e.g. `*.247advisers.com/*`) would silently break the map for any agent using their own custom domain (`AgentUser.DomainName`, a real, self-serve, already-supported feature — agents add new custom domains themselves with nothing on IPRO's end to update an allowlist against). Low-risk to leave unrestricted specifically because Maps Embed API has no usage fee at all (confirmed on Google's own pricing page) — the only protection actually applied is restricting the key to the Maps Embed API itself, so it can't be used for any other, potentially metered, Google service even if it leaked.
- **Net effect**: real visitors on default browser settings (the vast majority) will now see the map correctly; visitors with Strict tracking prevention or an aggressive ad-blocker may still see a blocked placeholder regardless of map provider — normal, expected, and not fixable from the website's side, same as it would be for any other site embedding a third-party map.

### 29. Add agent photo (phase 1 of Wix-style templating) (done)
- New `AgentUser.PhotoUrl` (nullable), schema-repaired in both apps. New `UploadPhoto`/`RemovePhoto` actions on `AccountController`, modeled explicitly on `WebsitePagesController.UploadImage`'s validated pattern (extension+content-type allow-list, `HasValidImageSignatureAsync` file-signature check, 8MB limit) — not the unvalidated `AgentWebsite.LogoUrl` upload path. Stored via `IBlobStorageService` in a new public `agent-photos` container; replacing or removing a photo deletes the old blob.
- Upload UI added to the top of the Agent Profile page (`Views/Account/Profile.cshtml`) as its own small form (separate POST from the main profile-save form, since it's the only file upload on that page) with a live preview and a Remove button.
- Wired into two places, both using the same `ToAbsoluteUrl` treatment the newsletter banner bug (item 28) already proved necessary: `NewsletterHtmlComposer.Wrap`'s footer (small circular photo next to the agent's contact info, only when set) and the public website's Contact block on all three real templates (`_ModernManagedPage.cshtml`, `_ClassicManagedPage.cshtml`, `_EditorialManagedPage.cshtml`), each styled to match that template's own visual language, graceful (nothing rendered) when no photo is set.
- Verified locally end-to-end: uploaded a real image as the `PollTester` dev agent, confirmed the blob URL persisted on `AgentUser.PhotoUrl`, confirmed the correct absolute `<img src>` was emitted on the Profile page, the Newsletter Preview footer, and the public Contact page (fetched with the agent's custom-domain `Host` header), and confirmed Remove Photo clears the column and deletes the blob.
- Not gated behind any package tier — a free profile field, same reasoning as the portal accent-color picker.
- **Real bug caught by the user during live testing the next morning, not by local verification**: the Upload Photo form was nested inside the page's main Profile-save form (invalid HTML — browsers silently drop the inner `<form>` tag), so clicking Upload Photo actually submitted the outer Profile form instead, discarding the file with no error. Local testing had missed it because it verified the controller action directly via curl rather than driving the actual rendered page. Fixed by moving both small forms outside the outer form and binding their controls back into the Photo card via the HTML5 `form=""` attribute; re-verified this time by attaching a real `File` to the input in an actual browser and clicking through. Full writeup: `DOCS/09_TROUBLESHOOTING.md` → "Agent Photo Upload Silently Did Nothing."
- Phases 2-4 of the Wix-style templating design below (more `LayoutVariant` options, wiring up the dead `NewsLetterArticle` entity, curated per-block style choices) remain designed but not started — no go-ahead yet.

### 30. Add more website block layout variants (phase 2 of Wix-style templating) (done)
- Extended `WebsiteBlockLayoutVariants` (purely additive, same mechanism Services/CallToAction already proved out) with three more block types: **Text** (`image-left`/`image-right`, only takes effect on blocks with an image — implemented via CSS `order:-1` on the image, the same technique Hero's `image-left` layout already uses; in the Editorial template this explicit choice overrides that template's automatic left/right zigzag, which stays as the default when no variant is set), **Review Badge** (`badge`/`banner` — banner reuses each template's existing Call to Action banner markup/CSS so it looks native, not bolted on), and **Testimonial Submission Form** (`list`/`grid` — grid arranges the approved testimonials shown below the form as 2-column cards instead of a stacked list).
- Same Layout dropdown UI on the block edit form (`Views/WebsitePages/Edit.cshtml`) agents already use for Services/CallToAction, just with type-specific option lists. No new architecture, no new tables — filling in a mechanism that existed for 2 of 10 block types with 3 more.
- Verified all six new variants (3 block types × 2 options) render correctly on all 3 public templates by seeding real dev blocks/testimonials and fetching each page with the agent's real custom-domain `Host` header, temporarily switching the same dev website between all 3 templates to check each one, then cleaning up the test data afterward.
- Phase 3 (wire up the dead `NewsLetterArticle` entity) and phase 4 (curated per-block style choices) remain designed but not started.

### 31. Redesign the Editorial template + add an Agent Info Card block (done)
- **Not part of the original 4-phase Wix-style templating design (items 29-30)** — a new ask after using those two features: the user's blunt verdict was that the website builder is "rich in function but presentation is low mark," the actual weakest point of the product. Investigated by actually looking at all 3 live templates via the SuperAdmin "preview on an agent's real site" tool (`WebsiteTemplates/Edit` → **Preview on an agent's real site**), not just reading the code — confirmed the hero's "no image" fallback (a flat color block or a lone icon) was the single biggest issue, followed by every section below it looking identical (white card, drop shadow, repeat).
- Agreed approach: redesign one flagship template first (**Editorial**, since it already had the strongest bones — serif type, real whitespace, shadow-free bordered cards), get sign-off, then roll the same visual language to Modern and Classic and eventually the newsletter wrapper. Not done yet: only Editorial has the full redesign; Modern and Classic are unchanged pending approval of this direction.
- **New block, ships everywhere now regardless of the redesign**: `AgentInfo` — a movable card block showing the agent's photo, name, designation/company, full mailing address, phone, and email, each individually toggleable (`WebsiteAgentInfoSettings`, same pattern as `WebsiteReviewSettings`). Directly answers the user's flexibility ask ("not full Wix-style, but a proper block that can be moved around... photo, name, full address") — the existing block system (add / reorder via `WebsitePagesController.MoveBlock` / show-hide) already provides exactly this shape of flexibility without a drag-and-drop canvas. Renders with a full "editorial byline" treatment on Editorial; on Modern and Classic it uses a plain version of the existing `.mp-contact-card`/`.cp-contact-card` styling (same as the Review Badge block) rather than being left unstyled — an unrecognized block type would otherwise fall into each template's generic Text catch-all and silently drop the photo/address (they live in `SettingsJson`, which that catch-all never reads).
- **Editorial redesign, three changes, all scoped to `_EditorialManagedPage.cshtml`** (nothing else touched): (1) the hero's "no image" fallback is now a soft diagonal gradient with a subtle line texture and an oversized, low-opacity serif monogram of the agent's first initial — ties personally to the agent, needs no upload, and fits the editorial/print identity, instead of reading as an unfinished placeholder. (2) Section rhythm: Services, Reviews, and Testimonials sections now alternate to the `--site-theme-soft` tint (a token that already existed but was underused) instead of every section sitting on the same background. (3) All per-agent design overrides (accent color, font, section spacing, etc.) still flow through unchanged — nothing hardcoded, verified by switching the test agent's accent color and confirming the hero gradient/monogram picked it up.
- Verified end-to-end locally: added a real Agent Info Card via the agent portal, toggled each show/hide field, confirmed correct rendering on all 3 templates via the SuperAdmin preview tool, confirmed the hero fallback and image-set path both render correctly, confirmed the accent-color override still works.

### 32. Add an in-editor page Preview (done)
- **The real root cause behind item 31 landing flat**: after shipping the Editorial redesign, the user's reaction was that it wasn't a meaningful enough improvement to justify rolling out further — and asked directly whether agents actually have satisfactory flexibility to shape their site. Investigated the actual editing experience end-to-end (not just the rendering code) and found the real problem: **agents edit completely blind**. `WebsitePages/Edit` had zero preview of any kind — plain forms, a full-page-reload Save, and the *only* Preview button in the entire product lived on an unrelated screen (`My Website`, not the page editor) and only showed pages where the page-level `IsPublished` toggle was already on. The actual loop was: guess → save → leave the editor → hunt down a different menu → maybe see it, if that page happened to already be published → go back → guess again. No amount of template CSS work fixes that.
- **Fix**: a real **Preview** button, now on both `WebsitePages/Edit` (top of the page) and the Pages list (per row), opening the page in a new tab exactly as currently saved — regardless of the page's own `Published` toggle or the site's overall publish state. New `WebsitePagesController.Preview(int id)` action, authorized via the same `OwnedPages()` ownership check every other action in that controller already uses, builds the same `PublicWebsiteViewModel` the real public site uses and renders `PublicWebsite/Index.cshtml` directly (so it's pixel-identical to production, not a separate mocked-up preview), sets `ViewBag.IsTemplatePreview = true` for the existing preview banner, and deliberately does not call `TrackPageViewAsync` so previewing your own draft never inflates your own analytics.
- Extracted the poll-results-aggregation logic (previously a private method on `PublicWebsiteController`) into a shared static `Infrastructure/PollResultsBuilder.cs` so both the real public route and the new agent preview route stay byte-for-byte consistent instead of two copies drifting apart.
- **Security boundary, verified explicitly**: marked a real page as Draft with distinctive test content, confirmed the real public URL for that page returns the generic "not published" view (content never leaks to real visitors), then confirmed the same content renders correctly via the new Preview action while logged in as the owning agent. `OwnedPages()` means an agent can only ever preview their own pages.
- This is the actual highest-leverage fix for "does the flexibility feel satisfactory," ahead of any further template CSS work — it closes the feedback loop between typing and seeing, which no amount of prettier defaults can substitute for.
- **Correction, same day**: the design above was incomplete. `Preview` only shows what's already *saved* — but for an already-published page (the normal case, not the exception), `UpdateBlock` writes straight to the same rows the live public site reads, so **Save Block on a published page goes live immediately**. There is no staging step at the block level. That meant Preview added zero safety for the most common editing scenario: an agent checking a tweak to a page that's already live gets no protection at all from "save first, then look" — by the time they can look, it's already live. A user caught this directly by testing it.
  - **Real fix**: new `WebsitePagesController.PreviewUnsaved` action (POST) — takes the same form fields as `UpdateBlock` but never calls `SaveChangesAsync`; it applies the submitted values to an `AsNoTracking` in-memory copy of the block and renders the identical `PublicWebsite/Index.cshtml` view other Preview paths use. Genuinely zero database writes, verified by checking the row was unchanged after calling it. Each block now has two buttons: **Preview without Saving** (safe, always available, shows unsaved form state) and **Save Block** (commits — goes live immediately if the page is published). The earlier "Save & Preview" button is gone; it was the wrong shape for the actual need.
  - Refactored `UpdateBlock`'s field-mapping logic into a shared `ApplyBlockFieldsAsync` helper and `Preview`'s view-model-building logic into `BuildPreviewViewModelAsync`, so `UpdateBlock`, `Preview`, and `PreviewUnsaved` all stay byte-for-byte consistent with each other instead of three near-duplicate copies drifting apart.
  - The unsaved preview renders with a distinct blue banner ("Unsaved preview — nothing has been saved yet") instead of the existing yellow saved-state banner, so the two modes can never be visually confused with each other. The editor page itself now shows a warning banner when the page being edited is already published, spelling out that Save Block is not a safe/staging action there.
  - **Second bug found by the same user, same session**: a Poll Results block never showed in *either* preview mode, no matter what was clicked. Cause: the "hide results until 10 responses" anonymity rule (`PollResultsBuilder`, protects real visitors from seeing individually-identifiable results) was applied unconditionally — including when the agent was looking at their own authenticated preview, where it serves no purpose and just hides their own content from them. Fixed with an `isOwnerPreview` flag: `Preview`/`PreviewUnsaved` bypass the threshold, the real public site does not. Added an inline note ("Preview only — hidden on your live site until this poll reaches 10 responses") on all 3 templates so seeing it in preview but not on the live site doesn't look like a second bug.

### AI Assistant — where this could expand next
Items 1 (the "why" line, item 26) and 2 (social post drafting, item 27) are done. Remaining ideas from the original "AI-assisted business tools" list, in priority order for a future pass:
1. **Newsletter draft generation** — a "Draft with AI" button in the Newsletter composer: topic in, subject + HTML body out, agent edits before sending.
2. **Website copy generation by vertical** — ties into the "Vertical starter packs" idea below.
3. **Client activity summarization** (Client Details page) — a higher-risk tier: client notes/timeline would leave the system in the API call, so this needs a PII-handling/redaction and consent decision made deliberately before writing any code, not bolted on after.
4. **Drip campaign generation** — a full multi-step sequence from one prompt, bigger scope than any single-shot draft above.
5. **Weekly/portfolio-wide digest** — a broader version of today's daily per-agent digest, e.g. "across your whole book, here's who needs attention this week."

The throughline for all six: AI drafts or suggests, the agent always reviews and acts — the same "never auto-send" instinct already used throughout IPRO (testimonial approval queue, Draft-only recurring invoices, agent-triggered newsletter/poll sends).

## Bigger Product Ideas

### Agent invoicing and billing system (v1 done — see item 13 below; automatic overdue reminders — see item 23 above)
The core of this shipped: estimates/invoices with line items, per-client tax, a no-login client view/approve/pay link, manual paid-tracking, recurring auto-drafted invoices, CSV export, and automatic overdue reminders. Still open for a future pass:
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

### SMS reminders (not built — vendor pricing researched 2026-07-20)
`PackageFeatureCodes.SmsReminder` already exists as a seeded package-feature checkbox ("Mobile SMS reminder", included for every package) but, like `FileUploadCapacity`/`SocialMediaIntegration` before it, has zero real functionality behind it today.

Two scope options were discussed, mirroring the Social Posts decision:
- **Composer-only** (no vendor, no cost): auto-draft a short SMS-ready reminder text for upcoming follow-ups/appointments with a character count; agent copies and sends it themselves. Ships immediately, same pattern as the Social Post composer.
- **Real Twilio-sent SMS**: IPRO actually sends the text. Requires the user to set up a Twilio account first. Researched current (2026-07) Twilio pricing so the vendor-cost decision can be made with real numbers instead of guessing:
  - Twilio has no monthly platform fee — pure pay-as-you-go. **$0.0083 per SMS segment** sent or received (US).
  - Phone number rental: **local 10DLC number ~$1.15/month** vs. **toll-free ~$2.15/month**. Toll-free's old advantage (skip A2P 10DLC registration) went away January 1, 2026 — toll-free numbers now require the same business-verification paperwork (EIN/business registration number), so there's no longer a reason to pay more for toll-free.
  - **A2P 10DLC registration** (required to send from a 10DLC number to US mobile carriers) — recommended tier for IPRO's actual use case (transactional appointment/follow-up reminders, not marketing) is **Low Volume Standard**: ~$24.50 one-time brand+campaign registration, ~$1.50–$10/month recurring campaign fee (vs. ~$71.90 one-time + higher monthly for High Volume Standard, which IPRO doesn't need at pilot scale).
  - **Realistic starting cost**: ~$25 one-time + ~$5–12/month fixed + $0.0083/message. Cheap enough to pilot.
  - **IPRO-specific wrinkle worth remembering**: IPRO would be sending on behalf of *many different agents' businesses*, not one business's own messages. For a pilot, registering one Twilio brand as IPRO itself for uniform "appointment reminder" traffic should work. If this scales and agents want personalized "from [Agent Name]" sender identity, Twilio's ISV/reseller model (sub-brands per agent/agency under one account) is the correct long-term path — more setup, not needed to start.
- **Status**: decision paused here — no scope chosen yet, no Twilio account exists. Revisit this section (not memory) for the pricing numbers when ready to decide; they're current as of 2026-07-20 but Twilio/TCR fees are set by external parties and can change.

### Real estate vertical: IDX listings (not scoped yet)
Real estate agents specifically need to display MLS listings on their site (IDX), which none of the other verticals require. Researched 2026-07-19; not yet designed or built. Costs show up at two separate layers:
- **Raw MLS data feed (platform/vendor side)**: no single national feed exists — 500+ regional MLSs each control their own data. **MLS Grid** is the most practical aggregator for a multi-region SaaS (~180+ MLSs under one vendor contract, standardized RESO Web API), priced at ~$250/month per feed + $20/month per participating agent license. **Bridge Interactive (Zillow)** charges vendors nothing directly, but the underlying MLS can still charge its own fee, and IPRO would still bear RESO certification, legal review, and field-mapping engineering either way.
- **Turnkey IDX widgets (agent side)**: providers like iHomefinder ($85–$500+/month) or IDX Broker sell a ready-made search widget an individual agent already embeds on their own site today, with zero MLS compliance burden on IPRO.

**Recommendation**: build a "Listings" content block (same pattern as the existing Services/Testimonials blocks) that lets an agent paste in their existing iHomefinder/IDX Broker embed code — fast, low-risk MVP with no vendor agreement or RESO certification needed. Only pursue a native MLS Grid integration later, once real estate signups justify the vendor contract and compliance overhead; that path would let IDX listings live inside IPRO's own package tiers instead of requiring a separate per-agent third-party subscription.

### AI-assisted business tools (daily assistant v1 done — see item 24 above; LLM reason line + usage tracking done — see item 26 above; social post drafting done — see item 27 above)
"Suggest follow-ups"/"recommend next best action" shipped as the AI Daily Assistant dashboard widget (item 24), its first real LLM call (the "why" line, plus SuperAdmin cost tracking) shipped as item 26, and AI-drafted social posts shipped as item 27. The rest of this original list — generate website copy by vertical, generate newsletter drafts, generate drip campaigns, summarize client activity — remains open; see "AI Assistant — where this could expand next" under item 27 for the prioritized order and the reasoning behind it.

### Client portal (v1 done — see item 14 above; real appointment scheduling — see item 15; campaign preferences — see item 23)
Secure login, messages, two-way documents, self-service "My Information," a real appointment-scheduling flow (not just a request queue), invoices, and campaign/newsletter preferences all shipped. Still open for a future pass:
- Payments taken directly inside the portal (today's Pay Now button still links out to the agent's own external payment link, same as the standalone invoicing feature) — deferred pending a decision on which payment processor to integrate.

### In-portal payments (not started — recommendation recorded 2026-07-23, decision still pending)

**Why this is a real fork, not just an implementation detail**: `AgentUser.DefaultPaymentLink` today is a free-text URL (placeholder `paypal.me/yourname`) — agents already point it at whatever they personally use (PayPal, Venmo, Square, a Stripe Payment Link, etc.), and the Pay Now button just opens it in a new tab. Genuine in-portal payment collection (card fields rendered inside IPRO itself, no redirect) means picking one specific processor to integrate via API/SDK, and — since IPRO is a platform where many separate agents each need to receive their *own* funds, not IPRO itself — it specifically needs that processor's multi-party/marketplace product, not a plain single-merchant integration.

**Recommendation**: **Stripe Connect**. It's built for exactly this shape (a platform with many separate merchants); each agent does a one-time "Connect your Stripe account" OAuth flow instead of IPRO ever touching or holding their money; card collection uses Stripe Elements/Checkout so the PCI compliance burden stays with Stripe, not IPRO. Square for Platforms is the fallback option if there's an existing Square relationship worth building on instead.

**What's actually blocking this**: a Stripe platform account with Connect enabled, and a decision to commit to it — not an engineering unknown. When ready, this needs: (1) an agent-facing "Connect your Stripe account" flow in Profile/My Website settings, replacing or supplementing `DefaultPaymentLink`, (2) a payment-collection UI on the invoice/portal Pay Now page using Stripe Elements, (3) webhook handling to confirm payment and update `ClientInvoice` status, (4) test-mode end-to-end verification before any live keys are involved.

**Status**: design recommendation only — no code written, no credentials exist yet. Revisit this section when there's a Stripe (or alternative) merchant account to build against.

### Google Calendar sync (done — verified live end-to-end 2026-07-19)
Full two-way sync between an agent's own Google Calendar and the Agent Portal Calendar, opt-in per agent:
- Connect/Disconnect flow (OAuth) from a new Calendar Source settings panel on My Profile; gated by a new togglable `GoogleCalendarSync` package feature Super Admin can enable on any package (defaults to off everywhere).
- IPRO follow-ups push to the agent's connected Google Calendar automatically; events created or edited directly in Google sync back into the linked follow-up (a Hangfire job polling every ~15 minutes, not realtime push). Deleting a follow-up in IPRO removes the Google event immediately rather than waiting for the next sync cycle.
- If a linked Google event is deleted directly in Google, IPRO unlinks the follow-up rather than deleting it — a follow-up is CRM history, not just a calendar block.
- Non-client Google events (personal appointments, other meetings) show on the Agent Portal Calendar for context, distinguished from client follow-ups with a Google icon, but aren't tied to any client record and can't be marked complete.
- Live setup is done: Google Cloud OAuth client provisioned, Calendar API enabled, the `.../auth/calendar` scope registered on the OAuth consent screen's Data Access page (a separate step from creating the OAuth client — missing this caused a "not syncing" incident where the sync job ran cleanly but every Google API call failed; see `09_TROUBLESHOOTING.md`), and both directions (IPRO → Google, Google → IPRO) confirmed working live.
- Still open: the OAuth consent screen is in Testing mode (max 100 manually-added test users) and the Google Cloud project is still owned by a personal account rather than a dedicated one — see the production-readiness notes for the path to full public availability (Google app verification/review) and account ownership handoff.

### Branded newsletter wrapper (done — see item 28 above)

The user shared a legacy version's newsletter templates (`index.htm` through `index6.htm`, a 2009-era set) and a screen recording of that legacy editor. The rendered templates were genuinely well-composed — branded header banner, colored title bar, styled sections, sidebar, proper footer — and comparing that against the current dispatcher code confirmed today's newsletters shipped with zero wrapping beyond an unsubscribe line. Watching the actual legacy editor flow revealed the polish came entirely from a fixed wrapper template around content that was authored *more* primitively than IPRO's current rich editor (a plain textarea, no formatting at all) — so the fix was additive: add the wrapper, leave the authoring experience alone. Full design and what shipped vs. what's deliberately deferred (sidebar CTAs, structured multi-article authoring) are in item 28.

### Reputation and social media
- Collect and approve testimonials (done 2026-07-20 — see `DOCS/15_TESTIMONIALS.md`: open public submission form + agent review queue; targeted per-client request links done 2026-07-20, see item 23 above). Star ratings and photo upload were considered but remain deferred.
- Publish social posts (done — see item 18: draft/track composer; live auto-publishing directly to a platform is still a separate, larger future item).
- Reuse newsletter/page content as social content.
- Campaign calendar for email and social.

### Broker/team/white-label model (not built — designed 2026-07-22)

Two genuinely different projects hide under "white-labeling," and the choice matters more than any implementation detail:

- **(A) Cosmetic white-label**: a broker's logo/colors/domain wrap around the existing multi-tenant platform; agents still relate to IPRO for billing and support. Roughly a 2-3 week slice, almost entirely additive — reuses patterns already shipped (the per-agent accent-color mechanism, the host-header-based custom-domain routing already built for agent public websites).
- **(B) Full reseller model**: the broker owns billing, support, and the agent relationship; IPRO becomes invisible infrastructure underneath. A much bigger, multi-month effort — new billing/entitlement hierarchy, broker-level support routing, revenue-share accounting.

**Decision**: build (A) first as a real, shippable slice, designed so nothing in it has to be thrown away if a real broker later justifies (B).

**(A) data model** — one new table, two new columns, nothing else:
```csharp
public class Broker
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string LogoUrl { get; set; }
    public string AccentColor { get; set; } = "#1457d9";
    public string? CustomDomain { get; set; }   // e.g. team.acmefinancial.com
    public bool EnforceBranding { get; set; }   // if true, agents under this broker can't override the accent color themselves
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}
```
`AgentUser` gains `BrokerId` (nullable FK — null means today's entire existing standalone-agent behavior, completely untouched) and `IsBrokerAdmin` (marks which agent(s) under a broker can manage that broker's team/branding).

**(A) phase-1 scope**:
1. `Broker` entity + schema repair, both apps, standard convention (raw SQL `CREATE TABLE IF NOT EXISTS`, not an EF migration).
2. New `BrokerController` **inside `IPRO.Web`** (not a third app) — `Dashboard` (list this broker's agents), `Branding` (logo/accent/domain), gated by `IsBrokerAdmin`.
3. Agent Portal `_Layout.cshtml`: when `AgentUser.BrokerId` is set, swap the "IPRO Agent Portal" wordmark for the broker's logo and default `--portal-accent` to the broker's color — same CSS-variable mechanism the per-agent accent picker already uses. If `EnforceBranding` is on, hide that agent's own color-swatch picker.
4. Custom domain for the broker's own management view — reuse the exact `Request.Host.Host` resolution already built for agent public websites; `team.acmefinancial.com` routes to the Broker Dashboard instead of the standard agent login.
5. SuperAdmin-only **Brokers** page — CRUD brokers, assign agents to one. Deliberately not self-serve in phase 1: onboarding a broker is a manual, high-touch relationship, not something a broker signs themselves up for yet.

**The one real gap phase 1 won't close**: transactional emails (invoices, welcome emails, reminders) are hardcoded to send as "IPRO Advisers" via one shared SendGrid sender identity (`EmailSettings`). True per-broker email branding needs per-broker sender domain authentication in SendGrid (DNS records, verified domains) — real infrastructure work, not a code change. Scoped out of phase 1; a broker's agents and public sites would look fully branded, but system emails would still show IPRO until this is built.

**Keeping the door open to (B)**: `Broker` is exactly the tenant record a reseller model needs — (B) would add `Broker.RevenueSharePercent`, a broker-scoped billing rollup, and broker-routed support tickets *on top of* this table, not instead of it. One decision worth pinning down before writing any code, even for phase 1: confirm `EnforceBranding` and SuperAdmin-only broker creation are the right defaults — cheap to build either way, but shapes the first real broker conversation.

**Status**: designed, not started. Revisit when a specific broker relationship makes this worth prioritizing.

### More flexible, Wix-style website and newsletter templating (not built — designed 2026-07-22)

**Where this came from**: after the branded newsletter wrapper shipped (item 28), the user's actual expectation was a real configurable *system* — agent photo, choice of what's displayed, real layout flexibility — not one fixed design with a banner-image and accent-color as the only knobs. They connected this to a standing complaint about the website builder too, citing agent feedback requesting "Wix-style" templating. Investigated the current ceiling directly rather than assuming:

- **No agent photo/headshot concept exists anywhere in the codebase** — not on `AgentUser`, not in any website block, not in the newsletter wrapper. The closest thing is `AgentWebsite.LogoUrl` (one company logo, header-only, uploaded with *no* content-type/signature validation — don't copy that upload path).
- **Website block flexibility is real but shallow**: 10 block types exist, but only **Services** (cards/list/icons) and **CallToAction** (banner/card/split) have any `LayoutVariant` option. The other 8 block types render exactly one fixed shape each, no exceptions. Hero has its own separate 5-option layout system, but it lives in `SettingsJson`, not the shared `LayoutVariant` mechanism.
- **Zero per-block style overrides exist, anywhere.** Every color/font/spacing choice is 100% template-wide (`AgentWebsite.ThemeColor`/`FontFamilyOverride`/etc., emitted as CSS custom properties every block's CSS references). No block type's `SettingsJson` carries a color, font, or spacing field — not even the ones that already use `SettingsJson` for structural settings (Hero, Reviews, PollResults, LeadMagnet). This is the real architectural ceiling, not a UI gap.

**The honest tradeoff**: true Wix-style templating — a free-form drag/drop canvas, arbitrary per-element styling, live WYSIWYG editing — is not a feature to add on top of the current block/template system, it's a different piece of engineering than what exists today, realistically months of work on its own as a dedicated page-builder product. That's not the right first move. What's achievable without a rebuild is making the *existing* block/template architecture noticeably more flexible — more layout variants per block type (reusing the exact mechanism Services/CallToAction already prove out), a real agent photo, and a narrow, curated set of per-block style choices (not an open color picker — a bounded set of pre-vetted combinations, the same philosophy Hero's `OverlayStrength`/`Layout` dropdowns already use) so agents get "feels configurable" without the risk of an agent making an ugly, off-brand page.

**Proposed phasing, smallest/most shared value first**:
1. **Agent photo** (`AgentUser.PhotoUrl`, new upload action modeled on `WebsitePagesController.UploadImage` — public, 8MB, image-type+signature validated — not the unvalidated Logo path). Unblocks two things at once: a headshot in the website's Contact/About area, and a headshot in the newsletter footer next to the agent's contact info (the newsletter wrapper — item 28 — already has a footer; this is a small, additive change to `NewsletterHtmlComposer.Wrap`). **Done — see item 29.**
2. **More `LayoutVariant` options on more block types** — e.g. Testimonials (list/carousel/grid), Text (image-left/right, matching Hero's existing pattern), Reviews (badge/banner). Purely additive to `WebsiteBlockLayoutVariants` and each template's existing per-type rendering branch — no new architecture, just filling in a mechanism that's already proven for 2 of 10 block types. **Done — see item 30** (shipped Text image-left/right, Reviews badge/banner, Testimonials list/grid; a carousel option was skipped as it would need new JS, not just markup/CSS).
3. **Wire up the newsletter's dead `NewsLetterArticle` entity** (flagged as unused in item 28's design — `Title`/`Content`/`ImageUrl`/`SortOrder` already exist in the schema and in the Edit page's UI, but the dispatcher never reads them) as the newsletter's actual content-section system, giving newsletters the same "add/reorder sections" flexibility websites already have, inside the wrapper chrome that already ships.
4. **A narrow set of curated per-block style choices** (e.g. background: white/light-gray/accent-tint; alignment: left/center) added to `SettingsJson` for the block types agents ask about most — only after 1-3 ship and it's clear which blocks actually need it.

**Deliberately not proposed**: a free-form drag/drop canvas or arbitrary per-block CSS/color picker. Both are real products in their own right, not incremental features, and an unbounded color picker risks agents producing off-brand or illegible pages — the same reasoning that shaped every template-wide style choice already in the system (curated dropdowns, not raw CSS).

**Status**: phase 1 (agent photo, item 29) and phase 2 (more layout variants, item 30) shipped. Phases 3-4 below are **superseded** by the "Position + Theme" design that follows — grounded in the actual legacy system rather than invented from scratch.

### Website redesign, take 3: Position + Theme (designed 2026-07-23, supersedes phases 3-4 above)

**Where this came from**: items 29-32 shipped (agent photo, more layout variants, the Editorial redesign + Agent Info Card block, and the in-editor Preview fixes), and the user was still not satisfied — the presentation problem hadn't actually moved. Direct quote: *"We have done a lot but it has not been what I wanted. Keeping it simple and beautiful rich site."* Rather than propose another round of incremental dials, the user pointed at the **legacy system** (43 old "skins," plus a set of "Home page positions" reference documents) as the actual model to rebuild from — not to copy visually (the old skins are genuinely dated: wood-texture backgrounds, black/orange CSS gradients, fixed-width Bootstrap 3, 'Abel'/'PT Sans' fonts — confirmed by extracting and reading two real skin ZIPs, `45.zip` and `24.zip`), but structurally.

**What studying the legacy artifacts actually revealed** (3 real "Home page positions" wireframes reviewed as screenshots): the old "43 templates" were never 43 different layouts. They were a small, fixed set of **named content zones** — Top Menu, Banner, Logo, Picture, Agent Info, Side Menus, Main Body — combined with **one real structural variable**: which side the sidebar column (Logo/Picture/Agent Info/Side Menus) sits on relative to the main content column (Top Menu/Banner/Main Body) — left, right, or (implicitly) not present. The "skin" (color/font/texture) was a fully separate, independent layer on top. Two axes, not a combinatorial mess of unrelated dials.

**The useful realization**: almost every one of those legacy zones already exists as a real building block in IPRO_Modern today — Logo (`AgentWebsite.LogoUrl`), Banner (Hero block), Picture (`AgentUser.PhotoUrl`, item 29), Agent Info (the Agent Info Card block, item 31), Main Body (the content block stream), Top Menu (`_PublicNavigation.cshtml`, already has header style/logo position/custom link options). The only genuinely missing piece is a real, mirrorable **sidebar layout mode** — Classic Sidebar half-does this today but isn't a clean "Left / Right / None" toggle reusable across templates.

**The design, two independent axes, matching the legacy system's actual proven shape**:
1. **Position** (structural): `Sidebar Left` / `Sidebar Right` / `No Sidebar` (full width, current Modern/Editorial-style single column). Arranges the zones that already exist — nothing new to build content-wise, just a real structural toggle instead of Classic Sidebar being its own separate fixed template.
2. **Theme** (visual identity): a small curated gallery (6-8) of complete, named, hand-composed looks — accent color + font pairing + spacing + hero treatment + button style bundled as **one atomic choice**, not independent dials an agent assembles themselves. This directly replaces the old "phase 4: curated per-block style choices" idea above with something more holistic — the reasoning (from a design-fresh take, deliberately not looking at the old per-block-dial proposal before writing it) being that independently-chosen dials rarely harmonize, while a whole look designed as a unit always does. Picking a named theme should feel like a real creative choice to the agent, while actually being fully guardrailed — the same "guided, not open-ended" philosophy every other choice in this system already uses (curated dropdowns, not raw color pickers).
3. **Graceful zone collapse** (new requirement from this conversation): if an agent doesn't use a given zone — no Logo uploaded, no agent Photo, no Agent Info block added — that space must not sit there empty. The layout needs to reflow/collapse to fill the gap, not just hide a box and leave dead space. This applies specifically to the Position-level sidebar zones; ordinary content blocks already behave this way via their existing per-block `Visible` toggle (hidden blocks are already skipped entirely, not blanked out) — the sidebar zones need the same treatment.

**The actual north star, stated directly and worth not losing**: *"Keeping it simple and beautiful rich site."* Simple for the agent to choose (two decisions: a Position, a Theme — not eight separate dropdowns), beautiful because every reachable combination was composed by someone with taste (not assembled from independently-chosen parts), rich because the underlying block system (10+ block types, reorderable, individually toggleable) stays exactly as capable as it is today. The lesson from this whole redesign thread: more configuration surface was mistaken for the fix multiple times (more layout variants, more per-block toggles, a preview tool) and each of those was a real, worthwhile improvement on its own — but none of them were the actual thing making the output feel plain, which was always the combinatorial-dial problem this design finally names directly.

**Status**: Theme gallery — first slice shipped same day (2026-07-23), budget allowing. Added a "Website Themes" picker to `Website/Index.cshtml` above the existing Color Palettes section, with 3 complete, named looks (Heritage Green — serif/warm sage paper; Modern Minimal — clean sans/bright white/spacious; Classic Navy — traditional serif/navy/formal). Each theme card is a plain button carrying its 8 field values as `data-*` attributes (accent color, background, font, heading/body size, button style, section spacing, hero style); a click handler sets all 8 underlying `WebsiteTemplateDesign`/`AgentDesignOverrides` form fields at once and re-runs the existing live-preview function — **zero schema changes, zero new controller actions**, exactly as anticipated, since those fields already existed for the independent-dial UI. Verified end-to-end in the local dev browser (not just code-reading): clicking each of the 3 cards sets all 8 fields and the live preview correctly, saved, and confirmed all 8 values persisted to the `AgentWebsites` row in MySQL. Committed and deployed (`0a818df`) immediately after verification, per the user's explicit ship-after-each-piece instruction so partial progress survives a token-budget cutoff. More themes (toward the original 6-8) and the Position (sidebar Left/Right/None + graceful zone collapse) axis are still the plan for a following session.

## Product Direction

The strongest path is not just "website builder" or "CRM". The winning position is:

> A vertical-ready business growth platform that gives small businesses a website, CRM, email campaigns, follow-ups, billing, client portal, and automation in one place.

The website builder is dependable, website leads connect into CRM, and agent-to-client invoicing (v1) now exists. The next phase should focus on the testimonial/poll/engagement backlog and a real client portal to build on top of the invoicing foundation.
