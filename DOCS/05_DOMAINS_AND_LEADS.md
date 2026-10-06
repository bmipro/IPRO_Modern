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
6. Make the short address (the root domain, without `www`) reach the site too: point it straight at IPRO with two DNS records, or forward it at the registrar. See **Point the short address straight at IPRO** below. IPRO checks it automatically and shows it as its own **Short address** row; the `www` site works either way.
7. Wait for DNS propagation.

IPRO checks pending domains automatically. Agents can also click **Retry** beside a domain to recheck it immediately (about every 2 minutes at most). Super Admin can also select **Recheck**.

Once the Connection status panel shows **Found**, **Connected**, **Secured** and the short address reads **Connected** or **Forwarding OK**, the two registrar instruction cards fold away behind **Show the setup steps (for adding another domain)**. They come back on their own if anything stops being green.

### Point the short address straight at IPRO

The short address is the domain without `www` (`yourfirm.ca`). It cannot use a CNAME, so there are two ways to
make it reach the site (553).

**Recommended: two DNS records.** On the registrar's DNS screen:

| Type | Name / Host | Value |
|---|---|---|
| A | `@` | `40.89.19.0` |
| TXT | `asuid` | the long code shown in step 2 of the setup steps on **My Website** |

- Ours must be the only A record for `@`: change the one that is there rather than adding a second, and remove
  any AAAA record for `@`.
- If the registrar's forwarding is switched on for the domain, switch it off first (it owns the A record while
  it is on).
- IPRO notices at its next check (**Check now** checks at once), connects the address and secures it with its
  own certificate, usually within a few minutes. The **Short address** row reads **Connecting**, then
  **Securing**, then **Connected**.
- Every address on the short name then opens the same page on the `www` name: `yourfirm.ca/services` goes to
  `www.yourfirm.ca/services`.

**Simpler, with one limit: the registrar's forwarding.** Forward `yourfirm.ca` to `https://www.yourfirm.ca`,
permanent, masking off. A registrar forwards only your home address: `yourfirm.ca` works, but a link to an inner
page (`yourfirm.ca/services`) shows the registrar's "not found" page. Fine for a domain that never had a
website. The row reads **Forwarding OK**.

Either way counts as done.

### Moving an existing website here

When the domain already has a website, its pages are in search results, on a Google Business Profile, in social
profiles and on printed QR codes. To keep them working:

1. Build the pages in IPRO first; the old site keeps serving while you do.
2. At the registrar, edit the `www` record that is already there rather than adding one (a registrar refuses a
   second `www`). Leave every mail record (MX, TXT, autodiscover) and the nameservers exactly as they are.
3. Wait for **Found**, **Connected** and **Secured**, and look at the new site on the `www` address. The short
   address still shows the old site, so nothing is lost while you check.
4. Point the short address straight at IPRO (above). Do not use forwarding: the old site's inner pages lived on
   the short address, and forwarding loses every one of them.
5. For each page whose address changed, list the old address on the page (**Old addresses**, see
   [04_WEBSITE_BUILDER.md](04_WEBSITE_BUILDER.md)). Addresses that differ only in spelling
   (`/bread-%26-pastries` and `/bread-pastries`, `/services.html` and `/services`) are matched on their own.

### The address on your emails

Once your custom domain is live (connected and secured), your e-cards, e-letters and newsletters show it instead of
the temporary domain, from the next one sent (552); their previews and the newsletter test send show it too. To have
it written with your own capitals -- `www.4iPro.com` rather than `www.4ipro.com` -- type it in **Shown in your emails
as**, next to **Custom Domain** on **My Website**, and save. It must be the same letters as your custom domain; only
the capitals can differ. Until the domain is live, your emails keep showing the temporary domain, which always serves
the same site.

## What to Tell a New Agent

Wording that can be used directly in onboarding, support replies, or a help page. It matches the
**How this works** panel in the portal.

