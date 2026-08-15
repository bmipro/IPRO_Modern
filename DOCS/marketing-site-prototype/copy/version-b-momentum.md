# IPRO homepage — copy deck, Version B: "Momentum"

Alternative copy for `concept02-navy.html`. Section order and section ids match the prototype exactly, so this can be dropped in block by block. The prototype file itself is unchanged.

**Voice in one line:** Confident and forward-leaning — every line names a result the adviser gets, states it as a plain fact, and moves to the next one without stopping to admire itself.

**Spelling note:** the prototype uses British-style `-ise` endings (*organised*, *customisable*) alongside Canadian `-our`/`-re`. This deck matches the prototype for consistency. If the house style is Canadian Press (`-ize`), find-and-replace before porting.

**How to read the flags:** anything marked **[VERIFY]** is a line I could not trace to the fact sheet. Do not ship it until someone confirms it.

---

## 1. Header nav

| Slot | Prototype now | Version B |
| --- | --- | --- |
| Nav 1 | Solutions | Your market |
| Nav 2 | Platform | How it works |
| Nav 3 | Pricing | Pricing |
| Nav 4 | About | Why IPRO |
| Nav 5 | Contact | Contact |
| Nav 6 | Sign in | Sign in |
| Header CTA | Build my preview | See my site free |

Rationale: "Solutions" and "Platform" are software-vendor words. "Your market" and "How it works" tell a self-employed adviser what is behind the click. The header CTA is the highest-traffic button on the page — leading with *see* and *free* removes the two things people hesitate over.

---

## 2. Hero — `#i2-top`

**Eyebrow (`.i2-label`)**
> One subscription. The website that wins the enquiry and the system that keeps the client.

*(Shorter variant if the line wraps badly: "Website and client system. One subscription.")*

### H1 — three alternatives

**A — recommended (31 characters)**
> Be the adviser they find first.

**B (47 characters, closest in length to the current H1)**
> Win the enquiry. Then keep the client.

**C (49 characters, sharpest edge)**
> Better marketing wins the client. Be better marketed.

Notes for whoever ports this: the H1 rule is `max-width: 13ch` at `3.2rem`, so the current line breaks over three lines. A is the safest fit and the most confident. B carries the whole business model in six words and is the best all-rounder if A reads too boastful. C is the most commercially aggressive and names the reader's actual problem — use it only if the owner wants the page to pick a fight.

**Subhead (max ~52ch per line, two lines)**
> Your website, client records, follow-ups and newsletters run on one system, from one login, on one bill. It arrives already written for your market.

### Primary CTA — three alternatives

**A — recommended**
> See my site free

**B**
> Show me my website

**C**
> See it before I sign up

C is the most persuasive and the most honest — the preview genuinely needs no account and no card — but it is long for the button. A is the port-ready default.

**Secondary CTA**
> Pick my market

*(Prototype: "Choose my business type". "Pick my market" is shorter and matches the four-tab section it scrolls to.)*

### Trust chips (`.i2-hero-meta`, three slots)

1. No credit card
2. Cancel anytime
3. Canadian since 2014

Chip 3 replaces "Canadian support". "Canadian support" is a service-level claim I cannot source — **[VERIFY]** whether support is Canadian-staffed before using that wording anywhere. "Canadian since 2014" is directly supported and does more work.

---

## 3. Proof bar (`.i2-proof`, four items, no id)

1. One platform, one bill
2. Hosting, domain and SSL included
3. Cancel anytime
4. Your data exports whenever you want

Item 4 replaces "Help from real people" — a support claim I cannot size — with a switching-risk answer that is on the fact sheet. If the owner wants the human-support note kept, use "Real people, not a ticket queue" and mark it **[VERIFY]**.

---

## 4. Starting point — `#i2-start`

**Section label**
> Choose your market

**H2**
> Your site arrives written. Not blank.

**Intro**
> One platform underneath, four starting points on top. Pick your market and the pages, wording, articles, enquiry form and calculators arrive already written for it.

> **Accuracy guard for this section:** only *content, terminology and starter material* differ by market. The features and workflows are identical across all four. Nothing in this section may imply market-specific functionality.

### The four market cards

The tabs stay numbered `01`–`04` as in the prototype.

#### 01 — Insurance / Financial

- **Edition label:** Insurance / Financial edition
- **H3:** Advisers do not lose clients. They lose track of them.
- **Body:** Your site explains the advice and captures the enquiry; the record behind it holds every review, renewal and life event so none of it lives in your head.
- **Bullets:**
  - Insurance and planning pages, plus a library of real articles
  - Enquiry forms that open a client record on submission
  - Review, renewal and life-event follow-ups on a schedule

#### 02 — Accountants

