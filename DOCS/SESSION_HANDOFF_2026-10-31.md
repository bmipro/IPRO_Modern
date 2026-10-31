# Session handoff -- 2026-10-31

## Branches and state: read this first

- **Live (production):** portal and SuperAdmin on `05e7e25b`; live builder (builder.iproadvisers.com) on `11bc328`.
  The Beta is on for the owner's own adviser account only.
- **Staging:** iPro on tonight's close-out docs commit (its code is `a7bf43a7`, the first batch); staging builder
  `11bc328`.
- **Waiting to go live: the first batch, 622 to 627, `a7bf43a7`.** Gate 2420 of 2420 at the first try, the owner's
  staging look passed. It stays on staging until after the second demo (about 11-03), then goes live on the owner's
  word: their Run workflow on `main_ipro-prod-web.yml` and `main_ipro-prod-admin.yml` with the full SHA staging serves,
  and their approval for each. Rollback = iPro `05e7e25b`.
- **At that release** both apps make two new tables at start-up (`GoogleRevocations`, `ContactEmailSuppressions`);
  check both container logs and `/health/version`, as usual web then admin.
- `feat/622-627-batch` is fully in `main` and can be deleted.
- **Local builders:** both run `Projects\ipro-builder-site\build-e853cae` on .NET 10 and answer 127.0.0.1 only. The
  owner restarted them at 12:50 PM and checked their site on 5087. The old `build` folder stays; the start files from
  before are in `backups\start-files-before-net10`. After a reboot both need a double-click, as before.
- **This PC:** C: 90% full, 24 GB free. 0 test databases left behind by the gate.

## What happened today

| Time (Eastern) | What | State |
|---|---|---|
| Morning | Own-account check of 621: Photo Gallery gone from the builder's menu, listed in Pages as "Not in menu", Preview matches | **passed** |
| Morning | Its first two tries showed "already used or has expired": the page sends itself and my test step said to press its button, so one pass went twice (builder log: 200 then 401, 5 s and 1 s apart) | new row 622 |
| 10:49-10:54 AM | 596's bridge off both staging apps, by me | done |
| 11:03-11:07 AM | The same on both live apps: my delete was refused on production and not worked round, so the owner did it in the Azure portal while a read-only watcher confirmed each new container | done, 0 5xx |
| Late morning | Full review of `DOCS/TODO.md` (1,264 lines, six readers, then my own checks): rows 623-643, row 612 closed, new private list | done |
| 12:15 and 12:17 PM | `Email__TrackingSigningKeyPrevious` off both live apps, by the owner in the portal (629) | done, 0 5xx |
| 12:47-2:54 PM | Gate on the first batch: **2420 of 2420 at the first try** | passed |
| 12:50 PM | The owner restarted both builders on .NET 10 (628) | done |
| 3:00 PM | `main` fast-forwarded and pushed; staging on it, no start-up errors | done |
| Afternoon | The owner's staging look: the opening page, the words by business type, the setup fee on Billing; I looked at the home page note and the sample testimonials | **passed** |
| Afternoon | The owner's read-only checks on production, and the deleted-files look in the portal | **clean**, below |

## The first batch (622 to 627)

- **622:** the builder's opening page shows a spinner and offers no second press; the button is only in `<noscript>`.
- **623:** closing an account withdraws iPro's access at Google, as the privacy policy says. The eraser (SuperAdmin's
  app, which cannot read the token) leaves it in `GoogleRevocations`; the web app's Google Calendar job revokes it.
- **624:** an opt-out outlives the contact: `ContactOptOuts` keeps it as a hash when a contact is deleted, and adding
  them again, by hand or by import, puts it back through `EmailConsentService.CarryOptOut`, still the one writer
  (INVARIANTS rule 7). A website form or the person's own preferences remain the person's own word.
