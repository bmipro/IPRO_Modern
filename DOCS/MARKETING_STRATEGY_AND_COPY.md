# IPRO Advisers — Marketing Strategy and Copy Deck

Written 2026-08-13 by an outside marketing strategist.

**Sources.** `DOCS/MARKETING_BUSINESS_BRIEF.md` (authoritative on what exists), plus a direct read
of the code: the current home page, the `/Preview` flow, `AccountController`'s registration actions,
`MockDailyInsightCatalog`, `PackageEntitlementSeeder`, and both website starter-content seeders.
Also read: the company's own professional marketing copy written in 2014 (13 documents), and a
factual description of the legacy site still serving at `iproadvisers.com`. Where the 2014 material
and the current product disagree, the product wins.

**The host this site is being built for is `https://app.iproadvisers.com/`.** That is the live front
door, confirmed by the owner. `iproadvisers.com` and `www.iproadvisers.com` currently serve a
different, legacy site and are reference material only — nothing in this document plans a page
there. See the note in Section E about a live conversion leak on that legacy site that needs the
owner's attention regardless.

Every claim in the copy below is traceable to something that is actually built or to verifiable
company history. Where I found the current site claiming something that is not built, I have said so
and written a replacement rather than repeating it. Section K lists the factual errors I found while
researching — some are live today, and a few are data problems rather than copy problems.
Section L records what I took from the 2014 copy and what I deliberately left behind.

**This document does not modify any existing file. It is the brief for the person who will.**

## One thing to get straight before reading further

IPRO is **not** a startup with no history. The business has been building and running websites for
Canadian financial professionals since at least 2014, with published marketing, a real client
portfolio, and established social accounts. The *platform* is a rebuild; the *company* is not new.

That distinction changes the copy substantially. It means the site does not have to write around a
total absence of credibility — it can say, truthfully, that this is a company that has done this
work for over a decade. What it still cannot do is name past clients without permission, imply they
are current customers of the new platform, publish counts, or invent testimonials. Section H sets
out exactly how to use the history and where the line is.

---

# A. Positioning

## The positioning statement

> **For the independent Canadian adviser running a practice on their own or with a small team,
> IPRO Advisers is the one system that runs the entire client side of the business — the website,
> the client list, the follow-ups, the newsletters and the invoices — so that nothing falls through
> and every morning it can tell you exactly who to call and why.**

The public-facing compression of that, and the line that should appear on the home page:

> **Everything your practice runs on, in one login.**

## The category we want to own

**The practice platform for independent advisers.** Not "adviser websites" — that category exists,
it is owned by US incumbents (Advisor Websites, Twenty Over Ten, FMG Suite), and it is a smaller,
cheaper, more commoditised thing than what IPRO actually is. IPRO should refuse the website
category and take the practice category, where the website is the front door and the client book is
the building.

This matters more than it sounds. If IPRO competes as a website company, the buyer compares it to
Squarespace at $25/month and IPRO loses on price for the rest of its life. If IPRO competes as the
system that runs the practice, the buyer compares it to the $110–190/month stack they are already
paying for, and IPRO wins on price *and* on scope. Same product, same price, completely different
mental shelf. Choose the shelf.

## The strategic argument

An adviser's real problem is not that their website is ugly. It is that the pieces of their
practice do not talk to each other, and so things quietly go missing. A prospect fills in a form on
the site and lands in an inbox. The client list lives in a spreadsheet, or in a CRM the MGA issued
that nobody opens. The newsletter list is a third, older copy of the same names in Mailchimp. Nothing
in that arrangement can ever answer the only question that actually generates revenue — *who have I
not spoken to who I should have?* — because no single system holds both the lead and the client and
the last conversation.

IPRO is one database behind one login. A form on the website creates a lead in the prospect manager.
The lead becomes a client. The client carries notes, follow-ups, a birthday, a renewal date, a
newsletter subscription, a portal login, and an invoice history — all on one record. Because it is
one record, the software can compute, overnight, what needs attention today: leads that came in and
haven't been touched, leads older than 24 hours, clients with no follow-up scheduled at all. Then it
ranks them and puts one sentence at the top of the dashboard: *call this person first, and here's
why.* That is not a feature you can assemble from five subscriptions. It is a structural consequence
of the data being in one place, and it is the whole argument.

Everything else IPRO sells — the instant website, the newsletters, the e-cards, the client portal,
the invoicing — is in service of that: more of the practice inside the one system means the morning
list gets smarter.

## Why this beats the alternatives the adviser is actually weighing

**Against assembling the stack (Squarespace + Wealthbox + Mailchimp + Calendly + a portal, roughly
$110–190/month).** Three arguments, in the order they land:

1. *It costs more and does less.* IPRO Platinum is $90/month and includes the client portal and the
   invoicing that most of those stacks don't have at all.
2. *You are the integration.* Nobody in that stack knows about anybody else in that stack. Every
   list has to be exported and re-imported by hand, forever, by the adviser — who is not a
   technologist and did not want a second job.
3. *It can never tell you who to call.* This is the argument that closes. The stack is structurally
   incapable of it. It doesn't matter how good each individual tool is.

**Against doing nothing** — which is the real competitor, and the one that wins most often. Doing
nothing is free and requires no decision. The counter-argument is not "you need a website." It is
naming the specific cost of nothing, in their language: *the quote request that sat in your inbox
for four days. The renewal that lapsed. The client you meant to call after the baby was born and
didn't. The book you spent twenty years building that nobody is systematically staying in touch
with.* Then show them, in 30 seconds, that starting is not a project. The preview exists precisely
to make "do nothing" harder to justify.

**Against the US vertical competitors.** Do not lead with this — advisers are not comparison
shopping across borders. But hold it in reserve for the pricing page and the FAQ: prices in
Canadian dollars, provincial tax calculated correctly for all thirteen provinces and territories on
both the adviser's subscription and the invoices they send their own clients, and a product built by
someone who is in the same country as the person buying it.

---

# B. The ideal customer

## Lead with insurance and financial advisers. Here's why.

Three verticals exist in the product. All three get real starter content. Only one should be on
the front of the website in the first year.

**Recommendation: lead with Insurance / Financial advisers.** Four reasons, in order of weight:

1. **The differentiating features were built for their business model.** Life-event reminders
   (birthdays, policy renewals, anniversaries), overdue follow-up ranking, a book you keep for
   twenty years — that is an insurance practice described in features. A mortgage broker's book goes
   quiet for five years between transactions; an accountant's book is a compliance calendar. The
   daily "who to call" digest produces the most obviously valuable answer for the adviser whose
   revenue is a long relationship.
2. **It is where the founder's own credibility is.** The company is called IPRO *Advisers*. The
   original market was insurance. Pre-launch, with no customers to point to, the founder's own
   network is the first channel, and it should not be diluted.
3. **The cost of a missed call has an obvious dollar value in insurance** and a vague one in
   accounting. That makes the "do nothing" argument land harder here than anywhere else.
4. **With no budget, the copy has to sound like one person's job, not three.** Three verticals on
   the home page means generic language on the home page, which means the recognition moment never
   fires for anyone.

Mortgage brokers are the second page, not the second priority — build the vertical page, but don't
put them in the hero. Accountants are third by sequence and first by content asset: they have the
deepest starter library (19 articles across four sections — Personal Accounting, Business
Accounting, Info Centre, Calculators) and that library is the most concrete "you get real content on
day one" proof anywhere in the product. Use it as proof on every vertical page, not only theirs.

## Buyer picture 1 — the insurance and financial adviser (primary)

**Dave, 52. Independent life and health adviser, contracted through an MGA. Barrie, Ontario.
Roughly 300 clients built over eighteen years. Wife does his bookkeeping. No staff.**

*His day.* Coffee at 7. A paper list on a legal pad, written last night, with four names on it and
two of them carried over from the day before. Two carrier portals with two different passwords, one
of which he resets most months. Outlook, with 1,400 unread. A spreadsheet called
`clients_2019_FINAL.xlsx` that is the closest thing he has to a database, and which he does not
fully trust. Three voicemails. A 7 p.m. appointment at a client's kitchen table in Innisfil, which
is where he does his best work and where he'd rather be all day.

*What he's already tried.* A website in 2017 from a guy his nephew knew — $1,800, looked good for a
year, and now says something about pandemic office procedures at the top. He has not had the login
since the guy stopped answering. He signed up for Mailchimp during a slow week in 2021, tried to
import his spreadsheet, got an error about a header row, and never went back. The MGA gave him a CRM;
he logs in about once a quarter, mostly to look something up.

*What he's quietly embarrassed about.* He doesn't send anyone to his own website. When a prospect
says "do you have a website?" he says "yeah, it's being redone." He could not tell you, with any
confidence, which of his clients he has not spoken to in a year — and he knows the answer is
"more than a few." His About page still has a photo from 2014 and the name of a partner who left.

*What he'd have to believe to sign up today.*
- That it will look professional without him designing anything, because he cannot design anything.
- That he personally can operate it. Not "it's easy" — he's heard that. He needs to *see* himself
  operating it.
- That his client list gets in without retyping 300 records.
- That if it doesn't work out he can leave with his data and his domain, and isn't trapped.
- That there is a real person who answers when it goes wrong.

## Buyer picture 2 — the mortgage broker

**Sandra, 44. Mortgage agent with a mid-size brokerage. Mississauga. Deal-driven; income is lumpy.**

*Her day.* Rates, texts and referrals. Leads arrive on her cell at 9 p.m. and get answered because
whoever answers first wins. Her real system is her phone's recent-calls list. Deals close, the
client is thrilled, and then five years go by and the renewal notice comes from the bank instead of
from her.

*What she's tried.* Whatever the brokerage's system does, plus a Facebook page she posts to when a
rate moves. She's paid for leads before and felt burned.

*What she's embarrassed about.* Past clients renewing with their bank because she never followed up.
She knows exactly what that costs and doesn't like thinking about it.

*What she'd have to believe.* That a lead from her site reaches her within minutes, not overnight.
That renewals and rate-hold expiries get flagged without her remembering to flag them. That posting
and following up don't require her to sit at a desk.

## Buyer picture 3 — the accountant or bookkeeper

**Peter, 58. Two-person firm — himself and a part-time bookkeeper. Ottawa. Around 200 personal
returns and 40 small-business clients.**

*His day.* January through April is a wall. May through December is quiet enough to worry about.
Clients email documents to a Gmail address; some of them include a SIN in the body of the email. A
shoebox arrives in March. He has never marketed and gets everything by referral, which he is proud
of and also faintly nervous about, because his referral sources are retiring at the same rate he is.

*What he's tried.* Nothing, on the marketing side. He has strong opinions about accounting software
and no opinions about websites.

*What he's embarrassed about.* The Gmail address. The documents sitting in it. The fact that his
website, if he has one, hasn't been touched since it was built.

*What he'd have to believe.* That the client portal actually gets documents out of email and into
somewhere defensible. That the content is written for him so he never has to write a blog post. That
this does not become a project in February.

---

# C. The message hierarchy

## The one thing a visitor must remember

> **One login runs the whole practice — and every morning it tells you who to call.**

If a visitor remembers nothing else, remember that. Everything on the site is either that sentence,
proof of that sentence, or removal of an obstacle to that sentence.

## The three pillars, and the proof for each

### Pillar 1 — You'll have a real website today, not a project.

*Why it's first:* it is the only pillar that can be demonstrated to a stranger in 30 seconds, so it
is the pillar that gets them in the door. It is also the concrete, embarrassing problem most of them
already know they have.

**Proof:**
- The `/Preview` flow. A real, browsable site with their name on it, rendered through the actual
  template engine, before they give us anything. This is not a claim; it's a demonstration.
- Real starter content chosen by their vertical, already written and already on the pages — not
  lorem ipsum, not "Your headline here." Home, About, Testimonials, Free Newsletter, Request
  Meeting and Contact pages arrive built.
- A real article library on day one. For accountants that is 19 pieces across four sections. Every
  vertical gets a stocked Resources section rather than an empty one.
- Three templates with theme variants; a page builder with text, images, video, galleries,
  testimonials, forms, lead magnets and financial calculators.
- Their own domain, with the security certificate issued and renewed automatically. Screenshot the
  status strip that says "Secured."

### Pillar 2 — Your website and your client list are the same system.

*Why it's second:* it is the argument that reframes the price comparison and makes the stack look
broken. It is also the hardest to demonstrate quickly, so it needs the most careful copy.

**Proof:**
- A form on the website creates a lead in the prospect manager. No export, no connector, no Zapier.
- The lead becomes a client on the same record: notes, activity timeline, follow-ups, account type.
- Newsletters, drip campaigns, e-cards and e-letters send to that same client list — one list, not
  a copy of a list.
- Invoices bill that same client, with tax calculated from *the client's* province, not the
  adviser's.
- One bill, one login, one place to learn.

### Pillar 3 — It tells you who to call, before you open your email.

*Why it's third but weighted heaviest:* it is the only thing here that no competitor and no stack
can do, and it is the reason to buy Platinum rather than Silver. Third position because it needs the
first two pillars established before it makes sense.

