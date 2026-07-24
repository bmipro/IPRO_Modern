# Public Registration and Account Provisioning

## Register a New Agent

1. Open the public registration page.
2. Enter required contact information.
3. Enter company, location, and business phone information.
4. Select a business type. The choice controls relevant starter content.
5. Select an active package.
6. Optionally enter a **promotion code** and click **Apply** to see the resulting discount before submitting.
7. Read the terms and conditions.
8. Enter the changing verification code.
9. Accept the terms.
10. Submit registration.

Optional information such as designation, company address, fax, and mobile phone may be left blank.

A promotion code is checked against the selected package immediately (some codes only apply to one specific package) and again later when the agent first subscribes through Billing, since the actual PayPal subscription isn't created at registration time. If a code expires or reaches its redemption limit in between, the agent simply pays the normal price — registration itself is never blocked by an invalid or expired code.

**Registering does not itself create a paid subscription.** Every paid feature is gated on an active subscription (`Billing`) or an active trial, not on having merely picked a package during registration — after registering for a paid package directly, the agent still needs to complete **Billing → Subscribe** before portal features unlock. The registration success page always says clearly which of the two applies.

## Trial Invitations

Trial packages are invitation-only — they never appear in the normal package dropdown above. A SuperAdmin creates a trial package under **Packages** (checking "This is a trial package", setting the trial length in days and how many reminder emails to send as it nears expiry), then generates a shareable link from **Trial Invite Codes** (`/TrialInviteCodes`), which also controls how many different people can redeem that one link.

1. The prospect opens their invitation link (`.../Account/Register?trialCode=CODE`).
2. The registration form switches into a locked "claiming your invited trial" mode — the package is implicit, not user-selectable.
3. On successful registration, the trial starts immediately (no payment step) and runs for the configured number of days.
4. As the trial nears its end, the agent receives the configured reminder emails, then a "trial ended" notice with a short grace period (SuperAdmin-configurable, default 1 day), then a final notice once that grace period lapses.
5. If the agent hasn't subscribed by then, the portal locks to the Billing page only — everything else redirects there until they subscribe. Subscribing at any point (during the trial, the grace period, or after) restores full access immediately.

## Automatic Account Setup

After successful registration, IPRO:

1. Normalizes the email address to lowercase and checks uniqueness.
2. Creates a username from first name and last name.
3. Adds a numeric suffix when the username already exists.
4. Generates a temporary password.
5. Requires a password change on first login.
6. Assigns a temporary `FirstNameLastName.247Advisers.com` domain.
7. Assigns the selected package and business type.
8. Creates starter website content using the matching business type/package rules.
9. Displays the registration success page.
10. Sends the welcome email when SendGrid is configured.

## Duplicate Email

An email already associated with an agent cannot be registered again. The registrant should sign in or contact IPRO support.

## Registration Success Page

The page displays:

- Agent name
- Temporary website address
- Username
- Temporary password
- First-login password instructions
- Sign-in and website links
- Either the trial end date (trial registrations) or a reminder that a subscription still needs to be completed via Billing (direct paid-package registrations)

The registrant should keep these details until the password has been changed.

## Welcome Email

The welcome email includes the same account information and temporary website link. If delivery fails, the account is still created and the success page displays the credentials.

Super Admin can correct the email and reset the password later.

