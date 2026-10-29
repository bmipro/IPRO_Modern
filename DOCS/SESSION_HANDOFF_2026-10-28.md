# Session handoff -- 2026-10-28

## Branches and state: read this first

- **Since today a push to `main` deploys STAGING only.** Production is released by hand: GitHub > Actions >
  `main_ipro-prod-web.yml`, then `main_ipro-prod-admin.yml`, Run workflow with the full SHA staging serves, and his
  approval in the `production` environment. My own `gh workflow run` of a production iPro release is refused by my
  tool's safety check, so he clicks Run workflow. The builder's production release (`builder-production.yml` in the
  builder repo) can be started by me, on his word.
- **Live (production):** portal and SuperAdmin on `7b72b40b` (585 Part E); live builder (builder.iproadvisers.com)
  on `e1939a6`. The Beta is on for his own adviser account only (`bahmanmotamed`, review before publishing).
- **Staging:** iPro `5120ccca` (613) served by the staging portal from 23:15 and SuperAdmin from 23:16 (production
  read `7b72b40b` before and after), then tonight's docs commit on top; staging builder on `9470a77` (613; its logo
  and bar styles served). Staging's pages sit behind its sign-in, so the bar itself is seen by signing in as the
  test adviser.
- **613 (stage 1 of the new builder) is built, gated and on staging, NOT live.** iPro `5120ccca` (gate 2367 of
  2367, four parts, 21:10-23:10); builder `9470a77` on the builder repo's `main` (406 of 406 locally, GitHub's
  tests passed before the staging builder deploy).
- iPro branch `feat/613-builder-ipro-look` is fully in `main` (fast-forward) and can be deleted. The builder's
  `ipro/613-ipro-look` and `ipro/customer-beta` both point at `9470a77` = its `origin/main`.
- Old side worktrees under `.claude/worktrees/` are all merged or abandoned (the 10-26 list); remove when convenient.

## What happened today

