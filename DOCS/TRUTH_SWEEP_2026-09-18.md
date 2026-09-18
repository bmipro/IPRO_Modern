# Truth sweep of the public pages — 2026-09-18 (three days to launch)

The owner found the first gap himself that morning: the home page sold a "Generic edition" that no
form offered (TODO 494/495). He then asked what else was left. Two read-only reviewers checked every
concrete promise a prospect sees BEFORE paying — the home page, both landing pages and their footer,
the pricing table against the real entitlements, the 30-second preview, sign-up and the welcome
email — against the code. Everything serious was re-verified by hand, in the code and on the live
site, before it was acted on. This is the record: what was found, what 496 changed the same day, and
what is still open. It closes the "Phase 3 truth sweep" line of `DOCS/AUDIT_RECONCILIATION_2026-09-17.md`
for the public surfaces; the in-portal help was not part of it.

## Found, verified, and fixed by 496

| Finding | What was true | What 496 did |
|---|---|---|
| "Your site is written, live and public from day one"; "02 Your site is live"; preview: "This becomes real the moment you sign up"; welcome email: "you can use this temporary domain right away" with the button "Open Your Temporary Website" | Nothing at sign-up or at payment created the website: only the Publish button under My Website did. The email's main button opened a 404 for every new customer | **Built:** `WebsiteProvisioning.EnsurePublishedAsync` (the code Publish always ran) is now also called at sign-up, before the welcome email, inside its own scope and a try/catch so a failure can never cost a sign-up. Safe before payment: the public site is gated on billing being active. The email and the preview now say "live the moment your subscription is active" |
| Setup-fee line on the /accountants and /mortgage price cards | The partial carried its own copy of the waiver test, inverted for a lapsed waiver. With "waived until September 30" it would have shown NO fee from 1 October while the home page showed it and PayPal charged it | One line: `package.IsSetupFeeWaivedOn(now)`, the one rule BillingRule says never to re-implement |
| Plan table row "Email reminder", ticked on all four plans, line four of every price card | Its only job was removed 2026-09-09; nothing reads the code | **Withdrawn** (RetiredFeatureCodes; the rows are deleted at start-up). A real daily "follow-ups due" email would bring it back: open, the owner's call |
| Plan table row "Support and training": Limited / Unlimited | "Limited" was defined nowhere; the support path is identical on every plan | Renamed "Support by phone, email and portal tickets", a plain tick on every plan (`RepairSupportRowAsync` clears the labels on existing rows) |
| Plan table row "PayPal integration" (Platinum) | What exists is the Payment Link on the profile, shown as a Pay Now button on client invoices | Renamed "Pay Now link on client invoices" |
| LIVE: the preview's plan card said "Expanded package with marketing, banners, coupons, and mail tools" (Gold) and "Premium package with managed content, SEO, and PayPal tools" (Platinum) | Banners, coupons, mail merge and managed SEO were withdrawn on 2026-08-28; the seeder only ever fills a BLANK description | New descriptions, and a start-up repair that replaces only the exact old seeded text (a description the owner wrote is never touched) |
| "One provider. One login. One number to call." (home and both landing pages) | No phone number appeared on any public page | **Owner's decision: publish it.** 1-416-363-2220 (`PlatformContact`) now appears in the home page's trust section and footer, both landing pages' trust band and footer, and the welcome email. The line is true |
| Hero badge "No credit card" beside "Cancel anytime" | True of the free preview; false as a sign-up term (trials are invitation-only; sign-up goes to PayPal) | "Free preview, no card needed" (the owner's choice) |
| "The website, starter content and terminology change to match your market" | Nothing in the portal changes by business type | "The website, its starter content and the meeting form are written for your market." The terminology build itself is open (below) |
| Insurance edition: "Policy review, renewal and life-event reminders" | Life-event reminders are Platinum-only and yearly; nothing is emailed | "Review and renewal follow-ups; yearly reminders on Platinum" |
| "Insurance and planning pages with an article library"; "Mortgage-specific service pages" | Six shared-shape pages each; two articles of their own plus two shared | Bullets now name what ships (starter pages, articles, calculators; a pre-approval form). The libraries themselves are open (below) |
| "creates a prospect record"; "Borrower enquiries added as prospects" | There is no prospect status; the record is a client | "a record in your client list" |
| Landing pages: "A professional website, client portal, ..."; Mortgage adds "document workflow"; "Clients upload slips and documents..." | The client portal is Platinum and Broker only; there is no document workflow | The portal is promised "on Platinum"; "document workflow" is gone |
| Landing pages: newsletters "in your own name" | Mail leaves from IPRO's sender; the adviser is the reply-to (480, parked) | "Replies come straight to you." Did You Know teasers are shown on the site, not sent |
| Landing pages: "Try the product before you register" | The preview is a read-only starter site | "See your site before you register" |
| "The Canadian calculators are in place: mortgage payment, ... retirement and savings" | The list was the union of what the four editions get | Says which edition gets which, and that any of the fourteen can be added to any page |
| "Hosting, domain and SSL are ours to worry about" | The adviser buys the domain and points it once | "Hosting and SSL are ours... You point your domain at us once" |
| "export them whenever you want" | True while the account is open; a lapsed account reaches only Billing and Profile | "any time your account is open". The small build that would make the original true is open (below) |
| "(two months free)" hard-coded beside the annual price (home, sign-up) | True on today's prices only | Shown only when the annual price really is ten times the monthly |
| The preview showed the AI Daily Assistant card under every plan, including "IPro Silver — the plan this preview is showing" | The assistant is a Platinum feature | For a plan without it the card says so and names the plan that has it |
| The preview says "Nothing is saved" while its GET form puts the visitor's name and company in the URL | App Insights stores URLs | `firstName`, `lastName` and `companyName` are scrubbed from telemetry |
| Welcome email: "video tutorials in each admin section" | There are none; help is text guides | "Each section of your portal has a help guide" plus the phone number |
| Terms §7 named SendGrid for email delivery | Mail has gone through Microsoft since 2026-08-30 | "Microsoft for email delivery" (owner: no counsel step needed for a provider name); both copies dated 18 September 2026 |

Also corrected: `DOCS/VERTICAL_PAGE_BRIEF_ACCOUNTANTS.md`, the designer's brief, which was the source of four of these (portal without the plan, "sent from your own name", "one number to call" with no number, "your site is live"). The /insurance package would have repeated them.

## Checked and true (so the owner knows what was covered)

Prices, setup fees and the waiver render from the database everywhere (apart from the one partial
above); no hard-coded price or limit in any public view. Of the 38 rows of the comparison table, 22
have an enforcing check that matches, 12 are all-plan features that exist, one is the SMS row already
accepted, and three were not backed (above). "Canadian dollars before applicable tax": charged in
CAD, tax by province at checkout. "Your card details go to PayPal, never to us": there is no card
field anywhere. Trials are invitation-only and no public page says "free trial". "Cancel anytime":
self-service, PayPal cancelled first, access to the paid-through date, exactly as the Terms and
DOCS/22 say. "Hosting and SSL included": custom domains with self-renewing certificates on every
plan. "Help from real people": tickets and email on every plan, no bot, hours in the Terms. One
system, one login, one bill; forms create the client record at once; the meeting form is per market;
import and export on every plan; nightly backups with 30-day retention; data in Canada.

## Still open after 496 (the owner's calls, in rough priority)

1. **A real "Email reminder".** A daily "follow-ups due today" email to the adviser (about a day:
   a job, a template, an opt-out on the profile, tests) would restore the withdrawn row.
2. **Insurance wording inside the portal for every business type.** Placeholders ("Call about policy
   renewal", "life insurance prospects", "John Smith Insurance Adviser") and the four AI prompts
   ("financial/insurance advisor") are the same for an accountant, a mortgage broker and a Generic
   business. About half a day, keyed on business type, with tests.
3. **An accountant's Resources menu shows "Calculators" twice** (live, verified): an article category
   of that name and the real calculators. A small fix in the provisioning helper and its preview mirror.
4. **The preview's template is hard-coded per edition; a real account gets the database default.** An
   accountant previews Classic Sidebar and may receive Modern Professional. Either set per-business-type
   defaults in SuperAdmin -> Website Templates, or let the real path fall back to the preview's mapping.
5. **Article libraries for Insurance / Financial and Mortgage** (four articles each today, against
   Accountants' twenty-one and Generic's ten). The 495 method, with the owner's read: regulated subjects.
6. **Three stale lines in the Accountants library**: a US term ("sales and use tax returns"), two
   federal credits that ended in 2017, and a pointer to a calculator an accountant's site does not get.
7. **Exports for a lapsed account.** The Terms give 30 days to "reactivate or export"; today export
   needs the reactivation. Exempting the three CSV exports from the gate is a small build.
8. **The setup fee on /Billing's plan cards**, and one sentence in the Terms that the fee applies to
   each new subscription, including a re-subscribe after cancelling.
9. **Owner's facts, not checkable in code:** "Canadian since 2014", "Built and supported in Canada",
   the "PayPal Verified" seal on the sign-up page, and whether the training mailbox and sessions in
   the welcome email are offered.
10. Presentational: the hero screenshot is the Platinum dashboard (caption it); the preview's two
    testimonials are invented (label them samples).
