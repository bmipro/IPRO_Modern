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
6. Forward the root domain to the `www` domain using a permanent redirect when the registrar supports it.
7. Wait for DNS propagation.

IPRO checks pending domains automatically. Super Admin can also select **Recheck**.

## Domain Statuses

- **PendingDns**: IPRO is waiting for the CNAME to resolve.
- **DnsReady**: DNS points to the expected Azure hostname.
- **BindingPending**: Azure binding or certificate work is in progress.
- **Bound**: DNS, Azure custom-domain binding, and SSL are ready.
- **Failed**: Review the displayed Azure or DNS error, correct it, and recheck.

## Add Multiple Domains

1. Confirm the active package permits additional domains.
2. Add each domain from **Domain Manager**.
3. Configure the `www` CNAME at each registrar.
4. Select **Primary** beside the preferred domain.
5. Remove unused domains with **Remove**.

All bound domains display the same agent website and selected content.

## SSL Automation

Once DNS is ready, IPRO uses Azure App Service automation to:

1. Add the custom hostname binding.
2. Request or locate a managed certificate.
3. Bind SSL using SNI.
4. Mark the domain as bound.

Super Admin monitors this under **Domains**. Azure service-principal settings and permissions must remain valid.

## Add a Contact Form to a Website Page

1. Open **My Website → Manage Pages**.
2. Edit the desired page.
3. Add a **Contact Form** content block.
4. Enter its heading and supporting content.
5. Save the block.

Public visitors provide name, email, optional phone/message, and consent.

## Add Newsletter Signup to a Page

1. Edit the desired page.
2. Add a **Newsletter Signup** block.
3. Enter the heading and supporting content.
4. Save the block.

A successful signup updates or creates the CRM client and enables newsletter subscription.

## Website Lead Processing

Public submissions are handled as follows:

1. Basic bot and duplicate protection is applied.
2. The submission is always saved as a website lead.
3. If the email already exists, that CRM contact is updated.
4. If it does not exist and the package has contact capacity, a CRM contact is created.
5. If the package contact limit is reached, the lead remains available without losing the submission. The lead card shows the processing note so the agent knows why it was not added to CRM.
6. A timeline note is added to connected CRM contacts.
7. The agent receives an email notification when email delivery is configured.

## Review Website Leads

1. Select **Website Leads** in the Agent Portal.
2. Filter by all, unread, new, contacted, or dismissed.
3. Search by name, email, message, or source.
4. Open the connected CRM contact when available.
5. Use **Plan Follow-up** to schedule the next action for connected CRM contacts.
6. Mark the lead **Contacted** after responding.
7. Dismiss irrelevant leads.
8. Use **Mark all read** when appropriate.

The dashboard displays new and unread lead counts.