- **Edition label:** Accounting edition
- **H3:** A practice that looks established on day one.
- **Body:** Accounting, bookkeeping and tax pages arrive written, and every enquiry that comes through them lands in the same client list you work from.
- **Bullets:**
  - Accounting, bookkeeping and tax pages with an article library
  - Enquiries added straight to your client records
  - Deadline and renewal follow-ups you set once

#### 03 — Mortgage

- **Edition label:** Mortgage edition
- **H3:** Turn mortgage enquiries into booked conversations.
- **Body:** Explain purchases, renewals and refinancing clearly, and keep every borrower and next action in one list instead of a spreadsheet.
- **Bullets:**
  - Purchase, renewal and refinance pages ready to publish
  - Canadian mortgage calculators — payment, affordability, land transfer tax by province
  - Borrower enquiries logged as prospects with follow-ups attached

#### 04 — Generic

- **Edition label:** Generic edition
- **H3:** Any professional-service business, same engine underneath.
- **Body:** Start from a neutral professional site, change the pages and language to the work you actually do, and run the same CRM, marketing and follow-up tools as everyone else.
- **Bullets:**
  - A professional site you rewrite to fit
  - Lead capture wired into client records
  - Newsletters, campaigns, forms and scheduling included

### Example-site mock (the `.i2-site` preview panel)

Keep the fictional brands — they read as illustrations, not testimonials.

| Market | Brand | Label | Headline | Sub | Services |
| --- | --- | --- | --- | --- | --- |
| Insurance / Financial | Cedar Advisory Group | Insurance and financial advice | Advice that keeps up with your life. | Protection, retirement and investments, reviewed when your circumstances change. | Life insurance / Retirement / Reviews |
| Accountants | Northline Accounting | Accounting and tax | Clear numbers. Better decisions. | Accounting, bookkeeping and tax support for owners who want to know what comes next. | Accounting / Bookkeeping / Tax planning |
| Mortgage | Harbour Mortgage Advice | Mortgage guidance | The right mortgage, explained plainly. | Straight answers on purchases, renewals, refinancing and investment properties. | Purchases / Renewals / Refinancing |
| Generic | Your Business Name | Professional services | Turn interest into lasting business. | A clear online presence and the tools to manage the leads and clients that follow. | Your services / Client support / Resources |

---

## 5. Client journey — `#i2-platform`

**Section label**
> One record, start to finish

**H2**
> A stranger finds you on Monday. You still know why on Friday.

**Intro**
> No exports between tools, no retyping the same name three times, no second subscription. The record created by your website form is the record you work from for the life of the relationship.

**Five steps**

1. **They find you**
   Your site is already written and already indexed as a real public address, so it earns the click your competitor is currently taking.
   *(**[VERIFY]** — "indexed" implies SEO behaviour not on the fact sheet. Safe fallback: "Your site is written, live and public from day one, so it earns the click.")*

2. **The enquiry lands**
   The website form creates a prospect record in your client list the moment it is submitted, with the market-specific "Request a Meeting" form doing the asking.
   *(**[VERIFY]** — the prototype claims the lead "alerts you immediately". Email notification is not on the fact sheet; I have written the record creation, which is. Add the alert only if confirmed.)*

3. **You follow up**
   Follow-ups carry a name, a reason and a date, so the next call is decided before you sit down to make it. On Platinum, the Daily Assistant puts the day's list in front of you.

4. **They become a client**
   Notes, account type, meetings, campaigns and — on Platinum — the client portal and their invoices all hang off the same record.

5. **They stay**
   Newsletters, drip campaigns, e-cards and review reminders keep you in front of them between meetings, which is where most advisers quietly lose people.
   *(E-cards and e-letters are Gold and above. If the step must stay package-neutral, cut "e-cards".)*

---

## 6. "Your first week" replacement — three options

*(Section after `#i2-platform`, before `#i2-pricing`. No id in the prototype; layout is `.i2-story` — label, H2, intro on the left, four `.i2-story-point` items on the right, each with an icon, an H3 and one sentence.)*

The owner's objection is correct: a week-long ladder describes a project. This product is self-serve and can be live the same day, so every option below removes the calendar.

### Option 1 — sequence, no time references at all

**Label:** The order of operations
**H2:** Four moves and the business is running.
**Intro:** Nothing here waits on a designer, a developer or a support ticket. You do each step yourself, in this order, and stop when it looks right.

1. **See it before you commit**
   Enter your name, business name and market, and a real website appears — no account, no card.
2. **Make it sound like you**
   Rewrite pages, swap colours and rearrange content blocks yourself in the built-in page builder.
3. **Bring your clients across**
   Import the records you already keep and sort them by account type.
4. **Point your domain at it**
   Connect the address you already own; hosting and the SSL certificate come with the subscription.

### Option 2 — genuine speed, no invented numbers

**Label:** Faster than a quote from an agency
**H2:** Most of this is done before your coffee goes cold.
**Intro:** There is no build queue, no kickoff call and nothing to install. The site is generated the moment you describe the business — everything after that is editing, not waiting.

