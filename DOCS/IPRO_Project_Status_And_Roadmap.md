# IPRO Project Status and Roadmap

Last updated: July 16, 2026 (evening)

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
- Pending/unpaid checkout recovery exists.

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

### Newsletter and campaigns
- Newsletter drafts exist.
- Preview exists.
- Send now and scheduled send exist.
- Sending to all subscribers, account type/group, or one individual exists.
- SendGrid delivery tracking exists.
- Open/failure tracking exists when SendGrid events are configured.
- Test send exists.
- Basic drip/campaign functionality exists.

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

### Documentation
- `DOCS` exists as the project documentation area.
- Some user-facing manuals have been started.

## Not Done or Still Fragile

### Important missing product pieces
- Better template preview before applying.
- Richer HTML/newsletter editor.
- Better newsletter templates.
- Stronger campaign reporting.
- Agent-side billing/invoicing system for their own clients.
- Client portal.
- Document upload/storage.
- Appointment booking.
- SMS reminders.
- Social media posting/management.
- More complete role/security model for admin users.
- Formal backup/release checklist.

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

## Bigger Product Ideas

### Agent invoicing and billing system
This could become a major differentiator. Agents and other vertical businesses should be able to:
- Create estimates and invoices for their own clients.
- Create recurring invoices.
- Add taxes by province/state.
- Email invoices as polished PDFs.
- Accept online payments.
- Track paid, unpaid, overdue, and failed payments.
- Send payment reminders.
- Store client payment history.
- Export to CSV/QuickBooks.
- Let clients view/pay invoices in a secure client portal.

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

### AI-assisted business tools
- Generate website copy by vertical.
- Generate newsletter drafts.
- Generate drip campaigns.
- Generate social posts.
- Suggest follow-ups.
- Summarize client activity.
- Recommend next best action.

### Client portal
- Secure client login.
- Messages.
- Documents.
- Forms.
- Appointments.
- Invoices and payments.
- Campaign preferences and unsubscribe controls.

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

The next phase should make the website builder dependable, then connect website leads into CRM, then add agent-client billing as the next major revenue feature.