**Proof:**
- The AI Daily Assistant, computed every morning: new leads, leads older than 24 hours, clients with
  no follow-up scheduled — plus one ranked next action and a one-line reason why it matters.
  (Included with Platinum and Broker. Say so every single time it appears.)
- The mock card the prospect already saw during their preview — the same component, the same
  layout, so the promise and the product match.
- Life-event reminders: birthdays, policy renewals, anniversaries, automatically ahead of time.
- Overdue invoice reminders that go out weekly on their own and stop the moment an invoice is
  marked paid.

## Ranking the product's capabilities by selling power

Most of the feature list is table stakes. Putting it on the home page is what makes the current page
read like a spec sheet instead of an argument. Here is the honest ranking.

**Tier 1 — these close sales. Give them space, screenshots, and their own sections.**

| Capability | Why it closes |
|---|---|
| The 30-second preview | Removes all risk from the first step. It is the product's best salesperson. |
| AI Daily Assistant (Platinum) | The one thing nothing else does. The reason to buy up a tier. |
| Pre-loaded content by vertical | Kills "I'd have to write it all" — the real reason websites never get built. |
| Client Portal (Platinum) | Solves a problem the buyer feels physically: client documents living in email. |
| Custom domain with automatic certificate | Removes the technical fear that stops the whole purchase. |
| Canadian tax done properly | Small feature, enormous trust signal, impossible for US competitors to fake. |

**Tier 2 — these support the sale. One line each, on a features page, not the home page.**

Newsletters with delivery and open tracking and built-in one-click unsubscribe handling; drip
campaigns; e-cards and e-letters (the "stay in touch without having to write anything" layer — this
one punches above its weight with this buyer, so give it a sentence on the home page); client
invoicing and estimates with a signed no-login approve/decline/pay link and QuickBooks CSV export;
life-event reminders; team member logins for an assistant (Gold 2, Platinum 5, Broker 10); testimonial
collection including request-by-email to a named client; Google Calendar two-way sync; CSV export of
your own client list, any time.

**Tier 3 — table stakes. These belong in the comparison table and nowhere else.**

SEO tools, polls and surveys, custom form builder, lead magnets, marketing calendar, social post
composer, mail merge, printable mailing labels, coupon manager, rotating banner, multilingual
editor, visitor tracking, framed link manager, newsboard, Outlook contact import, custom home
buttons, needs-analysis calculators, "Did You Know" teasers. Listing 30 features signals "we couldn't
decide what matters." Listing six and linking to the other thirty signals confidence.

**Tier 0 — remove from marketing entirely.**

*Mobile SMS reminders.* Not built. It is currently claimed on the live home page and it is currently
marked as included on all four plans in the seeded entitlement data, which means the "Compare all
features" table on the pricing section renders it as a checkmark on every plan. That is a data fix
as well as a copy fix. See Section K.

---

# D. The conversion path

## The fast path: cold visitor to paid, in under ten minutes

The goal is a single spine with no branches. Every branch is a place to leave.

**Step 1 — Home page. One job: get to the preview.** One primary button above the fold. Not three.
Everything else on the page is a secondary link or a scroll-down. The page's only measurable success
is preview starts.

**Step 2 — `/Preview`. One job: four fields and a button.** Today it asks first name, last name,
company (optional) and business type. Two changes:

- *Put "What do you do?" first.* It is the highest-signal field, it sets the content they're about
  to see, and answering it commits the visitor to the flow before they've given a name.
- *Say what happens next, above the button, not below it.* "You'll see a real site with your name on
  it. No account, nothing saved, nothing emailed to you unless you ask."

**Step 3 — `/Preview/Show`. This is the most important screen on the site and it is currently doing
about a third of its job.** It works: a live iframe of their real site, a package card with real
pricing, a mock AI card, and a Sign Up button that prefills registration. Four changes, in order of
value:

1. **Add "Email this to me."** Right now, a visitor who is impressed and not ready is lost forever —
   there is no capture of any kind in the entire funnel before registration. Every `/Preview` action
   is a GET with the identity in the querystring, so the preview URL is already stable and
   shareable; emailing it is a link, not a rebuild. This is the single highest-return change in this
   document.
2. **Move the price and the offer up.** The package card should carry the founding-customer setup-fee
   waiver (see below) with a date on it, not just the list price.
3. **Prompt the visit, don't just permit it.** Most visitors will look at the iframe and not realise
   they can click into About, Resources, Contact. Add three small buttons above the frame:
   "Home · About · Resources" that navigate the frame, so they discover the site is real by using it.
4. **Change the button label.** "Sign Up Now" asks for a commitment. "Claim this site" describes
   getting something. Same click, different feeling.

**Step 4 — `/Account/Register`. This is where the funnel leaks.** As built, it is one screen with
seventeen fields, a full subscription agreement in a scrolling text box, a four-digit code to retype,
and a terms checkbox — and after all of that, the visitor still does not have a subscription, because
registration and subscription are two separate acts (`Billing → Subscribe`).

Recommendations, in priority order:

1. **Split it in two.** Step 1: name, email, business type, package — four fields, and everything
   except email is already prefilled from the preview. Step 2: the company and address details,
   agreement and code. A visitor who completes step 1 has psychologically bought; step 2 is
   paperwork they'll finish. A visitor who sees seventeen fields at once bounces before field one.
2. **Replace the agreement text box with a link and a checkbox.** "I've read and accept the
   subscription agreement (opens in a new tab)." A 200-pixel-tall monospace textarea is the single
   most alarming object on the page for this buyer.
3. **Keep the verification code.** It's fine, and it reads as security to this audience rather than
   as friction — but move the hint above the field, not below it.
4. **Make the promotion code field visible and inviting rather than "Optional."** Label it "Founding
   customer code" and pre-fill it when the visitor arrived from the offer. The validation endpoint
   already returns friendly human text ("Code accepted: 100% off the setup fee") — that moment of
   the price dropping in front of them is a conversion event, and it's already built.

**Step 5 — Registration success, then subscribe.** Today this page reads as a receipt: here's your
username, here's your temporary password, here's your site address, and by the way you still need to
subscribe. **Rewrite it as a continuation, not a confirmation.** One primary button — "Choose how you
want to pay" — and the credentials in a quieter box below it. The current ordering trains the
customer to think they're finished.

## The slow path: the visitor who is not ready

This path does not exist today at all. There is no email capture anywhere on the marketing site.
Build the minimum version:

**What earns the email.** Not a newsletter signup — nobody wants another newsletter. Two offers,
both of which are things the product already produces:

1. **"Email me my preview."** Highest-intent capture in the funnel. They've just seen their own site;
   the email arrives with a link straight back into it. One field. No pitch attached.
2. **"See the content you'd start with."** For each vertical, a PDF of the actual starter articles
   that land on a new customer's site — 19 real pieces for accountants, the real pieces for insurance
   and mortgage. This is genuinely useful to an adviser who is thinking about content at all, and it
   is honest proof of Pillar 1 rather than a lead-magnet-shaped object.

**What brings them back.** A short sequence — four emails over twelve days, no more — each one
showing one real screen and making one point. Draft copy is in Section F16. Then a fifth email, only
if they haven't converted, offering the trial by invitation (below). Send it through the company's
own SendGrid, from the company's own product. Dogfooding is itself proof, and the founder will
discover every rough edge in his own onboarding before a customer does.

## The setup fee — the biggest friction in the funnel

The problem, stated plainly: a prospect sees $40 and then discovers $150 more due today. On Platinum
it's $90 and then $400. For a platform nobody they know has used yet, that is the moment the tab
gets closed.

**Recommendation: keep the fee, justify it in specifics, and waive it publicly as a founding-customer
offer with a real deadline — using the promotion-code system that already exists and is currently
doing nothing.**

Concretely:

1. **Never show a bare number.** "One-time setup: $400" is a tax. Rename and itemise it:

   > **Getting set up — $400, once.**
   > A person, not a form. We connect your domain and get the certificate issued, load your
   > vertical's content and adjust the wording to your practice, import your client list from
   > whatever you have it in now, and walk you through the whole thing live once you can see it
   > working.

   Now it is labour, and $400 for someone doing all that is obviously cheap. The number hasn't moved;
   the meaning has.

2. **Waive it for founding customers.** Create a promotion code that takes 100% off the setup fee,
   with a hard expiry and a redemption cap — all three are already supported. Present it on the
   pricing page as struck-through, not deleted:

   > ~~$400 setup~~ **$0 for founding customers — first 25 accounts, until [date].**

   Three reasons to waive rather than delete. It preserves the anchor, so the fee can return later
   without looking like a price increase. It creates honest urgency — a real cap and a real date,
   both of which the system enforces. And it is reversible: if conversion doesn't move, the code
   expires and nothing was permanently given away.

   *Name it accurately.* "Founding customers" is the right frame for the first accounts on the new
   platform even though the company itself is not new — it is the platform they are being early to,
   not the business. Do not let the offer imply the company just opened.

3. **Do not discount the monthly price.** Recurring discounts are supported, and using them here
   would be a mistake: it lowers the anchor permanently, it trains the earliest customers to be the
   cheapest customers forever, and it makes the eventual list price feel like a rise. Give away the
   one-time fee, protect the recurring revenue.

4. **Fix the annual price.** Annual is currently 12× monthly — no discount — and both numbers are
   shown on the same card. An adviser who does the arithmetic (this buyer does arithmetic) concludes
   the annual option is there to catch people who don't. **Make annual 10× monthly — two months
   free.** It front-loads cash for a company that needs cash, it's a second concession that costs
   less than a rate cut, and most importantly it removes a small, quiet credibility problem from the
   most scrutinised page on the site.

5. **Surround the fee with the two commitments that make it survivable — both of which the company
   used to make and doesn't any more.** From the 2014 pricing and Why Us pages: *"Monthly Billing.
   No Contract. Cancel Anytime."* and a 30-day money-back guarantee. Neither appears anywhere on the
   site today, and both are still true and still honourable — subscriptions cancel from the Billing
   page, and the owner can honour a 30-day refund at will. These belong directly under the setup fee
   on the pricing page, because they are the specific answer to what the fee makes the buyer afraid
   of: *paying $400 to be stuck.* Recommended wording is in Section F4.

6. **Revive the referral offer, manually.** The 2014 FAQ offered a free month to both the referrer
   and the referred. For a company selling into a referral-driven profession, that is the cheapest
   acquisition channel available and the first customers are the ones best placed to use it. There is
   no automated mechanism for crediting a referrer, so run it by hand at first: issue a named promo
   code per customer, and credit the referrer's next invoice when it's redeemed. Do not announce it
   on the public site until there are customers to refer — put it in the welcome email instead.

## Should we lead with the free trial? No.

**Recommendation: do not put a free trial on the home page. Lead with the preview, and keep the
trial as a closing tool the founder hands out by name.**

Three reasons:

1. **The trial is invitation-only by construction.** Trial packages never appear in the registration
   dropdown, and the registration action refuses a trial package without a valid invite code. Making
   it public is a product change and a support-load change, not a marketing decision — and it would
   be a change made before there is anyone to handle the load.
2. **The preview already does the trial's job, better and faster.** A free trial asks for an email,
   a password, seventeen fields and a decision. The preview asks for a first name and delivers a live
   site in 30 seconds. For this buyer, the preview is a strictly superior top-of-funnel instrument.
   Do not put a slower, higher-friction offer in front of a faster, lower-friction one.
3. **An abandoned trial is worse than no trial.** A half-built site with a stale trial banner is a
   bad first impression of a product whose entire pitch is "it will be done and it will look right."

**What to do with the trial instead.** Use it exactly where it's strongest — as the answer to
"I'd want to try it first," delivered by a person:

- On the FAQ and on every vertical page: *"Want to use the real thing before you pay? Ask me for a
  trial invitation and I'll send you one — 14 days, the full Platinum plan, no card."* One short form,
  the owner sends the invite link himself.
- As the standing offer to associations, MGAs and brokerages — the redemption cap on an invite code
  is exactly the mechanism for "here are 10 seats for your agents."
- As the recovery step at the end of the not-ready email sequence.

The result: the public funnel is preview → founding offer → paid. The trial is the close, not the
hook.

---

# E. Site architecture

## The host

Everything below is built at **`https://app.iproadvisers.com/`**, which is the live front door. The
marketing site is the unauthenticated root of the agent portal; signing in redirects to the
dashboard. Registration is `https://app.iproadvisers.com/Account/Register`. Every route in the table
below is relative to that host. Do not design anything that assumes the bare `iproadvisers.com`
domain.

## A live leak that needs the owner's attention before launch

