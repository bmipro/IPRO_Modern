# Moving an existing public name onto the platform -- the runbook

Written 2026-09-20, the evening the four old public names (`www.iproadvisers.com`, `iproadvisers.com`,
`www.iproaccountants.com`, `iproaccountants.com`) moved from the legacy web host to `ipro-prod-web`.
It is the procedure **as it was actually run**, with the three things that went wrong folded into the
order so that the next run does not meet them. Use it for any name that already has a life: an old
site, mailboxes, a DNS zone somebody else set up. For a brand-new name with no mail the 12 September
rehearsal (`DOCS/14_BACKUP_AND_RELEASE_CHECKLIST.md`, "Launch-day domain switch") is enough.

**Who does what.** Every DNS-panel edit is the OWNER's (cPanel Zone Editor at the legacy host for these
two zones). Every Azure change is run by the assistant ONLY on the owner's explicit go, each time
(binding names, the app setting and its restart, certificate orders and bindings). Reading is free.
No deploys while names are moving.

**What the app does with a name** is decided by `App__AliasHosts` on `ipro-prod-web`
(`PlatformAliasHosts`, the first middleware): a name WITH a landing path (`iproaccountants.com=/accountants`)
serves that page under its own name and sends every other path to the platform with the path kept;
`name=/` does the same with the HOME page (507, 2026-09-21: the `iproadvisers.com` pair, and the first
such name is the address the home page tells search engines); a bare `name` with no `=` only forwards,
everything, with a 301 to the platform. Forwards carry `Cache-Control: public, max-age=3600` since 507:
on the switch day they had no lifetime, and a browser may keep such a 301 indefinitely.

Since 509 (2026-09-21) a public name also: tells search engines its OWN address for the page it serves
(canonical, og:url and the share image name `https://www.iproaccountants.com/`, not the platform's
`/accountants`); sends an old-style page address left over from the legacy site (`/something.html`,
`/index.php`) to its own front page instead of a 404; answers `/robots.txt` and `/sitemap.xml`; and
answers HEAD on its front page. A new name gets all of this from its `App__AliasHosts` entry alone.
After a switch, the owner verifies the domain in Google Search Console and Bing Webmaster Tools and
submits `https://<name>/sitemap.xml`.

The three scripts are in `ops/domain-switch/` (Git Bash; the az CLI signed in; read the header of each):
`dns-check.sh` (read-only), `cert-order.sh` and `cert-wait.sh` (both change production: owner's go).

---

## 0. Read first, change nothing (10 minutes)

```bash
ops/domain-switch/dns-check.sh iproadvisers.com iproaccountants.com
```

It reads each zone from its OWN nameservers (several times -- see trap 3) and says, per zone: where the
bare name and `www` point, whether `mail` is a CNAME to the bare name, where the MX points, whether the
two `asuid` verification records are there and right, and whether CAA allows DigiCert. Also worth a look:

- **TTLs** (`nslookup -debug -type=A <domain> <its nameserver>`): the old records live in caches that
  long after every change. They were 14400 (4 hours) here. That number sets the whole day's timing.
- **What else rides on the bare name**: ask by TYPE, `nslookup -type=CNAME ftp.<domain> <nameserver>`.
  A plain lookup hides a CNAME -- it prints the final address -- and on the day that made
  `ftp.iproaccountants.com` look like an A record when it was an alias of the bare name.
- **The app's facts**: the verification id (portal -> ipro-prod-web -> Custom domains, or
  `az webapp show -n ipro-prod-web -g ipro-production --query customDomainVerificationId`), the address
  for bare names (`40.89.19.0`: what `ipro-prod-web.azurewebsites.net` resolves to), and the alias
  setting as it stands:
  `MSYS_NO_PATHCONV=1 az webapp config appsettings list -n ipro-prod-web -g ipro-production --query "[?name=='App__AliasHosts'].value" -o tsv`
- **How the owner reaches the DNS panel and his mail.** If either goes through the bare name
  (`https://<domain>:2083`, a mail app whose server is `<domain>`), it stops working when the bare name
  moves. Here cPanel is `res-cp9.yyz2.websiteservername.com:2083` (independent of the zone), and mail
  apps must say `mail.<domain>`.

## 1. Protect the mail -- BEFORE any name moves (owner, DNS panel)

In both zones `MX 0` pointed at the bare domain and `mail` was a CNAME to it: moving the bare name
would have sent every incoming message to Azure, which takes no mail. In this order, per zone:

