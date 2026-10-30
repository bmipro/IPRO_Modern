# Session handoff -- 2026-10-29

## Branches and state: read this first

- **Live (production):** portal and SuperAdmin on `be1bdfc7` (614); live builder (builder.iproadvisers.com) on
  `eed4ed9` (614). The Beta is still on for his own adviser account only. Rollback = release `e77790b2` (iPro, 613)
  and `74ad977` (builder, 613).
- **Staging:** iPro on tonight's close-out docs commit (its code is `be1bdfc7`); staging builder `eed4ed9`.
- **Release flow (unchanged since 10-28):** a push to `main` deploys STAGING only. Production iPro = his Run workflow
  on `main_ipro-prod-web.yml`, then `main_ipro-prod-admin.yml`, with the full SHA staging serves, and his approval
  (Review deployments > production > Approve and deploy). The two runs queue behind each other by themselves. The
  builder's production release (`builder-production.yml`) I can start on his word.
- iPro branches `feat/613-builder-ipro-look` and `feat/614-builder-site-look` are fully in `main` (fast-forward) and
  can be deleted. Builder: `ipro/614-site-look` = `ipro/customer-beta` = its `origin/main` = `eed4ed9`; the working
  copy is `C:\Users\admin\Projects\bb` (a worktree of `Projects\ipro-builder`), clean.
- **This PC's local database (his OK, tonight):** its change log (binary logs, 9 GB) cleared and capped at 3 days
  (`SET PERSIST binlog_expire_logs_seconds = 259200`, kept across restarts); C: went from 96% to 91% full. The 94
  test databases the tests had left behind since 08-28 were dropped (TODO 619).
- **Local bench** (`ops/Start-BuilderBench.ps1`, portal 5110, SuperAdmin 5210, builder 5310): stopped at close. Its
  test adviser (local agent 12, site 3) now has the address `bench-adviser.localhost` and header settings with phone,
  Email and "Book a call", for the side-by-side; local only.

## What happened today

| Time (Eastern) | What | State |
|---|---|---|
| Morning | His staging look at 613: two designers' leftovers seen and fixed in the builder (`74ad977`: the package summary hidden, "Identity" named "Logo" for an adviser) | done |
| 12:24 PM on | **613 live**: builder `74ad977` (builder-production), then his Run workflow and approval for iPro `e77790b2`, portal and SuperAdmin | done |
| Afternoon | His own-account test of 613 on his live site passed (sent, dropped). Found **617**: a second press of Sign in on SuperAdmin shows a 400 page (the first sign-in had worked) | done; 617 open |
| Afternoon | **610 closed**: the staging I/O meter went from C$0.67 a day to C$0.01 on 10-28 and none since (`4266cf19`) | done |
| Afternoon | **614 built** ("yes go ahead with stage 2", then "yes go ahead this way"): see below. Builder `eed4ed9` to the staging builder at 4:10 PM | done |
| 4:05-6:52 PM | iPro gate on `be1bdfc7`: 2370 of 2373 in the four parts; the three failures were the local database not answering within 30 seconds (no assertion); the three classes passed on a re-run on the same build = **2373 of 2373**. Cause found: a test database's drop is slow on this PC and others wait behind it (**619**) | done |
| About 6:55 PM | `main` fast-forwarded to `be1bdfc7` and pushed; staging portal and SuperAdmin on it | done |
| Evening | His staging look at 614: "it matches the preview" | passed |
| 7:25 PM | **614 live**, step 1: builder `eed4ed9` (builder-production run 40972965557) | done |
| 8:05 / 8:08 PM | Step 2: his Run workflow and approval, iPro `be1bdfc7`: portal 8:05 PM (run 40977054686), SuperAdmin 8:08 PM (run 40977270321); all three live hosts answer `/health` 200 | done |
| Evening | Step 3, his own-account test of 614: **postponed to 10-30** at his word (tired) | waiting |
| Evening | His question: what happens when a second and a third person with the same name sign up (answer below); found **620** | 620 open |

## 614 in one paragraph

