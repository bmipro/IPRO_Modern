# Domains, SSL, Website Leads, and Lead Forms

## Temporary Website Domain

Each agent receives a temporary domain such as `FirstNameLastName.247Advisers.com`. It is assigned during registration and managed by IPRO/Super Admin.

The agent should not alter temporary-domain DNS records.

## Add a Custom Domain

1. Open **My Website**.
2. In **Domain Manager**, enter the root domain or `www` domain.
3. Click **Add**.
4. IPRO normalizes the domain to the `www` hostname.
5. At the registrar, add:
   - Type: `CNAME`
   - Name/Host: `www`
   - Value: `ipro-prod-web.azurewebsites.net`
6. Forward the root domain to the `www` domain using a permanent redirect when the registrar supports it. IPRO now automatically checks whether the root domain resolves and actually forwards to the `www` address, and shows this as a separate, informational-only status — it never blocks the site from working, since only the `www` host is what IPRO actually binds and serves.
7. Wait for DNS propagation.

IPRO checks pending domains automatically. Agents can also click **Retry** beside a domain to recheck it immediately (about every 2 minutes at most). Super Admin can also select **Recheck**.

## Domain Statuses

- **PendingDns**: IPRO is waiting for the CNAME to resolve.
- **DnsReady**: DNS points to the expected Azure hostname.
- **BindingPending**: Azure binding or certificate work is in progress.
- **Bound**: DNS, Azure custom-domain binding, and SSL are ready.
- **Failed**: Review the displayed error, correct it, and recheck. Agents see a plain-language version of the error; Super Admin sees the underlying Azure/DNS detail for real diagnosis.
- **NotConfigured** (root/apex domain only): the bare domain does not resolve or does not forward to the `www` address yet. This is informational only and does not affect the `www` site.

## Retry a Domain Check

1. Open **My Website**.
2. Beside any domain in **Domain manager**, click **Retry**.
3. IPRO immediately rechecks DNS and Azure binding for that domain.
4. Retry is limited to about once every 2 minutes per domain to prevent overload.

IPRO's automatic background check backs off over time for a domain that keeps failing (checking less often, then eventually pausing automatic checks after a long stretch of failures). Retry always works regardless of how long automatic checking has been paused — clicking it re-arms automatic checking once the domain succeeds.

## Add Multiple Domains

1. Confirm the active package permits additional domains.
2. Add each domain from **Domain Manager**.
3. Configure the `www` CNAME at each registrar.
4. Select **Primary** beside the preferred domain.
5. Remove unused domains with **Remove** — type the exact domain name to confirm, since this cannot be undone.

All bound domains display the same agent website and selected content.

## SSL Automation

Once DNS is ready, IPRO uses Azure App Service automation to:

1. Add the custom hostname binding.
2. Request or locate a managed certificate.
3. Bind SSL using SNI.
4. Mark the domain as bound.

Super Admin monitors this under **Domains**. Azure service-principal settings and permissions must remain valid.

## Add a Contact Form to a Website Page

1. Open **My Website**.
2. Click **Manage Pages**.
3. Edit the desired page.
4. Add a **Contact Form** content block.
5. Enter its heading and supporting content.
6. Save the block.

Public visitors provide name, email, optional phone/message, captcha answer, and consent.

## Contact Form Captcha

Each public contact form and newsletter signup form includes a lightweight math captcha.

1. The visitor solves the displayed math question.
2. The answer is checked before IPRO creates a lead or CRM contact.
3. Refreshing the page generates a new challenge.
4. Expired or incorrect captcha answers stop the submission.

The captcha token is protected by the application and expires after a short period. This helps reduce automated spam without forcing visitors through a third-party challenge.

## Add Newsletter Signup to a Page

1. Edit the desired page.
2. Add a **Newsletter Signup** block.
3. Enter the heading and supporting content.
4. Save the block.

A successful signup updates or creates the CRM client and enables newsletter subscription.

## Add a Lead Magnet Download Block

1. Upload the file you want to give away (a PDF guide, checklist, etc.) from [Documents](12_AGENT_DOCUMENT_LIBRARY.md) first.
2. Edit the desired page.
3. Add a **Lead Magnet Download** block.
4. Enter its heading and supporting content.
5. Under **Which file?**, choose the document you uploaded.
6. Save the block.