1. `mail.<domain>`: Edit, Type **A**, Address the old server (`66.102.128.65`). An MX may not point at
   a CNAME, which is why this comes first. (cPanel lets Edit change the type in place; if a panel does
   not, delete and re-add within seconds.)
2. The MX row: Destination `mail.<domain>`, priority unchanged.
3. cPanel -> Email -> Email Routing: **Local Mail Exchanger** (it was). "Remote" would make the server
   refuse its own domain's mail.
4. Pin anything else that is a CNAME to the bare name and is still used (the owner pinned `ftp`).

The old MX stays in caches for its TTL. Moving the bare name inside that window risks mail arriving
**late** for senders whose resolver holds the old MX and fetches the new address (a sending server
retries for days; nothing is lost). On the day the owner chose not to wait the full four hours; say the
trade-off out loud and let him choose.

## 2. Let DigiCert issue -- BEFORE any name moves (owner, DNS panel)

App Service's managed certificates come from **DigiCert**. Both zones carried CAA records (the host's
AutoSSL put them there: Sectigo, Google, GlobalSign, Let's Encrypt; `issue` and `issuewild`), so
DigiCert had to refuse, the orders sat "in progress" for ever, and the two `www` names showed visitors a
certificate warning for about fifty minutes. Add ONE record per zone and delete nothing:

| Name | TTL | Flag | Tag | Value |
|---|---|---|---|---|
| `<domain>.` | 60 | 0 | issue | `digicert.com` |

The record on the bare domain covers `www` (a CAA lookup climbs to the parent). `dns-check.sh` reports
it. (The same records are why a managed certificate "never issued" for `app.iproadvisers.com` in July
2026 -- `DOCS/20_CERTIFICATES.md`, TODO 505.)

## 3. Prove ownership to Azure (owner, DNS panel)

Two TXT records per zone: **Name** box `asuid` and `asuid.www` (cPanel completes the domain), **Record**
box the 64-character verification id. On the day the first pair was saved with the NAME typed into the
Record box too; `dns-check.sh` catches that ("WRONG VALUE").

## 4. Bind the names and teach the app about them (assistant, on the owner's go; nothing visible)

Binding needs only the `asuid` records, so this happens while every visitor is still on the old site:

```bash
for h in www.iproaccountants.com iproaccountants.com www.iproadvisers.com iproadvisers.com; do
  az webapp config hostname add --webapp-name ipro-prod-web --resource-group ipro-production --hostname "$h" --query name -o tsv
done
```

Then the alias setting: READ it, APPEND to it (setting only the new names would drop the mortgage
entries), write it, read it back, and keep the restart away from five past the hour (the hourly
follow-ups job):

```bash
MSYS_NO_PATHCONV=1 az webapp config appsettings set -n ipro-prod-web -g ipro-production -o none --settings \
  "App__AliasHosts=www.ipromortgages.com=/mortgage,ipromortgages.com=/mortgage,www.iproadvisers.com,iproadvisers.com,www.iproaccountants.com=/accountants,iproaccountants.com=/accountants"
```