> ### Using your own domain name
>
> Your site is already live at **yourname.247advisers.com**. This connects your own address — like
> **www.yourfirm.ca** — so clients see your brand instead.
>
> **You'll need** the login for wherever you bought the domain (GoDaddy, Namecheap, your web designer).
> **Time:** about 10 minutes of work, then a wait — usually an hour, occasionally a day.
> **You can stop at any point and your current site keeps working.**
>
> **There are two settings to make at your registrar, and you should do both in one visit** — the
> CNAME in step 3 and the short address in step 4. Doing only the first is the single most common
> mistake: your site works at `www.yourfirm.ca`, but anyone typing `yourfirm.ca` lands on your
> registrar's parked page. Leave your nameservers alone; neither step changes them.
>
> **1. Pick your address.** We recommend the `www` version. It's the most reliable to set up, and the
> short version can redirect to it so both work.
>
> **2. Tell us the domain.** Website → Domain → Add domain. We'll show you the exact record to create.
>
> **3. Point your domain to us.** At your registrar, find DNS / DNS Management, and add:
>
> | Field | Value |
> |---|---|
> | Type | CNAME |
> | Name / Host | `www` |
> | Value / Points to | `ipro-prod-web.azurewebsites.net` |
> | TTL | Leave as-is |
>
> *A CNAME tells the internet "when someone asks for this address, send them to IPRO."* Save it — that's
> your part done. You don't need to keep the page open.
>
> **4. Make the short address work too** -- while you're still on your registrar's site. On the same DNS
> screen, set the two records the portal shows you:
>
> | Type | Name / Host | Value |
> |---|---|---|
> | A | `@` | the address shown in the portal |
> | TXT | `asuid` | the long code shown in the portal |
>
> Change the A record that is already there for `@` rather than adding a second one, and switch your
> registrar's forwarding off if it is on. Every address on `yourfirm.ca` then opens the same page on
> `www.yourfirm.ca`, and its certificate is automatic too.
>
> *Simpler, with one limit:* under **Forwarding** or **Redirect**, forward `yourfirm.ca` to
> `https://www.yourfirm.ca`, permanent redirect, masking off. A registrar forwards only your home address, so
> a link to an inner page on the short address stops working. Fine for a new domain; not for a website you
> are moving here. Where to find it: **GoDaddy** -- My Products, Domains, your domain, Forwarding, Add;
> **Namecheap** -- Domain List, Manage, Redirect Domain; **Squarespace/Google Domains** -- Domains, your
> domain, Forwarding; **Cloudflare** -- Rules, Redirect Rules.
>
> *Why the short address is different:* a bare domain can't use a CNAME -- the DNS standard forbids it
> alongside the `SOA` and `NS` records every domain must have. So it gets an address record instead, and a
> TXT record that proves the domain is yours.
>
> **5. Your certificate installs itself.** The last step is the padlock in the browser. It's issued and
> installed automatically, usually within a few minutes of step 3 completing, and renewed automatically
> from then on. The portal shows *Securing your site* while it happens and *Secured* when it's done.
> **There is nothing for you to do, and no second record to add.**
>
> **If something looks wrong**
> - *"Waiting" for more than a day* — the DNS record probably didn't save, or went in the wrong field.
>   Check that Name is `www` and not the full address; registrars differ on which they want.
> - *A security warning* — that's step 5 still finishing. Give it a few minutes and reload. If it's still
>   there after a couple of hours, contact support.
> - *Short address shows a parked page* — step 4 hasn't been done; your registrar is still showing its placeholder.
> - *Short address says Needs attention* -- the line under it says why: usually the TXT record is missing or
>   mistyped, or the old A record is still there beside ours.

## Domain Statuses

The agent portal shows plain-language labels; the underlying status values are in brackets for Super
Admin and support.

| Agent sees | Row | Meaning |
|---|---|---|
| **Waiting** `[PendingDns]` | Your domain | Waiting for the CNAME to resolve. Usually 15–60 minutes, up to 24 hours. |
| **Found** `[DnsReady]` | Your domain | DNS points at the expected Azure hostname. |
| **Setting up** `[BindingPending]` | Connection to your site | The custom hostname is not attached yet. |
| **Connected** `[Bound]` | Connection to your site | Azure is serving this hostname. |
| **Securing your site** `[BindingPending]` | Security certificate | Managed certificate is being issued. Normal, a few minutes. |
| **Taking longer than usual** `[BindingPending]` | Security certificate | Bound with no certificate for 3+ hours. Genuinely stuck; IPRO is alerted. |
| **Secured** `[Bound]` | Security certificate | Certificate is live and auto-renewing. |
| **Connecting** | Short address | The short address points at IPRO (an A record) and is being connected. |
| **Securing** | Short address | Connected; its certificate is being issued. A few minutes. |
| **Connected** | Short address | Points at IPRO and is secured. Every address on it goes on to `www` with its path. |
| **Forwarding OK** | Short address | The registrar forwards it to `www` (the home address only). |
| **Needs attention** | Short address | Points at IPRO but could not be connected: the TXT record is missing or wrong, or a second A record is still there. The row says which. |
| **Not set up** `[NotConfigured]` | Short address | The bare domain neither points at IPRO nor forwards to `www`. The `www` site still works. |
| **Failed** `[Failed]` | any | Agents see a plain-language error; Super Admin sees the raw Azure/DNS detail. |

