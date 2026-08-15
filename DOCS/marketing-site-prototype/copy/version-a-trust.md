# IPRO homepage — copy deck, version A: trust

Maps 1:1 onto `concept02-navy.html`. Section ids are the prototype's own. Nothing in this deck asks for a design change; where a claim needs a markup or data change to be true, it is called out in a **Note**.

**Voice in one line:** Say the true thing, in the fewest plain words, to someone who has already been sold to once and did not enjoy it.

Rules applied throughout: Canadian spelling, sentence case headings, no exclamation marks, no SMS or texting anywhere, no suggestion that anyone but the customer edits the site, no invented proof.

---

## Header — nav

| Element | Copy |
| --- | --- |
| Nav 1 (`#i2-start`) | Who it's for |
| Nav 2 (`#i2-platform`) | How it works |
| Nav 3 (`#i2-pricing`) | Pricing |
| Nav 4 (`#i2-trust`) | About |
| Nav 5 (`#i2-contact`) | Contact |
| Nav 6 (`#i2-signin`) | Sign in |
| Nav CTA (`#i2-preview`) | See a free preview |

Why the changes: "Solutions" and "Platform" are agency words, and this reader has heard them from the agency that let him down. "Who it's for" and "How it works" say what is behind the link. The CTA drops "Build my" because the visitor is not building anything at that moment; they are looking.

**Note:** `#i2-preview` and `#i2-signin` are referenced by the nav, the hero, the footer and the contact form, but no element with either id exists in the prototype. Both need a real target before launch, or the links dead-end on the reader who is furthest along.

---

## `i2-top` — hero

**Eyebrow**
> Canadian software for adviser businesses since 2014

**H1 — three alternatives**

The CSS sets `max-width: 13ch` on the H1 at 3.2rem, so this headline wraps into a narrow column. Six to nine short words holds three lines. Anything longer stacks into a wall.

1. **Your website and your client records, one subscription.** — The plainest true statement of what IPRO is. Recommended for a cautious reader: it makes no promise about outcomes, only about scope.
2. **See your finished website before you pay anything.** — Leads with the risk reversal instead of the product. Strongest if traffic is cold and the preview is the whole pitch.
3. **The website is the easy part.** — Speaks directly to a man who has already bought a website and found it did nothing. Sets up the subhead to carry the client-management half. Riskiest of the three, because it undersells the site itself.

**Subhead** (CSS caps at 52ch wide, so keep it to roughly 30 words)
> One place for the website, the client records, the follow-ups and the newsletters. Built in Canada, priced in Canadian dollars, cancel whenever you like.

**Primary CTA** (`#i2-preview`)
> See a free preview

**Secondary CTA** (`#i2-start`)
> Look at my market first

**Trust chips**
1. No credit card
2. No contract, cancel anytime
3. Canadian company since 2014

Chip 3 replaces "Canadian support", which reads as a claim about staffing that the fact sheet does not support. "Canadian company since 2014" is verified and does more work anyway.

### Hero portal mock — on-screen labels

The mock is demo data, not copy, but a careful reader will read it as a product claim, so it has to be honest about packages.

| Panel | Current | Issue and fix |
| --- | --- | --- |
| Dashboard | "Your next best action" | This is the Daily Assistant, **Platinum only**. Label it "Daily Assistant" so the reader does not assume it comes with Silver. |
| Calendar | "booked from her request in the client portal" | Client portal is **Platinum only**. Either add the badge or change to "booked from her request on your website", which is true on every package. |
| Marketing | "Birthday e-cards" | E-cards are **Gold and above**. Fine to show, worth a small package badge. |
| Website | "SSL secured", "Live", "6 pages" | Accurate on every package. No change. |
| Clients | "Showing 5 of 128" | Accurate. No change. |

**Suggested convention:** a small "Platinum" chip on the two panels above. It costs a reader nothing and it stops the pricing table from feeling like a bait-and-switch when they reach it.

---

## Proof bar (no id, `aria-label="IPRO service benefits"`)

1. One subscription, not five
2. Hosting, domain and SSL included
3. Cancel anytime
4. Real people to ask

Item 1 changes "One connected platform" into the thing the reader actually resents: paying four vendors. Item 4 keeps the support promise honest — people answer questions, they do not do the work for you.

---

## `i2-start` — choose your starting point

**Section label**
> Four starting points

**H2**
> Your site starts with the writing already done.

**Intro**
> The platform is identical for everyone. What changes is the words: the starter pages, the article library, the meeting request form and the terminology are written for the market you pick.

