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

Related: `DOCS/TODO.md` 553; `DOCS/05_DOMAINS_AND_LEADS.md` ("Point the short address straight at IPRO", "Moving an
existing website here"); `DOCS/SESSION_HANDOFF_2026-10-05.md` (yesterday's close-out and list).