### Bound-with-no-certificate is normal for a few minutes

Azure issues App Service Managed Certificates **asynchronously**. The first pass through
`EnsureManagedCertificateAsync` almost always gets no thumbprint back, because Azure hasn't finished.
The next domain check five minutes later re-PUTs the same certificate resource — same name, so it is an
idempotent update rather than a conflict — finds the thumbprint populated, and binds it.

During that window the site really is unreachable at the new address, since the app is HTTPS-only and
serves a certificate for the wrong name. So the portal shows amber and says so, but frames it as
"Securing your site" rather than a fault, because the agent has nothing to do.

**Only if it lasts hours is something wrong.** `DomainAutomationJob` alerts IPRO Operations after a
3-hour grace period.

**Support action when that alert fires:** check whether the managed certificate already exists and
simply failed to bind, *before* issuing anything:

```
az resource show --ids /subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.Web/certificates/managed-www-their-domain-ca --query properties.thumbprint
```

If it has a thumbprint, bind that. Only if no managed certificate exists should you fall back to
`ops/New-AgentCert.ps1`, which produces a Let's Encrypt certificate that does **not** auto-renew.

> **Learn from 2026-08-06.** `www.ouritems.ca` was diagnosed as "managed certificates don't work on this
> subscription," and a Let's Encrypt certificate was hand-issued and bound over the top. The managed
> certificate had in fact issued normally, minutes later, exactly as it had for `www.4ipro.com` and
> `www.drhug.ca` in July. The result was a 90-day manual renewal chore replacing a certificate that
> renewed itself. One `az` query against a working domain would have prevented it.

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
2. Request a managed certificate.
3. Bind SSL using SNI.
4. Mark the domain as bound.

Super Admin monitors this under **Domains**. Azure service-principal settings and permissions must remain valid.

### Step 2 is asynchronous, and that is the only subtlety

All four steps succeed automatically. Verified live on three agent domains:

| Domain | Certificate | Issued | Agent action beyond the CNAME |
|---|---|---|---|
| `www.4ipro.com` | Azure managed (GeoTrust) | 2026-07-11 | none |
| `www.drhug.ca` | Azure managed (GeoTrust) | 2026-07-11 | none |
| `www.ouritems.ca` | Azure managed (GeoTrust) | 2026-08-06 | none |

**One CNAME is the entire agent-facing process.** Managed certificates renew themselves, which is why
agent domains are deliberately absent from the expiry watch lists — only the two platform domains are
hand-renewed with lego.

The platform domains are on lego for their own historical reasons
(see [20_CERTIFICATES.md](20_CERTIFICATES.md)). **Do not read that as "managed certificates don't work
on this subscription."** They demonstrably do; the table above is the evidence.

**Not needed, despite appearances:** the `asuid` TXT record. Azure requires it for *apex* binding only —
a `www` hostname is proven by the CNAME itself. Nor is any `_acme-challenge` TXT record needed, since
nothing about the normal path uses ACME.

**The short address is the one place the `asuid` record IS needed (553).** Pointed straight at IPRO it is
exactly that apex binding, which is why step 2 of the setup steps shows a TXT record beside the A record. Its
certificate is a managed one too, validated over the A record, and renews itself.

**The A record's value is the app's inbound address** (`App:WebsiteAddress`; today `40.89.19.0`, what
`ipro-prod-web.azurewebsites.net` resolves to). A `www` name follows the CNAME wherever the app goes; a short
address does not. Before any change of App Service plan or region, check whether the inbound address changes:
if it does, every short address pointed at IPRO must be updated by its owner, and `App:WebsiteAddress` with
it. The domain check recognises a short address by resolving the CNAME target, never by the setting, so it is
not fooled by a stale one.

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

## Add a Custom Form Block

Build a reusable form with your own text fields, checkboxes, dropdowns, and section headers under **Forms** (Agent Portal menu), then attach it to any page with a **Custom Form** block and pick which form to display. Submissions land in Website Leads as a **Form submission**-type lead, with a **View Answers** link showing every field and its answer individually. See [DOCS/17_FORMS.md](17_FORMS.md) for the full walkthrough.

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
