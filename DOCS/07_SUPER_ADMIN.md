# Super Admin Manual

## Sign In

1. Open the Super Admin Portal.
2. Enter an admin username and password.
3. Sign in.

The original Azure-application-settings admin credential is automatically created as the first **Super Admin** account the first time the app starts. From then on, admin sign-in is checked against the **Admin Users** list (see below), not the Azure setting directly — additional admin accounts can be created from within the portal. Every login attempt (success or failure) and every admin-account change is written to an audit log.

## Manage Admin Users and Roles

1. Select **Admin Users** (Super Admin only).
2. **New Admin** creates an account with a username, full name, temporary password, and role.
3. Two roles exist:
   - **Super Admin** — full access, including Packages, Tax Rates, PayPal Setup, Email Setup, Website Templates, Newsletter Templates, and Admin Users itself.
   - **Support** — day-to-day operations: Agents, Domains, Starter Content, Website Leads, and Reports. Billing/platform configuration screens are not accessible and return "Access Denied."
4. **Edit** an account to change its full name, role, or active status. An admin cannot deactivate their own account.
5. **Reset Password** sets a new password for an account; share it with that admin securely.

The audit log (visible in the database, not yet a dedicated screen) records login success/failure and every admin-account change (create, edit, role change, deactivate, password reset) with the acting admin's username and a timestamp. It does not yet cover every action across every screen — that is a larger effort for a future pass.

## Dashboard

The dashboard summarizes agents, active subscriptions, packages, revenue, and system activity. Use the sidebar to open operational areas.

## Find an Agent

1. Select **All Agents**.
2. Search by name, username, email, company, or domain.
3. Filter by status.
4. Open the agent record.

## Edit an Agent

1. Open the agent record.
2. Click **Edit**.
3. Update login, package, business type, registration, address, or phone information.
4. Save changes.

Optional fields such as fax, mobile phone, promotion code, designation, and company address may remain blank.

## Reset an Agent Password

1. Open the agent record.
2. Click **Reset Password**.
3. Give the temporary password to the agent securely.
4. The agent changes it at the next login when the force-change option is enabled.

## Activate, Deactivate, or Delete an Agent

- **Activate** restores access.
- **Deactivate** blocks access while retaining records.
- **Delete** permanently removes the agent and dependent information. Use only after confirming retention requirements.

## Review Agent Billing

The agent details screen shows:

- Active package
- Billing period and next billing date
- PayPal subscription ID
- Invoices
- Failed payments
- Retry history

Use **Email Invoice** to resend a paid invoice to the agent's current email address.

## Create a Package

1. Select **Packages**.
2. Click **New Package**.
3. Enter package name, description, monthly recurring price, annual recurring price, and one-time setup fee.
4. Set newsletter or other numeric limits when applicable. Use `-1` for unlimited.
5. Enable the required package functions. **Select all** can be used before removing excluded functions.
6. For numeric functions, set values such as contact count, file upload MB, or domain count.
7. Enable **Package is active**.
8. Save.

Only active packages appear during registration and billing.

## Edit Package Functions and Limits

1. Open **Packages**.
2. Click **Edit** beside the package.
3. Add or remove included functions.
4. Change contact, upload, domain, newsletter, or other limits.
5. Save before synchronizing PayPal.

Package feature changes control Agent Portal access and upgrade messages.

## Create or Replace PayPal Plans

1. Save the package prices first.
2. Confirm PayPal settings are configured for the current sandbox/live mode.
3. Click **Create / Replace PayPal Plans**.
4. Confirm monthly and annual plan IDs appear.

Replacing plans affects future subscribers. Existing PayPal subscriptions continue using their existing plan unless changed.

## PayPal Setup

Select **PayPal Setup** to verify:

- Sandbox or live mode
- Client ID and secret status
- Base URL
- Webhook ID
- Return/cancel URLs
- Active package plan IDs

Webhook URL: `https://ipro-prod-web.azurewebsites.net/Billing/Webhook`

## Email Setup

Select **Email Setup** to review SendGrid configuration, sender identity, event webhook information, and recent delivery events.

The sender address must be verified in SendGrid. Deferred email usually indicates recipient throttling rather than application failure.

