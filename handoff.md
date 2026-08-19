# Handoff — 2026-08-18

Written at the end of the session. Companion to `DOCS/TODO.md` (the durable backlog) and
`DOCS/AUDIT_RECONCILIATION_2026-08-17.md` (per-finding truth). Where they disagree with this file,
they win — this is a snapshot, they are maintained.

---

## 1. The main goal

Close out the **audit backlog's worst findings** and get them genuinely live, so IPRO can move from
"hardening" to launch. Concretely, today's goal was:

1. Finish **JOBS-1**, the last CRITICAL still open (drip campaigns and unsubscribed clients).
2. Get it — and the five HIGH fixes finished just before it — **merged, deployed and verified in
   production**, not just committed.
3. Run the one remaining manual acceptance test, the **WEB-H-1 buyer pass**, which needs a human at
   a real PayPal checkout.

Standing rules that shaped how the work was done: **branch before deploying** (merging to `main` IS
the deploy, and that merge is Bahman's call); verify a deploy with **`/health/version`** matching the
pushed SHA, never `/health`; owner performs UI-side and production-money actions himself.

---

## 2. Current status

### Shipped and live

**`main` is at `51b5f24`** — the merge of `fix/audit-high-five`. Both apps confirmed serving that
exact SHA at `/health/version`. Test suite **146/146**.

That merge carried six fixes:

| Finding | What it was |
|---|---|
| A2-H6 | Both deploy workflows now share one concurrency group, so deploys serialize instead of racing |
| WEB-H-1 | PayPal now returns the buyer to the host they started on, not the canonical host (code done; **live proof still pending** — see §4) |
| ADMIN-2 / BILLING-9 | Checkout fails closed when a package's editable price has drifted from the price its PayPal plan was created at; plan re-sync no longer orphans plans |
| A2-H8 | ~30 duplicated schema-repair functions extracted into one shared `IPRO.DataAccess.StartupSchemaRepair` |
| A5-H11/H12/H14 | Blob family safe subset: a file is only deleted when nothing in the database references it; the orphan sweep is report-only |
| **JOBS-1** | **The last CRITICAL** — see below |

A quiet but real bonus: both apps booting cleanly on `51b5f24` is the production proof that the A2-H8
schema-repair extraction is safe. That was the riskiest change in the batch.

### JOBS-1 in detail (the headline)

An opted-out client could still be **enrolled** into a drip campaign. No mail actually went — the job
cancelled at the first due send — but the campaign screen showed a running enrollment for someone who
had asked to be left alone, for up to the first step's full delay, and the agent was never told.

Closed in three parts:

1. **Enrollment gate** — `CampaignsController` filters suppressed clients on *both* paths (category
   bulk-enroll and single client) via `EmailConsentService.IsSuppressed`, the single consent decision
   point (INVARIANTS rule 7). The agent is told how many were skipped and why.
2. **Truth sweep** — `EmailConsentService.CancelSuppressedDripEnrollmentsAsync()` cancels Active
   enrollments whose client is already suppressed. `DripCampaignJob` runs it at the top of every
   hourly tick, so a stale row survives at most an hour instead of weeks.
3. **Spam-complaint half** — already closed earlier by LB-2 (`RecordDripStepEventAsync` →
   `SuppressAllAsync`). **Verified in code this session rather than assumed.**

Pinned by 5 tests in `DripEnrollmentConsentTests`. Also verified by hand through the real UI on the
merged build: enrolling an opted-out client is refused with *"1 client was not enrolled because they
have unsubscribed from email"*, and a real unsubscribe cancels the live enrollment the same second.

### Audit position now

The audit's **only CRITICAL is closed**, and all five actionable HIGHs are closed. What remains is
HIGH-and-below work that was never started, plus the follow-ups in `DOCS/TODO.md`.

### New defect found today (not yet fixed)

**Signup says "Verify code is incorrect" when the code typed is exactly the one on screen.** The
expected code lives only in session (30-minute idle timeout); when it expires, the check takes the
same branch as a genuinely wrong code (`AccountController.cs:313`). The form then becomes permanently
unsubmittable and blames the user. Compounded by `POST:/Account/Register` being rate-limited to
**5 per hour per IP** — a few retries and you are locked out for an hour.

Proven locally on the merged build: correct code + live session → no verify error; the *same* code with
the session cookie dropped → the error. This hits **real buyers**, not just testing: anyone who spends
over half an hour on the form. Full write-up in `DOCS/09_TROUBLESHOOTING.md`; tracked as TODO item 12.

---

## 3. Failed attempts and course corrections (read this part)

Recording these because the pattern matters more than the individual mistakes.

**a) I called the verify-code bug "host-specific" — it was not.** My first probe failed on the agent
domain and passed on the canonical one, so I reported a host-specific failure. Wrong: the failing
probe's GET and POST straddled a long pause, so the session had simply expired. Re-run back-to-back,
both hosts behave identically. *Lesson: when a bug looks host-specific, control for elapsed time
before believing it.*

