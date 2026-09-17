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

## APPROVED 2026-09-17

Google's Third Party Data Safety Team approved the OAuth verification for project 433464086635
(total-now-243122) for `.../auth/calendar.events`, two days after submission (the email arrived on the
morning of 2026-09-17). Effect: the "Google hasn't verified this app" screen and the Advanced link are
gone for every adviser, and the 100-user cap no longer applies. Google's reminders, which are now rules
for this project: keep the Project Owner/Editor accounts current; any new scope, and ANY change to the
consent-screen configuration (Branding, Data access), needs a new verification request -- so do not add
a logo, rename the app, or touch the scope list casually; the approval is not inherited by other scopes.

## Submitted 2026-09-15 -- the order that actually worked

Status in the Verification centre at 2026-09-15: Branding "verified and being shown to users";
Data access "under review". Video: https://youtu.be/-RMdkghZKAk (unlisted, 4:15, recorded on the live
site; frame-checked before upload -- unverified-app screen, the consent box ticked on camera, a follow-up
reaching Google, a Google event reaching the IPRO Calendar page, Disconnect, the privacy paragraph).

1. **Data access**: Add or remove scopes (paged, ten rows; filter `events`) -- untick `calendar`, tick
   `calendar.events` and `userinfo.email`, Update; paste the justification (876 chars); paste the YouTube
   link; Save -> toast "Data access changes saved". The Save does NOT persist while the justification box
   is empty (two silent failures on 09-14).
2. **Branding -> Verify branding** answered "homepage URL is not registered to you" (View issues). Fix:
   Search Console, Domain property `iproadvisers.com`, TXT `google-site-verification=...` added in the
   cPanel Zone Editor at the apex (the zone lives at the legacy host, DNS_ZONE_RUNBOOK), Verify ->
   "Ownership verified" within minutes. Then View issues -> "I have fixed the issues" -> Proceed ->
   "verified, publish within 7 days" -> **Publish branding** -> green tick. Keep the TXT record forever.
3. **Verification centre -> Prepare for verification**: a read-back of Branding and Data access, an
   optional "Additional info" box (filled: live URL, single client, optional connection, the privacy
   section, test account on request via support@), Confirm, then a questionnaire (personal use / internal /
   dev-staging / WordPress SMTP plug-in: all No; two acknowledgements ticked) -> Submit for verification.

Expect Google's questions at support@iproadvisers.com and bahman.motamed@gmail.com (the contact
addresses on Branding); answer from the same Google account. Do not edit Branding while the review runs.
Until approval the Advanced -> "Go to iproadvisers.com (unsafe)" bridge stays, capped at 100 users.

## Seen on the page, 2026-09-14 (corrections to the steps above)

- The Data access list held ONLY the wide `calendar` scope; `calendar.events` and `userinfo.email` had
  never been saved. Step 1 above is therefore: untick `calendar`, tick `calendar.events` and
  `userinfo.email` in the Add-or-remove-scopes panel (paged ten rows at a time; filter `events`), Update.
- The justification ("How will the scopes be used?", 1000 characters) and the demo video's YouTube link
  are entered ON the Data access page, under the sensitive scopes; the Verification centre only submits.
  Two Saves with the justification box empty did not persist -- fill it before Save.
- Google's note on that page: the unverified-app screen will appear for the test account and MUST be
  shown in the video. The owner's reading copy of these steps: `C:\Users\admin\Documents\IPRO_Google_Verification_Steps.html`.

## Known trap: the checkbox on Google's consent screen (487)

Google's consent screen shows the calendar permission as a **checkbox** that is not ticked by
default. Left unticked, Google still returns a token, with the email only, and every sync run then
fails with `403 insufficient authentication scopes` (the owner's 2026-09-12 evening). Since 487 the
app refuses such a grant at connect time with a message that says to tick the box; the fix for an
adviser who sees it is Disconnect, Connect again, tick the box, Allow. Also the Data Access list:
a scope the app asks for but the list does not hold is dropped silently in the same way.

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
   with the two scopes; **tick the calendar box on camera**, then allow.
3. Back in IPRO, show the connection status (account email shown, Disconnect available).
4. Marketing Calendar: create an appointment. Open Google Calendar in another tab: show it there.
5. In Google Calendar, move or rename that appointment. Back in IPRO: show the change after sync.
6. Profile -> Google Calendar -> **Disconnect**. Show the status cleared.
7. End on the privacy page https://app.iproadvisers.com/privacy.

## The bridge until approval (history -- approved 2026-09-17)

After step 4 the notice changes from "being tested" to "Google hasn't verified this app"; an adviser
clicks **Advanced -> Go to IPRO Advisers (unsafe)** and connects. The cap is 100 users for an
unverified published app; calendar connections at launch are far below that. If Google rejects,
the message says what to change; resubmit from the same page.