| Time (Eastern) | What | State |
|---|---|---|
| Today | 600's open question closed on his word: every refund marked before the release was a PayPal sandbox test; no credit notes, no SELECT | done (`3da119d6`) |
| Morning to 12:12 | **585 Part D**: the builder took the .NET 10 patch; `main` merged into the Beta (`6f97c93`); gate 2361 of 2361; on his word `main` fast-forwarded and pushed (staging only from then on); staging iPro `451a5e9` at 12:10/12:12, staging builder `e1939a6` after two Linux test fixes | done |
| 1:40-2:50 PM | His staging try (runbook Part D step 3): trial code, sign-up, Beta on, open, edit, add a section, See it on my site, Send to iPro, review, Bring it in, Put my previous site back, Continue my draft. Fixed on his word: the staging sign-in exclusion needed `/builder-api/*`. Found: 611, 612 | **Part D done** |
| 7:10 PM | **585 Part E** step 1: the live builder released at `e1939a6` (builder-production) | done |
| 7:21 / 7:30 PM | Step 2: iPro `7b72b40b` released, portal then SuperAdmin, his Run workflow and approval; the live database got `AgentBuilderBeta`, `WebsiteBuilderSessions`, `BuilderTenantErasures`, `WebsiteBuilderImports.Source` | done |
| 7:32 PM | Step 3: `Builder__BaseUrl` and `Builder__ServiceKey` on `ipro-prod-web` (key copied server to server, checked equal by length, never shown); the old `oidc-credential-82ff` and the repository secret `AZUREAPPSERVICE_PUBLISHPROFILE_IPRO_PROD_ADMIN` deleted on his word (only `github-production-environment` is left; nothing started by a push can deploy production) | done |
| About 7:35-7:56 PM | Step 4: the Beta on for his own account (it was Off; he saved it On); live builder opened his site, edit, See it on my site; Send to iPro at 7:56 PM; SuperAdmin listed it (20 pages, 32 sections); he dropped it, nothing went live | **Part E done: the new builder is live for his own account** |
| Evening | His question: the builder "has no indication that the builder is iPro". My answer as a plan page (https://claude.ai/artifact/LcpdGX8EunAUcHaB4ncH2s): four stages, TODO 613-616. v2 corrected my mistake that his colours and fonts do not reach the builder (they do: `BuilderBeta.Look`) | agreed: "yes add them to the to-do list and start stage 1" |
| Evening | **613 built** (both repos, see below); builder `9470a77` to the staging builder; iPro gate 2367 of 2367 (21:10-23:10); `main` fast-forwarded to `5120ccca` and pushed (staging only) | on staging |

## 613 in one paragraph

For an adviser (hosted mode) only, the designers' builder is unchanged. The editor now wears iPro's bar: the
portal's logo on iPro navy, "Website builder" + Beta, the site's own name and the address its clients see, Help
into iPro's guide (new tab), and "My Website" back (the old hosted editor hid that link). The main button takes the
adviser's own portal colour, darkened until white writing on it is readable (4.5:1). The words are an adviser's:
Preview, Send for review (or Review and publish when the adviser publishes), Image library, Checks, Sections on this
page, Section settings, and a History note without "packages". On My Website, 612's note appears when the open
draft still has changes that were put back. iPro sends the four new fields (site, address, accent, help) when a
session opens; they are optional, so either side can be released first (an older iPro gives "<adviser>'s website"
and the default blue). The builder keeps them in a new column `BuilderSessions.FrameJson`, which it adds itself at
start-up on MySQL (checked locally on a copy of the live builder's schema; a second start changes nothing).

## The agreed test path for 613 (his words: "as close of the look ... before we make it live")

1. **Staging (now):** he opens staging's portal as the test adviser, My Website > the Beta card > open the builder:
   the new bar, his portal colour on the main button, Help, My Website back; Preview and Send for review.
2. **Live, on his approval:** the builder first (`builder-production.yml` at `9470a77`, I can start it on his word),
   then iPro (his Run workflow at the staging SHA, portal then SuperAdmin). Rollback = release the previous SHAs
   (`e1939a6`, `7b72b40b`).
3. **His own live account** (the Beta is on only for him): tonight's test again on his real site: open, edit,
   Preview, Send for review, drop it in SuperAdmin.
4. Only then any customer. 614 (the builder's drawing checked against the live site) follows.

## Done by him today

- Approved both production releases in GitHub (portal run 989, SuperAdmin run 951).
- Switched the Beta on for his own account and ran the live test (sent, then dropped).
- Asked the question that became 613-616 and chose the test path (staging, then his own live account).

## Open items with dates

| Item | Branch | State | Waiting for |
|---|---|---|---|
| 613 live | iPro `main` `5120ccca`, builder `main` `9470a77` | on staging | his look at staging, then the live release on his approval, then his own-account test |
| 614 | -- | open | after 613 is live; 1-3 days |
| 611 | -- | open | his choice: (a) a staging site root `*.staging.iproadvisers.com`, or (b) no public address on staging (code only, free) |
| 610 check | -- | setting changed 10-27 | Cost analysis from 10-29: the "Paid IO LRS IO Rate Operations" meter for staging stops |
| 596 bridge | -- | live on .NET 10 | remove `DOTNET_ROLL_FORWARD` on both live and both staging apps about 10-31, on his word |
| Second demo | -- | -- | a fresh health check and an updated run sheet before about 11-03 |
| His two Template Builders | builder `ipro/beta` | 5087 and 5088 were DOWN at close (no answer) | he double-clicks `Start-Builder.cmd` and `Start-Builder-For-Handovers.cmd`; the .NET 10 build in place before 11-10, on his OK |
| 590 MySQL 8.0 to 8.4 | -- | open | before 2026-12-31, staging first (staging already runs 8.4) |
| *.247advisers.com certificate | -- | Sectigo, manual | start early January (expires 2027-01-25) |

## Still his

- Sales tax outside Ontario (QST, PST, RST registration).
- The 69 stock photos' licences, and the sources of four pictures added since.
- Dropping the two old sample packages (Review/1, Review/2) on his site.
- 471: whether to narrow the Azure CLI's rights on this PC.
- The first real customer on the new builder: only after 613 passes his own-account test (his decision).

## Do this first next session

1. His builders (5087, 5088): down at close; he double-clicks the two Start-Builder cmd files.
2. Check both hosts: staging portal and SuperAdmin on tonight's docs SHA, staging builder `9470a77`; production
   still `7b72b40b` and live builder `e1939a6` (`/health/version`).
3. He looks at 613 on staging (step 1 of the test path). Then, on his approval, the live release and his own-account
   test (steps 2-3).
4. From 10-29: the 610 meter. Before 10-31: remove the .NET 10 bridge (his word). Before about 11-03: demo prep.
