# Product truth pack — 2026-08-28

**Purpose.** Two audiences. (1) The **marketing project** (separate from this repo) uses this as
its knowledge base so campaign copy can never claim a feature the product does not have — the
mistake the homepage audit already caught once ("SMS reminders", "one managed blog post a month").
(2) **Phase 3 of the launch runway** ("the front door tells the truth") uses the open questions
below as its work list.

Regenerate this file from the code whenever the product changes. Marketing copy is downstream of
it, never the other way round.

---

## 1. What the product demonstrably is

An ASP.NET Core 8 SaaS for Canadian advisers. Each agent gets:

- **A public website** on their own subdomain or custom domain (SSL automated), built from
  templates, with a page/navigation editor and content blocks (hero, services, forms, gallery,
  video, maps, testimonials, articles, calculator, lead magnet, polls).
- **A CRM**: clients, categories, follow-ups, life events, documents, an Outlook/CSV import.
- **Marketing tools**: newsletters, drip campaigns, e-cards, e-letters, polls/surveys, a
  "Did You Know" article mailer, website lead capture, testimonial requests, email
  activity/tracking, visitor analytics.
- **A client portal**: client logins, messages, documents, appointment requests, invoicing and
  estimates.
- **Scheduling**: calendar, Google Calendar two-way sync, reminders (calendar, birthday,
  life-event, overdue-invoice).
- **AI daily assistant** (Gold/Platinum) and **team member logins**.
- **Billing**: PayPal subscriptions, monthly/annual, setup fees with waiver windows, proration,
  invoicing with Canadian provincial tax.

Four editions are marketed: **Insurance / Financial, Accounting, Mortgage, Generic.**

---

## 2. Packages and pricing — READ FROM THE DATABASE, NEVER HARDCODE

Package names, monthly price, annual price and setup fee all live in `BillingRules` and render on
the homepage from live data. Marketing must quote whatever the live table says, and should link to
the pricing section rather than restating numbers, because prices change in SuperAdmin without a
deploy. Standing rule in this project: **never hardcode a price.**

Package tiers (names as shown to customers): **IPro Silver, IPro Gold, IPro Platinum**, plus a
**Broker Package**. Annual is pitched as "two months free". Setup fees exist per package and can be
waived for a window; the waiver shown on the page is the SAME call that decides what PayPal
charges.

---

## 3. OPEN QUESTIONS — features sold in the package table with no implementation found

The homepage pricing table renders **every** `PackageFeature` row marked included, so anything
here is currently being *promised to customers*. I searched the source for each concept, excluding
the two files that merely DEFINE the codes (`PackageFeatureCodes.cs`, `PackageEntitlementSeeder.cs`).

**Method caveat, stated up front:** absence of a name in the source is not proof a feature is
missing — it can ship under a different name. That already produced one false alarm here:
"Need analysis calculator" IS real, as the `Calculator` block (`_CalculatorBlock.cshtml`). So each
line below is a **question for the owner**, not a finding.

### 3a. Plausibly human services, not software (needs owner intent, not code)

| Feature | Label sold | Packages |
|---|---|---|
| `ManagedBlog` | RESOLVED 2026-08-28 -- **became a real product feature.** The Blog block lists the agent's own published articles on their site (inline `?post=` full view, no new route), and the article editor gained a Draft-with-AI button (gated by `AiDailyAssistant`, review-before-publish -- the author of record stays the human adviser). Label now "Blog on your website - publish your own articles". Same tiers. |
| `ManagedSeo` | RESOLVED 2026-08-28 -- **WITHDRAWN** (owner decision). Promised ongoing human work; the SEO TOOLING that exists stays sold as the built-in SEO tool row. |
| `DesignatedSupport` | RESOLVED 2026-08-28 -- **WITHDRAWN** (owner decision). (Was Broker-only, not Platinum as first drafted here -- the row was `no, no, no, all`.) Can return as package data if the commitment is ever made for real. |

