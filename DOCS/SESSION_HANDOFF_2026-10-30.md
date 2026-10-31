# Session handoff -- 2026-10-30

## Update 2026-10-31

- **His own-account check of 621 PASSED** on his live account: Photo Gallery gone from the builder's menu, listed in
  Pages with the "Not in menu" mark, Preview matches. Nothing was saved or sent; his live site did not change. The rows
  below that still show it as waiting are superseded by this note.
- His first two tries that morning showed "This link to the builder was already used or has expired". Each sent the
  one-time pass twice, 5 s and 1 s apart, which fits a press of the page's button, as my test step told him. **New TODO
  622**: show that button only when scripts are off. The builder log's `Database.Connection[20004]` at 10:25:16 AM is the
  dropped post, not a database fault.
- Still open from the list below: the .NET bridge, the demo prep, and 618, 611 and 615/616 on his word.

## Branches and state: read this first

- **Live (production):** portal and SuperAdmin on `05e7e25b` (617, 619, 620, 621); live builder
  (builder.iproadvisers.com) on `11bc328` (621). The Beta is still on for his own adviser account only. Rollback =
  release `be1bdfc7` (iPro, 614) and `eed4ed9` (builder, 614).
- **Staging:** iPro on tonight's close-out docs commit (its code is `05e7e25b`); staging builder `11bc328`.
- **Release flow (unchanged since 10-28):** a push to `main` deploys STAGING only. Production iPro = his Run workflow
  on `main_ipro-prod-web.yml` and `main_ipro-prod-admin.yml`, with the full SHA staging serves, and his approval
  (Review deployments > production > Approve and deploy). The two runs queue behind each other by themselves, and the
  queued one asks for its approval only when its turn comes. The builder's production release
  (`builder-production.yml`) I can start on his word.
- iPro branch `feat/617-621-batch` is fully in `main` and can be deleted (as can `feat/613-builder-ipro-look` and
  `feat/614-builder-site-look`). Builder: `ipro/621-not-in-menu` = `ipro/customer-beta` = its `origin/main` =
  `11bc328`; the working copy is `C:\Users\admin\Projects\bb` (a worktree of `Projects\ipro-builder`), clean.
- **Local bench** (`ops/Start-BuilderBench.ps1`, portal 5110, SuperAdmin 5210, builder 5310): stopped; the local
  adviser's pages and design were put back after the side-by-side (pages 40 and 46, TemplateId 1). Azurite, which
  the bench started this morning, was stopped at close.
- **This PC:** 0 test databases left behind, no leaked headless Edge; C: 89% full.

## What happened today

