# Session handoff -- 2026-10-06

## What happened

- **L'Avenue Boulangerie's two domains were connected this morning** (the owner, at GoDaddy; every step read back
  from outside). `www.lavenuebakery.com`: the existing `www` CNAME edited at 9:53 a.m., connected 9:55, certificate
  attached 10:00. `www.lavenuebakery.ca`: connected 10:00, certificate 10:06. The bakery's mail (Microsoft 365: MX,
  both TXT rows, autodiscover and the rest) read back unchanged on both GoDaddy nameservers after every edit. Both
  short addresses are on GoDaddy's forwarding (the .com one to `http://`, one hop more than `https://`).
- **What forwarding cost:** the bakery's old site lived on the short address (no www), and GoDaddy's forwarder
  answers 404 for everything but the home address (measured on three domains already on the platform:
  `4ipro.com/about` is a 404 too). Its four old page addresses -- `/bread-%26-pastries`, `/drinks-and-meals`,
  `/gallery`, `/about-us` -- died the moment the short address moved. The owner: "build it" -> **553**,
  deployed 2026-10-06 (build `23559d6`).
- **Found on the bakery's new site, theirs to clean:** 22 published starter pages written for advisers (most twice,
  the second ending in `-2`), not in the menu but in the sitemap under the bakery's name. Three other sites checked
  show no doubled pages, so it looks specific to this site; the cause was not looked into.

## Pushed today

| Code | Item | Gate |
|---|---|---|
| `cc65366` | **553** a short address pointed straight at the platform, and old addresses | 1532/1532 |

## After 553 is out: the bakery's short address (his steps, in this order)

**Put off to 2026-10-07 (his call, 8:20 p.m.):** signing in to the bakery's GoDaddy account needs a code texted to
the bakery's owner, who was not available. Nothing is half-done: 553 is live (both hosts on `23559d6` at 8:05 p.m.,
the web log clean after two runs of the domain job), the bakery's `www` old addresses already redirect, and the
short address is still on GoDaddy's forwarding (home works, inner pages 404) until step 1. A quiet hour is best: for
up to the old record's hour some visitors on the short address get an error while the change spreads. The watch from
outside is the scratchpad's `watch_short_553.sh` (`once`, `dns`, `live`): the zone's own nameservers and the
platform's address, read-only. This is the first real run of the Azure side of 553.

1. GoDaddy -> lavenuebakery.com -> **DNS**: first a TXT record named `asuid` with the code shown in step 2 of the
   setup steps on My Website (harmless by itself). Then **Forwarding** off, and the A record for `@` -> the address
   shown there (it must be the only A record for `@`; edit a "Parked" row rather than adding a second). Leave the
   mail rows and the `www` row alone.
2. The bakery's My Website -> **Check now**. The Short address row reads Connecting, Securing, Connected (a few
   minutes). From outside: `curl -sI https://lavenuebakery.com/drinks-and-meals` answers 301 to the www address.
3. The bakery's About page -> Page Settings -> **Old addresses**: `about-us`. (`/bread-%26-pastries` finds
   `/bread-pastries` by itself; `/drinks-and-meals` and `/gallery` are the same address on both sites.)
4. lavenuebakery.ca never had a website: its forwarding can stay.
5. My Website -> **Shown in your emails as**: `www.LAvenueBakery.com` (552). The Google Business Profile and
   Instagram links can keep the short address once step 2 is done.

## Close-out 2026-10-06

- **Code today:** 553 (`cc65366`), live on build `23559d6` at 8:05 p.m., gated in full first (1532/1532) and
  verified at /health/version on both hosts with live checks: on the bakery's own site `/bread-%26-pastries` now
  answers 301 to `/bread-pastries` and `/index.html` to the home page; the platform host's answers are unchanged; the
  web log was clean after two runs of the domain job on the new build (the start-up added the four columns). This
  close-out's docs push follows (553's check-mark, the postponement note and this section); its SHA is what both
  hosts show after it.