1. **First, look at it**
   The preview builds while you are still on the page, before you have created anything.
2. **Then make it yours**
   Change the words, colours and services in the same sitting, block by block.
3. **Then bring your clients**
   Import your records and start working from the follow-up list the same day.
4. **Your domain, when you are ready**
   Connect the domain you already own — the only wait left is your registrar, not us.

*(Item 4 is deliberately hedged: DNS propagation is outside IPRO's control, so no time claim is made.)*

### Option 3 — objection handling — **recommended**

**Label:** What actually holds people back
**H2:** Switching costs you nothing you are afraid of losing.
**Intro:** Advisers rarely stall on features. They stall on the four things they think a move will cost them.

1. **"I'd lose the site I already paid for."**
   Build and look at yours first — the preview costs nothing and commits you to nothing, so you compare before you decide.
   *(**[VERIFY]** — I have deliberately not claimed the old site can stay live in parallel. If that is true and worth saying, confirm it and the line becomes stronger.)*
2. **"My client list is a mess."**
   Import what you have, tidy it inside the CRM, and export the whole thing whenever you want it back.
   *(**[VERIFY]** — do not name a file format until someone confirms which imports are supported.)*
3. **"My domain took years to build up."**
   Keep it. You point your existing domain at IPRO, and hosting and SSL are part of the subscription.
4. **"I don't have a week to give this."**
   You do not need one — nothing to install, nobody to brief, and the site starts from finished content rather than a blank page.

### Which one I recommend

**Option 3.** It fixes the owner's complaint by removing the calendar entirely and, more usefully, it sits directly above the pricing table where a reader who is juggling three vendors is deciding whether switching is worth the disruption. Option 1 is the safe minimum-change fallback, and Option 2's headline can be lifted into Option 3's item 4 if the owner wants the speed note kept.

---

## 7. Pricing — `#i2-pricing`

**Section label**
> Every price on one screen

**H2**
> Three packages. One bill. No quote to wait for.

**Intro**
> Every package includes the website, hosting, SSL, client records, leads, follow-ups, calendar, newsletters, forms and the page builder. Gold adds e-cards, e-letters and mail merge. Platinum adds the client portal, invoicing, Google Calendar sync and the Daily Assistant. Pay yearly and two months are free.

**Setup-fee line (place near the fee row)**
> Setup is $150 on Silver. On Gold and Platinum it is waived until 30 September.

The prototype's struck-through Gold `$200` and Platinum `$400` figures are not on the fact sheet — **[VERIFY]** against `BillingRule.SetupFee` before the strike-through renders. Per the porting note in the prototype, all prices, the fee and the waived state must come from `BillingRule` rows, never hardcoded.

**Below-table note (keep as is, it is correct)**
> Canadian dollars before applicable tax.

**Package column sublines (optional, if the design gains a one-liner under each package name)**

| Package | Subline |
| --- | --- |
| Silver | One person, up to 500 clients. |
| Gold | Two logins, unlimited clients, the full mail toolkit. |
| Platinum | Five logins, client portal, invoicing and the Daily Assistant. |

**Team pricing line (`.i2-pricing-footer`)**
> Brokerage or a team of advisers? Tell us how many people, how many sites and what support you need, and we will price it in one conversation.

**Team pricing button**
> Talk team pricing

---

## 8. Why IPRO — `#i2-trust`

**Section label**
> Why businesses move to IPRO

**H2**
> One provider. One login. One number to call when something breaks.

**Intro**
> IPRO has built websites and business tools for Canadian professional-service firms since 2014. That is the whole pitch: instead of a web designer, a spreadsheet and a mail tool that do not speak to each other, there is one system and one place to ask.

### Point 1 — the founder / experience block (`.i2-founder`)

- **H3:** Canadian, and not new at this
- **Small:** Websites and business tools since 2014
- **Body:** Your website, hosting, client records, marketing and support all sit with one team. One system to learn, one invoice, and no vendor pointing at another vendor when something goes wrong.

### Points 2–5 (`.i2-trust-item`)

2. **Your data leaves as easily as it arrives**
   Import the records you have, work from them here, and export them whenever you want — no hostage-taking on the way out.

3. **Hosting, domain and SSL are ours to worry about**
   The certificate, the renewal and the connection to your domain are part of the subscription, not a separate bill and a separate login.

4. **Nothing starts from a blank page**
   Your site launches with written pages for your market, a Resources library of real articles, a "Request a Meeting" form and a set of Canadian financial calculators — then you edit any of it yourself.

5. **Built to Canadian rules, not adapted to them**
   Prices in CAD, provincial tax handled, Canadian mortgage conventions in the calculators, and consent and unsubscribe handling built into the mail tools.

---

## 9. Contact — `#i2-contact`

**Section label**
> Talk to IPRO

**H2**
> See the site first. Decide after.

**Intro**
> Give us your name, your business name and your market, and the preview builds immediately — no account, no card, no call booked before you have seen anything. Questions about packages, domains, migration or team use get a straight answer.

**Three contact items**

| Heading | Body |
| --- | --- |
| Free website preview | Your market, your business name, real starter content — on screen in one step. |
| Packages and setup | Pricing, domains, moving your records across and multi-adviser options. |
| Already a customer | Training, billing and technical help. |

> **Watch this one:** the prototype's current line, "We will show the appropriate website direction", reads as though a person builds your site. The product is self-serve. The rewrite above keeps the generator as the actor.

**Form labels (unchanged in structure)**

- Name — *Your name*
- Email — *you@business.ca*
- Business type — Insurance / Financial · Accountants · Mortgage · Generic
- What are you building? — *Your website, your client list, or the tools you are trying to replace*

**Form submit button**
> Build my preview

---

## 10. Footer

**Footer tagline (under the logo)**
> The website, CRM, marketing and client tools Canadian advisers and small firms run their business on. One subscription, since 2014.

**Column headings** — unchanged: Product · Markets · Company

**Product links** — How it works · Pricing · Free preview · Sign in
**Markets links** — Insurance / Financial · Accountants · Mortgage · Generic
**Company links** — Why IPRO · Contact · Privacy · Terms

**Footer bottom, left**
> © 2026 IPRO Advisers. Canadian dollars before applicable tax.

**Footer bottom, right**
> Built and supported in Canada.

---

## 11. Hero portal mock (`.i2-command`) — supporting copy

Visible copy, so it is included for completeness. The mock shows a Platinum account; the "next best action" panel is the Daily Assistant and should not be presented as standard on every package.

| Element | Version B |
| --- | --- |
| Dashboard greeting | Good morning, Michael — Thursday, 13 August |
| Next-action label | Your next best action |
| Next-action headline | Call Jennifer Walsh first. |
| Next-action body | Her policy review is four days overdue and the renewal lands next month. |
| Stat 1 | 3 new website leads |
| Stat 2 | 5 follow-ups today |
| Stat 3 | 2 unread messages |
| Clients panel sub | Showing 5 of 128 |
| Follow-ups panel sub | Four open this week |
| Calendar next-appointment | Jennifer Walsh, 9:30 this morning. |
| Calendar body | Annual policy review, booked from her request in the client portal. |
| Website panel sub | Your public address and pages |
| Leads panel sub | Straight from your website forms |

**[VERIFY]** — the mock's "6 pages" site fact. The fact sheet does not state how many pages a new site ships with.

---

## Claim register — what this deck asserts and where it comes from

| Claim used | Source |
| --- | --- |
| Website + client portal, one subscription | Fact sheet, opening line |
| $40 / $60 / $90 per month; $400 / $600 / $900 per year; two months free | Fact sheet, packages |
| Setup $150 Silver; waived on Gold and Platinum until 30 Sep | Fact sheet, setup fee |
| 500 clients on Silver; unlimited on Gold and Platinum | Fact sheet, limits |
| 1 / 2 / 5 team logins | Fact sheet, limits |
| Hosting, domain connection, SSL in every package | Fact sheet, inclusions |
| CRM, leads, follow-ups, calendar, newsletters, drips, polls, forms, page builder | Fact sheet, inclusions |
| E-cards, e-letters, mail merge = Gold | Fact sheet |
| Client portal, invoicing, Daily Assistant, Google Calendar sync = Platinum | Fact sheet |
| Daily Assistant = a daily list of who to contact next | Fact sheet |
| Sites ship with written starter content, Resources library, Request a Meeting form, Canadian calculators | Fact sheet |
| Calculator list: mortgage payment, affordability, land transfer tax by province, retirement, savings, after-tax return | Fact sheet |
| Preview needs only name, business name and market — no account, no card | Fact sheet |
| Four markets: Insurance / Financial, Accountants, Mortgage, Generic | Fact sheet + prototype |
| CAD, provincial tax, Canadian mortgage conventions, consent/unsubscribe | Fact sheet |
| Since 2014 | Fact sheet |
| Cancel anytime, import and export your records | Fact sheet |
| Broker/team pricing by conversation | Fact sheet |

**Deliberately absent, and why:** no SMS or texting of any kind; no line implying a person builds, reviews or sets up the site; no market-specific *features* or *workflows* (content and terminology only); no customer counts, named clients, testimonials, ROI figures, uptime or awards; no countdown or manufactured deadline — the 30 September setup-fee waiver is the only date used, stated plainly.

**Style compliance:** sentence case throughout, no exclamation marks, and none of *unlock, supercharge, seamless, revolutionise, game-changer, empower, effortless, 10x*.
