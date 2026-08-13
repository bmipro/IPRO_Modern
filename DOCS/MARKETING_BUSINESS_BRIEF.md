# IPRO Advisers — Business Brief for Marketing & Design Work

Written 2026-08-13 as the shared ground truth for the marketing-strategy and site-design work.
Everything here is verified against the live codebase and the seeded package data, not assumed.
If you are a strategist or designer picking this up: **this file is the product reality. Do not
invent features, prices, customer counts, or testimonials that are not in here.**

---

## 1. What the company is

**IPRO Advisers** sells an all-in-one web presence + practice-management platform to independent
financial professionals. One subscription replaces the four-to-six separate tools a solo adviser
normally stitches together: a website, a CRM, an email-marketing tool, a scheduler, a client
portal, and an invoicing tool.

The owner/operator is Bahman Motamed. The product is built and production-deployed on Azure. It is
a real, working, shipped product — not a concept.

The **business** has operated since at least 2014 (see "Company history" in §5). The **current
platform** is a ground-up rebuild that is finished and QA'd but has no meaningful paying customer
base on it yet. So: an established company relaunching on new software, not a startup with its
first product. This is the moment it turns from building to selling.

### Target customers (the three verticals the product actually ships content for)

The product pre-populates a new customer's website with real starter content chosen by vertical.
Exactly three verticals exist in the code:

1. **Insurance / Financial advisers** — the primary and original market
2. **Mortgage brokers / agents**
3. **Accountants / bookkeepers** (the deepest starter-content library: ~19 articles across 4 categories)

Buyer profile: a solo practitioner or a 2–10 person practice. They are licensed professionals,
not technologists. They are typically 35–65. They are time-poor, they already have clients, and
their existing "website" is either nonexistent, a stale WordPress site a nephew built in 2019, or
a corporate-issued page they cannot edit. They do not want to learn software.

### Geography

Primarily **Canada**. The billing engine calculates real provincial sales tax by province
(GST/HST/PST/QST — all provinces seeded). US customers are supported and treated as no-tax.
Currency is CAD. This Canadian-first posture is a genuine differentiator against US-built
competitors that cannot handle provincial tax or Canadian compliance language.

---

## 2. Pricing (verified from `PackageEntitlementSeeder.cs` — these are the real numbers)

| Plan | Monthly | Quarterly | Annual | One-time setup | Clients | Domains | Storage |
|---|---|---|---|---|---|---|---|
| **IPro Silver** | $40 | $120 | $480 | $150 | 500 | 2 | 50 MB |
| **IPro Gold** | $60 | $180 | $720 | $200 | Unlimited | Unlimited | 500 MB |
| **IPro Platinum** | $90 | $270 | $1,080 | $400 | Unlimited | Unlimited | 1,000 MB |
| **Broker Package** | Contact us | — | — | Varies | Unlimited | Unlimited | 1,000 MB/user |

Corrected 2026-08-13: an earlier draft of this table listed Silver's domain cap as 12. That figure is
`MaxNewsletters` from the `PackageDefinition` constructor, not a domain limit — the real cap is the
`MultiDomainSupport` entitlement, which is **2** for Silver and unlimited above
(`PackageEntitlementSeeder.cs` line 197). Silver's team-login seat count is **1**, not zero, so
describe it as "just you" rather than as having no team feature.

All prices are **pre-tax**; provincial sales tax is added at checkout and this is disclosed on the
public page today. Annual billing works out to 12× monthly with no discount currently — *note this
as a strategy question, not a fact to advertise.*

The **setup fee is the single biggest friction point in the funnel.** A prospect sees $40/month and
then discovers $150 more due today. It is currently disclosed but not justified. Also live in the
system and currently unused as marketing levers: **promotion codes** (percent or flat discount on
recurring price for N cycles or permanently, and/or a setup-fee discount, with expiry, redemption
limits, and package restriction — fully integrated with PayPal) and a **free-trial mechanism**
(trial packages with invite codes, duration in days, and automated reminder emails).

Payment is via **PayPal** subscriptions (sandbox-proven end-to-end; live credentials pending).

---

## 3. What the product actually does (feature reality by tier)

### On every plan, including Silver
- **Instant website**, pre-populated with real content for the customer's vertical — live within
  minutes of signup at `theirname.iproadvisers.com`, no design work required
- Three website templates (Modern, Classic, Editorial), each with theme variants
- A real page builder: pages, 3-level navigation with mega-menu, and content blocks — text, image,
  video, photo gallery with lightbox, reviews badge, testimonials, agent bio, section index,
  lead magnets, custom forms, and **financial calculators** (ported from the legacy product)