**b) I told Bahman a failed submit rotates the code; he said no, and he was right.** Both observations
were true in their own context — curl sees a new code on every server re-render, but in a real browser
a resubmit usually never reaches the server (HTML5 `required` blocks it and the digits stay put). I
was reasoning from curl and describing it as browser behaviour. *Lesson: test the surface the user is
actually on.*

**c) The console-override instruction for the hidden QA package was a trap of my own making.** The
injected `<option value="7">` is client-side only, so it vanishes on any re-render — meaning one
fumbled field puts you in an unwinnable loop. Worse, the buyer pass never needed it: WEB-H-1 only
requires PayPal to return to the right host, which any normal package proves. **Use IPro Silver from
the normal dropdown.** The hidden QA Daily plan is only needed to restart the separate daily-billing
QA clock.

**d) My own JOBS-1 fix had a bug the first test run caught.** The enrollment gate set a specific
warning, but the generic zero-enrolled fallback ("that client is already active in this campaign")
then overwrote it — the agent would have been told the wrong reason. Fixed before commit: the
fallbacks now defer to any existing warning.

**e) I rate-limited myself out of production.** Repeated diagnostic POSTs to `/Account/Register` hit
the 5/hour limit (HTTP 429). Harmless, but it cost time and it is how I discovered the limit. If the
page behaves oddly during the buyer pass, this may be why — wait, or switch networks.

**f) A stale conclusion nearly shipped.** I was about to report the host-specific finding as fact.
Re-testing before writing it up is the only reason it did not end up in the docs as a false lead.

---

## 4. The next 3 immediate steps

### Step 1 — Complete the WEB-H-1 production buyer pass (Bahman, ~5 min)

The last acceptance test for the fixes already live. Sandbox money only; production PayPal runs in
sandbox mode.

1. Hard-reload `https://bahmanmotamed.247advisers.com/Account/Register`.
2. Fill it **top to bottom in one sitting** — the session dies after 30 minutes idle.
3. Pick **IPro Silver** from the normal dropdown. **No console trick.**
4. Type the 4 digits **last**, immediately before submitting.
5. Approve on PayPal sandbox.

**Pass looks like:** you land back on **`bahmanmotamed.247advisers.com`** (not `app.iproadvisers.com`),
and Billing shows the package Active. **Fail looks like:** bounced to a login page on the canonical
host — if that happens, stop and report it; the fix regressed.

If anything errors, **reload the page** before retrying. Never resubmit an errored page. Remember the
5-attempts-per-hour ceiling.

### Step 2 — Fix the misleading expired-session message (TODO item 12 / task #443)

Small and safe, on a branch. Give the null/blank case its own message — *"Your session timed out — the
form was refreshed, please enter the new code shown"* — instead of accusing the user of mistyping.
Consider a longer idle timeout for the signup flow specifically. This is a real conversion defect: a
prospect who takes their time filling the form is told their captcha is wrong, and can lock themselves
out in five clicks.

### Step 3 — Restart the QA daily-billing clock (TODO items 367–369)

Only after Step 1. A fresh signup on **QA Silver Daily (package id 7, hidden — this one does need the
console override)** from the agent host, then day 3 verify the overnight charge and upgrade to Platinum
Daily, day 4 cancel and delete and confirm cancellation directly on PayPal. Doing these legs from the
agent domain also exercises WEB-H-1's upgrade and cancel paths.

---

## 5. Also open, not in the top 3

- **Task #393 / LB-2 remainder** — Bahman's resend test to read the SpamAssassin score.
- **Staging environment decision** — scheduled reminder fires **2026-08-25 09:00**. Costing is done;
  the open question is data (a real copy carries PIPEDA exposure) not cost.
- **#394** — PayPal charged ~6 times over 2 days while IPRO recorded 1 event and 0 invoices.
- **Newsletter test-send host leak** (`NewsletterController.cs:606`) — test sends bake the current
  request host into real delivered email; should use the canonical base.
- **Money-in-flight reconcile sweep** — buyers who approve at PayPal then close the tab leave an
  activated subscription against a Pending local row.
- Full list with context in `DOCS/TODO.md`.

---

## 6. Environment notes

- Local dev: `ops\Start-LocalEnv.ps1` brings up MySQL + Azurite; apps at `localhost:5100` (Web) and
  `localhost:5200` (Admin). Both were exercised today and left stopped.
- Local test agent created during JOBS-1 verification: **agent id 11** (`drip.tester@example.test`),
  with client **Casey Optout** (id 3) and campaign **Welcome series** (id 1). Local database only —
  nothing in production.
- Production has **no test agent** since the 2026-08-15 cleanup; the sole real agent is
  **BahmanMotamed #12**. That is why the buyer pass has to be a fresh signup.