ALL THREE RESOLVED 2026-08-28: ManagedBlog became a real product feature; ManagedSeo and
DesignatedSupport withdrawn. Section 3 is now fully dispositioned -- every feature the package
table sells either exists or is gone. **`ManagedBlog` is the exact claim the homepage audit flagged as a
service that did not exist.** (An earlier draft of this file said Gold+Platinum for these two; the Feature() signature is (silver, gold, platinum, broker), so `no, no, all, all` is Platinum+Broker. Corrected 2026-08-28.) If it is not being delivered, it must come out of the package data,
not just out of the homepage copy — the table renders from the database.

### 3b. RESOLVED 2026-08-28 (owner decision) -- four withdrawn, one renamed

| Feature | Outcome |
|---|---|
| `MailMerge` | **WITHDRAWN** -- definition and constant removed; rows deleted from existing databases |
| `PrintableLabelCreator` | **WITHDRAWN** -- same |
| `Newsboard` | **WITHDRAWN** -- same |
| `RotatingBanner` | **WITHDRAWN** -- same |
| `MultilingualEditor` | **KEPT, RENAMED** to "Supports multilingual content (paste from any editor)". The capability is real -- an agent writes in any editor and pastes it in; `bahmanmotamed.247advisers.com/article` is a live Farsi article created exactly that way. The old wording implied an editor we ship. |
| `FramedLinkManager` | **WITHDRAWN 2026-08-28** (owner agreed) -- the embeds that matter, Video and Maps, already exist as their own blocks |

Withdrawn means the row is DELETED, not un-ticked: the comparison table renders one row per
PackageFeature that exists, so an un-ticked row would still advertise the name with a dash against
every plan. Both halves shipped together (definitions + a startup repair for existing databases),
because `EnsureFeaturesAsync` only ever ADDS rows and never re-syncs an existing one -- which is
exactly how the SMS claim survived its first fix.

**`FramedLinkManager` -- open, with a recommendation.** A legacy concept: embed an external page in
a frame. Video and Maps embeds already exist. A general "frame any URL" block is buildable but
carries real baggage -- most modern sites send `X-Frame-Options`/CSP that refuse framing, so it
would silently render blank for many URLs, and letting agents frame arbitrary sites is a phishing
surface. Recommendation: retire it like the other four unless there is a specific thing to embed.

### 3c. Possible aliases — confirm before acting

| Feature | Label sold | Candidate existing feature |
|---|---|---|
| `RotatingBanner` | RESOLVED 2026-08-28 -- WITHDRAWN (see 3b). No rotating banner exists: the Gallery carousel and the static CallToAction "banner" variant are different things. |
| `CustomHomeButtons` | RESOLVED 2026-08-28 -- **RENAMED** to "Call-to-action sections with your own button text and link". The block carries the agent's own ButtonText/ButtonUrl in three layouts and works on any page, not just home. |
| `Newsboard` | RESOLVED 2026-08-28 -- WITHDRAWN (see 3b). |

### 3d. Already honest — no action

`SmsReminder` is labelled "Mobile SMS reminder (not yet available)" and is included in **zero**
packages. That is the pattern the others should follow if they are not shipping.

---

## 4. Claims marketing must NOT make until §3 is resolved

- Any per-feature claim drawn from the package comparison table without checking §3.
- Data location ("your data stays in Canada") — the Azure region is a Phase 3 verification item
  and is a PIPEDA-adjacent statement, not a copy flourish.
- Anything about SMS.
- Specific prices restated in copy rather than linked (they change without a deploy).

## 5. Facts marketing CAN rely on

- Canadian-built and supported; prices in CAD before applicable tax; provincial tax handled
  correctly including Quebec's GST+QST.
- Custom domains with automated SSL.
- The four editions above, each with its own starter website content.
- PayPal as the payment method.
- The client portal, AI daily assistant and invoicing are the Gold/Platinum differentiators.

---

**Status of the product itself as of 2026-08-28:** zero open CRITICAL, zero open HIGH, zero
in-scope MEDIUM defects; 381 automated tests green, none skipped. The launch blocker is not
software quality — it is §3 above, plus the PayPal sandbox→live cutover (Phase 4).
