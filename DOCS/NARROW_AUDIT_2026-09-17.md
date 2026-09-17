# Narrow pre-launch audit — 2026-09-17 (four days to launch)

Two independent review passes over everything built since 2026-08-28 (TODO 477 through 492), run
the afternoon of 2026-09-17 after `DOCS/AUDIT_RECONCILIATION_2026-09-17.md` had accounted for every
finding of the eight earlier review documents. One pass was adversarial (the tracking endpoints, the
ACS webhook, the send gate under a public-form flood, the Entra front, the alias hosts, Google OAuth
and the Data Protection ring, secrets, the privacy policy against the code); the other was correctness
under launch-morning load (the gate against the 15-minute claims, worker starvation, the pause path,
tracking tokens on a resume, the launch-day domain switch against the code, Google sync races, the
Email Activity screen). Every Critical and High below was re-verified by hand against the code
before it was acted on; the "checked and sound" list at the end is what both reviewers cleared.

**Headline.** One serious finding: 491 waited for a send slot *inside* the claimed send loops, and a
wait for the hour window is most of an hour. Everything else is small. The serious one and the small
ones worth closing before launch went out together as **493** the same day; the rest are recorded
below with a home.

## The serious one (491 waited in the wrong place)

On the defaults the subscription may send 100 an hour, 80 of them bulk once the reserve is taken.
Launch morning, with roughly 1,150 marketing messages queued (three newsletters, an e-card, a
Did You Know batch), the first 80 go in about four minutes and the 81st asks the gate to wait about
56 minutes -- inside one iteration of a loop whose 15-minute claim is refreshed only at the top of
each iteration. In order:

1. The claim goes stale ten to fifteen minutes into the wait. The next minutely pass re-claims the
   send (costing an attempt) and resumes its Queued rows, including the recipient the first run is
   still waiting to mail. When the first run wakes, it mails that recipient too, then its heartbeat
   fails and it stops. One duplicate per theft.
2. The second run parks in the same gate and is stolen the same way; after three thefts
   `RetireExhaustedAsync` marks the send **Failed** with hundreds of recipients still Queued and no
   path back.
3. Did You Know and drip steps have no heartbeat at all: a parked item is re-claimed every fifteen
   minutes and *each* re-claim sends, so one article could reach one client four times.
4. Five Hangfire workers, every blast job on the default queue: by 09:05 all five are parked and
   every other job (reminders, billing, domain automation, the calendar sync, the sweeps
   themselves) stops until one frees.
5. Transactional mail on the request path (password reset, registration, invoice send, portal invite,
   the website lead notification) awaited the same unbounded wait; Azure ends a request at 230
   seconds, so the person got an error page and no mail.

