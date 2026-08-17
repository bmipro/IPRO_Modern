# IPRO Invariants

**Read this before changing routing, hosts, authentication, or billing state.** It is short on
purpose. The roadmap is a 2,100-line narrative of what happened; this file is the small set of rules
that must stay true, so you can check your change against it in two minutes.

A rule earns its place here only after breaking it has cost real time. Each one says what it is, why,
and what happens when it's violated. If you need to change a rule, change it here first — a code
change that contradicts this file is a bug in one of the two.

---

## 1. URL space: `/portal` is the only portal

On an **agent's public host** (`<agent>.247advisers.com` or their custom domain):

- A bare path is **always** the public website. `/testimonials` is the agent's Testimonials *page*.
- The agent portal is reachable **only** under `/portal/...`.
- No exceptions for signed-in agents. No exceptions for slugs that collide with controller names.

The only paths an agent host does not surrender, from `IsNeverShadowedPrefix` in
`src/IPRO.Web/Program.cs`: `account`, `clientportal`, `clientportalaccount`, `billing`,
`publicwebsite`, `media`, `hangfire`, `health`. These are the ways in and the ways to recover, plus
the load-balancer probe — not features.

On the **platform host** (`app.iproadvisers.com`) bare paths still route normally; the marketing site
and the portal share it and there is no public agent website there to collide with.

**Why:** the portal and every agent website are one application sharing one URL space. Deciding
ownership per-path — by reserving controller names, checking for an auth cookie, or looking up
whether a page exists — produced bugs in both directions and survived three separate fixes because
each fix added a mechanism instead of removing one. `/portal` answers it with one segment and no
state.

**Violation looks like:** a visitor on an agent's firm website gets an IPRO login form; or an agent
clicks a link on their own public site and lands in the portal.

**Enforced in:** `ShouldRouteToPublicWebsite` in `src/IPRO.Web/Program.cs` — the single decision
point. Do not add a second one. Covered by `ops/Test-RoutingInvariants.ps1`.

---

## 2. Portal links are absolute and prefixed

Every portal link is written `/portal/...`, never a bare `/Controller/Action` and never relative.
Relative links were what made rule 1 look impossible to satisfy: on an agent domain they resolved
against the public site.

**Violation looks like:** a portal page whose nav works on `app.iproadvisers.com` and 404s or shows
the public site on an agent's own domain.

---

## 3. The auth cookie is host-only

An agent signing in at `www.theirfirm.com` gets a cookie for that host and no other. It does not
carry to `app.iproadvisers.com` or to another agent's domain.

So any **absolute** URL that must stay authenticated — redirects, callbacks, links in email — has to
be built for the host the agent is actually on (`PortalUrlHelper`), or bounce through the canonical
host first. A hard-coded `https://app.iproadvisers.com/...` sent to an agent working on their own
domain lands them on a login page.

**Never** decide anything from the mere *presence* of a cookie. It is unvalidated at the middleware
stage — any stale or forged `.AspNetCore.Cookies` value passes a presence check. This is exactly how
rule 1 got broken: `HasPortalSessionCookie` matched on the cookie name alone.

---

## 4. Both apps share one database and one schema-repair path

`IPRO.Web` and `IPRO.Admin` run as separate App Services against the same MySQL database, and both
run schema repair and seeders at startup — concurrently, on every deploy.

- New column or table: add the `EnsureTableColumnAsync` / `Ensure...SchemaAsync` call to **both**
  `Program.cs` files, not one.
- Any check-then-insert seeder must be wrapped in `SeedGuard` (a MySQL `GET_LOCK` advisory lock).
  An unguarded one raced and took production down with a SIGABRT once.

---

## 5. Package entitlement has exactly one gate per question

- "Does this agent have an active subscription at all?" → `IsAccessGatedAsync`.
- "Does their package include feature X?" → `PackageFeatureCodes.X` via `IPackageEntitlementService`.
- Every AI feature is gated by the single flag `AiDailyAssistant`. No per-feature AI flags, no
  per-agent or per-package AI usage metering.

