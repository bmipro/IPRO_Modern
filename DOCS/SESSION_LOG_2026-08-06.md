# Session Log — 2026-08-06

Nineteen commits. Three strands: the planned billing test, an unplanned custom-domain
investigation, and six audit findings closed.

The most useful outcome is not in the code. Two of the day's three strands began with me
asserting something I had not checked, and both times the user's question was what exposed it.
Those are recorded here in full, because the pattern is the reusable part.

---

## 1. Billing — the planned test, and two real bugs it found

The Silver → Gold → Platinum upgrade run went through PayPal sandbox end to end. It worked, and
it found two defects that only a live run could have surfaced.

**Upgrades charged a full extra cycle up front** (`339633f`). The subscription was created without
`start_time`, so PayPal began billing immediately instead of at the next cycle. The user spotted it
from a `-$60.00` line: *"Well. I dont think it worked."* Fixed by passing `start_time` for upgrades:

```csharp
if (startTimeUtc.HasValue && startTimeUtc.Value > DateTime.UtcNow.AddMinutes(5))
    payload["start_time"] = startTimeUtc.Value.ToUniversalTime()
        .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
```

**Tax was computed, invoiced, and then silently dropped** (`37111f3`). Found by the user asking
*"Are you properly passing on the Taxes on these packages with paypal?"* — a question, not a bug
report. `plan.taxes` is a **sibling** of `payment_preferences`, not a child; nested where it was, PayPal
ignored it. Invoices 005/006 had $7.80 of recorded-but-uncollected tax. After the fix the Platinum
upgrade collected **$33.85 = $29.96 + $3.89 HST**, matching the prediction exactly.

Both were found by probing the sandbox before writing the fix. Reasoning about the payload shape
would have produced a plausible wrong answer in both cases.

Also: `284c777` adds "plus applicable taxes" wherever a price is shown, and `9fa27c8` fixes the
package an agent appears to be on (Profile read `agent.PackageId` instead of the active billing row)
plus the public-slug/portal-route collision.

---

## 2. Custom domains — an error worth recording in detail

**What I claimed:** Azure App Service Managed Certificates never issue on this subscription, so every
agent binding a custom domain lands with a blocked site until a human issues a certificate by hand.

**What was true:** they issue fine. Three prove it — `www.4ipro.com` and `www.drhug.ca` (July),
`www.ouritems.ca` (that morning). All created by this automation, from one CNAME, with no human
involvement.

**How the error was made.** Managed certificates issue *asynchronously*. The PUT returns before Azure
has a thumbprint, so the first check sees nothing and reports "being issued; will retry on the next
check". That message was accurate. I read it as a permanent failure, generalised from two July
failures on the *platform* domains, and never checked a working agent domain — one `az` query away
the entire time.

**What it cost.** I hand-issued a Let's Encrypt certificate and bound it over the top of a perfectly
good auto-renewing managed certificate, replacing a self-renewing cert with a 90-day manual chore.
Then built an ops alert, a runbook, a script and a doc rewrite on that premise. All reverted in
`c0b15fd`; `www.ouritems.ca` rebound to the managed certificate.

**The tell I talked myself out of.** My own verification step read back `issuer=GeoTrust,
expires=02/06/2027` — exactly the managed certificate — and I dismissed it as a stale edge node. The
correct answer was on screen and I argued with it.

**What ended it.** The user asked *"why didn't we have this issue with www.4ipro.com... Was the code
changed at one point?"* That question, not any reasoning of mine, is what broke it open.

### The root-domain bug underneath

Separately, "Not forwarding" was shown for two domains that demonstrably forward. Six rounds of wrong
hypotheses — the domain parser, `WwwDomain`, nested forms, the Hangfire server, the cooldown — before
testing the actual HTTP request the app makes:

```
User-Agent: Mozilla/5.0   ->  301  http://www.ouritems.ca/
User-Agent: (none)        ->  403  Forbidden
```

**GoDaddy's forwarding service returns 403 to a request with no `User-Agent`, and `HttpClient` sends
none by default.** The check was being blocked as a bot and then honestly reporting what it saw.
Fixed in `fda2639`.

Two things made this take far longer than it should have. The check followed the whole redirect chain
to our own site over HTTPS from inside App Service, so any hiccup there read as "not forwarding" —
now it reads the first hop only (`9fe090c`). And the diagnostic logging I added to solve it was
`LogInformation`, which App Insights does not capture by default, so it was invisible in the only
place it mattered (`b955785`).

### Domain work that stands on its own

- Status panel speaks English instead of `BindingPending`, in two readable rows (`1fad69b`, `ab00cc1`)
- Add-domain screen presents **both** registrar steps as one visit, with per-registrar menu paths
  (`e2e618b`) — forwarding used to be reported as a failure after the agent thought they were done
- Bound domains re-checked every 30 minutes; they were previously excluded from the job forever, so
  forwarding status froze at bind time (`b819bb0`)
- "Retry" renamed "Check now", cooldown 2 min → 15 s, and it now reports what it found (`446684b`)

### The apex question, answered

`ouritems.ca` cannot work from the `www` CNAME alone. A zone apex cannot hold a CNAME — the standard
forbids it alongside the `SOA` and `NS` records every zone carries. So the bare domain needs its own
record: registrar forwarding, an A record, or nameserver delegation.

Recommended: **registrar forwarding**. An A record would hard-code `40.89.19.0`, a *shared* Azure
front-end IP — if Microsoft moves it, every agent's bare domain breaks at once, silently. Nameserver
delegation is the only true one-action option but would take over the agent's email (`ouritems.ca`
has a `_dmarc` record pointing at `onsecureserver.net`), and killing an accountant's email is worse
than a bare domain that does not resolve.

