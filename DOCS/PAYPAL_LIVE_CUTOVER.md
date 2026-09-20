# PayPal: sandbox to LIVE -- the cutover runbook

## DONE -- Sunday 20 September 2026

Production takes real money since about 12:30 p.m. Eastern. What exists now:

- **The live app at PayPal:** "IPRO Advisers", on the business account of I Pro Advisers Inc. (c/o
  Global Business Solutions). Card statements read `PAYPAL *IPROADVISER`. Subscriptions ticked, Payouts
  unticked (the owner's own transfers to the bank do not use it). Webhook id `8C545407P7783292M`, the
  six events, target `https://app.iproadvisers.com/billing/webhook`.
- **Both apps:** `PayPal__ClientId` 82 characters, `PayPal__ClientSecret` 80, `PayPal__WebhookId` 17,
  `PayPal__IsSandbox=false` (checked by name and length, never by value).
- **Live plans** (monthly / annual): Silver `P-77L71201W6799880TNKYAROA` / `P-5E662034RL247084VNKYAROI`;
  Gold `P-57753849B3441680HNKYASEY` / `P-1K049969TV027583HNKYASFA`; Platinum
  `P-6VW125527U009062BNKYASLY` / `P-9X6941285R639304KNKYASMA`. The three QA daily packages were NOT
  synced and must not be.
- **The owner's two demo accounts** (his own and MichaelTran) stay forever on code `OLD_DEMO` (100% off
  forever plus the setup fee, Platinum, 2 of 2 used): comped Active with no PayPal subscription, invoices
  IPRO-2026-000025 and -000026 at $0.00. The reconciliation job skips rows without a subscription id.
- **The real-money check:** code `LIVECHECK` (95% off one cycle, Gold, 1 of 1 used), account "Boby
  Moore": invoice **IPRO-2026-000027, $3.39, Paid**; subscription `I-06NA6SDTLYJW`; sale
  `582843411U3731847`; settled in CAD ($3.39 gross, $0.40 fee, $2.99 net). PayPal's event log showed
  sale completed, activated and cancelled, each **Success**. The owner refunded it at PayPal the same
  day; the test account is deleted WITH the financial tick, so invoice number 000027 is a gap with this
  explanation. The cancelled row PayPal lists at "US$0.00" is the subscription profile's
  cancellation, not a payment.
- **All four sandbox-era codes are Inactive**; the owner starts fresh with new codes.
- **Found by the test and fixed the same day (503):** the Billing page printed the discounted price
  as the next charge of a one-cycle code.

Left for the owner, none urgent: delete the Boby Moore account (eye icon -> Preview Erasure, tick the
financial records); untick "Package is active" on the three QA daily packages; set
`PayPal__BaseUrl` on ipro-prod-admin to `https://api-m.paypal.com` (display only).

The runbook below is kept as it was used, for the next time a PayPal environment changes.

Planned for **Sunday 20 September 2026, first thing**, the day before launch. Written 19 September from
the code as it stands (`PayPalBillingService`, `PayPalSettings`, SuperAdmin's PayPal Setup, Packages and
Agents screens), not from memory.

**Who does what.** Every PayPal, Azure-portal and SuperAdmin step is the OWNER's. The assistant never
sees a secret: it checks settings by NAME AND LENGTH only, reads the one non-secret flag
(`PayPal__IsSandbox`), watches `/health/version`, and reads the container log. No deploys while the
cutover is in progress (a deploy restarts the site under a half-finished change).

**What "live" changes.** Four settings on each app, and nothing in the code. `PayPal__IsSandbox=false`
makes every call go to `https://api-m.paypal.com` instead of the sandbox host. Plan ids belong to one
PayPal environment, so every plan is created again in live (one button per package). Production has
run in sandbox since day one: **no money in this system has ever been real**, and every account in the
database today is a test account (owner, 27 August and again 19 September).

## Before we start (5 minutes)

