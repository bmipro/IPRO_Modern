# Independent Review Request — 2026-08-07

Written for a reviewer with **no context from the session that produced these changes**. Everything
needed to form an independent judgement is below. Please disagree freely; the point of this review is
that the author's judgement is being questioned, including by the author.

---

## Repository

```
Local : C:\Users\admin\Documents\Codex\2026-06-30\ca\work\iPro_Project\iPro_Project\IPRO_Modern
Remote: https://github.com/bmipro/IPRO_Modern.git   (public)
Branch: main
```

Solution: ASP.NET Core 8. Two web apps — `src/IPRO.Web` (agent portal **and** all public agent
websites, one process) and `src/IPRO.Admin` (SuperAdmin) — sharing one MySQL database via
`src/IPRO.DataAccess/IPRODbContext.cs`.

**Deploys:** GitHub Actions → `ipro-prod-web` (RG `ipro-production`), `ipro-prod-admin`
(RG `ipro-prod-admin_group`). App Service Plan `ipro-prod-plan`, **B2, single worker**.

---

## The specific concern

A production outage occurred on 2026-08-07: all agent public sites plus the portal returned 503 for
roughly five minutes. It resolved after a second `az webapp restart`. **The cause was never
established.** Service was restored in preference to gathering evidence, so the diagnostic window was
lost.

The two commits deployed immediately before it are the primary review target.

---

## PRIMARY: the routing change

### `7052444` — "Give the portal its own URL space under /portal"

**Files:** `src/IPRO.Web/Program.cs`, `src/IPRO.Web/Views/Shared/_Layout.cshtml`

- `Program.cs` ~line 288 — new route `app.MapControllerRoute("portal", "portal/{controller=Dashboard}/{action=Index}/{id?}")`, registered **between** the `legacy-register` route and `default`.
- `Program.cs` — early-return guards for `/portal` added to **both** `MarkPublicSlugOverrideAsync` and `ShouldRouteToPublicWebsite`.
- `_Layout.cshtml` — 28 sidebar links rewritten from `/Controller` to `/portal/Controller`.

**Questions for the reviewer**

1. Can this route registration, or its ordering relative to `default`, cause a **runtime** failure that a `dotnet build` would not catch? It was never run locally — the project has no local database, so the app has never been started outside Azure.
2. Route ordering: is anything shadowed or made unreachable?
3. `_Layout.cshtml` links were rewritten by script from an explicit controller list, longest-name-first (so `WebsiteLeads` is not mangled by the `Website` rule). Please verify none were missed or double-prefixed, and that `Account/Logout` and any POST forms still resolve.
4. Both prefixed and unprefixed routes now resolve to the same controllers. Is that duplication a problem — SEO, auth, antiforgery, anything else?

### `cb12b1d` — the regression fix that preceded it

**File:** `src/IPRO.Web/Program.cs` — `HasPortalSessionCookie`, called from `MarkPublicSlugOverrideAsync`.

Reads `.AspNetCore.Cookies*` **by name from the raw request**, because the middleware runs before
`UseAuthentication()` (path rewriting must precede `UseRouting`).

**Questions**

5. Is cookie-name sniffing before authentication acceptable here, or is there a correct way to get this signal at that point in the pipeline?
6. The cookie name is the ASP.NET Core default (never set explicitly). Is depending on that default acceptable, given renaming it would sign out every agent?
7. Any security consequence of branching on cookie *presence* rather than a validated identity?

---

## SECONDARY: audit fixes from 2026-08-06, none exercised at runtime

All build clean and were reasoned against the code. **None have been run.** Two touch money.

| Commit | Change | Files |
|---|---|---|
| `f6c6ad5` | `CancelPayPalSubscriptionAsync` returns `bool`; local row no longer marked Cancelled if PayPal refuses | `src/IPRO.Billing/PayPalBillingService.cs`, `src/IPRO.Web/Controllers/BillingController.cs` |
| `f6c6ad5` | Recurring invoice numbering: save per schedule + detach on failure | `src/IPRO.Scheduler/RecurringClientInvoiceJob.cs` |
| `23b94ec` | "Resume payment" now starts a real subscription instead of a one-time order | `src/IPRO.Billing/PayPalBillingService.cs` — `ResumePaymentAsync` |
| `eae2575` | Five agent fields restricted to SuperAdmin | `src/IPRO.Admin/Controllers/AgentsController.cs`, `Views/Agents/Edit.cshtml` |
| `5c3b85b` | Startup DDL races: catch MySQL 1060/1061; `SeedGuard` on three seeders | both `Program.cs`, `src/IPRO.DataAccess/*Seeder.cs` |