`iproadvisers.com` / `www.iproadvisers.com` currently serve a different, legacy site — last modified
January 2016, an unmodified commercial HTML template with its demo navigation still live ("Colors →
Blue / Green / Dark pink", "Portfolio", links to `blue/index.html` and `coming-soon.html`). Per the
owner it is reference material, not a front door, and nothing here plans a page for it.

But it is not inert, and two things about it are actively costing money right now:

1. **Its signup link points at the legacy product** (`247advisers.com/pub/register.aspx?BT=3`).
   Anyone who searches the company name, lands there, and decides to buy is being sent to the wrong
   system. That is a live conversion leak on the brand's most obvious URL.
2. **It is the first thing an adviser sees** if a referral says "look up IPRO Advisers" — a 2016
   template with a demo colour-switcher, selling a product that has been replaced. It undermines
   the exact credibility argument the new site is built on.

This is an owner decision, not a design task, so I am flagging rather than specifying: at minimum,
point `iproadvisers.com` and `www.iproadvisers.com` at `app.iproadvisers.com`, or replace the legacy
page with a one-screen holding page that links there. Do it before any traffic is driven anywhere.

## The page list

Build in this order. The numbering is priority, not navigation order.

| # | Page | Route | The one job it does |
|---|---|---|---|
| 1 | **Home** | `/` | Get the visitor into the preview. Nothing else. |
| 2 | **See your website** | `/Preview` | Four fields, one button, zero doubt about what happens next. |
| 3 | **Your preview** | `/Preview/Show` | Convert, or capture the email. Currently does neither well enough. |
| 4 | **Pricing** | `/pricing` | Answer the money question in full, including the setup fee, without scrolling past anything else. Data-driven, as today. |
| 5 | **Your first week** | `/how-it-works` | Kill "I'm too busy to switch" with a dated timeline of who does what. |
| 6 | **For insurance and financial advisers** | `/for/insurance-advisers` | Recognition. The primary vertical's own page. |
| 7 | **Your data** | `/your-data` | Answer "is my client data safe" and "what if I leave" before they're asked. |
| 8 | **Who's behind this** | `/about` | The founder, by name, with a face. The social-proof substitute. |
| 9 | **What's included** | `/whats-included` | Absorb the entire long-tail feature list off the home page — including the honest list of what isn't built. |
| 10 | **Contact** | `/contact` | Reach a human. Also where the trial request lives. |
| 11 | **Questions** | `/faq` | Every objection, answered in the buyer's own words. |
| 12 | **For mortgage brokers** | `/for/mortgage-brokers` | Second vertical. |
| 13 | **For accountants and bookkeepers** | `/for/accountants` | Third vertical; showcases the 19-article library. |
| 14 | **Privacy policy** | `/privacy` | Legally necessary and currently missing entirely. |
| 15 | **Terms** | `/terms` | The subscription agreement as a real page, so registration can link to it instead of embedding a text box. |

Later, when there is something to put in them: a "What we shipped" changelog page (strong proof that
the product is alive), and a public help centre — note that a Support article system already exists
inside the product for signed-in agents, so this is a surfacing job rather than a writing job.

## What belongs on the home page

In this order, and nothing else:

1. Hero — one headline, one sub, one button to the preview, one reassurance line.
2. The one thing — a short band stating the core claim with a real product image.
3. Three pillars — one section each, each with one screenshot and 40–60 words.
4. Your first week — a compact four-step strip, linking to `/how-it-works`.
5. Plans in brief — three cards, price and one line each, linking to `/pricing`.
6. Four things we don't do — the honesty band. Short.
7. Who's behind this — three sentences, a photo, a link to `/about`.
8. Closing CTA — back to the preview.

## What must move off the home page

- **The four-row "what's included by tier" stack.** → `/whats-included`. It is a spec sheet in a
  place that should be an argument, and it is the section most responsible for the page reading as
  a features list.
- **The four pricing cards and the full comparison table.** → `/pricing`. Keep them data-driven and
  keep the tax disclosure. The home page carries three summary cards and a link.
- **The "email & SMS reminders" line.** → deleted. It isn't true.

---

# F. The copy deck

Everything below is finished copy, ready to set in type. No sentence needs writing.

**Conventions.** Text in `[square brackets]` is a value the owner supplies before launch. Lines
beginning *Build note:* are instructions to the developer, not copy. Prices shown are today's real
seeded values and must stay data-driven — the numbers in this document are for layout only.

---

## F1. Home page — `/`

**Page title:** `IPRO Advisers — one login for your website, your clients and your follow-ups`

**Meta description:** `One system for Canadian advisers, brokers and accountants: your website, your client list, your newsletters and your follow-ups — plus a daily list of who to call. See your own site free in 30 seconds. No account needed.`

### Navigation

`IPRO Advisers` (logo) · What's included · Pricing · Who it's for · Questions · **Sign in** · **See your website** (button)

### Hero

**Eyebrow:** For independent advisers, brokers and accountants across Canada

**H1:** Everything your practice runs on, in one login.

**Sub:** Your website, your client list, your newsletters and your follow-ups — in one system that
knows all of it, so nothing quietly falls through. Take thirty seconds and we'll show you your own
site, with your name on it, before you give us anything.

**Primary button:** Show me my website — 30 seconds

**Under the button:** No account. No credit card. Nothing saved.

**Quiet links:** See what's included · See pricing

### Band 2 — the one thing

**Eyebrow:** The part nobody else does

**H2:** Every morning, it tells you who to call.

**Body:** Because your website, your leads and your clients all live in the same system, IPRO can
look at the whole picture overnight and have an answer waiting before you sit down. Who came in and
hasn't been called back. Who's been waiting more than a day. Who you have nothing scheduled with at
all. Not a report to go through — one name at the top of the screen, and the reason underneath it.

**Image caption:** The first thing on the dashboard at seven in the morning. Included with Platinum
and Broker plans.

### Band 3 — the three pillars

**Eyebrow:** One

**H3:** You'll have a real website today, not a project.

**Body:** Tell us what you do and the pages arrive already written — home, about, testimonials, a
newsletter signup, a request-a-meeting page, contact — with an article library for your field
already sitting on them. Accountants start with nineteen pieces across four sections. You're editing
something finished instead of staring at an empty box. Point your own domain at it whenever you're
ready; the security certificate is issued and renewed for you.

**Link:** See what a finished site looks like →

---

**Eyebrow:** Two

**H3:** Your website and your client list are the same thing.

**Body:** Someone fills in the form on your site and they're in your prospect list before you've
finished reading the email. When they become a client, that same record carries your notes, their
follow-ups, their birthday, their renewal date, whether they get your newsletter, and every invoice
you've sent them. Nothing to export. Nothing to keep in step. One list, one bill, one password.

**Link:** See everything that's included →

---

**Eyebrow:** Three

**H3:** The things you meant to get to happen anyway.

**Body:** Birthdays, policy renewals and anniversaries come up on their own, in advance. Newsletters
go out with unsubscribes handled properly, which is most of why email lands. Overdue invoices chase
themselves once a week until they're paid, and stop the second you mark one paid. The cards and
letters are already written — you pick one and send it.

**Link:** See how a week actually goes →

### Band 4 — your first week

**Eyebrow:** What actually happens

**H2:** Your first week, honestly.

| When | What happens |
|---|---|
| **Today** | You sign up. Your site is live at your temporary address within minutes, with your content already on it. |
| **Day one or two** | We go through it together once, live. You say what's wrong, I change it while you watch. |
| **That same week** | Your client list comes across — from a spreadsheet, from Outlook, from whatever you've got it in now. |
| **By Friday** | Your own domain points at it, the certificate is issued, and you're the only person who needs the password. |

**Link:** The whole first week, step by step →

### Band 5 — plans in brief

**Eyebrow:** Plans

**H2:** Three plans. Canadian dollars, before tax, no asterisks.

**Card — IPro Silver — $40/month**
Everything you need to be online and organised: the website, the content, lead capture, your client
list, newsletters and campaigns. Up to 500 clients.

**Card — IPro Gold — $60/month**
Adds the staying-in-touch layer — e-cards, e-letters, mail merge, printable labels — and takes the
caps off. Plus a second login for whoever helps you.

**Card — IPro Platinum — $90/month** · *badge:* Most complete
Adds the three that change how you work: the daily who-to-call list, a portal your own clients log
into, and invoicing. Managed SEO, and one blog post a month written for you.

**Under the cards:** Running a team or a brokerage? Broker pricing is a conversation, not a card.
[Talk to us.]

**Button:** See all three side by side, including setup and tax

### Band 6 — the honesty band

**Eyebrow:** Straight answers

**H2:** Four things we don't do.

- **We don't send text messages.** Reminders go by email. SMS is on the list. It isn't built, and
  we're not going to tell you it is.
- **We don't touch your clients' money.** You can invoice a client through IPRO and they can pay
  online, but it goes to your payment link, not through us.
- **We don't post to social media for you.** You can draft a post here. You still publish it.
- **We don't have a phone app.** The whole thing works in a browser on your phone, which is a
  different claim, and the honest one.

**Link:** The full list of what's included — and what isn't →

### Band 7 — who's behind this

**Eyebrow:** Who you'll be dealing with

**H2:** Me, mostly.

**Body:** I'm Bahman Motamed. This company has been building and running websites for Canadian
advisers, accountants and mortgage brokers since 2014. IPRO is the rebuild — the same job, done
properly, on a platform we own end to end instead of six we rent. It's a small operation, which
means when something goes wrong you get me and not a ticket number. My email and my phone number are
on the contact page and they're the real ones.

**Link:** More about the company →

### Band 8 — closing

**H2:** Take thirty seconds and look at your own site.

**Sub:** You'll get a real, working website with your name on it. We don't ask for your email, we
don't save anything, and there's nothing to cancel afterwards.

**Button:** Show me my website

**Under:** Already have an account? Sign in

### Footer

**Column — Product:** What's included · Pricing · Your first week · Your data

**Column — Who it's for:** Insurance and financial advisers · Mortgage brokers · Accountants and bookkeepers

**Column — Company:** Who's behind this · Contact · Questions

**Column — Account:** Sign in · Create an account

**Bottom line:** © [year] IPRO Advisers. Prices in Canadian dollars, before applicable provincial
tax. · Privacy · Terms

**Footer tagline:** Built in Canada, for Canadian practices.

---

## F2. See your website — `/Preview`

**Page title:** `See your own website in 30 seconds — IPRO Advisers`

**Meta description:** `Enter your name and what you do. We'll build you a real, working website with your own content on it in about thirty seconds. No account, no email, nothing saved.`

*Build note: this page is currently `noindex,nofollow`. Remove that — it is a legitimate landing page
for links, referrals and any future advertising. Keep `noindex` on `/Preview/Show`, which is
personalised and should not be indexed.*

**Eyebrow:** No account. Nothing saved.

**H1:** Let's build you a website. Thirty seconds.

**Sub:** Answer three questions and we'll show you a real, working site with your name on it — the
actual pages, the actual content written for your field. Not a picture of one.

**Field 1 label:** What do you do?
**Field 1 options:** Insurance or financial advice · Mortgages · Accounting or bookkeeping
**Field 1 help text:** This decides which content your site starts with.

*Build note: put this field first — it is the highest-signal answer and it commits the visitor before
they've typed anything. The submitted values must stay exactly `Insurance / Financial`, `Mortgage`
and `Accountants`; only the visible labels change.*

**Field 2 label:** Your name
**Field 2 placeholders:** First name · Last name

**Field 3 label:** Business name
**Field 3 help text:** Optional. Leave it blank and we'll just use your name.

**Button:** Build my site

**Under the button:** Takes about thirty seconds. We don't ask for your email and nothing is saved.

**When a plan was carried in from the pricing page:** You're looking at the **[plan name]** plan.
You can compare the others afterwards.

---

## F3. Your preview — `/Preview/Show`

**Page title:** `[First name]'s website — IPRO Advisers`

**Top bar:** Live preview · **Start over**

**H1:** Here it is, [First name].

**Sub:** This is a real site, not a picture of one. Click through it — the About page, the articles,
the contact form. It's running on the same system you'd be using.

**Frame navigation buttons:** Home · About · Resources · Contact

*Build note: these three or four buttons navigate the iframe. Without them, most visitors will look
at one page and never discover the rest of the site is real — which is the entire point of the
preview.*

### Right column, card 1 — the plan

**Heading:** [Plan name]
**Price:** $[90] / month · plus applicable tax
**Struck through:** ~~$[400] one-time setup~~
**Highlighted:** $0 setup for founding customers — until [date]
**Link:** Compare all three plans

### Right column, card 2 — the daily list

**Heading:** What your mornings would look like
**Note under heading:** Included with Platinum and Broker plans.
**Existing card content stays as built.**
**Foot of card:** This is an example, with example names. Sign up and it's your actual clients,
every morning.

### Right column, card 3 — the main action

**Heading:** Want to keep it?
**Body:** Everything you're looking at becomes yours — the pages, the content, the address. You can
change any of it yourself, or tell us what to change and we'll do it.
**Button:** Claim this site
**Under the button:** Monthly. No contract. Cancel any time.

### Right column, card 4 — the email capture

**Heading:** Not today?
**Body:** We'll email you the link so you can come back to this exact site whenever you want. That's
all we'll use it for.
**Field placeholder:** Your email address
**Button:** Email me the link
**Microcopy:** One email with your link in it. No newsletter unless you ask for one.

**Confirmation state:** Sent. Check your inbox — the link doesn't expire.

---

## F4. Pricing — `/pricing`

**Page title:** `Pricing — IPRO Advisers`

**Meta description:** `Three plans from $40 a month, in Canadian dollars. Setup fees, provincial tax and what's in each plan, all on one page. Monthly billing, no contract, cancel any time.`

**Eyebrow:** Pricing

**H1:** Three plans. Everything on one page, including the parts most companies hide.

**Sub:** Prices are in Canadian dollars and are shown before tax, because provincial tax depends on
where you are and we'd rather show you the real number at checkout than a wrong one here.

### The plan cards

*Build note: keep this section reading real `BillingRule` rows exactly as it does today. The copy
below replaces the description text and the buttons only.*

**IPro Silver — $40/month** · *badge:* Getting online properly
Your website with real content on it, lead capture, your client list, newsletters and automated
campaigns. Everything you need to have a professional presence and stop losing enquiries.
· Up to 500 clients · 2 domains · 50 MB of files
· Setup: ~~$150~~ **$0 for founding customers**
**Button:** Start with Silver
**Quiet link:** See a live Silver site in 30 seconds →

**IPro Gold — $60/month** · *badge:* Staying in touch
Everything in Silver, plus the layer that keeps you in front of people without you having to write
anything: e-cards for birthdays and holidays, merge-field letters, mail merge, printable mailing
labels, a rotating banner and coupons. Caps come off, and your assistant gets their own login.
· Unlimited clients and domains · 500 MB of files · 2 logins
· Setup: ~~$200~~ **$0 for founding customers**
**Button:** Start with Gold
**Quiet link:** See a live Gold site in 30 seconds →

**IPro Platinum — $90/month** · *badge:* Most complete
Everything in Gold, plus the three things that change how the week runs: the daily who-to-call list,
a portal your own clients log into for documents and messages, and invoicing with the right
provincial tax worked out for you. We also write you a blog post every month and manage your SEO.
· Unlimited clients and domains · 1,000 MB of files · 5 logins
· Setup: ~~$400~~ **$0 for founding customers**
**Button:** Start with Platinum
**Quiet link:** See a live Platinum site in 30 seconds →

**Broker Package — let's talk**
For brokerages and teams. Multi-agent pricing, a designated support contact, and up to ten logins.
**Button:** Talk to us about a team

### The founding-customer band

**H2:** Setup is free for the first [25] accounts.

**Body:** Setting up an account normally costs $150 to $400 depending on the plan, and that money
pays for a person doing real work — connecting your domain and getting your certificate issued,
loading your field's content and rewording it for your practice, bringing your client list across
from whatever you have it in now, and walking you through the whole thing live once you can see it
running. For the first [25] accounts on the new platform, that's free. Use the code **[FOUNDING]**
when you register. The offer ends [date] or when the [25] are gone.

### The commitments band

**H2:** What we commit to.

- **Monthly billing. No contract. Cancel any time.** You cancel from your own billing page; you
  don't have to ask us and you don't have to explain.
- **Thirty days to change your mind.** If it isn't right for you within your first thirty days, tell
  me and I'll refund what you've paid, setup included.
- **Your list is yours.** You can download your full client list as a spreadsheet from inside the
  product, whenever you like, without asking anyone.
- **The price you see is the price.** Tax is added at checkout at your province's real rate, and
  nothing else is.

### Tax note

**Body:** Prices above are before tax. At checkout we add the right rate for your province — GST,
HST, PST or QST — calculated properly rather than guessed. Outside Canada, no tax is added.

### Payment

**Body:** Payment is by PayPal subscription, monthly, quarterly or annually. You can change or cancel
the subscription yourself at any time.

### Comparison table

**H2:** Everything, side by side.
**Intro:** The full list. Most of it every plan has; the differences are marked.
*Build note: keep this table data-driven from `PackageFeature` rows as it is today. See Section K —
the `SmsReminder` row must be corrected in the seed data before this page ships, because it currently
renders a checkmark on all four plans for something that isn't built.*

### Pricing FAQ (three questions, inline)

**Can I change plans later?** Yes, in either direction. Nothing you've built is lost when you move.

**What if PayPal doesn't work for me?** Email me and we'll sort it out. [Owner to confirm what
alternative he is willing to support before this line ships.]

**Is there a discount for paying annually?** [Owner decision — see Section D. If annual moves to ten
months, this reads: "Yes. Pay for the year and you get two months free."]

---

## F5. Your first week — `/how-it-works`

**Page title:** `Your first week with IPRO — IPRO Advisers`

**Meta description:** `What actually happens after you sign up: your site goes live in minutes, we walk you through it live, your client list comes across, and your own domain is connected. Usually inside a week.`

**Eyebrow:** No surprises

**H1:** Your first week, hour by hour.

**Sub:** The reason most advisers never fix their website isn't money. It's that the last one took
four months and three arguments. Here is exactly what this takes, and exactly which parts are yours.

### Step 1 — The first ten minutes. Yours.

You choose a plan, fill in the registration form and pay. When you're done, your website is already
live at a temporary address — real pages, real content written for your field, your name on it.
Nothing is a placeholder. You can send someone the link that afternoon if you want to.

### Step 2 — The first day. Mine.

I go through the site and adjust the wording to your practice — the way you describe what you do,
your credentials, your city, your photo. Then I email you and we book twenty minutes.

### Step 3 — The walkthrough. Twenty minutes, together.

We get on a call with the site open in front of both of us. I show you the four things you'll
actually use — editing a page, adding a client, sending a newsletter, and where the leads land — and
you change something yourself while I'm there, so you know you can. Anything you don't like, I fix
while we're on the call.

### Step 4 — Your clients come across. A day or two.

Send me whatever you've got — a spreadsheet, an export from Outlook, a list from another system,
even a scan of a paper list. It comes in as real client records with names, contact details,
birthdays and any dates worth remembering. You don't retype anything.

### Step 5 — Your own domain. Usually the same week.

If you already own a domain, we point it at your new site — you add one record at your registrar and
we tell you exactly what to type, or you give me access and I do it. The security certificate is
issued automatically and renews itself. If you don't own one yet, we'll help you pick one.

### Step 6 — After that.

You use it. If you're on Platinum, the who-to-call list starts appearing on your dashboard every
morning. Birthdays and renewals start coming up on their own. When something needs changing, you
either change it in about a minute or you email me and I do it.

### Closing

**H2:** What we need from you, in total.

Twenty minutes on a call, a list of your clients in any format, and an answer to "what would you
like it to say about you." That's the whole ask.

**Button:** See your own site first — 30 seconds

---

## F6. For insurance and financial advisers — `/for/insurance-advisers`

**Page title:** `Website, CRM and follow-ups for insurance and financial advisers — IPRO Advisers`

**Meta description:** `One system for independent Canadian insurance and financial advisers: your website, your client book, renewal and birthday reminders, newsletters, and a daily list of who to call. See your own site in 30 seconds.`

**Eyebrow:** For independent insurance and financial advisers

**H1:** You've built a book over twenty years. Nothing is watching it for you.

**Sub:** You know which clients you should have called this month. The problem is that knowing lives
in your head, and your head is also running the appointments, the applications and the carrier
portals. IPRO puts your website, your client book and your follow-ups in one place, and then it
does the watching.

### Section — the specific problem

**H2:** The three things that actually cost you money.

**A quote request that sits.** Someone fills in the form on your site on a Tuesday and you see it
Friday. By Friday they've spoken to someone else. In IPRO that enquiry is a lead the moment it's
submitted, and it's on your dashboard the next morning if nobody has touched it.

**A review that never got booked.** The client who said "let's look at this again after the baby
comes." It's been fourteen months. IPRO tracks clients with no follow-up scheduled and puts them in
front of you.

**A renewal that goes past.** Policy renewals, birthdays and anniversaries come up automatically,
ahead of time, so the call happens before the date rather than after it.

### Section — what you get on day one

**H2:** Your site arrives written.

Pages built and populated for an insurance and financial practice — a home page, an about page,
testimonials, a newsletter signup, a request-a-meeting page and contact — plus articles your clients
can actually use. *Do You Actually Have Enough Life Insurance?* and *RRSP or TFSA: Which Should You
Prioritize?* are on your site the day you sign up, written in plain language, ready for you to edit
or leave alone. Financial calculators are built in as page blocks, so a visitor can run their own
numbers on your website instead of somebody else's.

### Section — the morning list

**H2:** What Platinum adds.

Every morning before you start, one screen: new leads, anyone who's been waiting more than a day,
clients with nothing scheduled — and one name at the top with a sentence explaining why it's that
one. *"Call Jennifer Walsh first — her policy review is four days overdue."* Then the reason
underneath. It is the difference between a system that stores your book and a system that works it.

Platinum also gives your clients their own login, where they can send you a message, see and upload
documents, and ask for an appointment — which is a better answer than "email it to me" when the
document has a policy number on it.

### Closing

**H2:** See it with your own name on it.

**Sub:** Thirty seconds, no account, nothing saved. You'll get a real insurance and financial
practice website with your name on it and the content already in place.

**Button:** Show me my website

**Under:** Want to use the real thing before you pay? [Ask me for a trial invitation] and I'll send
you one.

---

## F7. For mortgage brokers — `/for/mortgage-brokers`

**Page title:** `Website, CRM and client follow-up for mortgage brokers — IPRO Advisers`

**Meta description:** `For Canadian mortgage agents and brokers: a real website, lead capture that reaches you the same day, renewal reminders so past clients don't renew with the bank, and one client list that stays current.`

**Eyebrow:** For mortgage agents and brokers

**H1:** You close the deal. Then five years go by and the bank sends the renewal notice.

**Sub:** Every mortgage practice has the same two leaks: leads that go cold because someone else
called first, and past clients who quietly renew somewhere else because nobody stayed in touch.
Both leaks are the same problem — no single system holding the lead, the client and the date.

### Section — the specific problem

**H2:** Speed, then memory.

**Speed.** Whoever calls back first usually wins. An enquiry from your site becomes a lead
immediately, and if it's still sitting untouched the next morning it's the first thing on your
dashboard, by name.

**Memory.** A closing is not the end of a relationship, it's the start of a five-year gap. Set the
renewal date once and IPRO brings it back to you before it matters. Same for birthdays and
anniversaries — the reasons to call that don't feel like a sales call.

**In between.** A newsletter when rates move, a card at Christmas, a note on the anniversary of
their closing. All of it goes to the same client list, from the same place, without exporting
anything to a mail tool.

### Section — what you get on day one

**H2:** Your site arrives written.

A mortgage practice website with the pages built and the content already on them — including
*Fixed or Variable: Choosing the Right Mortgage Rate* and *What First-Time Buyers Should Know About
Pre-Approval*, written to be genuinely useful to someone deciding, not to fill space. Mortgage and
loan calculators drop into any page as a block, so a first-time buyer can run their own numbers on
your site while your name is at the top of it.

### Section — the morning list

**H2:** What Platinum adds.

The daily list: who came in, who's been waiting, who has nothing scheduled — one name at the top
with a reason. *"Call David Park first — his pre-approval follow-up is three days overdue,"* and
underneath it, why it matters now. Plus a client portal where documents move without going through
email, and invoicing if you bill for anything directly.

### Closing

**H2:** See it with your own name on it.
**Button:** Show me my website
**Under:** Thirty seconds. No account, nothing saved.

---

## F8. For accountants and bookkeepers — `/for/accountants`

**Page title:** `Website, client portal and CRM for accountants and bookkeepers — IPRO Advisers`

**Meta description:** `For Canadian accounting and bookkeeping practices: a website that arrives with nineteen articles already written, a secure client portal so documents stop living in email, and invoicing with the right provincial tax.`

**Eyebrow:** For accountants and bookkeepers

**H1:** Your clients are emailing you their T4s. There's a better place to put them.

**Sub:** Every small practice runs the same two workarounds: documents arriving as email attachments
from wherever the client happened to be, and a website nobody has touched since it was built. IPRO
fixes both, and it does the writing for you.

### Section — the content

**H2:** Nineteen articles, already on your site, on day one.

Not stock filler. Real pieces across four sections — Personal Accounting, Business Accounting, an
Info Centre and Calculators. Personal tax preparation with a checklist of every slip and receipt to
bring. General ledger and chart of accounts. Accounts receivable and payable. Bank reconciliation.
Financial statements. Payroll. Whether to incorporate. A year-end checklist. Bookkeeping habits that
pay off at tax time. What the CRA is and what a CPA is, for the clients who've never asked.

You can edit any of them, delete any of them, or leave them exactly as they are — which most people
do, because they're already right. Either way, the answer to "shouldn't we put something on the
website?" stops being a project you keep deferring.

**Link:** [See the full list of what you'd start with]

### Section — the portal

**H2:** Documents stop living in your inbox.

On Platinum, each client gets their own login. They upload their documents there and you upload
theirs; both of you can download either. Files are stored privately, not on a public address, and
they can only be retrieved through a signed-in download. Only certain file types are allowed —
PDFs, Word, Excel, images, text and CSV — and the contents are checked against the extension, so a
renamed file doesn't get through.

They can also message you in one running conversation rather than a chain of forwarded replies, ask
for an appointment, and update their own contact details — which then update in your records
without you retyping them.

### Section — billing

**H2:** Invoices with the right tax on them, without you working it out.

Build an estimate or an invoice from line items and the provincial tax is calculated from *your
client's* province, not yours. Send it and they get a link — no login required — where they can
approve or decline an estimate, or pay an invoice through your own payment link. Overdue invoices
send their own weekly reminder until they're paid and stop the moment you mark one paid. Everything
exports to CSV for QuickBooks.

### Section — the timing

**H2:** Start in June, not February.

You know exactly how much attention you'll have in March. Setting this up takes about twenty minutes
of your time, and the quiet months are when it costs you nothing. By the time the season starts, the
portal is where the documents go.

### Closing

**H2:** See it with your own name on it.
**Button:** Show me my website
**Under:** Thirty seconds. No account, nothing saved.

---

## F9. What's included — `/whats-included`

**Page title:** `What's included — IPRO Advisers`

**Meta description:** `Everything in IPRO, by plan: the website builder, client records, newsletters, campaigns, the client portal, invoicing and the daily who-to-call list — plus an honest list of what isn't built yet.`

**Eyebrow:** The whole list

**H1:** Everything that's in it, and everything that isn't.

**Sub:** Most of this you'd expect. A few things you wouldn't. And at the bottom, the list of things
we don't do — which is the part worth reading first if you're deciding.

### Your website

Three templates, each with colour variants, and you can switch between them without losing any of
your content. Pages you create yourself, with navigation up to three levels deep and a mega-menu if
you need one. Content blocks for text, images, video, photo galleries with a lightbox, testimonials,
your own bio, a reviews badge, gated downloads, custom forms, section indexes and financial
calculators. Content already written for your field on every page from day one. Your own domain,
with the security certificate issued and renewed automatically. SEO fields on every page.
Multilingual editing. Visitor and page tracking.

### Your clients

Client records with account types and groups, notes, an activity timeline and follow-ups. Import
from a spreadsheet or from Outlook. Export your whole list to a spreadsheet whenever you want. A
calendar and scheduler with email reminders. Lead capture forms on your website that feed a prospect
list automatically. Custom forms with eight starter templates. Polls and surveys. Testimonial
collection, including sending a request to one named client.

### Staying in touch

Newsletters with a rich-text editor, starter templates, banners and editions, sent through a real
delivery service with open and failure tracking. One-click unsubscribe handled to the current email
standard, which is a large part of why email arrives at all. Automated drip campaigns. A marketing
calendar. A social post composer, with AI drafting if you want a starting point. Gated downloads
that capture an email. A "Did You Know" teaser block.

**Gold adds:** Fourteen pre-designed e-cards for birthdays, holidays and seasons. E-letters with
merge fields. Mail merge. Printable client mailing labels. A rotating homepage banner. A coupon
manager.

### The Platinum layer

**The daily list.** Every morning: new leads, leads older than a day, clients with nothing scheduled,
and one ranked next action with the reason it matters.

**A client portal.** Your clients get their own login for secure messages, shared documents,
appointment requests and their invoices.

**Invoicing and estimates.** Line items, automatic tax from the client's province, a signed link
they can approve, decline or pay from without logging in, recurring schedules, and QuickBooks CSV
export.

**Life-event reminders.** Birthdays, policy renewals and anniversaries, automatically, in advance.

**One blog post a month, written for you.** And SEO managed across every page.

**Google Calendar two-way sync.**

### Team logins

Your assistant or office manager gets their own login with access to everything except billing. Two
logins on Gold, five on Platinum, ten on Broker.

### What isn't built

**No text messages.** Reminders and notifications go by email. SMS is costed and planned. It is not
built, and you should not choose IPRO on the assumption that it's coming next month.

**No payment processing for your clients' payments.** You can send an invoice with a Pay Now button;
it goes to your own payment link. The money never passes through us, which also means we can't help
if something goes wrong with it.

**No automatic social posting.** You can draft a post in IPRO. Publishing it is still a copy, a
paste and a click.

**No MLS or IDX property listings.**

**No phone app.** It works in a phone browser. That is not the same thing and we're not going to
call it one.

**No integrations marketplace.** Google Calendar syncs. Nothing else connects automatically yet.

**No virus scanning on uploaded files.** Uploads are restricted by type and the contents are checked
against the extension, and files are stored privately. But they are not scanned for malware, and if
that matters for your practice you should know it now rather than later.

### Closing

**Button:** See all three plans and what each costs
**Quiet link:** Or see a working site with your name on it, in thirty seconds →

---

## F10. Your data — `/your-data`

**Page title:** `Your data, your clients, your domain — IPRO Advisers`

**Meta description:** `Straight answers about where your client information lives, who can see it, how documents are stored, and exactly how you get everything back if you decide to leave.`

**Eyebrow:** The questions nobody likes asking

**H1:** It's your client information. Here's exactly how we treat it.

**Sub:** You hold people's financial details for a living, so you already know the right questions.
These are the answers, in plain language, including the parts that aren't perfect.

### Who can see your client information

Your clients belong to your account and are visible to you and to any team logins you create. Nobody
else's account can see them. IPRO staff can access account data when you ask for help with something
— for example when we bring your list across during setup — and not otherwise.

### Where it's stored

Everything runs on Microsoft Azure. [Owner: confirm the Azure region for both applications and the
database before this page ships, and state it here plainly — for example "in Microsoft's Canadian
data centres." Do not publish a location claim that hasn't been checked.]

### Client documents

If you're on Platinum and using the client portal, documents you and your clients share are stored
privately — not on a public web address. The only way to get a file is through a signed-in download
link. Only PDFs, Word and Excel files, common image formats, text and CSV are accepted, and every
upload's actual contents are checked against its file extension, so a program renamed to look like a
PDF is rejected. Files are capped at 20 MB each.

**What we don't do:** we don't scan uploaded files for viruses. If a client uploads something
infected, we won't catch it. We'd rather tell you that than let you assume otherwise.

### Email and consent

Every newsletter and campaign email carries a working unsubscribe, including the one-click header
that mail providers now expect, and unsubscribes are honoured automatically across your account. You
don't have to manage a list by hand, and you don't have to worry about sending to someone who opted
out.

### Getting your information out

**Your client list.** Open Clients and click Export. You get the whole list as a CSV file, straight
away, without asking anyone. Do it today if you like.

**Your leads.** Same — the website leads list exports to CSV.

**Your invoices.** Export to CSV, including a QuickBooks-formatted version.

**Your website content.** There's no one-click export for your pages today. If you leave, email me
and I'll send you your content as files within five business days. That's a promise from a person,
not a button, and I'd rather describe it accurately.

**Your domain.** Your domain is registered to you, not to us. If you leave, you change one DNS
record and it points wherever you want it to. We can't hold it, and we wouldn't.

### Cancelling

You cancel from your own billing page. You don't have to phone anyone, and you don't have to explain
why. Within your first thirty days, tell me and I'll refund what you've paid, setup fee included.

---

## F11. Who's behind this — `/about`

**Page title:** `Who's behind IPRO Advisers`

**Meta description:** `IPRO Advisers has been building and running websites for Canadian advisers, accountants and mortgage brokers since 2014. The platform is new. The work isn't.`

**Eyebrow:** Who's behind this

**H1:** A small Canadian company that has been doing this for over a decade.

**Sub:** IPRO has been building and running websites for Canadian insurance advisers, mortgage
brokers and accountants since 2014. What's new is the platform, not the work.

### Section — why rebuild

**H2:** Why we started again.

The old system did what it was built to do. But it was built when a website was a brochure and the
client list lived somewhere else, and every year that gap cost our customers more. Advisers ended up
back where they started — a site here, a CRM there, a mail tool somewhere else, and no way to answer
the only question that matters, which is who should I be calling today.

So we rebuilt the whole thing as one system rather than several, on infrastructure we run ourselves.
The website, the client book, the follow-ups, the newsletters, the portal and the invoicing are the
same database now. That's what makes the daily list possible, and the daily list is the reason the
rebuild was worth doing.

### Section — the person

**H2:** Bahman Motamed.

[Owner: three or four sentences in your own words — how you got into this, how long, why advisers
specifically, and what you're actually like to deal with. Written first-person. This should not read
like a bio; it should read like the first thirty seconds of a phone call. Then a real photo. Not a
stock one, not a logo.]

### Section — how we work

**H2:** How this actually works, day to day.

**It's a small operation.** You'll deal with me or someone I work with directly. That is a real
constraint — we're not a company with a support floor — and it's also the reason your problem gets
solved instead of triaged.

**We do the setup ourselves.** Your domain, your content, your client list. It isn't a form you fill
in and hope.

**We build in Canada, for Canada.** Prices in Canadian dollars. Provincial tax calculated properly,
for your subscription and for the invoices you send your own clients. Written for the way advisers
here actually work.

### Section — what we won't do

**H2:** Things you won't find on this website.

We're not going to show you logos of companies to imply they endorse us. We're not going to invent a
number of customers. We're not going to publish testimonials we don't have. What we'll do instead is
let you use the thing before you pay for it, tell you plainly what it doesn't do, and put a real
phone number on the contact page.

**Button:** See a site with your name on it
**Quiet link:** Or just email me →

---

## F12. Contact — `/contact`

**Page title:** `Contact IPRO Advisers`

**Meta description:** `Email, phone and a short form. Ask a question, book a look at the product, or request a trial invitation.`

**Eyebrow:** Talk to a person

**H1:** Ask me anything before you decide.

**Sub:** There's no sales team here. Whatever you send arrives with me.

**Email:** [email] · **Phone:** [phone] · **Hours:** [hours, and the time zone]

### Form

**Heading:** Send a question
**Fields:** Your name · Your email · Phone (optional) · What do you do? (Insurance or financial
advice / Mortgages / Accounting or bookkeeping / Something else) · What would you like to know?
**Button:** Send it
**Microcopy under the button:** I'll reply personally, usually the same business day. Your details
aren't going on a mailing list.

### Three side cards

**Card — Just want to see it?**
Thirty seconds, no account, nothing saved.
**Link:** Show me my website →

**Card — Want to try the real thing?**
Ask for a trial invitation and I'll send you one — fourteen days on the full Platinum plan, no card.
**Link:** Request an invitation →

**Card — Running a team?**
Broker and multi-agent pricing is a conversation. Tell me how many people and I'll come back with a
number.
**Link:** Ask about team pricing →

---

## F13. Questions — `/faq`

**Page title:** `Questions — IPRO Advisers`

**Meta description:** `Straight answers about switching, setup fees, contracts, your data, how technical it is, and what happens if you leave.`

**Eyebrow:** Questions

**H1:** The things people actually ask.

**Sub:** In roughly the order they get asked.

**How technical do I need to be?**
Not at all, and that isn't a slogan — it's what the product is built around. Your site arrives
finished, so most of what you'd do is change a sentence or swap a photo, which works the way editing
a document works. The setup that genuinely is technical — pointing your domain, getting the security
certificate — is done for you. If you can use email and a web browser, you can run this. And if
you'd rather not, email me what you want changed and I'll change it.

**I already have a website. Do I have to throw it away?**
No, and you shouldn't decide that yet. Two options. Keep your existing site and use IPRO for the
client side — the list, the follow-ups, the newsletters, the portal. Or move across, keep your
domain name exactly as it is, and point it at the new site when you're happy with it. Nobody visiting
your address sees anything change except the site getting better. Whichever you choose, your domain
stays registered to you.

**How long does it take?**
Your site is live within minutes of signing up. Getting it right — your wording, your photo, your
client list, your own domain — usually takes a week, of which about twenty minutes is your time.

**I'm too busy to switch right now.**
That's the honest reason most advisers put this off for years, so here's what it actually costs you:
twenty minutes on a call, and sending me your client list in whatever form it's in. Everything else
is on my side. If you're an accountant, start in the summer. If you're an adviser, start in a week
where you've got one clear afternoon.

**Why is there a setup fee?**
Because a person does real work before you log in for the first time: connects your domain and gets
the certificate issued, loads your field's content and rewrites it around your practice, brings your
client list across from whatever you have it in now, and walks you through the whole thing live.
That's what the $150 to $400 pays for, depending on the plan. **Right now it's free** for the first
[25] accounts on the new platform — use the code [FOUNDING] when you register.

**Is there a contract?**
No. Monthly billing, cancel any time, from your own billing page. And if it isn't right for you in
the first thirty days, tell me and I'll refund what you've paid, setup included.

**What happens to my data if I leave?**
Your client list downloads as a spreadsheet from inside the product, any time, without asking us —
try it on day one if you want. Leads and invoices export the same way. Your website content doesn't
have a one-click export yet; if you leave, email me and I'll send it to you as files within five
business days. Your domain is registered to you, so you point it somewhere else and that's that.

**Is my client information safe?**
Your clients are visible to you and to any team logins you create, and to nobody else's account.
Everything runs on Microsoft Azure. Client documents in the portal are stored privately and can only
be downloaded through a signed-in link, with file types restricted and contents checked against the
extension. We don't scan uploads for viruses — that's a real gap and we'd rather say so. There's more
detail on [Your data].

**Can I try it before I pay?**
Two ways. Free and instantly: put your name in and we'll build you a real site to look at in about
thirty seconds — no account, nothing saved. Or ask me for a trial invitation and I'll send you one,
fourteen days on the full Platinum plan, no card.

**Do you send text messages?**
No. Reminders go by email. SMS is planned and not built, and we've taken the claim off this website
because it wasn't true.

**Which plan should I be on?**
If you mainly need to be online and organised, Silver. If you want the birthday cards and the
staying-in-touch tools and a login for your assistant, Gold. If you want the system to tell you who
to call, give your clients a portal, and send invoices with the right tax on them, Platinum — that's
the one most people who've seen the whole thing choose.

**Can my assistant have their own login?**
Yes, on Gold and above. They get everything except billing. Two logins on Gold, five on Platinum,
ten on Broker.

**Do you work with brokerages and teams?**
Yes. Broker pricing covers multiple agents, a designated support contact and up to ten logins, and
it's set by conversation rather than by a card on a pricing page. Tell me how many people and I'll
come back with a number.

**Who owns the content on my site?**
You do. Including the starter articles, which are yours to edit, keep or delete.

**Do I get charged tax?**
Yes, at your province's real rate, added at checkout — GST, HST, PST or QST depending on where you
are. Outside Canada, no tax is added. All prices on this site are shown before tax.

---

## F14. Registration — `/Account/Register`

*Build note: recommended as two steps. Copy for both below. The existing verification code and terms
checkbox stay; the embedded agreement text box is replaced with a link.*

**Page title:** `Create your account — IPRO Advisers`

### Step 1

**Eyebrow:** Step 1 of 2

**H1:** Let's get your account open.

**Sub:** Four things now, the rest on the next screen. Takes about a minute.

**Fields:** First name · Last name · Email address · What do you do? · Which plan?

**Field help — email:** This is your login and where your welcome details go. Use one you check.

**Field help — plan:** You can change plans later, in either direction.

**Promotion code field label:** Founding customer code
**Placeholder:** If you were given one
**Applied message:** [returned by the existing validation endpoint — for example: "Code accepted:
100% off the setup fee."]
**Rejected message:** That code isn't valid for this plan, or it's expired. You can still carry on —
you'll just pay the normal price.

**Button:** Continue

**Under:** Already have an account? Sign in

### Step 2

**Eyebrow:** Step 2 of 2

**H1:** A few details for your account and your invoices.

**Sub:** Your province sets your tax rate and your time zone, which is why we ask.

**Fields:** Business name · Business phone · Address (optional) · City · Province · Postal code ·
Country · Time zone · Designation (optional) · Fax and mobile (optional)

**Verification code label:** Type these four digits
**Help, above the field:** It's how we tell you apart from an automated signup.

**Terms:** I've read and accept the [subscription agreement].

**Button:** Create my account

**Microcopy under the button:** Creating your account doesn't charge you anything. You'll choose how
to pay on the next screen.

### Registration success — `/Account/RegisterSuccess`

**H1:** You're in, [First name]. One thing left.

**Sub:** Your account exists and your website is already live at the address below. To turn on your
plan, choose how you'd like to pay.

**Primary button:** Choose how to pay

**Secondary link:** Look at my website first

**Box heading:** Keep these somewhere safe
**Box body:** Your sign-in details are below and they're also in your inbox. You'll be asked to
change the password the first time you sign in.
- Website: [domain]
- Username: [username]
- Temporary password: [password]

**Foot:** Nothing else is needed from you today. I'll be in touch within one business day to book
your walkthrough. — Bahman

*Build note: the current page presents the credentials first and the subscription requirement as an
afterthought, which is why customers finish registration believing they're done. Inverting it is the
highest-value change on this screen.*

---

## F15. Standing elements

### Sign-in page microcopy

**H1:** Welcome back.
**Fields:** Email or username · Password
**Button:** Sign in
**Links:** Forgot your password? · Don't have an account yet? See what you'd get in 30 seconds

### 404

**H1:** That page isn't here.
**Body:** It may have moved, or the link may be wrong. Try the [home page], or [email me] and I'll
point you at the right place.
**Button:** Back to the start

### Error page

**H1:** Something went wrong at our end.
**Body:** That's on us, not on you. Try again in a minute. If it keeps happening, email [email] and
tell me what you were doing — that's usually enough for me to find it.

### Cookie / consent banner, if one is needed

**Body:** We use only the cookies needed to keep you signed in and to count page visits. No
advertising trackers.
**Buttons:** Fine by me · What's this?

---

## F16. The follow-up email sequence

For visitors who gave an email on the preview page but haven't signed up. Four emails over twelve
days, then stop. Plain text, from the owner's own address, no images, no template chrome — for this
audience a plainly-written email from a person outperforms a designed one from a company.

**Email 1 — immediately. Subject: Here's the site we built you**

> Hi [First name],
>
> Here's the link back to the site we put together for you:
>
> [link]
>
> It'll stay there. Click into the About page and the articles — it's a real site, not a picture of
> one, and everything on it is what you'd start with.
>
> If anything about it looks wrong for your practice, tell me and I'll tell you straight whether we
> can fix it.
>
> Bahman Motamed
> IPRO Advisers · [phone]

**Email 2 — day three. Subject: The part that isn't the website**

> Hi [First name],
>
> The website is the part you can see in thirty seconds, so it's the part we lead with. It isn't
> the part that matters most.
>
> What matters is that the site and your client list are the same system. Someone fills in your
> contact form and they're in your prospect list before you've read the email. When they become a
> client, that record carries your notes, their follow-ups, their birthday and their renewal date.
>
> Which means the software can do something no collection of separate tools can: look at all of it
> overnight and tell you who to call today.
>
> [See what that looks like]
>
> Bahman

**Email 3 — day seven. Subject: What it doesn't do**

> Hi [First name],
>
> Most companies don't send this email. I'd rather you found out now than in month two.
>
> IPRO doesn't send text messages — reminders go by email. It doesn't process your clients'
> payments; invoices link to your own payment method. It doesn't post to social media for you, and
> there's no phone app, though it works fine in a phone browser.
>
> If any of those are deal-breakers, no hard feelings and you can ignore the rest of these.
>
> If they're not, the full list of what is and isn't in it is here: [link]
>
> Bahman

**Email 4 — day twelve. Subject: Setup is free until [date]**

> Hi [First name],
>
> Quick note, then I'll leave you alone.
>
> Setting up an account normally costs between $150 and $400, depending on the plan. It pays for
> real work — connecting your domain, getting your certificate issued, loading and rewording your
> content, bringing your client list across, and walking you through it live.
>
> For the first [25] accounts on the new platform it's free. Use the code [FOUNDING] when you
> register. That ends on [date].
>
> If you'd rather use the real thing before deciding, say the word and I'll send you a trial
> invitation — fourteen days on the full plan, no card.
>
> Either way, thanks for looking.
>
> Bahman Motamed · [phone] · [email]

---

# G. Objection handling

Six objections do almost all the damage. Each one needs an answer in the buyer's own words, placed
where the objection actually forms — not collected at the bottom of the page where only the already-
convinced will scroll.

---

### 1. "I already have a website."

**Where it forms:** in the hero, within four seconds. It is the most common reason a qualified
visitor leaves immediately, and the current site does not address it anywhere.

**Where the copy goes:** a short band directly under the hero on the home page, and again on every
vertical page. Full version on the FAQ.

**The copy — home page band:**

> **Already have a website?**
> Then you already know the two problems with it: you can't change it yourself, and it doesn't know
> a single thing about your clients. Keep it and use IPRO for the client side, or move across and
> keep your domain name exactly as it is — nobody visiting your address sees anything change except
> the site getting better. Either way your domain stays registered to you.

**Why this works:** it refuses the fight. Arguing that their site is bad insults them. Reframing
"website" as "the thing that should be connected to your client book" changes the category, which is
the whole positioning argument delivered in four lines.

---

### 2. "I'm not technical."

**Where it forms:** at the pricing page and again at the registration form. It is usually stated as
a question about difficulty and is really a fear of being humiliated by software.

**Where the copy goes:** as a reassurance line under the primary button on the home page and on
every vertical page; as a full answer on the FAQ; as a sentence in the walkthrough step of
`/how-it-works`.

**The copy — short form, under buttons:**

> No setup on your end. We connect your domain, load your content, and show you how it works on a
> call.

**The copy — full form, FAQ:**

> Not at all, and that isn't a slogan — it's what the product is built around. Your site arrives
> finished, so most of what you'd do is change a sentence or swap a photo, which works the way
> editing a document works. The setup that genuinely is technical — pointing your domain, getting
> the security certificate issued — is done for you. If you can use email and a web browser, you can
> run this. And if you'd rather not, email me what you want changed and I'll change it.

**Why this works:** it names the specific technical tasks and assigns them to someone else, then
offers a permanent escape hatch. "It's easy" is what the last website guy said.

---

### 3. "What happens to my data if I leave?"

**Where it forms:** at the pricing page, silently, and it is rarely asked out loud. An adviser who
has been locked out of their own website by a developer before will be thinking about this the whole
time.

**Where the copy goes:** its own page (`/your-data`), a summary block on the pricing page, and the
FAQ.

**The copy — pricing page block:**

> **Leaving is a button, not a negotiation.**
> Your client list downloads as a spreadsheet from inside the product, any time, without asking us.
> Your domain is registered to you — you point it somewhere else and that's that. Subscriptions
> cancel from your own billing page. Nothing here is designed to make going somewhere else painful.

**The copy — the honest gap, on `/your-data`:**

> Your website content doesn't have a one-click export yet. If you leave, email me and I'll send it
> to you as files within five business days. That's a promise from a person rather than a button,
> and I'd rather describe it accurately than dress it up.

**Why this works:** the self-serve export is real, checkable today, and unusual. Naming the one place
where it isn't self-serve makes the rest believable. *Recommendation to the owner: build the website
content export. It's the last brick in the strongest trust argument on the site.*

---

### 4. "Why is there a setup fee?"

**Where it forms:** on the pricing page, at the exact moment the eye lands on the second number.

**Where the copy goes:** immediately beside the fee, never on a separate page. Also FAQ.

**The copy — inline, on every plan card:**

> Setup: ~~$400~~ **$0 for founding customers** — [what setup covers]

**The copy — expanded, directly under the plan cards:**

> **What the setup fee is for.**
> A person does real work before you log in for the first time. We connect your domain and get the
> security certificate issued. We load your field's content and reword it around your practice —
> your city, your credentials, the way you describe what you do. We bring your client list across
> from whatever you have it in now, whether that's a spreadsheet, Outlook, or another system. Then
> we walk you through the whole thing live, once it's running and you can see it.
>
> That's $150 to $400 depending on the plan. For the first [25] accounts on the new platform, it's
> free — use the code [FOUNDING] when you register. The offer ends [date].

**Why this works:** the fee stops being a toll and becomes labour, at which point $400 for a day of
someone's work is obviously cheap. Then it's waived anyway, so the buyer gets the value framing *and*
doesn't pay.

---

### 5. "Is my client data safe?"

**Where it forms:** on the vertical pages, especially for accountants, and for anyone whose regulator
has opinions.

**Where the copy goes:** `/your-data` in full; a three-line summary on the accountants page next to
the client portal section; FAQ.

**The copy — summary form:**

> **Where your clients' documents live.**
> Not in your inbox. Documents shared through the client portal are stored privately — never on a
> public web address — and can only be retrieved through a signed-in download. File types are
> restricted, and every upload's actual contents are checked against its extension, so something
> renamed to look like a PDF is rejected. We don't scan uploads for viruses; that's a real gap and
> we'd rather you heard it from us.

**Why this works:** specificity is the proof. "Bank-level security" means nothing to this buyer;
"checked against its extension" and "never on a public web address" are the kind of concrete
statements that read as someone who actually built it. And volunteering the one gap makes everything
before it credible.

---

### 6. "I'm too busy to switch right now."

**Where it forms:** at the end, as the polite exit. It is almost always the real objection when
someone says "let me think about it."

**Where the copy goes:** the closing band on the home page, the end of `/how-it-works`, the FAQ, and
email 4 in the sequence.

**The copy:**

> **"I'm too busy right now."**
> That's the honest reason most advisers put this off for years, so here's what it actually costs
> you: twenty minutes on a call, and sending me your client list in whatever form it's already in.
> Everything else is on my side. If you're an accountant, start in the summer. If you're an adviser,
> pick a week where you've got one clear afternoon.

**Why this works:** it agrees with them, then quantifies the ask so small that "too busy" stops being
credible to the person saying it. Naming their own seasonality proves you know their business.

---

### Two more worth pre-empting

**"How do I know you'll still be here in three years?"** — Answered by longevity, not by promises.
The company has been doing this since 2014. Say it on the home page, in the founder band, in the
adviser's own terms: this is not somebody's side project.

**"$90 a month is a lot."** — Never argue the absolute number; always reframe to the stack. On the
pricing page, under the comparison table:

> **What this replaces.** A website builder, a CRM, an email marketing tool, a scheduler and a
> client portal, bought separately, generally run somewhere between $110 and $190 a month — and none
> of them know anything about each other. You'd still be the one moving lists between them.

---

# H. Proof without social proof

The company has no testimonials it can publish, no customer count it can state, and no logos it can
show for the new platform. It also is not new, and that distinction is the foundation of everything
below. Eight instruments, in order of strength.

### 1. Demonstration instead of assertion — the preview

The strongest proof on the site is that a stranger can use the product before giving anything up.
Every claim about the website is settled in thirty seconds by looking. **This is why the preview is
the centre of the funnel and not a feature bullet.** Design and copy should both treat it as the
proof section, not the demo section.

### 2. Longevity, stated precisely

*"IPRO has been building and running websites for Canadian insurance advisers, mortgage brokers and
accountants since 2014."* True, defensible, and worth more than any testimonial to a buyer whose
main fear is that this company disappears. It should appear on the home page founder band, the About
page, and the FAQ answer about longevity.

**Guardrails.** The 2014 About page claims "since 2001" and "more than a decade," and describes the
company as a subsidiary of another firm. None of that is verified here. *Owner: confirm the actual
founding year and the current corporate structure before publishing anything more specific than
2014.* And drop "world renowned" — an unverifiable superlative attached to a parent company is
exactly the kind of claim the rest of this site is built to avoid.

### 3. The founder, by name, with a face and a phone number

A real photo, a real first-person paragraph, a real email address and a real phone number on the
contact page. For a solo buyer choosing a small vendor, "who will I be dealing with" outranks every
feature. It also converts the company's smallness from a liability into the reason support is good.

### 4. Numbers about the product, since there are none about customers

Every one of these is countable in the codebase today and none of them require a customer:

- **19** articles waiting on an accounting practice's site on day one, across **4** sections
- **3** website templates, each with colour variants
- **14** e-card designs
- **8** starter form templates
- **13** provinces and territories with sales tax calculated properly
- **5** team logins on Platinum, **10** on Broker
- **20 MB** per client document, **1,000 MB** of storage on Platinum

Specific numbers read as inventory. Round claims read as marketing.

### 5. Radical transparency as a trust instrument

Three deliberate acts of disclosure that competitors won't match:

- **The "four things we don't do" band on the home page**, and the full version on `/whats-included`.
- **The whole price on one page**, including the setup fee and the tax treatment, with no "starting
  from."
- **Naming the virus-scanning gap** on `/your-data`.

This is the single most differentiating decision in this document. A buyer who has been oversold
before recognises honesty faster than they recognise features, and volunteered bad news is the
cheapest credibility available to a company that cannot yet quote a customer.

### 6. Commitments that cost the company something

A guarantee is only proof if it could hurt. Three that can:

- **Monthly billing, no contract, cancel any time.** (Revived from the company's own 2014 pricing
  page, still true, and currently stated nowhere.)
- **Thirty days to change your mind — a full refund including setup.**
- **Your client list exports to a spreadsheet from inside the product, without asking us.**

The third is the strongest, because the visitor can verify it on day one, and a company planning to
trap you does not build that button.

### 7. Real screenshots of the real product

Unretouched, including the boring parts of the interface. See Section I for the shot list. A
screenshot of working software is worth more to this buyer than any amount of illustration, and
faking or beautifying them destroys the effect entirely.

### 8. Infrastructure named plainly, not as a logo wall

Microsoft Azure, PayPal, SendGrid, automatic certificate issuance. State them as facts in sentences
(*"Payment is by PayPal subscription"*, *"Everything runs on Microsoft Azure"*). **Do not build a
logo strip.** A row of vendor logos on a page with no customer logos reads as borrowed credibility
and invites the exact question you don't want asked.

### What must not be done

- **Do not put the legacy portfolio on the new site.** Of the eleven client sites listed on the old
  page, seven no longer respond, one is a parked domain, and the three that survive do not appear to
  run on IPRO infrastructure. Publishing that list would be a checkable falsehood.
- **Do not imply past clients are current customers of the new platform.**
- **Do not revive "Top financial gurus of the country trust us"** or any variant. It was an
  unsupported claim in 2014 and it is a worse one now.
- **No stock photography of people presented as customers or staff.**

### Two things the owner should start now, which this document cannot write

1. **A named-reference programme.** Approach the surviving portfolio clients and any current
   customers, ask permission properly, and offer something real in exchange — a discount, a free
   period, help with something. One named Canadian adviser saying one specific true sentence is
   worth more than the entire proof architecture above.
2. **Build the case study into the founding offer.** When someone claims the free setup, ask: *"In
   exchange, if it goes well, may I write up how it went — with you approving every word before it's
   published?"* That converts the first twenty-five customers into a legitimate testimonial pipeline
   instead of a gap that has to be designed around forever.

---

# I. Designer handoff

Written for whoever builds this. Assume ASP.NET Core Razor views under `src/IPRO.Web/Views/`, assets
in `wwwroot/`, Bootstrap 5 and Font Awesome already available, and a strict CSP — no inline event
handlers, and inline `<script>` needs `nonce="@Context.GetCspNonce()"`. Raw CSS in a `.cshtml`
`<style>` block must escape `@media`, `@keyframes` and `@font-face` as `@@media` and so on.

## Global direction

**Keep the existing brand core. Retire the hero treatment.**

Keep navy `#1a3a6b`, the brass accent `#a9812f`, and Georgia for headings. For a fifty-year-old
financial professional, a serif headline over navy reads as an institution rather than an app, and
the brass is already doing useful work distinguishing the premium tier. This is not a rebrand.

Retire the full-bleed navy-to-blue gradient hero. It is the single most generic signal on the current
page — this buyer has seen that exact gradient on every software company that ever cold-called them.
**Invert the page instead:** a light, warm, near-white hero with navy type and one strong button;
navy reserved for two or three full-width bands lower down the page and the footer. Colour becomes
punctuation rather than wallpaper, and the product screenshots — which are the actual argument —
stop competing with the background.

**Type.** Body at 17–18px minimum, line height 1.6, measure capped at 65–75 characters. Headings in
Georgia; body in the system sans stack. This audience frequently reads on a laptop at low brightness
in a bright room. Small grey type is not a style choice here, it is an accessibility failure.

**Contrast.** Body text at 7:1 or better against its background. No grey-on-grey. No thin weights
below 16px, ever.

**Buttons.** Large targets, verb labels, never icon-only, never relying on hover to be discoverable.
One primary button per screen — the current home page's three same-weight CTAs are why the hero was
already narrowed to one, and that discipline should hold everywhere.

**No dark mode.** Not this audience, not this budget, not worth the surface area.

## Page by page

### Home page

**What dominates:** the headline and one button, then — immediately below the fold — a real
screenshot of the dashboard with the morning list on it. The first product image should arrive
before the second scroll.

**Emotional register:** relief. Not excitement, not ambition. The feeling to design for is *somebody
has finally organised this.* Calm spacing, generous white, nothing bouncing.

**Imagery required:**
- **Shot 1 — the dashboard at 7 a.m.** Must show the AI Daily Assistant card at the top of the agent
  dashboard: three counts, the ranked suggestion, and the reason line beneath it. Use Ontario-plausible
  names. Include enough of the surrounding interface that it reads as a real screen, not a widget
  someone drew.
- **Shot 2 — a finished adviser website**, desktop and phone in one composition, rendered from the
  real template engine. It must look like a site an adviser would be pleased to have, because the
  entire first pillar is that claim.
- **Shot 3 — the clients list** with a follow-up showing as due. Must show real record structure:
  names, account type, a next-follow-up column.
- **Shot 4 — a newsletter as it lands in an inbox**, in the branded wrapper with a banner and the
  adviser's own details in the footer.

**Where motion earns its place:** exactly one moment — the three counts on the AI card counting up
once when the section scrolls into view. Fast, under 600ms, no loop, no replay. It draws the eye to
the one claim that matters. Nothing else on the page moves.

**What would be a mistake here:**
- Any hero illustration — abstract shapes, isometric offices, blob people.
- A carousel of anything.
- Fade-up-on-scroll for body copy. A skeptical reader who has to wait for text to appear reads less
  of it.
- Feature icon grids. Four rows of coloured circles with checkmarks is what the current page does and
  it is precisely what makes it read as a spec sheet.
- Stock photography of young people in a bright loft. If real photography is used at all, it should
  look like a Canadian small office in February.

### `/Preview` — the form

**What dominates:** the form. It should be the only thing on the screen. One card, centred, four
fields, one button.

**Emotional register:** effortless. The visitor should sense the end of this before they start it.

**Design notes:** put the "What do you do?" select first and make it visually the largest field —
three big radio cards with a plain label each, rather than a dropdown, so the choice is one click
instead of two. Keep the promise sentence directly above the button where it will be read, not below
it where it won't.

**What would be a mistake:** adding fields. Every additional field here costs conversions on the
highest-value action on the site. Also: do not add a fake progress bar or an artificial delay to
"earn" the thirty seconds. If it renders in two seconds, let it — speed is the surprise, and a
manufactured wait is a small lie that sets the tone for everything after it.

### `/Preview/Show` — the result

**What dominates:** the iframe. It should be the largest object on the screen by a wide margin — the
whole page is a frame around somebody's own website, and the right-hand column is a sidebar, not a
partner.

**Emotional register:** ownership. This is the moment the visitor thinks *that's mine.* Everything
should reinforce possession — their name in the headline, their business in the frame.

**Design notes:**
- Put their name in the H1, large.
- The frame navigation buttons (Home · About · Resources · Contact) sit directly above the frame and
  must look clickable at a glance. Most visitors will not otherwise discover the rest of the site is
  real, which is the single biggest wasted opportunity on the current screen.
- Right column stacking order matters: plan and offer first, then the morning-list card, then the
  primary action, then the email capture last. The email capture must be visually quieter than the
  main action — it is the fallback, and if it competes it will cannibalise signups.
- On mobile, the frame goes first and full-width; the sidebar cards stack beneath it.

**Where motion earns its place:** the frame's own page transitions when the visitor clicks the
navigation buttons. That's it — the product moving is the motion.

**What would be a mistake:** confetti, a "your site is ready!" celebration state, or any modal. Also:
do not scale the iframe down so far that the site inside looks like a toy. Better to show a
convincing slice at readable size than the whole page at 40%.

### `/pricing`

**What dominates:** three cards of equal visual weight, with Platinum lifted by one degree — the
brass top-rule already in the codebase does this well and should stay. Below them, the founding-offer
band should be the second-strongest object on the page.

**Emotional register:** relief again, this time about being told the truth. The design goal is *no
surprises,* which means the setup fee, the tax note and the commitments band must be visible without
hunting.

**Design notes:**
- The struck-through setup fee needs to read instantly as a saving. Old price in grey with a strike;
  the $0 in brass, bold, one size up.
- The commitments band (no contract / thirty days / your data exports / the price is the price) is
  four short items with a rule between them, in navy on white. It should feel like a signature, not a
  feature list.
- The comparison table must stay data-driven. Keep it collapsed by default; the summary cards carry
  the decision and the table is for the one buyer in ten who wants everything.

**What would be a mistake:** a pricing toggle with animated numbers. Also, any "Most Popular" badge —
there is no popularity data, so the badge would be a small lie. "Most complete" is factual and does
the same job.

### `/how-it-works`

**What dominates:** a vertical timeline with the days as anchors, and a real screenshot at three of
the six steps.

**Emotional register:** competence and calm. This page exists to make a switch feel small.

**Imagery required:**
- **Shot 5 — the domain status strip** showing "Secured" with the padlock state. This is the single
  most reassuring screenshot available, because it makes the scariest technical step look finished.
- **Shot 6 — the page editor mid-edit**, with a content block open and clearly ordinary form fields
  in it. It must look boring and obvious. That is the entire point.
- **Shot 7 — the client import screen** or a before/after of a spreadsheet becoming client records.

**Where motion earns its place:** nowhere. This page is read, not experienced.

### Vertical pages

**What dominates:** the headline, and a screenshot of *that vertical's* site and that vertical's
morning-list card. The recognition moment is the whole job of these pages, so the imagery must be
vertical-specific, not shared.

**Emotional register:** being understood. The visitor should feel the page was written about them,
not adapted for them.

**Imagery required:** three variants of Shot 1 and Shot 2 — one per vertical, using that vertical's
real starter content and that vertical's entry from the daily-insight catalogue (Jennifer Walsh for
insurance, David Park for mortgage, Robert Kim for accounting).

**What would be a mistake:** building one template and swapping the noun. If the accountants page and
the mortgage page differ only in the word "mortgage," neither will work.

### `/your-data` and `/faq`

**What dominates:** the text. These are documents, not pages.

**Emotional register:** plain-spoken and unhurried. Design for reading — a single narrow column,
generous line height, clear question hierarchy, no cards, no icons, no accordions that hide the
answers. This buyer will skim the questions and stop at the one that worries them; hiding answers
behind clicks costs the exact conversion these pages exist to win.

### `/about` and `/contact`

**What dominates:** the founder's photograph on About; the phone number and email on Contact.

**Emotional register:** a handshake.

**Imagery required:** one real, well-lit photograph of Bahman. Not a headshot against a grey studio
backdrop — at a desk, in an office, looking like a person you could phone. No stock, no illustration,
no logo standing in for a face.

**What would be a mistake:** a team grid of avatars for a company that is essentially one person. The
smallness is an asset when stated and a liability when disguised.

## Anti-pattern list, in full

For this audience, all of the following are mistakes regardless of how well executed:

Dark mode · scroll-jacking or parallax · carousels · auto-playing video with sound · hover-only
navigation · modal popups on entry or exit · countdown timers (the founding offer has a real date,
which is enough) · chat bubbles nobody is behind · "AI" glow effects, purple gradients, neural-network
motifs · abstract 3D renders · isometric illustrations · emoji in headings · body text below 16px ·
grey text on grey backgrounds · icon-only buttons · accordions hiding FAQ answers · progress bars
that don't measure anything · testimonial-shaped placeholders with no testimonials in them.

---

# J. Measurement

Eight numbers. Not a dashboard — a short list the owner can look at on a Monday, each of which points
at one specific thing to change.

| # | Number | What it tells you | What you'd change |
|---|---|---|---|
| 1 | **Preview starts ÷ home page visits** | Whether the hero promise lands at all. | Below ~15%: the headline or the button label is wrong. Test the button first — it's one word change and the biggest lever on the page. |
| 2 | **Preview completions ÷ preview starts** | Whether the form is too much. | Below ~70%: cut or reorder fields. This should be very high; if it isn't, something on that screen is scaring people. |
| 3 | **Registration starts ÷ preview completions** | The single most important number on the site. Whether seeing their own site actually makes people want it. | If it's low but time-on-page is high, the preview is impressing and not converting — the offer and the price card on that screen are the fix. If time-on-page is also low, the preview itself is underwhelming and the template or starter content needs work. |
| 4 | **Registrations completed ÷ registrations started** | Form abandonment, straightforwardly. | This is the number that proves or kills the two-step form recommendation. Measure it before the change so the comparison is real. |
| 5 | **Active subscriptions ÷ completed registrations** | The structural leak: people who register and never subscribe. | If this is well under 100%, the registration-success page is the problem, not the price. Rewrite it as a checkout continuation before touching anything else. |
| 6 | **Emails captured on the preview, and return rate from those emails** | Whether the not-ready path is worth running. | If capture is decent and returns are near zero, the sequence is wrong. If capture itself is low, the ask is in the wrong place or competing with the main button. |
| 7 | **Promotion code redemptions, by code** | Which offer actually moved anyone. | Run one code per channel and per message. This is free attribution the billing system already gives you, and it will settle arguments that opinion can't. |
| 8 | **Cancellations in the first 90 days, with the stated reason** | Whether the promise and the product match. | Any cancellation citing "too complicated" means onboarding failed, not the software. Any citing "not using it" means the daily list isn't reaching them — check whether they're on Silver, which doesn't have it. |

**One more, which is not a marketing number but will decide the setup fee.** Track **hours of your
own time per new customer in their first fortnight.** If a new account costs six hours of real work,
$400 is underpriced and the waiver is a deliberate, costed investment in the first twenty-five
customers. If it costs ninety minutes, the fee is a conversion tax and should be permanently folded
into the monthly price. You cannot decide the long-term answer to the biggest pricing question in
this document without that number, and nobody is collecting it today.

**What not to measure.** Page views, time on site as a headline number, bounce rate, social
followers, newsletter open rate as a goal in itself. None of them will change a decision, and
watching them costs attention that belongs on the eight above.

---

# K. Accuracy problems found while researching

Fix these before or alongside the site work. Several are data or code problems that copy alone
cannot solve, and two are actively costing money today.

1. **The home page claims SMS reminders and SMS is not built.** `Views/Home/Index.cshtml` line 125:
   "Calendar / scheduler + email & SMS reminders." Delete "& SMS". *Copy fix.*

2. **The feature comparison table also claims SMS, from the database.**
   `PackageEntitlementSeeder.cs` line 185 seeds `SmsReminder` — "Mobile SMS reminder" — as included
   on all four packages. The pricing section's "Compare all features" table renders straight from
   `PackageFeature` rows, so it shows a green checkmark for SMS on every plan. Removing the copy in
   item 1 does not fix this. *Data fix — required before any pricing page ships.*

3. **[RESOLVED 2026-08-13 — owner decided: keep `247advisers.com`, correct the marketing.]** The
   home page hero now renders `firstnamelastname.` + the `App:TemporarySiteRootDomain` config value,
   read from the same key `GenerateUniqueDomainAsync` builds against, so the advertised address is
   the issued address by construction. Copy in this document may now name a temporary address, using
   that form. Original finding follows.

   **New customers are given a `247advisers.com` address, not an `iproadvisers.com` one.**
   `AccountController.GenerateUniqueDomainAsync` (line 969) builds
   `firstnamelastname.247advisers.com`. The home page hero's browser frame shows
   `yourname.iproadvisers.com`, and the business brief states the same. Both cannot be true. This is
   a deliberate decision the owner needs to make, not a copy tweak: a company called IPRO Advisers
   handing new customers a 247advisers.com address undercuts the rebuild it is selling. Either change
   the generated subdomain or stop showing the other one in marketing. *Until it's decided, no copy
   in this document names a temporary address.*

4. **The business brief's Silver "domain cap" of 12 is wrong.** The real entitlement is **2**
   (`MultiDomainSupport`, `PackageEntitlementSeeder.cs` line 197). The 12 in the brief is
   `MaxNewsletters` from the package definition, mislabelled as a domain cap. The current home page's
   "2 domains" is correct. *Fix the brief so nobody writes 12 into a pricing page later.*

5. **Storage figures worth stating correctly:** Silver 50 MB, Gold 500 MB, Platinum 1,000 MB, Broker
   1,000 MB per user.

6. **Team logins on Silver are seeded as 1, not zero.** The brief describes Silver as "deliberately
   excluded," which is true in spirit — one login means just you — but copy should say "your
   assistant gets their own login on Gold and above" rather than implying Silver has none.

7. **The preview does not collect a city.** The brief describes the preview as taking name, business
   type, city and package; the form takes first name, last name, company and business type. No copy
   should promise a city-localised preview.

8. **The home page's pricing cards send buyers to registration with no plan selected.**
   `Views/Home/Index.cshtml` line 253 links to `/Account/Register` with no query string, while the
   preview link immediately below it does carry `?package=`. The `Register` GET action already
   accepts a `package` parameter and will preselect the plan. A visitor who clicks "Get Started" on
   the Platinum card arrives at a form with an empty package dropdown and has to choose again — after
   they'd already chosen. One-line fix, real conversion cost. *Code fix.*

9. **`/Preview` is `noindex,nofollow`.** Correct for `/Preview/Show`, which is personalised. Wrong
   for `/Preview`, which is a legitimate landing page for links and referrals. *Remove from Index
   only.*

10. **Registration does not create a subscription, and the success page buries that.** Structurally
    the largest leak in the funnel. See Section D step 5 and Section F14.

11. **The legacy site at `iproadvisers.com` sends buyers to the legacy product.** Its signup link
    points at `247advisers.com/pub/register.aspx?BT=3`. Anyone who searches the company name and
    decides to buy is being routed into the old system. See Section E — this needs an owner decision
    before any traffic is driven anywhere.

12. **The Azure region has not been verified for this document.** The `/your-data` page must state
    where data lives, and the 2014 site claimed Toronto servers. Confirm the actual region for both
    apps and the database before publishing any location claim.

---

# L. What I took from the 2014 copy, and what I left

The company paid for thirteen documents of professional marketing copy in 2014. I read all of it. The
observations about these three buyers are genuinely good and still true; the register is dated agency
hype and the feature claims have drifted from the product. Here is the ledger, so it's clear the old
investment was read rather than ignored.

## Carried forward

| From 2014 | Where it went |
|---|---|
| **"Monthly Billing. No Contract. Cancel Anytime."** (Plans and Pricing) | Revived close to verbatim as a commitment on the pricing page and in the FAQ. Still true, still strong, and stated nowhere on the site today. |
| **The 30-day money-back guarantee** (Why Us) | Revived as a commitment on the pricing page, `/your-data` and the FAQ. It is the correct counterweight to the setup fee. |
| **The referral offer — a free month for both sides** (Support FAQ) | Recommended in Section D as a manual programme, run through named promo codes, announced in the welcome email rather than on the public site. |
| **"We Provide You Everything You Need With Just One Simple Login."** (Why Us) | This is the position. I arrived at the same place independently before reading it, which is worth the owner knowing: the right answer was already in his 2014 copy, buried under decoration. It is now the home page H1 — "Everything your practice runs on, in one login." |
| **The insurance observation** — that these professionals' schedules make website work impossible to fit in | The core of `/for/insurance-advisers`, rewritten as "You've built a book over twenty years. Nothing is watching it for you." |
| **The mortgage observation** — that rates are a commodity war, so client retention is the only durable edge | The core of `/for/mortgage-brokers`, rewritten as the renewal-goes-to-the-bank argument. |
| **"Industry Specific Content — no need to hire a content writer"** | Still the strongest single argument for the website pillar, and more true now than in 2014 because the seeded content is real and countable. |
| **The Why Us diagnostic questions** ("Do you already have a website but can't attract traffic? Don't have time to add content? Don't have the technical know-how?") | Good structure. Reused as the framing for Section G's objection handling — meeting the buyer at the problem they'd name themselves. |
| **"Will You Help Me Transfer My Web Site?"** (Pre-Sales FAQ) | Revived as the "keep your domain, point it when you're ready" answer to "I already have a website." |
| **The pre-sales FAQ format** — answering blunt infrastructure questions plainly | Reused as `/your-data` and the technical half of `/faq`. The instinct was right: this buyer's adjacent technical adviser asks these questions. |
| **"Don't take our word for it — see for yourself"** (Taglines) | The whole preview-led strategy is this line taken seriously. |

## Deliberately dropped

**Unsupported claims** — "Top financial gurus of the country trust us"; "the leading choice of
mortgage professionals... all across Canada"; "ranked today as one of the best resources"; "almost
80% of all consumers do their research online" (unsourced, 2014). Every one of these is exactly what
the truth constraint exists to prevent.

**"A subsidiary of the world renowned Global Business Solutions"** — unverifiable and puffed. If the
corporate relationship still exists, state it plainly without the superlative.

**The entire register** — "tech gurus", "tech whiz experts", "make a bang on the social scene", "woo
over your customers", "Wow your competition", "conquer the web world", "power packed", "par
excellence", "in a brand new and catchy avatar". None of it survives contact with a fifty-year-old
adviser in Barrie.

**Feature claims that no longer match the product:**
- "over 30 attractive templates" — there are **3**
- "over 40 pre-defined e-cards" — there are **14**
- "over 25 calculators" — not verified against the current build; use only a number that can be counted
- "25 to 45 populated pages" of content — the current seeded set is specific and countable; use the real figures
- **Domain-associated email accounts** (`info@SmithInsurance.com`) — not a current feature
- **Online quote system integration / comparative quotes** — not a current feature
- **"notified via email and/or SMS"** in the calendar description — the same false claim that is
  still live on the home page today, twelve years later
- **"Unlimited bandwidth"**, **"daily backups"**, **"Dell servers with Quad-Core Intel Xeon, RAID 5"**
  — hosting-era claims that no longer describe anything; the product runs on Azure
- **"Toronto servers"** — plausibly still true in spirit, but must be re-verified, not inherited

**Support promises the company cannot keep at its current size** — "24/7 support", "round the clock
support", "support tickets 24/7", "personalized support 24/7", and the live chat widget with staffed
hours. A one-person operation promising 24/7 loses more trust on the first unanswered evening than it
ever gained. Replaced throughout with real hours, a real name, and a real reply commitment.

**The Wikipedia-sourced CRM definition** on the 2014 home page, which was reproduced with its source
line intact. Not a copy problem so much as a sign of a page written to fill space.

**"iPro Accountants" as a separate brand** — the 2014 material runs it as its own identity with its
own meta titles. Consolidate under IPRO Advisers with `/for/accountants` as the vertical page. One
brand, three doors.

**The portfolio section** — "Our Work Speaks For Itself", "Our Portfolio is our Identity". See
Section H: of the eleven sites listed, seven are dead, one is parked, and the three surviving ones do
not appear to run on IPRO infrastructure. It cannot be republished. The named-reference programme in
Section H is the honest replacement.

---

*End of document.*
