# Session handoff — 2026-09-21 (launch day)

## What happened

- **Launch morning, 8:35 a.m.:** both hosts healthy on `01b9062`; all four public names and the
  mortgage pair answer from the new site over ordinary public DNS with full certificate checking; both
  bare names resolve to `40.89.19.0` and both MX records to `mail.<domain>` on Google's resolver. The
  owner reported the 7 a.m. follow-ups emails himself: "13 follow-ups" for MichaelTran and "4 overdue"
  for his own account, the same counts as the day before.
- **507, asked the evening before:** typing `www.iproadvisers.com` turned into `app.iproadvisers.com`
  and "it should stay as www.iproadvisers.com". His answers in the morning: the bare name stays too;
  `www.iproadvisers.com` is the address Google should treat as the main one. Built as `name=/` in
  `App:AliasHosts` (TODO 507). The forwards had no cache lifetime, so browsers that visited since
  Sunday afternoon may keep landing on app. for a while: check in a private window.
- **507 live:** the setting rewritten at 9:35 a.m. on the owner's go (the advisers pair as `www.iproadvisers.com=/,iproadvisers.com=/`; the app back within a minute); proved from outside with full certificate checking: both names 200 with the home page, canonical and og:url `https://www.iproadvisers.com/` (also when the page is opened at app.), its 8 files 200, deep paths 301 to the platform with `Cache-Control: public, max-age=3600`, the accountants and mortgage names unchanged, the accountants page's links to the home page's sections now at www.iproadvisers.com.
- **Promotion codes (TODO 508):** the owner will send codes to prospects and wants "one month free or
  more". A code's cycle is whatever billing period the customer picks, so "100% off, 1 cycle" was a
  free YEAR on annual billing. Built the same morning: a code can be limited to monthly or to annual
  billing (its own table; enforced at checkout, said on the registration page, **Applies to** on
  SuperAdmin's form). A "months free" code is: Recurring 100% off, Duration = the months, one package
  per code, Applies to = Monthly billing only, plus Expires and Max Redemptions (both already existed).
  **Before he sends any:** he opens SuperAdmin -> Promotion Codes once (it reads the new table, which
  proves the table exists in production), then one live free-month sign-up together -- a $0 first
  cycle has never run on live PayPal: watch activation, the $0 invoice, the hourly reconcile moving
  the next billing date, and the Billing page's next charge (503 counts cycles by dates for this case).
- **The owner's independent review of the public names (TODO 509):** tested at 9:10 a.m., before 507's
  setting change, so part of it was already stale. Checked claim by claim against the live site and
  the code and answered as his adviser: most true, none a launch blocker, all cheap; fixed the same
  day -- each page known by its own brand address, old `.html`/`.php` addresses forwarded to the
  name's front page, `robots.txt` and `sitemap.xml`, 1200x627 share cards, HEAD for the public
  addresses only, `/mortgages` forwarding. His own steps afterwards: Search Console and Bing
  (verify, submit the sitemaps, request indexing) and LinkedIn's Post Inspector before he posts.
- **The free-month test, first half:** his test code was refused because its Expires date was today --
  the date is read as the first moment of that day (UTC). He moved it a day and it was accepted; he
  said to leave the behaviour (TODO 504, item 13: set Expires one day after the last day wanted).
  That the form saved and re-opened with "Monthly billing only" proved the new table exists in
  production.
- **The free-month test, second half: it works on live PayPal.** Subscription `I-1P5H6REKEL35` on the
  plan "IPro Gold Monthly - Promo": set-up fee $0.00, trial period 1 of 1 at $0.00 in progress, then
  $67.80 CAD every month (13% included), custom id 56; PayPal's activity row reads "Created - US$0.00".
  Our side: the account active, invoice IPRO-2026-000028 Paid at $0.00, the welcome email sent, and not
  one warning in the container log for the whole window. PayPal sent him no email: nothing was
  charged, so there is no receipt, and its set-up notice goes to the PAYER's PayPal address. Seen on
  the invoice: the line reads "IPro Gold subscription adjustment" (polish item 8, now the first
  document every free-month customer sees; offered to the owner, his call). He cleans up the test
  account himself (cancel, then delete with the financial tick).
- **The owner's rule for brand domains in anything a person reads:** `www.iProAdvisers.com`,
  `www.iProAccountants.com`, `www.iProMortgages.com` -- the capitals separate the words and the `www.`
  stays (TODO 504, item 14, lists the three places that do not follow it yet). The share cards were
  redone with the capitals before 509 shipped -- and once more right after it, because that redo had
  dropped the `www.`, which he had not meant.
- **510, the afternoon batch:** Search Console refused 509's shared sitemap ("URL not allowed" for the
  other two domains) -- my design, wrong in practice; each site's sitemap now lists its own pages only.
  With it, at the owner's word: the first invoice says what a promotion code did and names it (no more
  "subscription adjustment" on a free month), and the three places that wrote the brand domain
  without its capitals. Asked and still open: whether displayed email addresses follow the capitals.