A gated agent keeps `/Billing`, login/logout, and their own account (`/Account/Profile`,
`/Account/ChangePassword`, `/Account/SetPortalAccentColor`). Everything else redirects to `/Billing`.
The sidebar still *shows* every item — visible but unavailable is the point; hiding them removes the
reason to subscribe.

**If you add a UI control a gated agent can click, check it against the exemption list.** Both
Profile and the accent swatches shipped silently bouncing to `/Billing` because nobody did.

---

## 6. Deploys are serialized; PayPal is sandbox in production

- **A green deploy does not mean the new code is serving — now enforced, not remembered.**
  `WEBSITE_RUN_FROM_PACKAGE=1` means the worker serves an already-mounted package, so a deploy can
  succeed and leave production on the previous build. Observed three times on 2026-08-08; `/health`
  returned `Healthy` throughout, because the app was up, just not new. Basic tier has no deployment
  slots, so a swap (which restarts by definition) is not available.

  Both workflows now stamp the commit into the build (`-p:SourceRevisionId`), restart the app, and
  **poll `/health/version` until it returns that exact commit**, failing the job after 5 minutes if
  it never does. So a green run again means what it appears to mean.

  `/health` still answers liveness only. To ask "which build is live?", use `/health/version` —
  never `/health`. Under the `health` prefix deliberately, so it inherits never-shadowed routing.
- Both workflows share the GitHub Actions concurrency group `deploy-ipro-production`. Don't remove
  it: parallel deploys against one database interleave schema repair.
- Production currently runs PayPal in **sandbox** (`PayPal__IsSandbox=true`). Anything that creates
  real plans or charges must check this flag, and QA-only packages must set `IsHiddenTestPackage`.
- Webhooks cannot reach localhost, so subscription *activation* can only be verified on production.

---

## 7. One opt-out, one decision point, and it stops everything

An unsubscribe suppresses **all** marketing email to that client. The only exception is an e-card
whose design is flagged `SendAfterUnsubscribe` **and** whose recipient has explicitly opted back in
to greetings on the preferences page. Both halves, or nothing.

- **`EmailConsentService.IsSuppressed` is the only place this is decided.** Every dispatcher asks it.
  Nothing re-implements the test. A new exception changes that file, not a dispatcher.
- **The RFC 8058 one-click POST must suppress broadly and immediately.** Gmail and Yahoo fire it with
  no human present, so it cannot ask a question or depend on anyone loading a page. The preferences
  page is where a person makes the narrower choice, afterwards.
- **`SendAfterUnsubscribe` defaults to false.** A design added later cannot inherit an exemption
  nobody chose. It is a real column, not a rule matched on `Occasion` — `simple-birthday` is filed
  under Occasion "Simple" while `birthday-audi` is under "Birthday", so any string rule would let one
  birthday card through and block the other.
- `Client.IsNewsletterSubscribed` still exists and still means "wants the newsletter". The unsubscribe
  sets **both** it and `EmailOptOutAt`, so one action has one result. Do not add a third flag.

**Violation looks like:** someone who pressed Unsubscribe receives an email. That is not a bug report,
it is a spam complaint, and complaints damage deliverability for every agent on the platform.

**Verify by sending, not by reading.** The test is that mail STOPS: opt a client out, dispatch a
non-greeting card, and confirm the recipient row says "unsubscribed" rather than reaching the mail
provider at all.

**Every path that records a "stop" goes through `SuppressAllAsync`.** As of 2026-08-17 that is the
newsletter footer link, the drip footer link, the one-click endpoint, the preferences page, and
SendGrid's `spamreport` / `unsubscribe` / `group_unsubscribe` events on **any** sender. `EmailOptOutAt`
is written in exactly one file; a grep that finds a second writer is a defect. Turning consent back
ON is narrower: only the person themselves may do it (preferences page, their own portal, or a signup
form they filled in). An agent may switch the newsletter off for a client but may not switch it back
on for someone who opted out of everything.

**Transactional mail is exempt and must stay exempt.** Password resets, invoice and document links,
overdue reminders, portal invitations and every billing email send regardless of `EmailOptOutAt`.
This holds because those senders never consult consent, and because the delivery tracker dispatches
only on `ecard | eletter | poll | didyouknow`. Do not add a consent check to a transactional sender,
and do not widen that dispatch list.

