# Session handoff -- 2026-10-22

## Branches and state: read this first

- `main` = production. Live build at close: **c14a2b2** (594), unchanged today, on both hosts. Local `main` is
  four docs commits ahead (594 check-mark, the 10-21 handoff, the MySQL-version correction, this file): held, not
  pushed (a push restarts both sites). They go out with tomorrow's 596 release.
- **Tomorrow's 596 release:** the 596 branch was cut from local `main`, but this file now sits on `main` too, so
  `main` takes the 596 branch with a merge (docs-only difference). Before pushing, `git diff d67fc8b <merge> --
  src tests` must be empty: the released code is exactly the gated tree.

| Item | Branch (worktree under `.claude/worktrees/`) | Head | State | Waiting for |
|---|---|---|---|---|
| 596 .NET 10 (step 1) | `claude/elated-wozniak-b86e1b` | d7a537d (code d67fc8b) | gated 2,126 of 2,126; builder `ipro/beta` e853cae (375 of 375); runbook `DOCS/DOTNET10_SWITCH_RUNBOOK.md` | **the live switch, early 2026-10-23 morning, with him watching** (runbook section B), then release |
| 596 customer builder | builder `ipro/customer-beta` | -- | ready patch `596-builder-customer-beta.patch` in the shared notes folder (402 of 402); the safety check blocked edits in `C:\Users\admin\Projects\bb` | the Beta session (585) applies it with `git am --3way` |
| 585 builder Beta for customers | `claude/builder-beta-for-customers-2ebe5a` | f55d20e | Azure Parts A, B, C done today (below) | Part D after 596 is live: main merged in again, `staging.yml` to dotnet `10.x`, gate, first staging deploy on his word |
| 593 two-step sign-in | `claude/musing-hodgkin-64d1ee` | ebc6340 (code fb6d9cb) | gated 2,140; merges main cleanly (596 notes: re-run its gate after 596) | **his 15-minute try** (`593-try-it.md`), then release |
| 595 "Connect your domain" guide (own Domains page, his call today) | `claude/nervous-banach-ebe68f` | 12adada (code 8cad7a9) | third review fixed (1 medium, 6 low); **gated 2,180 of 2,180** on 8cad7a9; domains on their own page `/portal/Website/Domains`; notes `595-domain-guide.md` | release on his word (after 596; its TODO row and 596's both follow 594: resolve with `resolve_todo_row.py`); then his test with www.drhug.ca / www.ouritems.ca on bahmanmotamed; watch the first domain's DNS checks (TCP 53 to its own name servers, public resolvers as fallback) |

## 596: .NET 10 (built and gated today, nothing deployed)

- .NET 8 support ends 2026-11-10. All 10 projects and both deploy workflows on net10.0; EF Core stays on the 8 line
  (8.0.32 + Pomelo 8.0.3: Pomelo has no EF Core 10 release; EF Core 8 also loses support 2026-11-10 = step 2).
- **The catch that mattered:** .NET 10's compiler turns "value in list" inside queries into a form EF Core 8.0.2
  cannot run: 15 spots in 7 methods, one in the start-up package setup (neither app would have started). EF Core
  8.0.32 runs them; a new test class proves all 15 on MySQL.
- Noticed, not done: `ipro-prod-admin` has Always On off (the other apps on); the builder's planning-desk sample was
  already out of date; his builders (5087/5088) still run the .NET 8 build (republish is a separate step, his word).

## 585: Azure set-up done today (he watched; I worked the portal and CLI; his secrets typed by him)

- **Part A:** group `ipro-builder-staging`, plan B2 Linux, four apps (now **.NET 10**, Always On, HTTPS only, TLS 1.2,
  FTPS and basic auth off, health `/health`; unique default hostnames `ipro-builder-e5akhkdnawa3h4dp`,
  `ipro-builder-staging-b9bxchcdfnbegfhj`, `ipro-staging-web-fqh6htg8a8h7cqd5`, `ipro-staging-admin-h7dtcrc2dncdbahk`
  .canadaeast-01.azurewebsites.net); `ipro-mysql-staging` (MySQL 8.4, databases `ipro_staging`, `ipro_builder`);
  storage `iprostoragestaging`; database `ipro_builder` on the live server; deploy identities and GitHub
  environment `production` (reviewer bmipro, admins cannot bypass); Microsoft sign-in in front of both staging
  sites (only his account; `/health`, `/health/version`, `/builder-api` open; client secret renew ~2027-04-20).
- **Part B:** every setting on all four apps, checked by name and length (none empty, no App Insights). The
  builder service keys were generated straight into the apps (staging pair matches). Both `ipro_builder` database
  passwords were replaced by random ones made inside Cloud Shell, in the database and the setting at once (one
  paste of his; nobody needs to know them). PayPal sandbox keys copied from the local web settings file.
  Staging web/admin carry `DOTNET_ROLL_FORWARD=Major` (596 runbook A; remove with production's a week after).
- **Part C:** `builder.iproadvisers.com` CNAME + TXT `asuid.builder` at the cPanel host (a cPanel "builder"
  subdomain created first blocked the CNAME; removed in cPanel Domains), hostname added, free managed certificate
  (to 2027-04-22, auto-renews) bound. Answers 404 until the builder is deployed.
- **Left for Part E (production):** copy the live builder's service key into `ipro-prod-web`'s
  `Builder__ServiceKey` (server to server; restarts the live site). **After the first approved production
  release under the new workflows:** delete `oidc-credential-82ff` on `oidc-msi-b4cc` and the REPOSITORY secret
  `AZUREAPPSERVICE_PUBLISHPROFILE_IPRO_PROD_ADMIN`.

## Domains today

- **www.411trades.com and 411trades.com** (iPro's own, on michaeltran): his `_ipro-verify` TXT added, GoDaddy
  forwarding removed (only A 40.89.19.0 left); both names serve with padlocks.

## Still his

- 592's after-release SELECT (`ops/sql/domain-ownership-after-release.sql`, Cloud Shell, read-only).
- Sales tax: his calls (Revenu Quebec, BC, Manitoba, Saskatchewan, or an accountant), then scope the change.
- Empty the Recycle Bin (old backup snapshots moved there by the two-week pruning).

## Do this first next session

1. His builders (5087, 5088): if down, he double-clicks Start-Builder.cmd and Start-Builder-For-Handovers.cmd.
2. **Early morning: the live .NET 10 switch** (`DOCS/DOTNET10_SWITCH_RUNBOOK.md` section B on the 596 branch):
   pre-check (both hosts' SHA, overnight errors), bridge setting web then admin, stack web then admin (each
   ~90 s), release 596 (merge, empty code diff against d67fc8b, push), verify both hosts and both container logs.
3. 585 Part D: the customer-builder patch, main into the Beta, `staging.yml` dotnet 10.x, gate, first staging
   deploy on his word (this changes releases: push = staging, production by "Run workflow" with his approval).
4. 593 try, then release; 595 his domain test, then release.
5. 592's after-release SELECT.
