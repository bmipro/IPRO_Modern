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
| `ManagedBlog` | "One unique blog per month written and managed" | Gold, Platinum |
| `ManagedSeo` | "Managed SEO for all pages" | Gold, Platinum |
| `DesignatedSupport` | "Designated support" | Platinum |

These need no code if IPRO delivers them by hand — but the business must actually be committed to
delivering them from day one. **`ManagedBlog` is the exact claim the homepage audit flagged as a
service that did not exist.** If it is not being delivered, it must come out of the package data,
not just out of the homepage copy — the table renders from the database.

### 3b. Software features with no implementation found and no obvious alias

| Feature | Label sold | Packages |
|---|---|---|
| `MailMerge` | "Mail merge function" | 3 of 4 |
| `PrintableLabelCreator` | "Printable label creator" | 3 of 4 |
| `MultilingualEditor` | "Multilingual editor support" | all 4 |
| `FramedLinkManager` | "Framed link manager" | all 4 |

### 3c. Possible aliases — confirm before acting

| Feature | Label sold | Candidate existing feature |
|---|---|---|
| `RotatingBanner` | "Rotating banner" | the `Hero` block — but "rotating" implies a carousel |
| `CustomHomeButtons` | "Create custom buttons on home page" | the `CallToAction` block |
| `Newsboard` | "Newsboard" | possibly the `DidYouKnow` mailer, possibly nothing |

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
in-scope MEDIUM defects; 378 automated tests green, none skipped. The launch blocker is not
software quality — it is §3 above, plus the PayPal sandbox→live cutover (Phase 4).