- Custom domain support with **automatic SSL certificate provisioning**
- Lead capture forms → an automatic prospect/lead inbox
- CRM: clients, account types/groups, notes, activity timeline, follow-ups
- Calendar/scheduler with email reminders
- Newsletters (rich-text editor, starter templates, banners, editions) with SendGrid delivery and
  open/failure tracking
- Drip/automated marketing campaigns
- Testimonial collection (including request-by-email to a named client)
- Polls & surveys
- Custom form builder with 8 starter templates
- Lead magnets (gated downloads)
- Article library + "Did You Know" gated-content teasers
- Social media post composer (with AI drafting)
- Marketing calendar
- SEO tools
- Built-in unsubscribe/consent handling (RFC 8058 one-click), so email actually lands

### Gold adds
- Pre-designed **e-cards** (14 designs: birthdays, holidays, seasonal) and **e-letters** (merge-field
  templates) — the "stay in touch without writing anything" layer
- Rotating homepage banner, coupon manager, mail merge, printable client mailing labels
- Unlimited clients and domains, 500 MB storage

### Platinum adds — this is the real differentiator
- **AI Daily Assistant** — a daily digest that tells the adviser *who to call today and why*
  (new leads, leads older than 24h, clients with no follow-up scheduled), with a suggested next
  action and the reasoning behind it
- **Client Portal** — the adviser's clients get their own login: secure messages, document sharing,
  appointment requests, invoice viewing
- **Client invoicing & estimates** — line items, automatic tax from the client's province, a signed
  no-login approve/decline/pay link, recurring schedules, QuickBooks CSV export
- **Life-event reminders** — automatic follow-ups ahead of client birthdays, policy renewals,
  anniversaries
- One managed blog post per month, written for them
- Managed SEO across every page

### Broker Package adds
- Designated support contact, custom team/multi-agent pricing

### Recently shipped (not yet on the marketing site at all)
- **Team member logins** — an adviser's assistant/secretary gets their own login with everything
  except billing. Seats by tier: Gold 2, Platinum 5, Broker 10 (deliberately excluded from Silver).
- Full SuperAdmin accounting backend with invoice retention after account deletion

### Honest gaps — do not imply these exist
- **No SMS sending yet.** SMS reminders are costed and roadmapped but not built. The current public
  page says "email & SMS reminders" — *this is an accuracy problem to flag, not to perpetuate.*
- No in-portal payment processing for the adviser's own clients (IPRO never touches that money —
  the adviser supplies their own payment link)
- No IDX/MLS real-estate listing embeds
- No automatic social publishing (composer only — posts are drafted, not auto-published)
- No native mobile app
- No integrations marketplace (Google Calendar sync exists on Platinum/Broker)

---

## 4. The conversion asset that already exists — and is under-used

There is a working, no-account-required **prospect preview** at `/Preview`.

A visitor enters their name, business type, city, and chosen package, and the system builds them a
**real, live, browsable website with their own name on it in about 30 seconds** — populated with the
genuine starter content for their vertical, rendered through the real template engine (not a mockup),
plus a simulated AI Daily Assistant card showing what their morning would look like. From there they
can hit Register with everything pre-filled.

This is the single strongest thing the company has and it is currently one link on one page. Any
strategy should treat "see your own site before you give us anything" as the centre of the funnel,
not a feature bullet.

---

## 5. The current marketing site — and why it is the problem

**The entire public marketing presence is one Razor page**: `src/IPRO.Web/Views/Home/Index.cshtml`.
It is served at the root of the web app. There is no About page, no Contact page, no How-It-Works
page, no per-vertical page, no case studies, no blog, no help centre, no comparison page, no
privacy/terms pages, no social proof of any kind.

The page today has: a gradient hero ("Still juggling five logins to run your practice?") with a
browser-frame mock of the AI card, a four-row "what's included by tier" stack, a pricing section
with four cards plus a collapsible full feature-comparison table, and a closing CTA band.

Current visual identity as implemented:
- Deep navy `#1a3a6b` → blue `#2563eb` gradient hero
- Gold accent `#a9812f` on featured/premium elements
- Georgia serif headings, system sans body
- Bootstrap 5 + Font Awesome, both loaded from CDN
- Logo: `/images/ipro-advisers-logo.png` (white-inverted on the dark hero)

**The credibility gap the owner correctly identified:** IPRO sells "a strong, clear, concise website"
to advisers. Its own website is a single scrolling page that mostly lists features. The product's
own marketing is the least convincing demonstration of the product. Fixing that is the job.

### Live addresses

