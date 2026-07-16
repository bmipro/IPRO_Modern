# IPRO Project Status and Roadmap

Last updated: July 16, 2026

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
- Template output is more differentiated: Classic Sidebar's Call to Action, Contact, and generic Text blocks now use distinct grid/card/band layouts instead of one flat stack. Hero Style (Gradient/Clean/Classic) now has a real, visible effect on all 3 templates (it was previously a no-op). Agents can now also override Background Color, Button Style, and Section Spacing per site, in addition to Theme Color and Font.

### Documentation
- `DOCS` exists as the project documentation area.
- Some user-facing manuals have been started.

## Not Done or Still Fragile

### Highest priority bugs
1. Template output still needs stronger visual differentiation.
   - Different templates should produce clearly different public websites.
   - Home, About, Services, Contact, and future pages need consistent rendering.

2. Domain automation needs production hardening.
   - Clearer pending-DNS messaging.
   - Better retry visibility.
   - Better error handling for Azure permission/config problems.
   - Safer remove/retry workflow.
   - Consistent root-domain and `www` guidance.

### Important missing product pieces
- Full website lead inbox.
- Website analytics.
- Better template preview before applying.
- Template governance:
  - usage count,
  - default template by business type,
  - duplicate/version templates,
  - prevent deletion while in use,
  - retirement/agent notification workflow.
- Richer HTML/newsletter editor.
- Better newsletter templates.
- Automatic unsubscribe links and preferences.
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

### 4. Improve template governance
- Show how many agents use each template.
- Set default template by business type.
- Prevent deleting templates in use.
- Add duplicate/version workflow.
- Add retire/notify workflow.

### 5. Build website analytics
- Track page views.
- Track contact form submissions.
- Track domain source.
- Track newsletter/campaign source where possible.
- Show analytics in the agent portal.

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