That last line matters. The old intro said the site and terminology change by market, which invites the reader to think the features change too. They do not, and promising that is how a demo goes badly.

### Card 01 — Insurance / Financial

- **Label:** Insurance and financial edition
- **Title:** Written for advisers who manage relationships over years.
- **Copy:** Your pages arrive explaining protection, retirement and investment advice in the words you already use with clients. Enquiries land in the same client record you work from every day.
- **Bullets:**
  1. Insurance and planning pages, already written
  2. A Resources library of real articles, not placeholder text
  3. A "Request a Meeting" form written for advice work

### Card 02 — Accountants

- **Label:** Accounting edition
- **Title:** A practice that reads as established from the first day.
- **Copy:** Accounting, bookkeeping and tax pages come written and organised. Every enquiry becomes a record you can follow up on, rather than an email you meant to answer.
- **Bullets:**
  1. Accounting, bookkeeping and tax pages, already written
  2. A Resources library of real articles for practice clients
  3. A "Request a Meeting" form written for accounting work

### Card 03 — Mortgage

- **Label:** Mortgage edition
- **Title:** Explain purchases, renewals and refinancing once, properly.
- **Copy:** The pages that answer the same six borrower questions are already written. Canadian calculators are built in, including land transfer tax by province.
- **Bullets:**
  1. Purchase, renewal and refinancing pages, already written
  2. Canadian mortgage payment, affordability and land transfer tax calculators
  3. A "Request a Meeting" form written for borrower enquiries

### Card 04 — Generic

- **Label:** General professional edition
- **Title:** A neutral starting point you can point at your own work.
- **Copy:** A professional site with the structure in place and the language kept general, so you can change the services and wording to match what you actually do.
- **Bullets:**
  1. A complete professional site, worded generally
  2. A Resources library you can add to
  3. A "Request a Meeting" form you can rename and reword

**Shared line for all four cards** (add below the bullet list, or as the panel footer)
> Same CRM, same leads, same follow-ups, same newsletters. Only the writing differs.

### Example website mock (inside the panel)

| Market | Site label | Headline | Site copy | Services |
| --- | --- | --- | --- | --- |
| Insurance / Financial | Insurance and financial advice | Advice that keeps up with your life. | Protection, retirement and investments explained plainly, and reviewed when your circumstances change. | Life insurance · Retirement · Reviews |
| Accountants | Accounting and tax | Numbers you can actually act on. | Accounting, bookkeeping and tax support for owners who want to know what comes next. | Accounting · Bookkeeping · Tax planning |
| Mortgage | Mortgage guidance | A straight answer on your mortgage. | Help with purchases, renewals, refinancing and investment properties, without the runaround. | Purchases · Renewals · Refinancing |
| Generic | Professional services | Clear work, clearly explained. | An honest description of what you do, and the tools to look after the people who get in touch. | Your services · Client support · Resources |

**Note:** the mock uses invented firm names (Cedar Advisory Group, Northline Accounting, Harbour Mortgage Advice). They read as real customers to a reader who has been told the rules about testimonials. Add a visible "Example" tag on the mock, or use obviously neutral names. The `aria-label` says "Example customer website" but nobody sees an `aria-label`.

---

## `i2-platform` — one client journey

**Section label**
> One record, start to finish

**H2**
> From a stranger on your website to a client you keep.

**Intro**
> No exporting, no retyping, no second subscription. The record created when someone fills in your form is the same record you are still working from three years later.

**Five steps**

1. **They find you** — Your website explains the work in finished, written pages, so the first impression is not a placeholder.
2. **A lead arrives** — The form creates a prospect record and tells you it is there.
3. **You follow up** — Your follow-up list holds the name, the reason and the date, so it is not sitting in your head.
4. **They become a client** — Notes, account type, meetings and history live on one record you can search. *(Invoicing joins that record on Platinum.)*
5. **You stay in mind** — Newsletters, drip campaigns and review reminders keep you present between the meetings that matter.

**Note on step 3:** the prototype says "the dashboard raises the right name and reason at the right time". That is the Daily Assistant, which is Platinum only. The wording above describes the follow-up list, which every package has, and leaves the assistant to be sold in the pricing table where it belongs.

---

## The "first week" section (no `id` in the prototype)

The owner is right that this section undersells the product, and "Today / Day one" does read as two names for the same moment. Three complete replacements follow. Each is a drop-in for the whole section: label, H2, intro, four items.

