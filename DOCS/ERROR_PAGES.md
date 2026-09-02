# Error pages

Added 2026-09-02 (TODO 456). Before this, only an unhandled exception produced a page of ours;
every other status (a 404, the antiforgery 400, a 403) returned an empty body and the browser
showed its own error page.

## What a visitor sees now

One branded page per status, in both apps, keeping the **real status code** so monitors, search
engines and scripts still see the truth:

| status | title |
|---|---|
| 400 | That request could not be processed |
| 401 | Please sign in |
| 403 | You do not have access to that page |
| 404 | Page not found |
| 405 | That action is not available this way |
| 408 / 429 | The request took too long / Too many requests |
| 500 | Something went wrong (with a support reference id) |
| 502 / 503 / 504 | The service is briefly unavailable |
| anything else | Something went wrong (error N) -- the number, never an invented reason |

The **way back** depends on where the visitor was:

- a path under `/ClientPortal...` -> "Back to the client portal" (`/ClientPortal`)
- a path under `/portal` -> "Back to your portal" (`/portal`)
- anything else (public site, sign-in pages) -> "Back to the home page" (`/`)
- in Admin, always "Back to the dashboard"

## Who does NOT get an HTML page

`StatusPagePolicy.ShouldRender` (IPRO.Utility) decides. A page is rendered only when the request
explicitly accepts `text/html` **and** the path is not a machine endpoint:

- `/health`, `/health/version` -- probes read the body
- `/AzureEmailEvents`, `/Newsletter/SendGridEvents`, `/billing/webhook` -- Event Grid, SendGrid and
  PayPal read the status code and must never be answered with HTML
- `/hangfire` -- has its own UI
- `/error` -- never re-enter ourselves

Everything else keeps the bare status it always had.

## How it is wired

- `app.UseWhen(StatusPagePolicy.ShouldRender, b => b.UseStatusCodePagesWithReExecute("/error/{0}"))`
  in both `Program.cs` files, **before routing** -- the re-executed request must travel the rest of
  the pipeline to reach `ErrorController`.
- `app.UseExceptionHandler("/error/500")` (production only; development keeps the developer page).
- `ErrorController.Status(code)` (anonymous) reads the original path from
  `IStatusCodeReExecuteFeature`, sets the status code, renders `Views/Error/Status.cshtml`.
- On an agent host, `error` is a never-shadowed prefix (`IsNeverShadowedPrefix` in IPRO.Web's
  `Program.cs`), so `/error/404` reaches the controller instead of the public site's slug lookup.

Tests: `tests/IPRO.IntegrationTests/ErrorPagesTests.cs` (policy tables, controllers, wiring pins).

## Later: agent-level pages (TODO 458)

These pages carry the platform's branding. On an agent's own domain, a 404 for a public-site page
would ideally look like the agent's site: their header, their colours, their navigation. That is a
second step, deliberately deferred (owner, 2026-09-02: "for now we go with server level errors and
later we could change it to agent level as well").

When it is built:

1. In `PublicWebsiteController`, where an unknown slug returns `NotFound()`, render a
   `NotFound.cshtml` through the site's managed-page shell instead (the same partials the three
   shells use), with `Response.StatusCode = 404` -- the status-page middleware only fires when the
   response has **no body**, so a themed 404 with a body simply wins.
2. Keep the platform page for everything that is not a public-site page (the agent portal, the
   client portal, sign-in), since those are not the agent's site.
3. Consider a per-website "404 page" content block so an agent can write their own message.