- **Outside the code:** the bakery's two domains were connected in the morning with every step read back from
  outside. The gate was started three times: two restarts for cases found while it ran (Azure refusing a certificate
  order while its DNS check still sees the old record, which `ops/domain-switch/cert-order.sh` already recorded; and
  what old-style page addresses reaching the public site would cost under scanner traffic). The redirect and the old
  addresses were seen on the local app over HTTP, before and after the gate; the setup card, the status rows and the
  editor's field as static markup at desktop and phone width. No signed-in page was seen (no password is ever
  typed), and nothing has yet gone through the Azure side for real.
- **His, still to do:** the bakery's short address (the section above), when its owner can text the GoDaddy code;
  the About page's old address; the bakery's 22 starter pages; from yesterday, Refer a Friend is still OFF and
  "Shown in your emails as" is still to fill in for www.4iPro.com.
- **Local machine:** the build servers shut down; the local app stopped after each look and its test rows removed
  (the local database has the four 553 columns, added by its own start-up); the blob emulator stopped; MySQL is the
  IPROLocalMySQL service, nothing to do.

## Do this first tomorrow

1. **The bakery's short address, with him at GoDaddy** (the section above; the TXT row first). It is the first real
   run of 553's Azure side, so watch every stage from outside: `watch_short_553.sh once` before he starts, `dns`
   while he saves (it checks both values on both nameservers), `live` after **Check now**. Expect Connecting, then
   Securing -- possibly for up to the old record's hour, while Azure's own DNS check still sees GoDaddy's forwarder
   (`its certificate order was not accepted yet` in the web log, asked again every five minutes) -- then Connected,
   and `https://lavenuebakery.com/drinks-and-meals` answering 301 to the www address. If the row reads Needs
   attention, its sentence is the diagnosis; the raw Azure answer is in the web log at Warning (`Short address ...
   could not be bound`). The way back, if it has to be taken: Forwarding on again restores today's state within the
   hour. Then the About page's **Old addresses**: `about-us`.
2. **His first look at what only a sign-in shows, 553:** step 2 of the setup steps with the two records, the Short
   address row and its sentence, **Old addresses** under Page Settings, and SuperAdmin -> Domains (the Root line).
   And still from 10-05: the "Shown in your emails as" box and an e-card preview (552), SuperAdmin -> Referrals and
   Refunds -> Referral rewards, the Refer a Friend page and cards once the program is on, the credit note. Fix
   whatever he finds.
3. **The bakery's site:** the 22 published starter pages for advisers in its sitemap (theirs to unpublish or
   delete; why most are doubled was not looked into). Its menu pages are built -- item 5 of the 10-05 list is done.
4. **After the bakery:** his own 4ipro.com is on forwarding too (`4ipro.com/about` is a 404 at GoDaddy); the same
   two records fix it, at his convenience. Not built, and worth asking him about: a copy button beside the two
   values, and a warning when the address in `App:WebsiteAddress` stops being what the CNAME target resolves to
   (today only DOCS/05 says to check before a plan change).
5. **Carried from 10-05:** Refer a Friend once he switches it on (follow the first referral through; the
   automatic-refund switch is still to build); the first real sign-up after 551 (two emails at
   support@iproadvisers.com); SES 8.1's first week, **8.2 about 10-11** (`Email__Ses__Streams` = `notify,news`, his
   change), then 8.3's alarms; a card and a letter on his phone after 548; the few supplied e-card pictures still
   off; 539's two images and the stock-image licences; the stale sections of `DOCS/08_PUBLIC_REGISTRATION.md`; the
   calendar as in `DOCS/SESSION_HANDOFF_2026-10-02.md`, items 7 and 8 (TrackingSigningKeyPrevious about 17 Oct, ACS
   closed to new customers 23 Oct, the renewal 25 Oct, the Platinum waiver 30 Oct, .NET 10).

Related: `DOCS/TODO.md` 553; `DOCS/05_DOMAINS_AND_LEADS.md` ("Point the short address straight at IPRO", "Moving an
existing website here"); `DOCS/SESSION_HANDOFF_2026-10-05.md` (yesterday's close-out and list).