Visitors see a short form (name and email) instead of a direct download link. Submitting it creates a website lead the same way a Contact Form submission does, and reveals a "Download Now" button that unlocks the file. If you haven't uploaded any documents yet, the block shows a reminder to do that first instead of a file picker.

## Website Lead Processing

Public submissions are handled as follows:

1. Basic bot, captcha, and duplicate protection is applied.
2. The submission is always saved as a website lead.
3. If the email already exists, that CRM contact is updated.
4. If it does not exist and the package has contact capacity, a CRM contact is created.
5. If the package contact limit is reached, the lead remains available without losing the submission. The lead card shows the processing note so the agent knows why it was not added to CRM.
6. A timeline note is added to connected CRM contacts.
7. The agent receives an email notification when email delivery is configured.

Website Leads is the source of truth. Email is only a notification, so a lead is not lost if SendGrid, sender verification, or a mailbox provider delays or rejects the message.

## Contact Form Email Notifications

When a visitor submits a contact form, IPRO saves the lead first and then attempts to email the agent. Each lead records whether that notification actually succeeded.

If a lead in **Website Leads** shows a **notification not delivered** note:

1. Confirm the lead was saved — it always is, independent of email delivery.
2. Check the agent email address under the agent profile.
3. In Super Admin, review **Email Setup**.
4. Confirm Azure has the SendGrid app setting configured.
5. Confirm the SendGrid sender identity is verified.
6. Check SendGrid Activity for deferred, bounced, blocked, or spam-rejected messages.

Super Admin can also review notification delivery and blocked spam/bot attempts across every agent from the **Website Leads** screen in the Super Admin Manual.

## Anti-Spam Protection

Public contact and newsletter forms include a math captcha, a hidden honeypot field, and a minimum-fill-time check. A submission that fails any of these is never shown to the visitor as an error beyond a generic message, and no lead is created — but IPRO records the blocked attempt (reason, domain, page, and IP address only, never the submitted name/email/message) so Super Admin can review volume and patterns. The public contact form endpoint also has a dedicated rate limit separate from ordinary page browsing.

The honeypot is a hidden decoy field that real visitors never see or fill. Some browsers can still autofill hidden form fields based on field naming, so the decoy field is deliberately named and structured to avoid common autofill triggers.

### A visitor says they submitted the form but nothing shows up

1. Check Super Admin's **Website Leads → Blocked Attempts** tab for a matching domain/page/timestamp — this confirms whether the anti-spam checks (captcha, honeypot, or timing) caught the submission.
2. If a blocked attempt shows a honeypot reason but the visitor is confident they filled out the form normally, their browser's autofill may have populated the hidden decoy field. This is uncommon (the decoy field is deliberately named to avoid common autofill triggers), but can still happen with some browser/password-manager combinations.
3. Confirm the visitor's request landed within a few seconds of the page loading — submissions faster than 2 seconds are treated as automated and blocked by the timing check.
4. If nothing shows up in **Blocked Attempts** either — no lead, no blocked-attempt record, no trace at all — this points to a model-validation rejection rather than an anti-spam block. A validation failure happens before any record is created, so it is otherwise invisible. This exact scenario happened platform-wide on 2026-07-17 (see the "Public Contact/Newsletter Leads Silently Not Saving" incident in `09_TROUBLESHOOTING.md`) and is now fixed, but if a similar report comes in again, check the application logs for a `Public lead submission rejected by validation` warning first.

## Review Website Leads

1. Select **Website Leads** in the Agent Portal.
2. Filter by all, unread, new, contacted, or dismissed.
3. Search by name, email, phone, message, or source.
4. Narrow further with a **From**/**To** date range.
5. Sort by newest first, oldest first, or by status.
6. Open the connected CRM contact when available.
7. Use **Plan Follow-up** to schedule the next action for connected CRM contacts.
8. Mark the lead **Contacted** after responding.
9. Dismiss irrelevant leads.
10. Use **Mark all read** when appropriate.

The dashboard displays new and unread lead counts.

## Bulk Actions and Export

1. Check the box beside each lead to act on, or use **Select all** to select every lead currently shown on the page.
2. Click **Mark Selected Contacted** or **Dismiss Selected** to apply that status to every selected lead at once.
3. Click **Export CSV** to download every lead matching the current filter, search, date range, and sort as a spreadsheet-ready file — not just the leads on the current page.