**Delivery statistics stay honest.** An unsubscribe is not a delivery failure. Recording one must
never set a recipient's status to Failed or write `BouncedAt` — the mail arrived and was read, and
the "Delivered" column exists to say so.

---

## 8. An erasure predicate must anchor on something the agent cannot delete first

Every `AgentDataEraser` / `ClientDataEraser` map entry deletes by a `WHERE` clause. If that clause
selects **through a parent row**, the entry is only as good as that parent's continued existence.

- **Membership in the map is not reachability.** The coverage tests assert a table is listed. They
  cannot assert the predicate still matches anything. `WebsiteFormSubmissionAnswers` was in the map
  for its whole life and erased nothing, because the agent's own Delete Form button removed the row
  the predicate selected through. Preview said 0, erase said 0, four audits agreed. (2026-08-17)
- **Before writing a parent-scoped predicate, name the delete action for that parent** and say why it
  cannot run first. If one exists and can, anchor somewhere else — the agent or client id directly,
  or a table only the erasers delete.
- **A real `ON DELETE CASCADE`, or a UI that refuses to delete a parent with children, is a valid
  reason** to keep a parent anchor. "No delete action exists today" is a reason with an expiry date;
  write it down at the entry so the person adding that action finds it.
- The same shape hits controllers: `RecurringInvoicesController.Delete` removed a schedule without
  its line items, which had no FK and no loaded navigation collection, so nothing removed them and
  both erasers lost them. Fixed in the same pass.

**Violation looks like:** a deletion request the product cannot honour, on rows that no longer say
whose they were. `AgentErasureOrphanTests.Nothing_the_agent_owned_survives_a_full_shred` is the
generic guard — it runs the agent-visible deletes first, then shreds, then requires every covered
table to be empty.

---

## Before calling a cross-cutting change "done"

**Fix by SURFACE, not by symptom.** When a change alters a shared convention — routing, auth, naming,
gating — it does not have one location. It has a fixed set of surfaces, and the work is not finished
until every one is checked. Enumerate them *first*, write the list down, and tick it off.

This exists because on 2026-08-08 the `/portal` migration was "finished" three times. The owner found
each remaining surface by clicking, after being told the class was closed:

| # | Surface | Found by |
|---|---|---|
| 1 | the routing decision itself | intended work |
| 2 | controller redirects (`Redirect("/Dashboard")`) | **owner, signing in** |
| 3 | 253 hardcoded links in views | **owner, clicking "Add Client"** |
| 4 | the never-shadowed list (5 client-portal routes 404ing) | found while fixing #3 |

Each fix was correct. Each was declared complete. The unit of work should have been "the /portal
migration", never "the login redirect".

**The surfaces, for a routing/URL change in this repo:**

```bash
# 1. the decision point
#    read ShouldRouteToPublicWebsite + IsNeverShadowedPrefix in src/IPRO.Web/Program.cs

# 2. controller redirects
grep -rn 'Redirect("/[A-Z]' src/IPRO.Web/

# 3. hardcoded links in views, and 4. the exemption lists
./ops/Test-NoBarePortalLinks.ps1

# 5. absolute URLs built for emails and background jobs
grep -rnE '"/(Clients|Dashboard|Newsletter|Website|Forms)' src/IPRO.Business src/IPRO.Email

# 6. live behaviour, signed in, on an AGENT host -- not just curl, not just signed out
./ops/Test-RoutingInvariants.ps1 -AgentHost <agent>.247advisers.com -AgentUser <u> -AgentPassword <p>
```

**Two rules that follow from it:**

- **Enumerate before fixing.** If you cannot list the surfaces, you do not yet understand the change
  well enough to declare any part of it finished.
- **"I fixed the instance you reported" is not "I fixed the bug."** Say which surfaces were checked
  and which were not. An honest "views not yet swept" is worth more than a confident "done".

## How to use this file

When a change touches routing, hosts, auth, or billing state:

1. Read the relevant rule above.
2. If your change needs an exception to one, that is the signal to stop — exceptions are what created
   every bug listed here. Change the rule, or find a design that doesn't need one.
3. Run `ops/Test-RoutingInvariants.ps1` against the deployed environment afterwards.