- **625:** client invoices know "Yukon Territory".
- **626:** truth-sweep leftovers: examples and AI drafts by business type (`BusinessWords`), the three exports open to a
  limited account with buttons on Billing, the setup fee on the Billing cards, "(sample)" on invented testimonials,
  a note under the home page screenshot, three stale lines in the Accountants library (a copy already on an adviser's
  site stays theirs to edit). **One sentence for the Terms (the fee applies to each new subscription) waits for the
  lawyer's revisit.**
- **627:** neither app takes the committed preview placeholder for a secret.

## The read-only checks (all 2026-10-31)

- **592 after-release query:** clean. Its one row is `www.411trades.com` on MichaelTran, kept on purpose and proved on
  10-22; the query's line predates that.
- **588 look for planted data:** nothing. Its one row, contact 96 on the owner's account, is the Amazon complaint
  simulator of 10-04 (pilot test 7); the `sendgrid:` in its reason is an old label in the code (TODO 644).
- **573 deleted files:** no sign the hole was used. Reach about 30 days. The only file from before the fix that is
  gone, `a66cc105..._hall2.png` in `website-gallery`, is named by no block, so it was removed the normal way.
- **The four adviser accounts** (MichaelTran, SaeedMasoudian, ArashMalekzadeh, BahmanMotamed): the owner knows them all.
- The queries are in `Documents\iPro_Read_Only_Checks_2026-10-31.sql`.

## Not seen yet

- 623 (needs a real Google connection and a closed account), 624, 625, 627 and the exports for a limited account:
  automated tests only.
- 620 on a real sign-up; 617 on the two-step page in a browser.
- The first real referral: **Refer a Friend is On** (the owner's screen, 0 referrals). My list said off, from a note of
  10-12; nothing had recorded the change. Follow the first one through.
- 601 after the clocks change on 11-01: one appointment should land at the right hour.

## How far to trust the to-do notes

Anything held only in the live database (SuperAdmin switches, display settings) cannot be read from here, so a note
about it is a dated claim. Say how old it is, or ask the owner to look. The list's last box sorts its items into
confirmed, reported and from notes only.

## Open items with dates

| Item | State | Waiting for |
|---|---|---|
| Second demo | -- | fresh health check and updated run sheet before about 11-03 |
| First batch live | on staging | after the demo, on the owner's word |
| 615, 616 builder pilot | unblocked | the owner's choice of 2-3 advisers |
| 601 | watch | an appointment after the clocks change, 11-01 |
| EF Core 8 support ends | no version for .NET 10 yet (Pomelo's newest is 9.0.0) | 2026-11-10; nothing to install; keep checking |
| 590 MySQL 8.0 to 8.4 | not started | read-only checks and a rehearsal mid-November; upgrade before 2026-12-31 |
| Lawyer's revisit | -- | Nov-Dec; privacy changes logged, plus the Terms sentence above |
| *.247advisers.com certificate | manual | start early January; expires 2027-01-25 |
| 636 Azure sign-in | watch | no Authenticator prompt on 10-31 (500121); look again only if it repeats |
| SNS `ipro-ses-alarms` | notes say Confirmed 10-27 | the owner's 30-second look in the AWS console |

## Still with the owner

- 643: where the repository's working notes live (going private loses the Approve step unless on GitHub Enterprise).
- 635: a copy of the sign-in keys.
- 618 fonts; 611 staging addresses; 471 this PC's rights.
- Sales tax outside Ontario; picture licences (69 stock photos, four added since, 539's banners, 546's e-card pictures).
- Platinum's setup-fee waiver ended 10-30 as set; the live page shows the fee: extend it only if wanted.

## The current list

`Documents\iPro_System_Book\iPro_Open_Work_2026-10-31_final.pdf` (source `open_work_2026-10-31.html`, kept out of this
public repository). Its three earlier copies from today are superseded.

## Do this first next session

1. After a reboot neither builder runs: the owner double-clicks the two Start-Builder cmd files.
2. Check the hosts (`/health/version`): production portal and SuperAdmin `05e7e25b`, live builder `11bc328`; staging on
   tonight's docs SHA.
3. Before about 11-03: demo prep.
4. After the demo: the first batch goes live on the owner's word.