**Fix (493).** The gate never waits longer than `Email__MaxSlotWaitSeconds` (90). A slot that would
not free inside the bound is not waited for: `TryWaitForSlotAsync` returns the wait it declined to
make, and `AzureEmailService` answers **Deferred** -- transient, so the four blast loops take the
pause path they already had (Scheduled, claim released, running total written, no attempt spent,
resumed by the next minute's pass) -- with `IsDeferred` set, so Did You Know releases its claim and
counts nothing, drip counts nothing and removes the step-send row it minted, and both end their pass
because everything behind them would get the same answer. A web request gets the honest message
("Sending is paused for the email provider's hourly limit; the next slot opens in about N minutes")
through the `!result.Success` branches every caller already has. The hourly transactional reserve
goes 10 -> 20 (bulk keeps 80 of the 100), and `SendBulkAsync` -- the one send path 491 did not pace --
goes through the gate too. Ninety seconds covers the minute window (at most 60) and stays well under
Azure's cut-off; the hour window is never waited out in place.

**What launch morning looks like now.** The same 1,150 messages go at 80 an hour: the first 80 in
four minutes, then every blast pauses and resumes once a minute until the window rolls, finishing
around fourteen hours after the first send. Email Activity shows each of them as **In progress** with
its running total. Transactional mail keeps its 20 an hour and never waits more than the minute
window. Two advisers sending the same morning interleave.

## Findings, both passes, with dispositions

Severity as the reviewer gave it; "verified" means read in the code by hand before acting.

### Correctness under launch load

| Id | Sev | Finding | Verified | Disposition |
|---|---|---|---|---|
| A-1 | Critical | Gate wait inside the loop outlives the claim; duplicate, then Failed | yes | **493** (bounded wait, deferral) |
| A-2 | Critical | Did You Know has no heartbeat; re-sends every 15 min while parked | yes | **493** (deferral releases the claim, counts nothing) |
| A-3 | High | Drip enrollment has no heartbeat; same re-send | yes | **493** (deferral not counted, step-send row removed) |
| A-4 | High | Five workers parked in the gate; every other job stops | yes | **493** (no wait past 90 s) |
| A-5 | Critical | Request-path sends wait up to an hour; Azure 502s at 230 s | yes | **493** (Deferred answer; reserve 10 -> 20) |
| A-6 | Medium | Admin has its own gate; `SendBulkAsync` bypasses the gate | yes | **493** for the bulk path; Admin's own gate accepted (its volume is a handful of notices a day) |
| B-1 | High | A persistent 401 / bad key pauses a send forever, one warning a minute, never Failed | yes | **After launch.** Deliberate for now: during a provider outage a send that waits and resumes is better than one retired as Failed, which invites a manual resend and duplicates. What is missing is an alert; see "Still open" |
| B-2 | High | A paused poll leaves the survey on Sending; agent locked out of editing | yes | With B-1: during a real pause the survey *is* mid-send and the lock is right; the permanent case is B-1's |
| B-3 | Medium | A paused send reads "Scheduled, Sent 0" | yes | **493** (running total written on pause; **In progress** on the screen) |
| B-4 | Low | A timeout-class transient can duplicate one recipient (outcome unknown) | yes | Accepted: at-least-once, by design, one recipient |
| C-1 | Medium | Tracking token minted before the send but saved after; a crash orphans it | yes | After launch (one lost open on a crash-duplicate; the drip path already saves first) |
| C-2 | Medium | The newsletter roll-up writer marked the whole send row modified, racing the dispatcher's terminal and pause writes | yes | **493** (no Repository Update call in the recorders; tracked entities) |
| C-3 | Low | Recorders are not concurrency-safe but self-healing (full recounts) | yes | Accepted |
| D-1 | Medium | Plain-HTTP arrival on a brand domain redirects to the platform host (brand lost) | yes, masked | HTTPS Only is **on** for ipro-prod-web (read 2026-09-17): the front end redirects before the app sees it. Recorded in DOCS/14 |
| D-2 | Low | Alias 301s carry no HSTS / security headers | yes | After launch (move the alias middleware below the header middleware) |
| D-3 | Low | Checklist never mentions `App:PlatformDomains`; a mistyped alias shows the wrong page, not an error | yes | DOCS/14 step 5 now curls all four names |
| E-1 | High | A synced event deleted in Google is re-created 15 min later, forever | yes | **493** (`GoogleUnlinkedAt`; push skips unlinked follow-ups) |
| E-2 | Medium | Disconnect racing a running sync can resurrect the deleted copies | yes | After launch |
| E-3 | Medium | A broken connection logs an error every 15 min forever; adviser never told | yes | After launch (failure counter, deactivate, show on Profile) |
| E-4 | Medium | No overlap guard on the sync job; two runs duplicate Google events | yes | After launch (`DisableConcurrentExecution`); at launch scale a run cannot exceed 15 min |
| E-5 | Low | Reconnecting a different Google account pushes nothing (ids kept) | yes | After launch |
| F-1 | Low | Email Activity built the send list twice per request | yes | **493** |

### Adversarial

| Id | Sev | Finding | Verified | Disposition |
|---|---|---|---|---|
| H-1 | High | The tracking signing key falls back to the webhook secret, which rides in a query string App Insights stores unscrubbed | yes | **493** scrubs `secret` and `s`; code accepts `Email__TrackingSigningKeyPrevious`. **Owner:** set a dedicated `Email__TrackingSigningKey` on both apps (see below). Moving the webhook secret to a header: after launch (re-creating the Event Grid subscription) |
| H-2 | High | Public forms can spend the platform's hourly quota; the gate had no timeout | yes | **493**: bounded wait; 10 notifications an hour per website (leads still saved); form limits 10 -> 3 per 5 min per IP. Residual: a distributed flood across many sites still competes for the 100; a queued notification job is the durable answer (after launch) |
| M-1 | Medium | The signed redirect never expires and has no destination allowlist; any agent can mint one | yes | After launch (an expiry in the signature; an allowlist is not possible for adviser-authored links) |
| M-2 | Medium | Per-recipient tracking tokens in the path are not scrubbed from telemetry; the policy says they are | yes | **493** |
| M-3 | Medium | Account erasure deletes the Google connection without revoking the grant; the policy says it does | yes | After launch, first week (revoke before the eraser deletes the row) |
| M-4 | Medium | Data Protection keys were never configured (default ring on disk) | config yes | The ring is on the app's persistent storage (`WEBSITES_ENABLE_APP_SERVICE_STORAGE` unset on a built-in image = persisted); sign-ins and Google connections survive every deploy, which is the evidence. After launch: blob-backed ring protected by Key Vault |
| M-5 | Medium | The pixel is an unauthenticated writer; a replay re-runs the recorder | yes | **493** (a milestone already stamped skips the recorder; one indexed read per replay) |
| L-1 | Low | Verify the Entra exclusion is exact, not a prefix | probed | `/health/../AdminDashboard`, `%2e%2e` and `/health/version/../../AdminDashboard` all answer 401 from outside (2026-09-17). Closed |
| L-2 | Low | = D-1 | yes | as D-1 |
| L-3 | Low | The alias redirect re-emitted the decoded path (a `%0A` became a 500) | yes | **493** (`ToUriComponent`) |
| L-4 | Low | The sync job ignores entitlement and account state | yes | After launch |
| L-5 | Low | The policy says "the one calendar you choose"; there is no chooser | yes | **493** (wording: "your primary Google calendar"; Last updated 17 September 2026) |
| L-6 | Low | = C-1 | yes | as C-1 |
| L-7 | Low | `t` became a reserved first segment with no check against existing page slugs | yes | After launch (slug validation) |

## Owner-side, after 493 is live (five minutes)

1. **A dedicated tracking signing key**, on BOTH App Services, on the owner's go:
   `Email__TrackingSigningKey` = a new random value (32+ characters), and
   `Email__TrackingSigningKeyPrevious` = the current value of `Email__AzureEventWebhookSecret`
   (copied in the portal, so every click link sent since 2026-09-14 keeps redirecting). Clear
   `Previous` in a month. Both apps restart once.
2. Nothing else. The rate-limit and reserve changes ship in the code.

## Checked and sound (both reviewers, independently)

The gate's window arithmetic and locking; the two-arm claims, the heartbeat, the resume gates and
the recounts in all four dispatchers; the tracking signature (HMAC-SHA256 over kind, token and
target; fixed-time compare; `IsTrackable` applied identically at sign and verify time; the empty-token
trap at both ends; unknown tokens still get the GIF); the ACS webhook (fixed-time secret check before
the body is read, fails closed, only Bounced suppresses, replays idempotent); alias-host parsing
(exact, case-insensitive, trailing dot, both separators, `/.well-known` passes); `IsAppHost`'s
dot-prefix guard; the Google OAuth `state` (protected, agent-bound, ten minutes, `Forbid` on
mismatch) and the refusal of a grant without calendar scope; the Admin app (no anonymous actions
outside the error controller, SuperAdmin-gated Hangfire, per-request revalidation); secrets in the
tree (placeholders only); the rate limiter's forced-null real-IP header; `Jobs:RecurringDisabled`;
the Email Activity invoice carve-out in both directions.

## Still open after this document (carried to TODO and the reconciliation)

Post-launch, in rough priority: a queued lead-notification job with a global hourly cap (H-2
residual); an alert for a send paused more than an hour with nothing sent (B-1); the webhook secret
to a header (H-1); an expiry in the redirect signature (M-1); Google -- revoke on erasure (M-3),
failure counter and Profile notice (E-3), overlap guard (E-4), disconnect-vs-sync race (E-2),
reconnect to a different account (E-5), entitlement in the sync (L-4); the Data Protection ring to
blob + Key Vault (M-4); the tracking token saved before the send (C-1); HSTS on alias redirects
(D-2); reserved-slug validation for `t` (L-7); the Admin app on the shared budget (A-6).