**Note:** this section is the only major one without an id. Suggest `id="i2-ready"` (option 3) or `i2-steps` (options 1 and 2) so the nav and any future links can reach it.

### Option 1 — sequence, no time words at all

- **Section label:** In your own order
- **H2:** Four things to do, and none of them wait on us.
- **Intro:** You see the site before you sign up, you edit it yourself, you bring your records over, and you point your domain at it. How long that takes is your call, not a schedule we set.

1. **Look before you sign up** — Enter your name, business name and market, and a real website appears; no account, no credit card.
2. **Make it yours** — Change the pages, colours, services and wording yourself in the page builder, without asking anyone.
3. **Bring your clients in** — Import the records you already keep, and organise them by account type.
4. **Point your domain at it** — Connect your own web address; hosting and the SSL certificate are part of the subscription.

### Option 2 — time, used to show speed

- **Section label:** From preview to live
- **H2:** You can be online the same afternoon.
- **Intro:** The preview takes about a minute. Everything after that is your own editing time, and you decide how much of it to spend.

1. **About a minute: see a real site** — Name, business name, market, and a working website appears on screen. **[VERIFY]** — the "about a minute" figure is a reasonable read of a three-field form, but it is not in the fact sheet.
2. **The same afternoon: make it yours** — Rewrite the pages, change the colours, set your services. Nothing is queued and nobody schedules you.
3. **Whenever you are ready: bring your clients** — Import your existing records and organise them by account type.
4. **Then your domain, on DNS time** — You connect it in a few minutes; the internet takes a few hours to catch up. That wait is the one part nobody controls.

Item 4 deliberately admits the slow step. On this page, one honest limitation buys more than three confident claims.

### Option 3 — reframe: what is already done before you touch anything

- **Section label:** Before you change a thing
- **H2:** Nothing starts from a blank page.
- **Intro:** Most website tools hand you an empty canvas and call it freedom. Yours arrives with the writing already done for the market you chose, so the first decision is what to change, not what to write.

1. **The pages are already written** — Real starter content for insurance and financial, accounting, mortgage or general practice, in the language of that market.
2. **The article library is real** — A Resources section of genuine articles, not headings with placeholder text underneath.
3. **The meeting form fits your market** — A "Request a Meeting" form worded for the work you do, connected to your client records.
4. **The Canadian calculators are in place** — Mortgage payment, affordability, land transfer tax by province, retirement, savings and after-tax return.

### Recommendation

**Option 3.** It replaces a promise about the customer's future effort, which is the thing the owner objects to and the thing nobody can actually guarantee, with a claim about what already exists on the day they log in. It is also the strongest verified fact on the whole page, and it answers the exact objection of a man who has paid an agency before and received a half-empty site with "your content here" in it.

If you want the sequencing back, add one line under the intro rather than a second section: *"After that, you edit it yourself, bring your records over and point your domain at it."*

---

## `i2-pricing` — packages

**Section label**
> What it costs

**H2**
> Three packages, all in Canadian dollars.

**Intro**
> Every package includes the website, hosting, SSL, client records, leads, follow-ups, calendar, newsletters, forms and the page builder. Gold adds e-cards, e-letters and mail merge. Platinum adds the client portal, invoicing, Google Calendar sync and the Daily Assistant.

**Annual line** (beside or beneath the table)
> Pay annually and you pay for ten months, not twelve.

**Table footnote** (replaces "Canadian dollars before applicable tax.")
> Canadian dollars, before applicable tax. Cancel anytime; there is no term to see out.

**Setup fee row copy**
> One-time setup: $150 on Silver. Waived on Gold and Platinum until 30 September.

**[VERIFY]** two things in the current table. First, the struck-through amounts (`$200` Gold, `$400` Platinum) are not in the verified fact sheet — only that the fee is waived on both. A struck-through price that cannot be substantiated is exactly the kind of detail this audience checks. Second, confirm the waiver year before the date goes on a live page.

**Note:** the HTML comment at line 27 is correct and should be honoured — the fee amounts, the waived state and the date all render from `BillingRule`, never from hardcoded copy. Write the sentence with the numbers as tokens.

**Team pricing line**
> Brokerage or a team of advisers? Team pricing depends on how many users, sites and logins you need, so it is a conversation rather than a table.

**Team pricing CTA**
> Talk about team pricing

**Daily Assistant row label** (currently "Daily Assistant")
> Daily Assistant (who to contact next)

That parenthesis is the whole feature. Leaving it as two capitalised words invites the reader to imagine either much more or much less than it is, and both are bad.

