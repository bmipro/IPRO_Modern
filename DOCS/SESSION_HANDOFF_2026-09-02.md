# Session handoff — 2026-09-02 (19 days to launch)

## What shipped

The owner's first day of testing on his own domain. Each item red-first, full gate green, verified
at `/health/version` on both hosts before its TODO row was ticked.

| commit | what | gate |
|---|---|---|
| `595d4a1` | **446** portal links built as strings now carry `/portal` — five writers (digest job, client timeline, marketing calendar ×3, leads return-URL), read-time repair on the dashboard for rows already stored bare, regression sweep across Controllers/Scheduler/Business | 599/600 (the 1 = the flake, now named) |
| `92263ee` | **448** drip step 1 sends at enrolment (Hangfire one-off, `drip` queue) under a claim shared with the hourly run (`DripEnrollmentClaims`); drips appear in Email Activity (Campaigns tab + recipient detail); Performance panel provider-neutral, "nothing sent yet". Two new columns via the schema repair | 614/614 |
| `a0e8e4e` | **449** page-editor Image Library is a picker not a gallery: 6-across, 4:3 thumbs, actions side by side; JS hooks and delete form pinned | 615/615 (combined) |
| `73bb14d` | **451 (2)** Hangfire `ShutdownTimeout = 10s` (Web); Admin pinned server-less | 616/617 (the 1 = a second load-only failure, named under 447) |
| `eda4efa` | **452** invoice emails tracked: a `ClientInvoiceEmails` row per send/resend/reminder with the provider id; Send mails first and only then marks Sent (a failure keeps the draft and says why); delivery events resolve to invoices (hard bounce suppresses); the public page stamps client views; history on the invoice, a Delivery column, an Invoices tab in Email Activity | 628/629 -- the 1 was the client-eraser coverage pin catching the new table (fixed, covered) |
| `cd46a25` | **453** support tickets: dashboard panel (unanswered first), do-not-reply footer + ticket button on the reply email, existing inbox notification pinned | whole tree, see log |
| `f320512` | **455** client-portal Logout 400: the logout form's antiforgery token was bound to the client identity but validated against the default (agent/anonymous) principal; Logout now authenticates against the ClientPortal scheme | whole tree |
| `69651fd` | **456** branded status pages (400/403/404/405/500...) in both apps, real status kept, browsers only (webhooks/health excluded), way back by area; `DOCS/ERROR_PAGES.md`; agent-level pages deferred as 458 | whole tree |
| `de4d8b5` | **457** one resolver for the client portal's address (healthy custom domain > free subdomain > platform); invite email and profile card use it; sign-in address shown while invited and once active | whole tree |
| `2b426c2` | **459** the registrar setup cards collapse once the primary domain is fully connected (DomainSetupState) | whole tree |
| `8c14f1e` | **460** appointment emails carry the client-portal sign-in link; 457's resolver now follows the owner's rule exactly (attached domain, else the platform host -- the free subdomain is not a tier) | whole tree |
| `09c0c82` | **454** the portal invite, appointment and testimonial emails keep the provider's answer; invite outcome remembered on the client | whole tree |
| `2f1415a` | **461** four unlisted guides indexed; today's features written into the guides; HelpDocsTests | pinned |
| `2664b6b` | **461 (2)** eight guides written from the real screens (23-30), indexed and embedded; the index test lists all 24 | 687/687 |
| `bae8929` | **447** parallelism capped at 4 (23m47s, zero failures vs 26-28 min and one failure at the default); the overdue test names its cause; full gate logs kept | measured |
| _see log_ | **450** visitors vote in a poll on the website: PollVote block, anonymous Website recipients, cookie + hourly cap, results rule, website-vote count on Results | whole tree |
| `2784987` | **458** verified already built (site-themed 404 live on the owner's domain); **462** six small findings from the guide research | docs |
| `aa985a5` `d86ad05` `f286c00` `cd2729e` `b3c3004` `9ea67ac` | docs commits (ticks, TODO rows, this handoff) | docs |

## Findings worth keeping

- **The URL-space rule bites string-built links, not tag helpers.** On an agent host a bare path
  is the public site; the portal is only `/portal`. Tag helpers pick the `portal` route because it
  is registered first; `$"/Clients/..."` strings do not. It worked on `app.iproadvisers.com`, so
  nobody saw it. `PortalPaths` is now the one way to write a portal link as a string, and the sweep
  test fails on any new bare one. `/Billing` and `/Account/...` are never-shadowed by design.
- **The drip job is hourly; newsletters are minutely.** That single fact explained "the newsletter
  arrived instantly, the drip did not." Confirmed live at the 12:00 run: step 1 sent, enrolment
  advanced to step 2, Performance Sent 1. 448 makes step 1 immediate and adds the claim the job
  never had against overlapping runs.
- **After a reboot, start local MySQL before any gate.** ~40 DB tests fail in 13 s with "Unable to
  connect"; it looks like a regression and is not. `DOCS/16_LOCAL_DEV.md` has the command.
- **Do not run a second `dotnet test`/build while a gate is running.** The test host holds
  `IPRO.Web.dll`; the second build fails on the lock and prints nothing that looks like a failure.
  Cost me one unobserved "red" today, caught before it was claimed.
- **Container swaps kill MySQL sessions mid-work, and their locks outlive them (451).** The first
  boot after 448 logged six Hangfire `MySqlJobQueue` command timeouts at once, a minute after a clean
  start, then nothing; the DB showed no aborts. App Service's 5-second stop grace period was shorter
  than Hangfire's 15-second shutdown wait, so a worker died mid-dequeue holding its row lock. Same
  root as 445's outage (a metadata lock, then). Fixed both ways: `WEBSITES_CONTAINER_STOP_TIME_LIMIT=30`
  on both apps (owner-authorised, 13:49) and Hangfire `ShutdownTimeout = 10s` in code (Admin runs no
  server -- pinned). The general lesson: on this platform, anything that holds a MySQL lock must be
  able to finish or roll back inside the stop window.
- **Invoices were fire-and-forget (452).** The invoice was stamped Sent before the mail was
  attempted, the provider's answer was discarded, no message id was stored, and the public page
  recorded nothing when the client opened it. Found by the owner asking one question. The general
  check for any transactional sender: does it keep the EmailSendResult, and does the delivery
  resolver know its table? Lead notifications, receipts and welcome mails still do not.
- **The coverage pins earn their keep (452).** The full gate's one failure was
  `ClientDataEraserCoverageTests` noticing the new `ClientInvoiceEmails` table has a ClientId column the
  client eraser did not list. Adding a table means three erasers/pins: agent eraser, client eraser,
  delivery-correlation pin. All three are tests; let them tell you.
- **An antiforgery 400 on a POST from an authenticated area is an identity mismatch (455).** The token
  carries the identity that rendered the form. If the receiving action does not authenticate against the
  same scheme, HttpContext.User is somebody else at validation time and the filter refuses. Any
  [AllowAnonymous] controller receiving a form from an [Authorize(AuthenticationSchemes=...)] page has this.
- **The flaky test has a name (447):** `SendClaimsTests.Retiring_a_poll_send_that_mailed_nobody_returns_the_survey_to_draft`
  — fails under parallel load, green alone. Shared-state race; investigate the fixture, do not
  retry-loop it.

## Deployed in one push at the end of the day

Build `9ea67ac`, verified at `/health/version` on both hosts: 452, 453, 454, 455, 456, 457, 459, 460, 461.
Everything was red-first, green, and gated over the whole tree (684/684 before the push; the
help-index change got its own red/green run and build after the last gate).

## Do this first tomorrow

1. **Watch ticket `2608310040012537`** (ACS quota → 500/min, 10,000/hour + engagement tracking).
   Reply sent 09-01; up to 72 h → expect Wed–Thu. Until granted: no test sends to addresses that
   cannot receive; `test@iproadvisers.com` only.
2. **PayPal live cutover** — production is still sandbox; every package must be re-synced. The one
   item that stops real money on launch day.
3. **Keep testing on your own domain** — today's three finds all came from that, and none would
   have shown on `app.iproadvisers.com`.

## Known-open, honestly

- **447** the named flake — open.
- **Delivered on drip steps** — was 0 seconds after the 12:00 send; correlation is wired both
  ways (send stores the provider id; the resolver reads it). If it is still 0 on the Campaigns tab
  after the 448 deploy, that is a real gap and worth an hour.
- Local MySQL: checked at the end of the day, zero orphaned test databases remain (the harness drops them on dispose).
- Post-launch: fold `StartupSchemaRepair` DDL into migrations; unify `SeedGuard`/`StartupGuard`
  lock primitives; consider a Standard-tier slot swap so deploys stop costing ~90 s of 503.

Related: `DOCS/TODO.md` items 442, 446–449; `DOCS/16_LOCAL_DEV.md`; `DOCS/INVARIANTS.md` (URL-space rule).