- **511, the same rule for email addresses:** asked in 510's report, answered "yes". Shown with the
  capitals (`billing@iProAdvisers.com` on the invoice and its email, `support@` on the home page,
  the legal pages' addresses), through one helper, `BrandText.WithCapitals`; the addresses mail is
  SENT from and every `mailto:` keep the configured value, and a test pins that.
- **The free-month test account is gone** (the owner's click, 2:35 p.m.): Freetest deleted with the
  financial records, 107 rows across 16 tables, the PayPal subscription cancelled by the delete; PayPal
  told the payer so by email ("canceled your automatic payments": CA$67.80 from 21 October, trial
  CA$0.00 from 21 September). Invoice number 000028 is therefore a gap, like 000027. Two accounts
  remain, both the owner's.
- **510's first full gate failed (1092 of 1093) and nothing was deployed from it:** a source pin on
  the first invoice's call site, in a suite I had not run. Fixed by leaving the pinned line alone;
  the second gate passed. Since then the tests are searched for every rewritten line BEFORE a gate.

## Pushed today

| Code | Item | Gate |
|---|---|---|
| `05b2dbd` | **507** the home page under `www.iproadvisers.com` and the bare name; forwards carry a cache lifetime | 1014/1014 |
| `bdd4cf1` | **508** a promotion code can be limited to monthly or to annual billing | 1027/1027 |
| `35da4fe` | **509** the public front door after the owner's independent review | 1074/1074 |
| `52f4abf` | **510** a sitemap per site; promotion wording on the first invoice; the brand domain's capitals | 1093/1093 |
| `f0cfea8` | **511** brand email addresses shown with the capitals, sent from as configured | 1111/1111 |

## Close-out 2026-09-21

Final build on both hosts before this close-out: `9e0004a`. Tree clean and pushed; nothing half-built and
nothing waiting for a deploy. Launch day: five items shipped (507-511), each red first, each behind a
full gate (1014, 1027, 1074, 1093 after one failed run, 1111), each verified on both hosts.

**State left:** PayPal live; all four public names and the mortgage pair on the new site with
self-renewing certificates; `App__AliasHosts` writes the advisers pair as `name=/`; two accounts in
the database, both the owner's; FREETEST used up (1 of 1) and its test account erased; the owner was
in Search Console adding `ipromortgages.com` and `iproaccountants.com` as their own properties (a TXT
record each) and resubmitting the advisers sitemap -- ask how that went.

**Drafted for TODO 512, for the owner and his lawyer -- NOT in the policy yet.** A new short section in
the Privacy Policy, after "From visitors to your public website":

> **From visitors to our own website.** On our own public pages -- iProAdvisers.com,
> iProAccountants.com, iProMortgages.com and the registration page -- we count page views the same
> way: the page visited, the referring site, any campaign tag in the link that brought the visitor
> (for example from a social media post), and the date. Visitors are counted with a one-way hashed
> identifier; the count keeps no raw address and sets no cookie. If a visitor goes on to open an
> account, we note which page or campaign they came from.

The last sentence is the one that matters legally: it ties an origin to an identified account.

Backups of the pushed HEAD: `IPRO_Modern_backup_<stamp>.zip` in `C:\Users\admin\OneDrive\Codex_Code_Bkup`
and `C:\Users\admin\Documents\IPRO_Backups`. Build servers shut down; no local app, emulator or test
process running; MySQL is the Windows service and needs nothing. Reboot-ready.

## Do this first tomorrow

1. Both health endpoints, the Job Scheduler, Email Activity, PayPal's webhook event log for any real
   sign-up (each event **Success**).
1a. **Build TODO 512**, the SuperAdmin Visitors report the owner approved on launch day: start with the
   Privacy Policy sentence for him (and his lawyer), then the recorder for the platform's public pages
   with `utm_` tags, the report, and sign-ups by origin. If he asks for launch-day numbers before it
   exists, pull them from the web app's raw HTTP logs (3 days kept), in aggregate only.
2. **Calendar:** TODO 505 or the hand renewal before 5 October (certificates expire 19 October); clear
   `Email__TrackingSigningKeyPrevious` around 17 October; Microsoft quota mid-October; .NET 10 in October.
3. **Open:** TODO 504 (the polish list), 506 (an adviser's domain with CAA records); the truth sweep's
   open items; Google Calendar sync reads a follow-up's date as UTC; a site-language option, French
   first; SOC 2 after launch; whether the vertical pages, Terms and Privacy should also live under
   `www.iproadvisers.com` (507 moved the home page only).

Related: `DOCS/TODO.md` 507-511; `DOCS/SESSION_HANDOFF_2026-09-20.md`; `DOCS/DOMAIN_SWITCH_RUNBOOK.md`.