---

## `i2-trust` — why businesses choose IPRO

The section carries five h3-level points: the founder block, then four cards. They are numbered here in the order they appear in the markup.

**Section label**
> Why IPRO

**H2**
> One company for the site, the software and the help.

**Intro**
> IPRO has built websites and business tools for Canadian professional-service firms since 2014. You edit your own pages, and when something needs answering there is one place to ask rather than a web designer, a host and a CRM vendor pointing at each other.

**Point 1 — founder block**
- **Heading:** Canadian, and here since 2014
- **Sub-line:** Websites and business tools for professional-service firms
- **Body:** Your website, hosting, client records and support sit with one company. One bill, one system to learn, and nobody to coordinate between when something needs fixing.

**Point 2 — card**
- **Heading:** Your data stays yours
- **Body:** Import the records you already have, and export them whenever you want, including on the way out.

**Point 3 — card**
- **Heading:** Hosting, domain and SSL handled
- **Body:** Your web address, hosting and security certificate are part of the subscription, not three separate renewals you have to remember.

**Point 4 — card**
- **Heading:** Nothing starts from a blank page
- **Body:** Your site goes live already written for your market, and every page is yours to edit from the first day.

**Point 5 — card**
- **Heading:** Built for Canadian businesses
- **Body:** Canadian dollars, provincial tax handled, Canadian mortgage conventions, and consent and unsubscribe rules built into the mailing tools.

**Note:** point 3 says "handled", not "we look after it for you", on purpose. The distinction between *managed infrastructure* and *managed service* is the one this page must never blur.

**Note on point 2:** "including on the way out" is the single most persuasive clause available to this audience, and it is entirely true. Keep it even if it feels uncomfortable.

---

## `i2-contact` — contact

**Section label**
> Get in touch

**H2**
> Look at it first. Ask us after.

**Intro**
> The preview needs nothing from you but a name, a business name and a market, and it runs on its own. This form is for the questions that come after: packages, domains, moving your records over, or how a team would work.

**Three contact items**

1. **Free website preview** — See a real site for your market and business name. No account, no credit card, no call.
2. **Questions before you buy** — Packages, setup, domains, importing records, team logins.
3. **Support for customers** — Help with training, billing and anything technical.

**Form labels** (unchanged, they are already plain)
- Name / Email / Business type / What would you like to build?

Suggested change to the last one:
> What can we help with?

**Submit button**
> Send my question

**Note — worth fixing:** the prototype's submit button says "Build my free preview", but the form posts to a contact endpoint and asks for a message. Two different actions are wearing the same label, and this reader will notice the moment he presses it and does not get a preview. Either the button sends the enquiry ("Send my question") or the section gets a separate, genuinely instant preview control. The first is less work and more honest.

---

## Footer

**Tagline** (under the logo)
> Website, client records, marketing and follow-ups in one Canadian subscription. Built for advisers, accountants, mortgage brokers and the businesses that work like them.

**Column headings** (unchanged): Product · Markets · Company

**Link labels**
- Product: How it works · Pricing · Free preview · Sign in
- Markets: Insurance / Financial · Accountants · Mortgage · General
- Company: About IPRO · Contact · Privacy · Terms

**Bottom line, left**
> © 2026 IPRO Advisers. Canadian dollars before applicable tax.

**Bottom line, right**
> Built and supported in Canada.

---

## Claims deliberately not made

Recorded so the next person to touch this file does not helpfully add them back.

- No reminder or notification is described as a text or SMS. The product has none.
- Nobody at IPRO builds, reviews, sets up or approves a customer's site anywhere in this deck. Every verb about editing has the customer as its subject.
- No market gets its own *feature*. Only the writing changes, and the section intro says so out loud.
- No customer counts, named firms, testimonials, uptime numbers, certifications or awards.
- The AI is described once, as one thing, on one package.
- No claim about how quickly a domain resolves, beyond option 2's honest admission that DNS is slow.

## Open items for the owner

1. **[VERIFY]** the struck-through Gold `$200` and Platinum `$400` setup fees.
2. **[VERIFY]** the 30 September waiver date and its year.
3. **[VERIFY]** the "about a minute" preview timing, if option 2 is chosen.
4. Decide whether the trust section keeps four cards or gains a fifth; this deck writes five points by counting the founder block, which is how the markup already reads.
5. `#i2-preview` and `#i2-signin` have no targets in the prototype.
6. The contact form's submit button promises a preview it does not deliver.