**Questions**

8. `23b94ec` calls `CancelPendingPaymentAsync` and then `CreateSubscriptionAsync`. Non-transactional — what happens if the second fails after the first succeeds?
9. `f6c6ad5` treats PayPal **422** as success ("already cancelled"). Correct, or does it mask real failures?
10. `RecurringClientInvoiceJob` now saves inside the loop and detaches **all** `Added` entities on failure. Could that discard something legitimately queued by another part of the same context?
11. `5c3b85b` swallows 1060/1061. Any case where those indicate a genuine problem rather than a benign race?

---

## The outage — what is known

**Timeline.** `7052444` deployed successfully to `ipro-prod-web` (twice: push + `workflow_dispatch`).
The app kept serving the **previous** build. An `az webapp restart` was issued; `/portal/Dashboard`
returned 302 on the first poll, and several endpoints verified correct. A few minutes later, all hosts
began returning **503 in ~0.15s** while `az webapp show` reported `state: Running`. Ten polls over
~3.5 minutes stayed 503. A second restart restored everything on the first poll.

**Ruled out:** startup crash (the app served correct responses before dying); duplicate route names
(`legacy-register`, `portal`, `default` are distinct).

**Never established:** why it died.

**Contributing factors worth the reviewer's attention**

- `az webapp log tail` returned nothing in 30 seconds. The app appears to have **no usable
  process-level logging**.
- Application Insights (`ipro-prod-web-insights`) returned nothing for a control query — it captures
  Warning and above by default, and almost nothing is logged at that level.
- The user reported `ipro-prod-web.scm.azurewebsites.net` returning **access denied**. Unexplained.
  Kudu cannot take the app down, but an access rule on it may point at something wider.
- **B2, single worker.** No second instance, so any process death is a total outage.

**Question 12 — the one that matters most:** was this caused by the deployed code, or by the platform
(the app was serving fine both before and after)? And what instrumentation should exist so this is
answerable next time rather than guessed at?

---

## How to reproduce locally

There is **no local database**, which is why nothing here has been run outside Azure. Standing that up
is arguably the single highest-value fix available, and the reviewer's opinion on it is welcome.

```bash
git clone https://github.com/bmipro/IPRO_Modern.git
cd IPRO_Modern
dotnet build src/IPRO.Web/IPRO.Web.csproj
dotnet build src/IPRO.Admin/IPRO.Admin.csproj
```

Both build clean at `7052444`.

---

## Background the reviewer should have

The portal and every agent's public website share **one application and one URL space**, so a portal
controller name and an agent's page slug are the same string. That namespace produced two production
bugs in two days, in opposite directions:

- `9fa27c8` — an agent's `/testimonials` page showed **visitors** a login form
- `cb12b1d` — the fix for that showed a signed-in **agent** their own public page

`7052444` (the `/portal` prefix) is the attempt to remove the ambiguity at its root. It is
**additive**: unprefixed routes still work, because password-reset links, invoice links and portal
invitations already in people's inboxes point at them.

Roughly 120 lines of compensating machinery still exist and are intended for deletion once nothing in
the wild depends on the old URLs: `MarkPublicSlugOverrideAsync`, `IsNeverShadowedPrefix`,
`HasPortalSessionCookie`, `BuildPortalRoutePrefixes` (all `src/IPRO.Web/Program.cs`).

**Reviewer's call explicitly invited:** is the additive prefix the right approach, or should the
unprefixed routes be retired immediately with redirects? A case can be made that carrying both is
what created the ambiguity in the first place.

## Also worth reviewing

- `DOCS/SECURITY_AUDIT_2026-08-05.md` — 6 audit reviewers, ~78 findings; **8 High and ~17 Moderate remain open**. Note that none of those reviewers flagged the shared-URL-space issue above, which is the root cause of two outages' worth of work.
- `DOCS/SESSION_LOG_2026-08-06.md` — the preceding day, including a documented case of a correct system being "fixed" on a false premise.
- `DOCS/09_TROUBLESHOOTING.md` — known traps, including a prior SIGABRT outage on 2026-07-29 from a startup race.