| Time (Eastern) | What | State |
|---|---|---|
| Morning | **His own-account test of 614 passed** on his live site; he dropped the draft. Found **621**: Photo Gallery at the end of the builder's menu, not on his site or in Preview | done |
| Morning | "yes go ahead with the four fixes": one release with 617, 619, 620, 621 | done |
| 9:40-11:41 AM | iPro gate on `05e7e25b`: **2397 of 2397 at the first try**, in 2 hours (it was 2 h 40), with no database timeout, 0 test databases and 0 Edge left behind: 619's fix proved on itself | done |
| 9:44 AM | Builder `11bc328` on the staging builder (417 of 417 tests) | done |
| 11:46 / 11:48 AM | `main` fast-forwarded to `05e7e25b` and pushed; staging portal and SuperAdmin on it | done |
| Afternoon | His staging look: 617 "worked perfectly as designed"; 621: About hidden with the Visible switch left the builder's menu, and came back when made visible | passed |
| 4:18 PM | **Live**, step 1: builder `11bc328` (builder-production run 41120973974) | done |
| 6:53 / 6:58 PM | Step 2: his Run workflow and approval, iPro `05e7e25b`: SuperAdmin 6:53 PM (#954, run 41140547903), portal 6:58 PM (#992, run 41140564822); all three live hosts answer `/health` 200. His first presses of Run workflow never reached GitHub (no run existed); the second try did | done |
| Evening | Step 3, his own-account check of 621: **moved to 10-31** at his word | waiting |

## The four fixes

- **617, one press of Sign in:** on the portal's and SuperAdmin's sign-in pages and the two-step page the button now
  says "Signing in..." or "Checking..." and cannot be pressed again (the browser's Back button brings it back), and
  someone already signed in who opens a sign-in address goes to their dashboard instead of a 400 page later.
- **619, the tests' throwaway databases:** the test connections wait 300 seconds instead of 30, the first test of a
  run sweeps `ipro_test_` databases over a day old, and the accessibility tests ask Edge to close itself. Temp still
  holds 102 harmless `ipro-a11y-edge-*` folders from before the fix: my deletion was refused and I did not work round
  it (recorded in TODO 619).
- **620, accented names at sign-up:** `SignUpIdentifier` keeps the letters (Helene Cote written with accents gives
  `HeleneCote`), for the user name and the temporary site address.
- **621, the builder's menu:** when a session opens iPro sends `notInMenu`, the pages its own menu leaves out (hidden
  or unpublished; contract section 11, optional field). The builder leaves them out of its menu drawing and marks
  them "Not in menu" in Pages; they stay editable and still travel in the hand-over. Measured on the local bench in
  all three designs: the builder's menu equals the site's. Pictures:
  `Documents\iPro_Builder_Images\614_side_by_side\621_*.png`.

## Not seen yet

- 620 on a real sign-up: automated tests only.
- 617 on the two-step page in a browser: a source test only. The portal and SuperAdmin pages were tried in a browser.
- 621 on his own live account: his check below.
- I could not read live SuperAdmin's sign-in page from here (Azure's sign-in sits in front of it, answering 401 to
  anything else, on staging too). The portal's live page carries the new button; SuperAdmin is on the same build.

## Done by him today

- Ran 614's own-account test on his live site, and dropped the draft.
- Looked at 617 and 621 on staging and passed both.
- Ran and approved the two production iPro runs.

## Open items with dates

| Item | Branch | State | Waiting for |
|---|---|---|---|
| 621 own-account check | -- | live | **10-31**: on his live account, My Website > open the builder: Photo Gallery gone from the end of the menu; in Pages it is listed, marked "Not in menu"; Preview matches; drop the draft |
| 618 | -- | open | his decision (live sites drop a font whose name has quote marks) |
| 615, 616 | -- | open | the builder's next stages, after 621's check; who pilots first is his decision |
| 611 | -- | open | his choice: (a) a staging site root `*.staging.iproadvisers.com`, or (b) no public address on staging |
| 596 bridge | -- | live on .NET 10 | remove `DOTNET_ROLL_FORWARD` on both live and both staging apps about 10-31, on his word |
| Second demo | -- | -- | a fresh health check and an updated run sheet before about 11-03 |
| His two Template Builders | builder `ipro/beta` | 5087 answered at close, 5088 did NOT | he double-clicks `Start-Builder-For-Handovers.cmd`; after a reboot both need starting |
| 590 MySQL 8.0 to 8.4 | -- | open | before 2026-12-31, staging first |
| *.247advisers.com certificate | -- | Sectigo, manual | start early January (expires 2027-01-25) |

## Still his

- Sales tax outside Ontario (QST, PST, RST registration).
- The 69 stock photos' licences, and the sources of four pictures added since.
- 618 (the font fix changes how affected live sites look).
- 471: whether to narrow the Azure CLI's rights on this PC.
- The first real customer on the new builder (his decision).

## Do this first next session

1. His builders: after a reboot neither 5087 nor 5088 runs; he double-clicks the two Start-Builder cmd files.
2. Check the hosts (`/health/version`): production portal and SuperAdmin `05e7e25b`, live builder `11bc328`; staging
   on tonight's docs SHA, staging builder `11bc328`.
3. His own-account check of 621 (first row above). If it fails, rollback is above.
4. 10-31: remove the .NET 10 bridge (his word, not at the same time as a release). Before about 11-03: demo prep.
5. Then 615/616 as he picks.
