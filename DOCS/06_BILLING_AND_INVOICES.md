# Packages, Billing, PayPal, and Invoices

## Access Without An Active Subscription

Portal features require an active subscription, or an active trial (trials are by invitation from iPro). The Billing page itself always shows the current state:

- **Trial active**: an info banner shows the trial end date. Subscribing at any point keeps access uninterrupted.
- **Cancelled, still paid up**: a banner reads "Subscription cancelled — you keep (your package) until (date)". Everything keeps working until that date and you are not billed again. If you subscribe again before then, the new plan starts when that paid period ends, so you never pay twice for the same days.
- **No active subscription or trial (including a lapsed trial's grace period)**: a banner reads "Your access is currently limited to this page", and the other portal pages send you back to Billing until a package is chosen and paid for.

While access is limited, your own account stays open:

- **Profile** (including your photo) and the portal colour swatches at the foot of the left menu.
- **Change Password** and **Two-Step Sign-In**.
- **Logout**.

Billing also shows a **Your data stays yours** card with three downloads that work without subscribing: **Clients**, **Invoices and quotes** and **Website leads**. After you cancel, your records are kept for 30 days.

## Choose a Package

1. Select **Billing** in the Agent Portal.
2. Review the active packages, prices, and included functions.
3. Choose monthly or annual billing.
4. Continue to PayPal.
5. Complete payment and return to IPRO.

The first payment can contain a one-time setup fee. Recurring payments contain only the recurring package amount and tax.

No setup fee is charged:

- when the package's setup fee is waived (the package then shows no setup fee);
- when you keep your own website instead of an iPro website;
- when you are finishing a plan change you scheduled (see **Upgrade or Downgrade**);
- when you change plan while your subscription is active.

## Resume or Cancel an Unpaid Checkout

If PayPal was closed before completion:

1. Open **Billing**.
2. Find **Unpaid Checkout**.
3. Click **Continue Payment** to restart checkout.
4. Or click **Cancel Checkout** to abandon it and choose another package.

## Upgrade or Downgrade

1. Open **Billing**. Your package is marked **Current Plan**.
2. Under the package you want, click **Upgrade Monthly** or **Upgrade Annually**, or **Downgrade Monthly** or **Downgrade Annually**.
3. What happens next depends on the change, as below.

### Upgrade

There is no review screen: you go straight to PayPal to approve the new subscription. You pay the prorated difference for the rest of your current billing period up front, and the new package's regular price starts on the date you had already paid up to. The new package is yours once PayPal confirms the payment. If you close PayPal without approving, you stay on your current package, and Billing offers **Continue Payment** or **Cancel Checkout**.

### Downgrade or Change of Billing Term

A downgrade does not happen at once. It is scheduled for the end of the period you have paid for, and Billing shows a **Scheduled package change** banner with the date.

- To change only the billing term of your package, click **Switch to Annual Billing** or **Switch to Monthly Billing** under your current plan. It is scheduled the same way, for the end of the paid period. It is not offered while another change is scheduled.
- To undo a scheduled change, click **Keep My Current Plan** on the banner. Your package and billing continue unchanged.
- On an annual plan, a downgrade also offers **Switch now** with an estimate of free months: you move to the cheaper package today, the unused part of your year becomes free time on it, and monthly billing starts only after that free time ends. You approve the new subscription at PayPal; nothing is charged today.

On the day a scheduled change takes effect, your current subscription ends and you are emailed. Your account then waits at the Billing page, with everything kept, until you approve the new plan at PayPal: Billing reads "Your plan change to (package) is ready to finish". Click **Subscribe Monthly (as scheduled)** or **Subscribe Annually (as scheduled)** under that package, or the other term if you have changed your mind. No setup fee applies.

## Failed Payments

When PayPal reports a failed payment:

- The Agent Portal shows a **Payment failed** banner with the amount due. Its **Fix Billing** button opens the Billing page; there is no retry button in iPro.
- You receive an email about it.
- PayPal tries the payment again automatically over the next few days, and your account keeps working meanwhile. To avoid an interruption, update your payment method in your PayPal account.
- If PayPal's retries fail and it suspends the subscription, your access is limited to the Billing page: choose a package there and subscribe again, or contact support for help.

iPro support can also see the failed payment on your account. Scheduled jobs and PayPal webhooks reconcile recurring subscription status.

## Cancel a Subscription

1. Open **Billing**.
2. Click **Cancel Subscription**.
3. Confirm the action.
4. Review the resulting access and end-date status.

You are not billed again, and the Billing page then reads "Subscription cancelled — you keep (your package) until (date)": you keep everything you paid for until that date. Subscribing again before then starts the new plan when that paid period ends. Download or print the invoices you want to keep before your access ends.

## View and Print an Invoice

1. Open **Billing**.
2. Open the invoice.
3. Review the package and billing cycle, the bill-to details, each line with its tax, the tax summary, subtotal, tax, total, payment received and balance due, the status (Paid, Unpaid or Payment failed) and the PayPal transaction.
4. Select **Print / Save PDF** (or use the browser print command).
5. Select **Save as PDF** for a PDF copy.

Invoices are formatted to fit one Letter-size page. Invoice numbers use the `IPRO-YYYY-000001` pattern. The supplier block (company name, address, GST/HST registration number, billing email and website) is what SuperAdmin -> Company Details holds; the bill-to block is frozen at the time the invoice was issued.

## Refunds and Credit Notes

When iPro refunds you money -- for example the unused part of an annual plan when you cancel, or a payment PayPal took after your subscription had ended -- you receive a **credit note** by email on the day the refund is made. It shows:

- its number (`CN-YYYY-0001` pattern) and date
- the invoice it credits
- the amount credited before tax, the tax returned with its region and rate, and the total credited
- PayPal's refund id and the payment it was refunded against
- iPro's company details and GST/HST registration number

PayPal shows the refund right away; a card can take a few business days. Keep the credit note with your invoices: it reduces what you paid on the invoice it credits. Refer a Friend rewards get the same kind of credit note, on your **Refer a Friend** page.

## Tax Calculation

- United States billing addresses receive no Canadian sales tax.
- Canadian tax is selected from the editable province/territory rate table.
- Tax applies to the package and setup fee as configured.

The agent profile address must be accurate before payment.