**The front door is `https://app.iproadvisers.com/`** — confirmed by the owner. That host serves the
marketing page at its unauthenticated root and redirects to the dashboard once signed in. Signup is
`https://app.iproadvisers.com/Account/Register`. Design and build for this host.

- SuperAdmin: `admin.iproadvisers.com`
- Customer sites: `theirname.iproadvisers.com` or their own custom domain
- Registration: `/Account/Register` · Login: `/Account/Login` · Preview: `/Preview`

**The bare domain currently serves something else.** `iproadvisers.com` and `www.iproadvisers.com`
return a separate legacy site: last modified January 2016, served by nginx, built on an unmodified
commercial HTML template whose demo navigation is still live ("Home variation", "Colors → Blue /
Green / Dark pink", `coming-soon.html`), carrying a Zopim chat widget, and pointing its signup link
at the **legacy** product at `247advisers.com/pub/register.aspx`. Owner's instruction, 2026-08-13:
*"That site could be used to learn but not as a front door."* So it is reference material, not a
page to design. Reconciling the two domains is a separate decision for the owner, not an assumption
for this work.

### Company history — this is not a startup

IPRO has been serving Canadian financial professionals since at least 2014. The legacy site carries
a portfolio of 11 client sites; of those, 3 still respond today (FinancialAgency.com,
girardfinancial.com, financialstudio.ca), 1 has become a domain-parking page, and 7 are gone — and
the survivors do not appear to run on IPRO infrastructure. Established social accounts exist:
`facebook.com/AllAdvisers`, `twitter.com/alladvisers`, `linkedin.com/company/i-pro-advisers`,
`pinterest.com/iproaccountants`.

The current platform is a **rebuild of a long-running business**, not a first product. That decade
of operating history is a legitimate and usable trust asset. Past client names and logos are **not**
usable without verification and permission, and must never be presented as current customers.

### Prior professional copy — 2014, worth mining

A set of professionally written marketing documents exists at
`\\tsclient\X\ipro_related\Paul_Words` — finished Home, About Us, Why Us, Features, Plans and
Pricing, and Support pages, three per-vertical pages, two tagline sets, a web-marketing checklist,
and banner and print-ad copy. The vertical arguments are well observed and still true, and it
contains commercial promises worth reviving (notably **"Monthly Billing. No Contract. Cancel
Anytime."**, which the current site does not say anywhere). The voice, however, is dated agency hype
and includes at least one unsupportable claim ("Top financial gurus of the country trust us") — mine
it for substance, never for tone.

---

## 6. Competitive context

Advisers evaluating IPRO are otherwise assembling: Squarespace/Wix/WordPress for the site
(~$25–40/mo) + a CRM like Wealthbox/Redtail (~$45–75/mo) + Mailchimp/Constant Contact (~$30–60/mo)
+ Calendly (~$12/mo) + a client portal or none. That stack runs $110–190/month, requires the adviser
to integrate it themselves, and none of it knows anything about their book of business.

Vertical competitors exist (Advisor Websites, Twenty Over Ten, FMG Suite) but are US-centric,
typically website-only, and generally price above IPRO for less scope.

**The strategic argument is not "cheaper website." It is "one system that already knows your
clients, so your marketing and your follow-ups come from the same place — and it tells you who to
call today."**

---

## 7. Constraints for anything produced

- **Truth constraint.** Every claim must be traceable to section 3. No invented testimonials, no
  fabricated customer counts, no "trusted by 500 advisers," no fake logos, no made-up awards. There
  are no current referenceable customers on the new platform and no testimonials to quote, so the
  writing has to persuade without conventional social proof. Two honest assets are available and
  should carry that weight: the **decade of operating history** (§5) and the **live 30-second
  preview** (§4). Beyond those, propose honest substitutes — a founder's promise, a specific
  guarantee, transparent pricing, real product screenshots.
- **Technical constraint.** The site is ASP.NET Core Razor. Final deliverables land as `.cshtml`
  views in `src/IPRO.Web/Views/`. Assets go in `src/IPRO.Web/wwwroot/`. A strict CSP is enforced:
  **inline event handlers do not work** (`onclick=` is silently dropped) and inline `<script>` needs
  `nonce="@Context.GetCspNonce()"`. Bootstrap 5 and Font Awesome are already available.
- **Pricing/feature data is live.** The pricing section reads real `BillingRule` rows from the
  database, so plan names, prices, and the feature-comparison table must stay data-driven, not
  hard-coded.
- **Audience constraint.** Write for a 50-year-old licensed insurance adviser in Ontario who is good
  at their job and bad at software, not for a startup audience. No growth-hacking vocabulary.
