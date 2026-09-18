# Session handoff — 2026-09-18 (3 days to launch)

## What happened

- **494 (morning):** the owner noticed that the home page sells a fourth starting point, **Generic**, that
  no form offered. The neutral content already existed (the "All" starter pages, forms and articles every
  vertical falls back to), so Generic is the business type that takes exactly that set. One shared list
  (`StarterBusinessTypes.Offered`) now feeds sign-up, the profile, the preview and SuperAdmin's agent
  editor; the preview's whitelist is the same list and an unknown type falls back to Generic instead of
  Accountants; the daily-assistant preview speaks each type's own language (it had insurance copy for
  everyone); Generic gets three neutral calculators. Details in `DOCS/TODO.md` 494.

- **495 (afternoon):** the owner's follow-up -- content written FOR the Generic edition, and a button on
  the panel that sells the four editions. Six Generic starter pages (the shared set's home page read
  "Professional professional services..."), eight articles in two categories drafted by two
  content-writer agents to a strict brief and edited, two forms; seeded so they reach the already-seeded
  production tables; all editable under SuperAdmin -> Starter Content. The panel's two buttons carry the
  chosen edition into sign-up and the preview. Details in `DOCS/TODO.md` 495.

- **The local environment, fixed rather than avoided (afternoon):** a local look at 494 failed because
  IPRO.Web never listened. Cause: `ops\Start-LocalEnv.ps1` ran `Start-Process "azurite"`, which on this
  machine opens npm's extensionless shim in Notepad instead of running it, so the blob emulator never
  started and two start-up steps sat retrying 127.0.0.1:10000. The script now calls `azurite.cmd` and
  reports whether port 10000 came up; DOCS/16 says so, and says how to stop a Browser-pane run (close
  the tab first, or the pane restarts the app and its process locks the next build). The owner's
  words: "why can't you fix it instead of avoiding it?"

## Pushed today

| Code | Item | Gate |
|---|---|---|
| `4ae2eaa` | **494** the Generic edition is offered as a business type | 894/894 |
| `e493950` | **495** the Generic edition's own starter pack; buttons on the starting-point panel | 901/901 |

## Do this first tomorrow

1. **PayPal live cutover (owner's call on the day, Friday or Saturday):** production is still sandbox;
   portal-to-portal (the owner enters the live client id, secret and webhook id), then the webhook and a
   first live charge path verified together, with margin before the 21st.
2. **Owner-side:** the DNS switch for the four live names on or before 21 September (DOCS/14 -- APPEND to
   `App__AliasHosts`, verify all four names); counsel's short read of the privacy policy's 14/17 September
   changes when convenient; the designer's items and the /insurance package when ready.
3. **Owner, when convenient:** read the Generic pack under SuperAdmin -> Starter Content (six pages, eight
   articles, two forms) and edit anything that does not sound like IPRO; every adviser who picks Generic
   from now on starts from it.
4. **Launch morning (Monday 21 September):** watch both health endpoints, the Job Scheduler dashboard and
   Email Activity with the owner while the first sends go out; a blast should read **In progress** with a
   rising total and one Information line a minute in the log, never Failed. Bulk mail goes at 80 an hour.
5. **Calendar:** clear `Email__TrackingSigningKeyPrevious` on both apps around 17 October; Microsoft quota
   re-application mid-October; .NET 8 leaves support on 2026-11-10 -- move to .NET 10 in October.
6. **After launch:** the list at the end of `DOCS/NARROW_AUDIT_2026-09-17.md`; then 471, 450, the From
   display name (480), the Entra client secret rotation (482); white-label per `DOCS/WHITE_LABEL_UPGRADE.md`.

Related: `DOCS/TODO.md` 494; `DOCS/SESSION_HANDOFF_2026-09-17.md`; `DOCS/NARROW_AUDIT_2026-09-17.md`.