For an adviser's site, the builder now draws the page in the site's own design (Modern, Classic or Editorial)
instead of one generic layout: the design's own header (top menu or Classic's side menu, with the phone, Email link
and button), its hero (business name above, the first letter where there is no picture), services, call to action,
contact (a picture of iPro's form), text and footer, using iPro's own style rules copied into the builder
(`tools/sync-ipro-site-css.mjs` in the builder repo makes `wwwroot/css/ipro-site.css` from iPro's views; run it again
when iPro's public designs change). iPro sends what that needs when a session opens (optional `look` fields:
`design`, `vars`, `bodyClass`, `headerClass`, `siteFont`, `company`, `monogram`, `contact`, `copyright`,
`headerActions`; contract section 11), held to the shells by `BuilderSiteLook614Tests`. Measured pixel by pixel at
1280 wide on this PC: Modern and Editorial identical, Classic identical but for long article names in its side menu
(0.17%, the hand-over clips them to 40 letters). Typing on the new drawing checked in each design (a heading, a menu
label, a service line, saved and reopened). Not drawn yet: sections only iPro edits (calculators, newsletter
sign-up and the like) stay a plain box in the design's frame.

## His question: two or three people with the same name

Answered from the code (`AccountController.Register`, `GenerateUniqueUserNameAsync`, `GenerateUniqueDomainAsync`):
the first John Smith gets the user name `JohnSmith` and the temporary address `JohnSmith.247advisers.com`; the
second gets `JohnSmith1`, the third `JohnSmith2` (address likewise). Each is a separate account with its own site;
they sign in with their email or that user name. The same email twice is refused ("An account already exists for
this email address"); the database holds user name, email and address unique. Business names may repeat; a custom
domain cannot ("That domain is already in use by another IPRO account"). Found while answering: accented letters
are dropped from the user name and address (`HlneCt` for Helene Cote written with accents): **620**.

## Done by him today

- Looked at 613 and 614 on staging and passed both; ran 613's own-account test on his live site.
- Ran and approved four production iPro runs (613 and 614, portal and SuperAdmin).
- Said yes to clearing this PC's database change log and capping it at 3 days.

## Open items with dates

| Item | Branch | State | Waiting for |
|---|---|---|---|
| 614 own-account test | -- | live | **10-30**: on his live account, My Website > open the builder: his site's own design; compare with his site or Preview; type, Save, reopen; drop the draft |
| 617 | -- | open | rides with the next release |
| 618 | -- | open | his decision (live sites drop a font whose name has quote marks) |
| 619 | -- | open | rides with the next release's gate (test connections' timeout, sweep old test databases, and the accessibility tests' leftover headless Edge: 351 processes ended at close) |
| 620 | -- | open | about an hour plus a gate |
| 615, 616 | -- | open | the builder's next stages, after 614's own-account test |
| 611 | -- | open | his choice: (a) a staging site root `*.staging.iproadvisers.com`, or (b) no public address on staging |
| 596 bridge | -- | live on .NET 10 | remove `DOTNET_ROLL_FORWARD` on both live and both staging apps about 10-31, on his word |
| Second demo | -- | -- | a fresh health check and an updated run sheet before about 11-03 |
| His two Template Builders | builder `ipro/beta` | 5087 answered at close, 5088 did NOT (down since this morning) | he double-clicks `Start-Builder-For-Handovers.cmd`; after a reboot both need starting |
| 590 MySQL 8.0 to 8.4 | -- | open | before 2026-12-31, staging first |
| *.247advisers.com certificate | -- | Sectigo, manual | start early January (expires 2027-01-25) |

## Still his

- Sales tax outside Ontario (QST, PST, RST registration).
- The 69 stock photos' licences, and the sources of four pictures added since.
- 618 (the font fix changes how affected live sites look).
- 471: whether to narrow the Azure CLI's rights on this PC.
- The first real customer on the new builder: only after 614 passes his own-account test (his decision).

## Do this first next session

1. His builders: after the reboot neither 5087 nor 5088 runs; he double-clicks the two Start-Builder cmd files.
2. Check the hosts (`/health/version`): production portal and SuperAdmin `be1bdfc7`, live builder `eed4ed9`; staging
   on tonight's docs SHA, staging builder `eed4ed9`.
3. His own-account test of 614 (step 3 above). Then 615/616, 617, 619 and 620 as he picks.
4. Before 10-31: remove the .NET 10 bridge (his word). Before about 11-03: demo prep.
