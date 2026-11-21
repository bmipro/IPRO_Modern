# Public Registration and Account Provisioning

## Register a New Agent

Signing up is the first half of checkout (the sign-up rebuilt on 2026-08-13): the registrant chooses their own
password on the form, and the form leads straight to payment.

1. Open the sign-up page. A plan's **Get started** button on the pricing page opens it with that plan already
   chosen (a locked summary with a **Change plan** link); otherwise the plan is picked on the form.
2. Enter the contact information, then the company details (company, address, city, province, postal code,
   country and time zone).
3. Enter the business phone and choose the **Business Type**. The choice decides the starter content.
4. Choose **Monthly** or **Annually** for the plan.
5. Optionally enter a **Promotion Code** and click **Apply** to see the discount before submitting. A Refer a
   Friend link fills the code in and the page leads with the gift.
6. Under **Your Website**, choose **Build my website with iPro.** or **I already have a website: keep it.** (with
   its address, optional).
7. **Choose a Password** (at least 8 characters) and **Confirm Password**.
8. Read the **Terms and Conditions**, enter the changing **Verify Code**, accept the terms and submit.

The company address may be left blank.

A promotion code is checked against the chosen plan when **Apply** is pressed (some codes apply to one plan only)
and again when the PayPal subscription is created, right after the form is submitted. If a code has expired or
reached its limit in between, the normal price applies; an invalid code never blocks the sign-up itself.

**A paid plan is active only once PayPal approves the subscription.** Every paid feature is gated on an active
subscription or an active trial. A registrant who leaves PayPal without approving is still signed in, and the
**Billing** page shows the one step left; they are not charged twice when they finish.

## Trial Invitations

Trial packages are invitation-only — they never appear in the normal package dropdown above. A SuperAdmin creates a trial package under **Packages** (checking "This is a trial package", setting the trial length in days and how many reminder emails to send as it nears expiry), then generates a shareable link from **Trial Invite Codes** (`/TrialInviteCodes`), which also controls how many different people can redeem that one link.

1. The prospect opens their invitation link (`.../Account/Register?trialCode=CODE`).
2. The registration form switches into a locked "claiming your invited trial" mode — the package is implicit, not user-selectable.
3. On successful registration, the trial starts immediately (no payment step) and runs for the configured number of days.
4. As the trial nears its end, the agent receives the configured reminder emails, then a "trial ended" notice with a short grace period (SuperAdmin-configurable, default 1 day), then a final notice once that grace period lapses.
5. If the agent hasn't subscribed by then, the portal locks to the Billing page only — everything else redirects there until they subscribe. Subscribing at any point (during the trial, the grace period, or after) restores full access immediately.

Looking at the sign-up page with a trial or referral code in its address is limited to 10 looks in 5 minutes per
visitor (640, 2026-11-20); an ordinary visit without a code is never counted.

## Automatic Account Setup

When the form is accepted, IPRO:

1. Lower-cases the email address and checks that no other adviser has it.
2. Makes the user name from the first and last name (letters kept, accents written plainly), adding a number when
   the name is already taken.
3. Creates the account with the password the registrant chose. There is no temporary password and no forced change
   at the first sign-in.
4. Gives the free website address `firstnamelastname.247advisers.com` (the root is the `App:TemporarySiteRootDomain`
   setting, the same one the home page shows; a number is added when it is taken).
5. Assigns the chosen plan, period and business type.
6. Writes AND publishes the starter website from the business type's starter content. An unpaid account's public
   site answers "not found" until billing is active.
7. Records "I already have a website", when chosen: iPro's address then points to the adviser's own site, and the
   checkout charges no setup fee (646).
8. Sends the welcome email (below) and tells iPro about the sign-up (further below).
9. Signs the registrant in, then: an invited trial opens the Dashboard; a paid plan goes straight to PayPal to
   approve the subscription; a code that covers the whole price activates at once and opens the Dashboard; if PayPal
   cannot be reached, the account still exists and the Billing page says to pick the plan there to finish.

## Duplicate Email

An email already associated with an agent cannot be registered again. The registrant should sign in or contact IPRO support.

## Welcome Email

The welcome email carries the account's details, the website address and a sign-in link, and **no password**
(nothing secret travels by email). If it cannot be delivered, the account is still created and the registrant is
already signed in.

Super Admin can correct the email and reset the password later.

## iPro Is Told About Every Sign-Up

Two emails go to iPro's own mailbox (551, 2026-10-05):

- **New sign-up**, the moment the form is submitted -- before PayPal, so a registrant who never pays is still
  known: name, business, business type, the plan and its price as offered, the code typed (if any), email, phone,
  city and province, where they came from (the same origin the Visitors page counts), and an **Open in
  SuperAdmin** button to the adviser's page. A trial invitation says so instead, with its end date.
- **Subscription started**, when PayPal approves the subscription (or a no-cost code activates it): the plan, the
  first bill (tax included), and whether this adviser has had a subscription before ("restarted"). One per
  subscription: the return page and PayPal's ACTIVATED message both arrive at the same place, and the second
  finds nothing left to start.

A first email with no second one means the registrant stopped before paying -- a phone call may finish the sale.

The address is `Signups:NotificationEmail` when it is set in the Web app's settings (`Signups__NotificationEmail`
in Azure), otherwise `Support:NotificationEmail` -- `support@iproadvisers.com`, where the support-ticket notices
go. A notice never holds up or fails a sign-up or a payment; a failure is written to the log. Accounts created in
SuperAdmin send no notice.

