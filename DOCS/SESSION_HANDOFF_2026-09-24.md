# Session handoff — 2026-09-24

## What happened

- **The TemplateBuilder reviewed, visually and operationally,** at the owner's ask, on the build running on
  this machine (`http://127.0.0.1:5085/`, working tree at `c242914` on `feature/builder-ux-refresh`). The
  write-up is `HOST_REVIEW_2026-09-24.md` in the builder repo, two local commits, **not pushed** until the
  owner says so (he has the file to forward meanwhile). Ten findings; the first two -- nested pages
  rendering permanently expanded under the menu, and the split hero not stacking on phones -- before any
  adviser sees the preview. Confirmed: all seven golden fixtures valid against every contract rule (a
  scripted check), 98 of 98 builder tests, readiness catching violations correctly, slugs normalising
  themselves, sections adding cleanly. Not run: the export download (a file download; on his go).
- **Two former clients' sites assessed** (the owner wants to win them back with incentives):
  girardfinancial.com is a WordPress site by a small studio -- every section maps to our blocks, 80-90% in
  look, the gaps being their embedded Linktivity booking widget and a cookie banner; a demo on a trial
  account with placeholder content was offered. northwoodfinancial.ca is John Klotz, an IPC adviser on the
  dealer's Veriday platform -- 70-80% in structure (no video hero, no floating video, YouTube not Vimeo,
  two custom widgets, co-branding and CIRO/CIPF badges), but the real question is the dealer's approval,
  which led to TODO 519.
- **TODO 519 added:** a dealer-compliance mode (archive, approval publishing mode, dealer profile), off by
  default so nothing changes for anyone else; the owner: "we will revisit it hopefully soon".
- **TODO 520 added, to decide tomorrow:** a bring-your-own-website package (the portal without the
  website screens, hosted forms and a client-login link for a site they keep elsewhere) -- about a week
  in two halves; the dangers and their guards are in the row. The owner: "I need to revisit this
  tomorrow with a fresh head and decide".
- Docs only today; no code deployed.

## Pushed today

| Code | Item | Gate |
|---|---|---|
| _docs only_ | TODO 519, TODO 520; this handoff | -- |

## Do this first tomorrow

1. Both health endpoints, the Job Scheduler (the certificate job passes now; its old failed entries may be
   deleted), Email Activity, Reports -> Visitors.
2. **The builder review:** the owner relayed it himself ("almost all with a few exceptions are done"); the
   two local commits on `feature/builder-ux-refresh` stay unpushed -- rebase or drop them when the
   developer's branch moves. On his go, pull one export ZIP and validate a real export with assets (the
   one path the fixtures leave untested).
3. **Decide:** TODO 520 (bring-your-own-website package) -- name, price, setup fee, whether the unpublished
   site stays; then the build order in the row.
4. **Open:** 506 (an adviser's domain with CAA records); retention for the two page-view tables; an adviser's
   own icon and logo; the client-invoice email's look; the comped plans' renewal date (8 July 2027); 519
   when the owner returns to it; the Girard demo if he wants it.
5. **Calendar:** clear `Email__TrackingSigningKeyPrevious` around 17 October; Microsoft quota mid-October;
   .NET 10 in October.

Related: `DOCS/TODO.md` 467, 519, 520; `DOCS/SESSION_HANDOFF_2026-09-23.md`; the builder repo's
`HOST_REVIEW_2026-09-24.md`.
