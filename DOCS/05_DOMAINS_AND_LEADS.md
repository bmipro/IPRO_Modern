# Domains, SSL, Website Leads, and Lead Forms

## Temporary Website Domain

Each agent receives a temporary domain such as `FirstNameLastName.247Advisers.com`. It is assigned during registration and managed by IPRO/Super Admin.

The agent should not alter temporary-domain DNS records.

## Add a Custom Domain

1. Open the **Domain** page: **Domain** in the menu under Website, the **Domain** button at the top of **My
   Website**, or **Manage domain** / **Connect your own domain** on My Website's **Your domain** card. Since 595
   (2026-10-22) domains have this page of their own, at full width; My Website keeps only that card.
2. Under **Your domains**, enter the root domain or `www` domain (`yourfirm.ca` or `www.yourfirm.ca`).
3. Click **Add**. IPRO saves it as the `www` name, and the domain's step-by-step guide opens below it, from step 1.
4. Follow the guide (see **Connect your domain step by step** below). It shows each record to add, with a **Copy**
   button beside its type, name and value, and ticks each step as soon as the record is seen.

A custom domain is a domain you own. IPRO's own addresses cannot be added: www.iProAdvisers.com, www.iProAccountants.com, www.iProMortgages.com and every name under them (app., admin. and so on), the free sites' 247advisers.com, and the hosting's own `azurewebsites.net` names. The Domain page answers that the name is one of IPRO's own addresses. A domain with accents is entered in its `xn--` form (the ASCII or Punycode name your registrar shows).

IPRO checks pending domains automatically. Super Admin can also select **Recheck**.

### Connect your domain step by step

Every domain on the **Domain** page has its own guide (595). It opens by itself for a domain just added, from step
1, whatever other domains are already connected; a domain that is live has its guide folded, one click away
(**Setup guide**). One step is open at a time. A done step shows a tick and the word **Done**; later steps stay
visible and can be opened at any time, so all the records can be added in one visit.

1. **Prove it's yours.** A TXT record named `_ipro-verify` with the value made for your account and this domain
   (see **Prove the domain is yours** below). Nothing is connected until IPRO sees it.
2. **Point www at iPro.** A CNAME record named `www` with the value `ipro-prod-web.azurewebsites.net`. If `www`
   points somewhere else today, the guide says exactly where, and that the record is to be edited rather than a
   second one added.
3. **The short address (optional, recommended).** An A record for the domain itself (`@`) with the value
   `40.89.19.0`, and a TXT record named `asuid` (see **Point the short address straight at IPRO** below), or the
   registrar's forwarding. Before you change anything, the guide checks where the domain's email goes (its MX
   records): usually somewhere else (Microsoft 365, Google, a mail host), and then it says **Your email is not
   affected**. When the email lives on the short address itself (a web host with the website and the mail on one
   server, as cPanel sets it up), it says so in red: pointing the short address at iPro would stop the email, so use
   the registrar's forwarding instead, or ask us. When it could not tell, it says that, never "not affected". If the
   short address already points at iPro while the email depends on it, the domain's row says the email may not be
   arriving (the page looks once for every live domain whose short address points here, even with its guide
   folded). **Skip this step for now** moves on to step 4.
4. **iPro finishes.** Connected to your website, then the padlock (security certificate) ordered and in place,
   then live. IPRO emails you when the domain is live, and once, a day after it was added, if it is still not
   connected, with what is missing. When the padlock is held up by a record only you can add (a CAA record), or is
   hours late, this step opens ahead of step 3. "Hours late" is counted from when the domain was proven and
   connected, not from when it was added (a domain added one day and proven the next is not late at once); IPRO's
   own overdue-certificate alert counts the same way.

**Where to do it.** The guide reads the domain's name servers and names the company that manages its DNS:
GoDaddy, Namecheap, Cloudflare, Squarespace (formerly Google Domains), Wix, IONOS, Hover, Tucows/OpenSRS
resellers, SiteGround, Bluehost, or a web host with cPanel (HostPapa, Web Hosting Canada, HostGator, hosting.com,
InMotion). It then says where to click there, what goes in the Name box (`@` or empty for the domain itself,
`www`, `_ipro-verify`, `asuid`; the whole name with a final dot in cPanel), links that company's own help page,
and warns of its trap for the step: GoDaddy's forwarding locks the `@` record (the guide then says: open the
domain's Forwarding tab and delete the forwarding; its two A records go away by themselves), Cloudflare's proxy must be set to
DNS only (grey cloud), Namecheap's URL Redirect hides an extra A record, IONOS adds an AAAA record, Squarespace's
default records must go. For any other company the wording fits every DNS screen.