- [ ] Both hosts answer the same build at `/health/version`; tree clean; no deploy in flight.
- [ ] The morning follow-ups email reached **bobmoore** at about 7:05 (the follow-up dated 20 September
      was added on the 19th so that a brand-new account is part of this morning's run).
- [ ] Keep list agreed: **the owner's own account and MichaelTran** (demos). Everything else goes.

## Stage 1 -- clear out the sandbox-era accounts, WHILE STILL IN SANDBOX

Why first: a sandbox subscription can only be cancelled at PayPal while the app still holds sandbox
credentials. Left behind, each one is an "Active" row the hourly reconciliation cannot read after the
switch: it logs an error for it every hour (it never revokes access on an answer it cannot read, so
nothing breaks -- it is noise), and the Revenue report would carry test money beside real money.

1. [ ] SuperAdmin -> **Refunds**: if any row is still Pending for a test account, mark it Waived --
       an account that is owed a refund refuses to be deleted, by design.
2. [ ] SuperAdmin -> **All Agents**: for every account NOT on the keep list (bobmoore, generictest, the
       rest): **Delete** -> on the erasure preview tick **"Also erase the financial records above -- QA/test
       agents only"** -> confirm. This cancels the sandbox subscription at PayPal, removes the account's
       invoices from the ledger, and deletes its files.
3. [ ] The two KEPT accounts: in each one's portal -> **Billing -> Cancel subscription**. They keep access
       to their paid-through date; Stage 4 puts them on a comped plan that never touches PayPal.
       Their old sandbox invoices stay in the ledger -- see "Left over" at the end.
4. [ ] SuperAdmin -> **Promotion Codes**: switch off or delete the test codes (FINALTEST and any other).
       A launch code is created fresh AFTER the switch, so its plan is born in live.

Assistant, read-only: confirms from outside that the deleted accounts' sites answer "not found".

## Stage 2 -- the live app at PayPal (developer.paypal.com, toggle **Live**)

1. [ ] **Apps & Credentials -> Live -> Create App** (type Merchant), named e.g. "IPRO Advisers". It must
       belong to the business account that is to receive the money.
2. [ ] Note the **Client ID** and the **Secret** (they go from PayPal's page to Azure's page; never into
       this chat, a file or an email).
3. [ ] In that app: **Add Webhook** -> URL **`https://app.iproadvisers.com/Billing/Webhook`** -> tick exactly
       these six events (they are the six the code handles; anything else is ignored):
       - `BILLING.SUBSCRIPTION.ACTIVATED`
       - `BILLING.SUBSCRIPTION.CANCELLED`
       - `BILLING.SUBSCRIPTION.SUSPENDED`
       - `BILLING.SUBSCRIPTION.EXPIRED`
       - `BILLING.SUBSCRIPTION.PAYMENT.FAILED`
       - `PAYMENT.SALE.COMPLETED`
4. [ ] Note the **Webhook ID** PayPal shows for it (17 characters in sandbox; the live one is similar).
5. [ ] In the live business account, confirm it can receive **CAD** and that **subscriptions / recurring
       payments** are enabled for it (a brand-new live app sometimes needs this switched on).

## Stage 3 -- the four settings, on BOTH apps (Azure portal -> Environment variables)

On **ipro-prod-web** and then on **ipro-prod-admin** (SuperAdmin's Sync plans and Refunds talk to PayPal
too), edit -- do not add duplicates:

| Setting | New value |
|---|---|
| `PayPal__ClientId` | the live Client ID |
| `PayPal__ClientSecret` | the live Secret |
| `PayPal__WebhookId` | the live Webhook ID |
| `PayPal__IsSandbox` | `false` |
| `PayPal__BaseUrl` (admin only; display on the PayPal Setup page, not used for calls) | `https://api-m.paypal.com` |

**Apply** on each app; each restarts once (about a minute).

Assistant, read-only, after each Apply: the four names are present with plausible lengths;
`PayPal__IsSandbox` reads `false` on both; both hosts are back on the same build at `/health/version`.

- [ ] SuperAdmin -> **PayPal Setup**: the page now says Live, every setting reads Configured, and the
      expected webhook URL matches Stage 2.

## Stage 4 -- plans, a launch code, and the two demo accounts

1. [ ] SuperAdmin -> **Packages**: on **IPro Silver**, **IPro Gold** and **IPro Platinum** press **Sync PayPal
       plans**. Each creates a live product with a monthly and an annual plan and stores the new ids; a
       sync that works is also the proof that the live credentials are right. (Broker Package has no
       recurring price and needs none.) The Packages screen must show no price-divergence warning.
2. [ ] Promotion codes restricted to a package have their cached plan ids cleared by that package's
       sync and are re-created in live the first time they are used. Create the launch code(s) now.
3. [ ] The two kept accounts: create a promotion code that is **100% off the recurring price with no
       end, and 100% off the setup fee**, restricted to their package -- that is the "fully comped" path,
       which activates without calling PayPal. A code is tied to ONE package, so if the two accounts are
       on different packages it takes one code each. In SuperAdmin -> All Agents -> Edit, put the code on
       the account; then in its portal -> Billing, subscribe again. Expect: no PayPal page, no charge, no
       invoice email for 0.00. The Billing page will say the new plan starts when the cancelled one's
       paid period ends -- that is the designed behaviour ("never charged twice for the same days"), and
       access is continuous across it. A code can be used once per account; that is enough here.

## Stage 5 -- the real-money check, together (about 15 minutes)

One real sign-up with a real card or PayPal account that is NOT the receiving business account.
Cheapest honest path: **IPro Silver, monthly** (the setup-fee waiver is active), or a launch code.

1. [ ] Register a new adviser -> PayPal's page says the real business name and the right amount in CAD
       -> approve.
2. [ ] Back on the site: Billing shows **Active**; the invoice email arrives with a real PayPal
       transaction id; the welcome email arrived; the site is live.
3. [ ] PayPal (live) -> the webhook's **event log**: `BILLING.SUBSCRIPTION.ACTIVATED` and
       `PAYMENT.SALE.COMPLETED` each show **Success** (PayPal's live log words it so; it is our 200). (A **401** here -- that is what
       `/billing/webhook` answers a bad signature with, not 400 -- means the Webhook ID in Azure is
       not the id of THIS webhook: the signature check uses it.)
4. [ ] In the new account's portal -> Billing -> **Cancel**. PayPal shows the subscription cancelled; the
       event log shows `BILLING.SUBSCRIPTION.CANCELLED` with 200; the account keeps access to its
       paid-through date. A cancelled MONTHLY plan mints no refund row -- that is the rule (DOCS/22:
       access is honoured to what was paid for; only an ANNUAL plan cancelled before its month-10
       crossover puts a Pending row in SuperAdmin -> Refunds).
5. [ ] Getting the test money back is done AT PAYPAL: business account -> Activity -> the payment ->
       **Refund**. No code in this system moves money back; SuperAdmin -> Refunds is the ledger of what is
       owed, worked by hand (Mark refunded, with PayPal's refund transaction id, or Waive). If the test
       left a row there, close it the same way.
6. [ ] Delete the test account afterwards -- WITH the financial tick (it was a test; a refunded charge
       should not sit in the Revenue report), after step 5, and after any Refunds row is closed (an
       account that is owed money refuses to be deleted).

Assistant, read-only, throughout: the container log for billing errors; `/health/version`; the ACS
metrics for the invoice and welcome emails.

## If something goes wrong

- **Sync plans fails, or PayPal answers 401:** the Client ID and Secret are not a pair from the same
  LIVE app, or `PayPal__IsSandbox` is still `true` on that app. Nothing has been charged. Fix the
  setting and Apply again.
- **Checkout works but the account never turns Active:** the webhook is not arriving or is failing
  its signature check (Stage 5.3). The hourly job does not activate a subscription; the webhook does.
- **Full rollback:** put the four sandbox values back on both apps (`PayPal__IsSandbox=true` and the
  three sandbox values, which the owner holds) and press Sync PayPal plans on the three packages
  again. Any live subscription created in between must be cancelled at PayPal by hand first.

## Left over after the cutover (not blockers)

- The two kept accounts' sandbox-era invoices remain in the ledger and therefore in the Revenue
  report's history. The plan of record (27 August) is one FK-safe purge of the sandbox ledger by the
  owner, in SQL, mirroring `AgentDataEraser.FinancialMap`; with every other test account deleted with
  the financial tick, what is left is those two accounts' rows only. Decide after launch.
- The hourly `subscription-billing` job's PayPal reconciliation logs an error for any Active row whose
  subscription id PayPal (live) does not know. After Stage 1 there should be none; a line naming one
  means an account was missed.
- Then the **DNS switch** for the four public names (`DOCS/14_BACKUP_AND_RELEASE_CHECKLIST.md`,
  "Launch-day domain switch"): independent of PayPal; rollback is the old records.
