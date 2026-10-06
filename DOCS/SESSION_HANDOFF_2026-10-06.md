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
  `/gallery`, `/about-us` -- died the moment the short address moved. The owner: "build it" -> **553**, built and
  gated 2026-10-06, held for his word.
- **Found on the bakery's new site, theirs to clean:** 22 published starter pages written for advisers (most twice,
  the second ending in `-2`), not in the menu but in the sitemap under the bakery's name. Three other sites checked
  show no doubled pages, so it looks specific to this site; the cause was not looked into.

## Built today (held)

| Code | Item | Gate |
|---|---|---|

## After 553 is out: the bakery's short address (his steps, in this order)

1. GoDaddy -> lavenuebakery.com -> **Forwarding** off. Then **DNS**: the A record for `@` -> the address shown in
   step 2 of the setup steps on My Website (it must be the only A record for `@`), and a TXT record named `asuid`
   with the code shown there. Leave the mail rows and the `www` row alone.
2. The bakery's My Website -> **Check now**. The Short address row reads Connecting, Securing, Connected (a few
   minutes). From outside: `curl -sI https://lavenuebakery.com/drinks-and-meals` answers 301 to the www address.
3. The bakery's About page -> Page Settings -> **Old addresses**: `about-us`. (`/bread-%26-pastries` finds
   `/bread-pastries` by itself; `/drinks-and-meals` and `/gallery` are the same address on both sites.)
4. lavenuebakery.ca never had a website: its forwarding can stay.
5. My Website -> **Shown in your emails as**: `www.LAvenueBakery.com` (552). The Google Business Profile and
   Instagram links can keep the short address once step 2 is done.

Related: `DOCS/TODO.md` 553; `DOCS/05_DOMAINS_AND_LEADS.md` ("Point the short address straight at IPRO", "Moving an
existing website here"); `DOCS/SESSION_HANDOFF_2026-10-05.md` (yesterday's close-out and list).