**What iPro sees.** Each step says what is there now beside what it should be: "We don't see this record yet",
"Right now www.yourfirm.ca points to yourfirm.myshopify.com. Edit that record and change its value to ...", "...
points to iPro but also to ... Delete the other A records". While the guide is open the page looks again about
every 20 seconds (only while the page is in front and the guide is on screen), and **Check now** looks at once and
asks IPRO to check the domain fully (15 seconds apart at most; sooner, it says it checked a few seconds ago and
shows that); neither ever asks you to wait. IPRO asks the domain's own name servers, so a record counts within seconds of being saved. (For a domain
whose DNS is signed with DNSSEC, the proof in step 1 is still read through the public resolvers, which check the
signatures; it can then take a few minutes longer.)

**I need help** opens a support ticket already written: the domain, its DNS host, the step you are on, and for
every record what IPRO expects beside what it sees. Add a line if you like and send it. Super Admin sees the same
step for each domain (**Domains**, and **Custom domains** on the agent's page), so support can say "you are on
step 2".

### Prove the domain is yours (TXT record)

A domain name says nothing about who owns it, so since 592 (2026-10-19) IPRO connects a custom domain only
after whoever controls the domain's DNS has said which IPRO account it is for. When a domain is added,
step 1 of the domain's guide on the **Domain** page shows a record made for that account and that domain only:

| Type | Name / Host | Value |
|---|---|---|
| TXT | `_ipro-verify` | the value shown beside the domain (`ipro-` followed by 32 letters and digits) |

- The record goes on the short address: for `www.yourfirm.ca` its full name is `_ipro-verify.yourfirm.ca`, and most
  registrars want only `_ipro-verify` in the Name box. For a sub-domain added as the address itself
  (`clients.yourfirm.ca`) the guide shows the name to enter.
- Until IPRO has seen it, nothing happens for the domain: it is not connected, no certificate is ordered, and
  the address does not show the website. Step 1 of the domain's guide says what to add, and ticks once it is seen.
- IPRO asks the domain's own name servers (595), so the record counts the moment it is saved, before `www` points
  anywhere, when all of them agree. A domain whose DNS is signed (DNSSEC) is proven through the public resolvers,
  which check the signatures. Only when those servers cannot be reached does it ask public DNS resolvers, and then only once the
  `www` name points at IPRO: a resolver asked before the record was added can keep answering "no such record" for
  up to a few hours (ten minutes at GoDaddy).
- Once seen, it is not looked for again. It can stay in place: it proves nothing for anyone once the domain is
  removed from IPRO.
- Another IPRO account's value proves nothing for this one, and a record left behind after a domain is removed
  proves nothing for the next account that adds it.
- A domain another account has added but not proven does not lock out its real owner. Adding it shows the
  owner their own value for it; once that record is published, adding it again moves the domain to their account
  (proven at once) and the other account's unproven entry is dropped. If the other account's record is the one
  published, the domain is theirs and stays. A proven domain never moves this way.
- Removing a domain forgets its value: adding it again shows a new one, and the old record proves nothing any
  more (not even for the same account). Replace the record when you add a domain again.
- Domains that were connected before 15 October 2026 count as proven; nothing changes for them. A domain added on
  or after that day, or one that never connected, needs the record like a new one.
- A name only the website's own settings field holds (no row under **Your domains**) is not shown as the site any
  more: every custom domain is served through its proven row.

### Point the short address straight at IPRO

The short address is the domain without `www` (`yourfirm.ca`). It cannot use a CNAME, so there are two ways to
make it reach the site (553).

**Recommended: two DNS records.** On the registrar's DNS screen:

| Type | Name / Host | Value |
|---|---|---|
| A | `@` | `40.89.19.0` |
| TXT | `asuid` | the long code shown in step 3 of the domain's guide on the **Domain** page |

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
as**, under **The address on your emails** on the **Domain** page, and click **Save**. It must be the same letters as your custom domain; only
the capitals can differ. Until the domain is live, your emails keep showing the temporary domain, which always serves
the same site. When you make another domain your primary one (or remove the primary one), what you typed there goes
with it and your emails use small letters until you type the new one.

## What to Tell a New Agent

Wording that can be used directly in onboarding, support replies, or a help page. Since 595 the portal's own
guide (above) walks the agent through the same records one step at a time, with their DNS host's wording.

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
> **Also on that screen: prove the domain is yours.** The portal shows one more record beside your domain,
> made for your account only: Type `TXT`, Name `_ipro-verify`, and the value shown (it starts with `ipro-`).
> We connect nothing until we see it, so nobody else can connect your domain to their account.
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
| **Prove it's yours** (step 1 of the guide, with the TXT record) | Your domains | 592: the domain's `_ipro-verify` TXT record has not been seen yet. Nothing is connected, secured or shown on the address until it is. |
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
az webapp config ssl list -g <rg> --query "[?contains(hostNames, 'www.their-domain.ca')].{name:name, thumbprint:thumbprint}" -o table
```

It finds the certificate by the address it was made for, whatever its name: a new one is called `managed--www-their-domain-ca-` followed by 16 letters and digits, and one ordered before October 2026 keeps the older name `managed-www-their-domain-ca` (the alert gives both). If it has a thumbprint, bind that. Only if no managed certificate exists should you fall back to
`ops/New-AgentCert.ps1`, which produces a Let's Encrypt certificate that does **not** auto-renew.

> **Learn from 2026-08-06.** `www.ouritems.ca` was diagnosed as "managed certificates don't work on this
> subscription," and a Let's Encrypt certificate was hand-issued and bound over the top. The managed
> certificate had in fact issued normally, minutes later, exactly as it had for `www.4ipro.com` and
> `www.drhug.ca` in July. The result was a 90-day manual renewal chore replacing a certificate that
> renewed itself. One `az` query against a working domain would have prevented it.

### The certificate never arrives: a CAA record

Some web hosts publish **CAA** records for a domain: a list of the only companies allowed to issue its security certificate. IPRO's certificates come from **DigiCert**. If your domain has such a list and DigiCert is not on it, the certificate cannot be issued, however long you wait.

IPRO checks this for you while your certificate is pending. When it is the cause, step 4 of the domain's guide on the **Domain** page shows a red note, **One record is missing at your registrar**, with the record to add:

| Type | Name | Value |
|---|---|---|
| CAA | `@` (your domain) | `0 issue "digicert.com"` |

1. Sign in where your domain's DNS is managed.
2. Add the record above **beside** the CAA records already there. Do not remove the others; your host uses them.
3. Some registrars ask for it in three boxes: flag `0`, tag `issue`, value `digicert.com`.
4. Go back to the **Domain** page and click **Check now** in the domain's guide. The note disappears once IPRO has checked again (Check now does that at once; on its own the page looks again every five minutes), and the certificate follows within minutes.

A domain with no CAA records at all needs nothing: any company may issue for it.

## Retry a Domain Check

1. Open the **Domain** page and the domain's guide (**Guide** or **Setup guide** beside it).
2. The guide looks again on its own about every 20 seconds while it is open; **Check now** looks at once and has
   IPRO check the domain fully, whatever its state, 15 seconds after the last check at the soonest (the background
   check's included); sooner than that it says so and shows the latest.
3. When DNS shows something IPRO has not acted on yet (the proof record, `www` pointing here, the short address's
   two records, a padlock on its way, a CAA record), IPRO goes on to check the domain at once; the background
   check does the rest every five minutes. The page never asks you to wait: if it checked a moment ago it simply
   shows what it saw. The page's own checks never use up the background check's retries.

IPRO's automatic background check backs off over time for a domain that keeps failing (checking less often, then eventually pausing automatic checks after a long stretch of failures). The guide's checks work regardless, and connecting the domain re-arms automatic checking.

## Add Multiple Domains

1. Confirm the active package permits additional domains.
2. Add each domain under **Your domains** on the **Domain** page.
3. Configure the `www` CNAME at each registrar.
4. Select **Primary** beside the preferred domain.
5. Remove unused domains with **Remove** — type the exact domain name to confirm, since this cannot be undone.

All bound domains display the same agent website and selected content.

### What happens when a domain is removed

Removing a domain (or Super Admin deleting the agent) also removes the address from IPRO's hosting in Azure:
its binding, and its certificate when the certificate was made for it. Each address is written down with the
removal and IPRO keeps asking Azure until Azure confirms; the background domain check does this every five
minutes at first, then hourly, then daily, so an Azure hiccup or a switched-off setting is caught up on its
own. An address another domain still uses, and IPRO's own addresses, are never removed.

Until 592 (2026-10-19) a removal that did not get through was forgotten: `www.411trades.com` stayed bound
after it left its account, its DNS still pointed at IPRO, and another account could add it and show its own
site there. The TXT proof above now stops that even if a binding is left behind. Bindings left behind
before 592 are listed by `ops/domain-ownership/orphan-bindings.sh` (read-only); removing one is the owner's
decision.

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
2. Filter by **All open** (everything except dismissed leads), unread, new, contacted, or dismissed.
3. Search by name, email, phone, message, or source.
4. Narrow further with a **From**/**To** date range.
5. Sort by newest first, oldest first, or by status.
6. Open the connected CRM contact when available.
7. Use **Plan Follow-up** to schedule the next action for connected CRM contacts.
8. Mark the lead **Contacted** after responding.
9. **Dismiss** a lead you do not need to act on. It leaves your open list and stays under **Dismissed**.
10. Use **Mark all read** when appropriate.

The dashboard displays new and unread lead counts.

### Delete a lead for good

Use the red trash button on a lead to remove it permanently: spam, a test you sent yourself, or a person who asked you to remove their details. The lead and any form answers it carried are deleted and cannot be brought back. A CRM contact that was created from the lead is your own record and is kept; delete it under **Clients** if it should go too.

## Bulk Actions and Export

1. Check the box beside each lead to act on, or use **Select all** to select every lead currently shown on the page.
2. Click **Mark Selected Contacted** or **Dismiss Selected** to apply that status to every selected lead at once, or **Delete Selected** to remove them for good.
3. Click **Export CSV** to download every lead matching the current filter, search, date range, and sort as a spreadsheet-ready file — not just the leads on the current page.