## Edit Tax Rates

1. Select **Tax Rates**.
2. Update province/territory name, tax label, and percentage.
3. Save.

Tax changes apply to future invoice calculations.

## Manage Website Templates

1. Select **Templates**.
2. Create, edit, duplicate, retire, restore, or set a default template.
3. Configure template family, colors (click a **Color Palette** swatch for a quick, coordinated accent and background, or set them individually), typography (including font family, heading font size, and body font size), header, hero, spacing, buttons, and preview image.
4. Set defaults by business type when appropriate.

Heading and body font size set the default for section headings and body text across an agent's site; agents may override both from their own **My Website** settings. The hero/banner title keeps its own large display size regardless of this setting. Background color, button style, section spacing, and hero style also set real per-template defaults that agents may override individually; Hero Style (Gradient, Clean, or Classic) changes the background treatment behind the hero banner.

The template list shows how many agent sites and package defaults use each template, and the "Make default" action is scoped per business type/vertical, not just one global default. **Duplicate** creates an inactive `v2`/`v3` draft for safely testing changes before activating. **Delete** is blocked with the affected agent/package names whenever a template is still in use. **Retire** deactivates a template (existing sites stay online, unchanged) and emails every affected agent that their template was retired and they can switch whenever they're ready from **My Website**; those agents also see an in-app "Your current template has been retired" notice on that page until they switch. Retiring is blocked if the template is currently a business-type default — choose another default first.

From an existing template's edit screen, **Preview on an agent's real site** opens the candidate template rendered against a chosen agent's actual pages and content in a new tab — nothing is saved or changed. This requires `AdminPreview:SharedSecret` to be set to the same value in both the Web and Admin apps' Azure App Settings; until then the button shows a configuration warning instead of a broken link.

## Manage Newsletter Templates

1. Select **Newsletter Templates**.
2. Create, edit, hide (deactivate), or delete a starter template agents can use when composing a newsletter.
3. Set the default subject, HTML body (same rich editor agents use), plain-text body, and sort order.
4. Hidden templates stay in the list here but no longer appear in the agent-facing picker.

A handful of starter templates (announcement, market update, thank-you note, seasonal greeting) are seeded automatically the first time the app starts, so agents never see a completely empty list.

## Manage Vertical Starter Content

1. Open **Starter Content**.
2. Create a starter page set for a business type and optional package.
3. Define page title, slug, navigation, SEO fields, publication, and order.
4. Add and configure content blocks.
5. Save.

New agents receive matching starter pages based on business type/package. Agents may edit their resulting content.

## Monitor Domain Automation

1. Select **Domains**.
2. Review DNS, Azure binding, SSL, and last-check statuses.
3. Click **Recheck** after DNS or Azure permissions are corrected.
4. Use **Mark Bound** only when the Azure domain and SSL binding have been independently confirmed.

Automation settings require valid tenant ID, client ID, client secret, subscription ID, resource group, web app, App Service plan resource ID, and location. If a required setting is missing, the domain's error message names exactly which setting is blank.

Each domain row also shows the root/apex domain's forwarding status when it differs from the `www` host (informational only — it never blocks the site). A domain that has failed repeatedly shows an "Auto-retry paused" badge once IPRO's background job stops automatically rechecking it; **Recheck** still works normally on a paused domain and resumes automatic checking if it succeeds.

Super Admin always sees the real underlying Azure/DNS error text for a failed domain (unlike the agent-facing view, which shows a plain-language translation) — use this for actual diagnosis.

## Review Website Leads and Blocked Attempts

1. Select **Website Leads**.
2. Use the **Leads** tab to review public website submissions across every agent, including whether the agent notification email was delivered. Filter to notification-not-delivered leads to spot email delivery problems early.
3. Use the **Blocked Attempts** tab to review submissions stopped by the honeypot, timing check, or captcha before they became leads. Filter by reason, domain, or IP address to spot abuse patterns.
4. Blocked attempts store only the reason, domain, page, and IP address — never the submitted name, email, or message.

## Reports

Use the reporting screens to review:

- Agent statistics
- Revenue by period
- Subscription status