**One CNAME remains the entire agent-facing process for `www`.** Verified on four domains.

---

## 3. Audit — six High findings closed, all four Criticals now closed

| | Start of day | End of day |
|---|---|---|
| Critical | 4 open | **0** |
| High | 14 open | **8** |

C-3 was already fixed on 2026-08-05 but still listed as open, which made three Criticals look like
four outstanding. Corrected.

**H-4 + H-5 — startup races that abort both apps** (`5c3b85b`). Taken first: the only open findings
that take the whole platform down, and this class already caused a real outage on 2026-07-29.

- `EnsureTableColumnAsync` was check-then-ALTER; the app that ALTERs second got MySQL **1060**,
  unhandled, SIGABRT. Now caught. Found while fixing: `EnsureUniqueIndexAsync` caught **1062**
  (duplicate data) but not **1061** (duplicate index name) — same race, index-shaped.
- Three seeders never got July's `SeedGuard`. `PackageEntitlementSeeder` was the dangerous one:
  `BillingRules` has no unique index on `PackageName`, so the race *duplicates* rows, and
  `ToDictionaryAsync` then throws on the duplicate key. The rows persist — **one race becomes a
  permanent boot crash-loop no restart clears.** Two defences: the lock, and a duplicate-tolerant
  read so a database that has *already* raced can still boot.

**H-1 — cancellation failure was invisible** (`f6c6ad5`). `CancelPayPalSubscriptionAsync` returned
`Task`, so nothing could observe it; the local row was marked Cancelled and `true` returned
regardless; and `BillingController` discarded even that and always said "Subscription cancelled." An
agent whose cancellation PayPal refused was told it worked and kept being charged. Now returns
`bool`, and on failure the row is deliberately **left Active** — showing the truth beats a false
reassurance, and a retry can still succeed.

*Audit correction:* it said the agent-delete guard "can never trigger". The guard is correct and does
abort; it was being fed a constant. The fix was the return value, not the guard.

**H-9 — recurring invoice numbering** (`f6c6ad5`). Numbers were generated against *committed* rows
while the batch stayed unsaved, so two schedules for one agent got the same number — and L-12's
unique index (our own hardening) then failed the whole batch, stopping recurring invoicing for
everyone. Now saves per schedule, and detaches on failure so one poisoned row cannot fail every save
after it.

**H-3 — "Resume payment" made a one-time charge** (`23b94ec`). It created a PayPal *order*, so the
agent paid one month and no subscription ever existed. Same class as C-2 through a different door.
Now voids the stale attempt and delegates to `CreateSubscriptionAsync` — the real subscribe path, so
promo codes and setup fees cannot drift from a second copy. Trade-off accepted deliberately: the
agent approves at PayPal rather than paying in one click, which is the price of the button's name
being true.

**H-2 — Support admin → agent takeover** (`eae2575`). `Agents/Edit` is gated by `AdminAccess`
(= `RequireAuthenticatedUser()`), and writes `Email`. Change it, then use public password reset —
straight around the SuperAdmin gate on `ResetPassword`. The audit named Email; reading
`ApplyEditModel` it is **five** fields: `UserName`, `Email`, `PackageId`, `IsActive`,
`MustChangePassword`. All five now require SuperAdmin, blocked at field level so Support can still
fix addresses and phone numbers. Attempts are reverted, reported, and audit-logged.

---

## Still open

**H-6, H-7, H-8 — mass duplicate email.** All four dispatchers claim work read-then-write (a whole
newsletter audience can send twice); a crash mid-dispatch strands sends in `Sending` forever; and
email-then-mark plus 10 Hangfire retries can produce up to 2,000 duplicate invoice emails from one
transient DB error. One coherent block, and the highest remaining risk to real clients.

**H-10 to H-14 — erasure and data leaks.** Portal documents leak on client delete; form answers
survive erasure (visitor PII); gallery blobs orphaned; cross-agent artwork destroyed; article image
replace breaks newsletters already delivered.

**Not yet exercised at runtime.** Today's six audit fixes build clean and are verified against the
code, but none have been run. H-1 and H-3 touch real money and H-3 changes what an agent sees — a
sandbox pass on both is worth doing before a real agent clicks "Resume payment".

---

## Operational notes

**GitHub Actions was in `major_outage`** for much of the afternoon, throttling webhooks to ~15%. A
push would land in git and produce **no run at all**. The workaround: both workflows declare
`workflow_dispatch`, and the REST API was operational, so `gh workflow run <file> --ref main`
bypasses the throttled path entirely. Worth remembering — if a push produces no run, dispatch it
rather than re-pushing.

**Don't leave a deploy watcher armed against a superseded commit.** One set up early in the day was
still waiting to re-run `c0b15fd` after three newer commits had shipped. It happened to fail; had it
succeeded it would have rolled production backwards.

---

## The pattern worth keeping

Three times today I stated something confidently that one command would have falsified: that managed
certificates never issue here, that the domain check was correct, that the two-line layout fix was
done. Each time the user found it from the screen.

The common shape: **I reasoned from code and treated the conclusion as verified.** The corrective is
not more caution in wording — it is running the cheap check first. `curl` against a working domain.
`az` against a domain that already succeeded. Looking at both rows, not one.

And the counter-example from the same day: the two billing bugs were found precisely *because*
the sandbox was probed before the fix was written. The same instinct, applied earlier, would have
saved most of the afternoon.
