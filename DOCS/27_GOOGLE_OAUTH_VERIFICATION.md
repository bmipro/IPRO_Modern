# Google OAuth app verification -- the Calendar connection (486, 2026-09-12)

**Why.** Google Calendar sync asks an adviser for access to their Google account. Until Google has
verified the app, every adviser sees "Google hasn't verified this app". While the consent screen is in
**Testing**, only listed test users can connect at all (up to 100). Published but unverified, anyone
can connect through an "Advanced -> Go to IPRO Advisers (unsafe)" link, capped at 100 users. Verified,
the consent screen is clean. Google reviews the app once, per scope.

**What the app asks for (486).** `https://www.googleapis.com/auth/calendar.events` (read, create,
update, delete events on the calendar the adviser chooses) and `https://www.googleapis.com/auth/userinfo.email`
(which Google account is connected). Nothing wider: no calendar settings, no sharing, no calendar
list. The console's **Data Access** page must list `calendar.events` exactly, or Google answers
`ACCESS_TOKEN_SCOPE_INSUFFICIENT` (DOCS/09, the July incident). The old full `calendar` scope can be
removed there once 486 is deployed and a reconnect has been tested.

**Where the client lives.** Google Cloud console, the project holding the OAuth client whose id is in
the `GoogleCalendar__ClientId` app setting on `ipro-prod-web`. The callback address registered on that
client is `https://app.iproadvisers.com/GoogleCalendar/Callback` (485), plus the azurewebsites.net one.

## Owner's steps, in order (Google Cloud console -> Google Auth Platform)

1. **Data Access -> Add or remove scopes:** add `https://www.googleapis.com/auth/calendar.events`
   (keep `userinfo.email`; leave `calendar` until 486 is deployed and reconnect works, then remove).
   Save.
2. **Branding:** App name `IPRO Advisers`; user support email (a mailbox that is read);
   app logo -- leave empty (a logo triggers a separate brand review; add it after approval if wanted);
   Application home page `https://app.iproadvisers.com`; Privacy policy `https://app.iproadvisers.com/privacy`;
   Terms of service `https://app.iproadvisers.com/terms`; Authorized domains `iproadvisers.com`;
   developer contact email. Save.
3. **Authorized domain proof:** Google requires the authorized domain to be verified in Search Console.
   https://search.google.com/search-console -> Add property -> **Domain** -> `iproadvisers.com` ->
   copy the `google-site-verification=...` TXT record -> add it at the registrar that holds
   `iproadvisers.com`'s DNS, as a TXT on `@`, touching nothing else there (SPF, DKIM and MX carry
   the platform's email). Verify.
4. **Audience:** External. **Publishing status -> Publish app** (In production). Confirm.
5. **Verification Center (or "Prepare for verification" on the Audience/Branding page):** submit.
   Google asks for:
   - the scopes and a justification per sensitive scope -- the text below;
   - a demo video, unlisted on YouTube -- the script below;
   - confirmation that the privacy policy is on the authorized domain and describes the data use.
6. Expect email from Google within a few business days for the branding part and questions over the
   following weeks for the scope review; answer from the same account. Nothing in the app changes
   during the review.

## Justification text (calendar.events)

> IPRO Advisers is a client-management platform for Canadian financial, insurance, accounting and
> mortgage advisers. An adviser may connect their own Google Calendar so that client appointments and
> reminders created in IPRO appear on their Google Calendar, and changes made in Google Calendar are
> reflected in IPRO (two-way sync of the adviser's own events). The app uses calendar.events to read,
> create, update and delete events on the one calendar the adviser chooses, and userinfo.email to show
> the adviser which Google account is connected so they can disconnect it. It does not read other
> calendars, change calendar settings or sharing, or use calendar data for anything but displaying and
> syncing that adviser's own appointments. Data is stored in Canada (Microsoft Azure, Canada East),
> never shared or sold, and deleted when the adviser disconnects or closes the account.

## Demo video script (2-3 minutes, unlisted YouTube, screen recording with the browser address bar visible)

1. Open https://app.iproadvisers.com, sign in as an adviser (a test account).
2. Profile -> Google Calendar -> **Connect**. Show the Google account chooser and the consent screen
   with the two scopes; accept.
3. Back in IPRO, show the connection status (account email shown, Disconnect available).
4. Marketing Calendar: create an appointment. Open Google Calendar in another tab: show it there.
5. In Google Calendar, move or rename that appointment. Back in IPRO: show the change after sync.
6. Profile -> Google Calendar -> **Disconnect**. Show the status cleared.
7. End on the privacy page https://app.iproadvisers.com/privacy.

## The bridge until approval

After step 4 the notice changes from "being tested" to "Google hasn't verified this app"; an adviser
clicks **Advanced -> Go to IPRO Advisers (unsafe)** and connects. The cap is 100 users for an
unverified published app; calendar connections at launch are far below that. If Google rejects,
the message says what to change; resubmit from the same page.