(That is the value as set on the switch day. Since 507 the live value writes the advisers pair as
`www.iproadvisers.com=/,iproadvisers.com=/`, in that order: the first one is the home page's public address.)

(`MSYS_NO_PATHCONV=1` or Git Bash rewrites `=/accountants` as a Windows path; `-o none` because the
command otherwise prints every app setting.) **Prove it before DNS moves** by asking the app directly
for each name -- the certificate check is skipped for THIS test only, because no certificate exists yet:

```bash
curl -sk --resolve www.iproaccountants.com:443:40.89.19.0 -o /dev/null -w "%{http_code} -> %{redirect_url}\n" https://www.iproaccountants.com/
```

Expected: the accountants pair 200 with the Accountants page (its `<title>`), `/Account/Register` on
them 301 to the same path on `app.iproadvisers.com`; the advisers pair 301 to
`https://app.iproadvisers.com/` (on the switch day; since 507, 200 with the home page and
`<link rel="canonical" href="https://www.iproadvisers.com/"/>` in it). A name answering "Website not published yet" is a typo in the setting.

## 5. Move the `www` names (owner), then their certificates (assistant, owner's go)

Owner: the `www` row, still a CNAME, Record `ipro-prod-web.azurewebsites.net`, **TTL 300** (a rollback
then takes minutes). From this moment visitors whose resolver has the new record see a certificate
warning until the certificate is bound -- minutes, IF step 2 was done.

```bash
ops/domain-switch/cert-order.sh www.iproaccountants.com www.iproadvisers.com   # one ACCEPTED order per name
ops/domain-switch/cert-wait.sh 30 www.iproaccountants.com www.iproadvisers.com # poll, bind SNI when issued
```

- Azure's own DNS check may refuse the order ("Missing one DNS record ... The current CNAME record ...
  is <old>") while the DNS host still hands out the old answer; the script retries. It took three tries
  for `www.iproadvisers.com`.
- An accepted order must NOT be placed again. Success looks odd: a Python traceback and "Managed
  Certificate creation in progress".
- The certificate resource is named after the host name; `az webapp config ssl show -g ipro-production
  --certificate-name <hostname> --query thumbprint` is empty until it issues.
- Times on the day, with CAA in order: 13 minutes for each `www` name, 2 minutes for each bare name.
  The front ends serve a new binding about a minute after it is made.

Proof is the step-4 command WITHOUT `-k`: it must succeed, and
`echo | openssl s_client -connect 40.89.19.0:443 -servername <name> | openssl x509 -noout -subject -issuer -enddate`
must show the name, DigiCert, and a date six months out.

## 6. Move the bare names (owner), then their certificates (assistant, owner's go)

Owner: the row named exactly like the domain, type **A**, Address `40.89.19.0`, TTL 300. Nothing else.
Then `cert-order.sh` and `cert-wait.sh` for the two bare names, and the same proof. (A bare name is
validated by an HTTP token through the A record; HTTPS Only on the app does not get in the way.)

## 7. Prove the whole thing

- All four names with full certificate checking (step-5 proof), plain `http://` answering 301 to
  `https://` on the SAME name (HTTPS Only does that before the app sees the request).
- Mail as the public sees it: `https://dns.google/resolve?name=<domain>&type=MX` -> `mail.<domain>`;
  `mail.<domain>` -> the old server. One real message from an outside mailbox (Gmail) to
  `support@iproadvisers.com`, read in the owner's mailbox.
- The app's own sending records untouched: the one SPF TXT, the two
  `selector*-azurecomm-prod-net._domainkey` CNAMEs, `_dmarc`, `ms-domain-verification`.
- A machine that looked the names up before the switch keeps the old address for up to the OLD TTL
  (4 hours): the owner's own PC showed the old site for a while. A phone on mobile data is the honest
  check. Note that an office network may also interfere with port 25, so a port-25 probe from there
  proves nothing either way; port 587 answered with the mail server's banner.

## Rollback

Put the old value back in the DNS row (the bare domain name for a `www` CNAME; `66.102.128.65` for a
bare-name A record). With TTL 300 it takes minutes. The bindings, the certificates and the alias
setting can stay: they do nothing for a name that does not point at the app.

## The day as it ran (Eastern time, 2026-09-20)

| When | What |
|---|---|
| 1:20 p.m. | `iproadvisers.com`: `mail` pinned, MX moved to it (owner). Accountants zone about 2:25 |
| 2:15 | `asuid` records, advisers zone -- first saved with the name as the value; corrected |
| 2:36 | All four names bound (assistant, on "go bind"); 2:38 `App__AliasHosts` set, app back in under a minute; each name proved against the app directly |
| 2:45 | The two `www` CNAMEs switched (owner) |
| 2:47 - 3:05 | Certificate orders: accountants accepted but never issuing; advisers refused twice by Azure's DNS check (the host still served the old CNAME), accepted 3:00. Cause found 3:05: CAA without DigiCert |
| 3:09 | `0 issue "digicert.com"` added to both zones (owner); visible on Google's and Cloudflare's resolvers within a minute |
| 3:11 / 3:13 | Fresh orders. 3:24 `www.iproaccountants.com` bound; 3:37 `www.iproadvisers.com` bound |
| 3:50 - 4:00 | 503 deployed in the gap (owner's "go deploy") |
| 4:30 | The two bare-name A records switched (owner, who chose not to wait out the MX TTL) |
| 4:34 / 4:36 | Orders accepted first try; bound 4:37 and 4:38. All four names proved 4:39 |

What cost the time: the CAA records (about fifty minutes of certificate warnings on two names -- check
CAA FIRST), the host's nameservers serving old and new answers side by side for ten to forty-five
minutes after each save, and two operator slips (the TXT value; the assistant's first lookup script
mistaking a CNAME for an A record).
