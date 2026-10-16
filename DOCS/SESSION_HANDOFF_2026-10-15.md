# Session handoff -- 2026-10-14 and 2026-10-15

## Branches and state: read this first

- `main` = production. Live build at close: **7084c99** (580), on both hosts, verified at /health/version.
  Local `main` may be one docs commit ahead (this file and the 580 check-mark): held, not pushed, because a
  push restarts both sites. Push it with the next code change.
- `hold/576-locked-boxes` (47e986d) is superseded: its content went to `main` as 5956344 on 2026-10-14. Safe to
  delete when convenient.
- **581 is parked** on the local branch `wip/581-moved-pages` (34e9ae7), in its own worktree
  `C:\Users\admin\Projects\wt581` (from c914e19). Not gated, not on `main`. Its hand-over note is in the shared
  notes folder (`side-session-notes\...`). Finish = gate it in the worktree, bring the commit onto `main`, hold,
  ask for his word.
- The builder repo `C:\Users\admin\Projects\ipro-builder` is at `ba49da9` on `ipro/beta` (nothing pushed); that
  build runs under both his builders.

## What went live (all on his word, each gated in full first)

| Item | What | Build | Gate |
|---|---|---|---|
| 576 | Locked boxes (contract v0.5): sections the builder does not carry travel as boxes; keep-ticks; Look says what each page is; two wording fixes from his screenshot | 5956344 (10-14 19:05/19:09) | 1,768 |
| 576 builder | ba49da9 under 5087 and 5088 (rehearsed on a copy of his data first; old build kept as `ipro-builder-site\build-6ffed14`) | -- | 375 + 37 |
| 577 | A menu label over 40 characters goes to the builder shortened and comes back as the page's own | 7688173 (10-15 08:28/08:31) | 1,778 |
| 578 | Public websites fixed for WCAG 2.1 AA (menus by keyboard, readable text on any colour -- his option 1, picture descriptions, forms keep what was typed, page/site language) | 58cc1fd (10-15 14:48/14:51) | 1,867 |
| 579 | Accessibility statement on every site (footer link) and iPro's own at /accessibility; no phone on the statements (his call; support@iProAdvisers.com is read) | c914e19 (10-15 19:05/19:08) | 1,923 |
| 580 | Pricing line "Accessible to visitors with disabilities" (second, all packages; short views show seven lines); Terms, Privacy and Accessibility served under each brand name instead of forwarding to app. | 7084c99 (10-15 21:22 admin, 21:25 web) | 1,935 |

Also committed: the read-only accessibility audit, `DOCS/ACCESSIBILITY_AUDIT_2026-10-14.md`
(published for him as a page: https://claude.ai/artifact/RZmeTeHpDZVnJD6RvZZDy5).

## His first real round trip (10-14 evening)

He made the package of his own site (after shortening four starter-article menu labels by hand, which 577 now
makes unnecessary), opened it in the hand-over builder (5088), changed it, exported, uploaded it on his own site
(Review/3, a return of the 8:00 PM hand-over), saw nothing red, and brought it in at 8:12 PM. All 19 sitemap
pages answered 200 afterwards; the Did You Know teaser came back as a locked box. App Insights showed "Make the
package" pressed three times (7:54, 7:58, 8:00 PM ET), each followed one second later by a GET of the same
address answered 405: most likely a download-manager browser extension re-fetching the download. Harmless (only
the newest hand-over can come back, and he used it); a GET there could land on the Template Builder page
instead of 405 if he saw an error. **Two sample packages still wait on his site (Review/1 and Review/2): he was
advised to Drop both.**

## Accessibility (the government lead's question)

- Audit 10-14: public sites close to WCAG 2.1 AA, portals further. Phase 1 (public sites, 578) and the
  statement (579) are live; the pricing line (580) says so.
- **Not done, and nothing may claim it:** a real screen-reader pass (needed before any conformance claim or
  VPAT/ACR), portal and marketing-page fixes (audit phases 2-3; iPro's own statement lists them openly),
  translating iPro's own words on French sites.
- He asked about overlay widgets ("Accessibility Menu" with profiles): told no, and why.
- Social media push: draft posts given in chat; safe claims only ("built around WCAG 2.1 AA", the features, no
  plug-ins); post after the statement page went live (it is).
- The audit's suggested reply to the government lead overclaims in its first sentence; he was told to say
  "We are bringing our shared website templates up to WCAG 2.1 Level AA ..." instead.

## The builder's pictures

The hand-over builder's "IPRO images" panel reads `X:\ipro_related\pics\ALL` on HIS computer (Remote Desktop
drive). At his request on 10-15: duplicates out (he moved them to `_not_used`), the five pictures over 8 MB got
3200 px copies (originals in `_not_used`), all 140 renamed to plain names. **`ALL\_original-names.csv` is the only
link from a picture to its stock number and licence: keep it.** He will add more pictures over the next days and
was asked to note each one's source. His own builder (5087) reads an empty library folder.

## After the reboot

- MySQL is his Windows service and starts by itself.
- **The two builders do NOT start by themselves** (nor the 5090 review copy): double-click
  `C:\Users\admin\Projects\ipro-builder-site\Start-Builder.cmd` (5087, his design) and
  `Start-Builder-For-Handovers.cmd` (5088). Windows Update had already restarted the PC on 10-14 at 01:29 and
  01:42 and they stayed down until started by hand.

## Still his

- Drop the two waiting sample packages (Review/1, Review/2) on his site.
- Licences of the stock pictures (now traceable through `_original-names.csv`) and the source of each new one.
- Cards waiting for him: the rate-limit bypass on public form posts (found by 578's review, older than that
  work); accessibility phases 2-3 (iPro's marketing, sign-in and portal pages).
- Whether builder design should travel (it does not: words, pictures and sections only).
- From before: SES step 8.2, GST/HST number, the 573 question, Search Console validations.

## Close-out 2026-10-15

| What | Where |
|---|---|
| Code (main, with this file) | `IPRO_Modern_backup_20261015-212719.zip` |
| Branches beyond production (main's held docs commit, `wip/581-moved-pages`) | `IPRO_Modern_branches_20261015-212719.bundle` |
| 581, ungated, as a patch on c914e19 | `IPRO_Modern_wip-581-moved-pages_20261015-212719.patch` |
| The builder's code (`ipro/beta`) | `IPRO_TemplateBuilder_ipro-beta_20261015-212719.bundle` |
| The hand-over builder's data (5088, his round trip of 10-14; no key ring) | `iPro_TemplateBuilder_handover-test_20261015-212719.zip` |
| The picture names list (`X:\ipro_related\pics\ALL\_original-names.csv`) | `iPro_Builder_picture-names_20261015-212719.csv` |

Both folders: `C:\Users\admin\OneDrive\Codex_Code_Bkup` and `C:\Users\admin\Documents\IPRO_Backups`. His own
builder's data (5087) has not changed since 2026-10-13 12:39 and is in the 2026-10-13 17:34 owner-site zip.
Build servers stopped. Nothing of mine left running; his builders (5087, 5088) were running at close and will
not come back after a reboot by themselves (see above).

## Do this first next session

1. Check both builders are running (5087, 5088); if not, start them with their scripts.
2. Push the held docs commit with the next code change (or on his word alone).
3. 581: gate it in `C:\Users\admin\Projects\wt581`, bring it onto `main`, hold, explain, ask.
4. Ask whether he dropped the two sample packages, and whether he wants the rate-limit card or accessibility
   phases 2-3 started.
