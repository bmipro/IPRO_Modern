# Super Admin Manual

## Sign In

1. Open the Super Admin Portal.
2. Enter an admin username and password.
3. Sign in.

The original Azure-application-settings admin credential is automatically created as the first **Super Admin** account the first time the app starts. From then on, admin sign-in is checked against the **Admin Users** list (see below), not the Azure setting directly — additional admin accounts can be created from within the portal. Every login attempt (success or failure) and every admin-account change is written to an audit log.

After 5 wrong passwords in a row an admin name is locked for 15 minutes, even with the right password (a name that is not an admin gets the same answers). Another Super Admin unlocks it under **Admin Users** (see **See and Clear a Sign-In Lock** below) or by resetting its password; the break-glass recovery in the troubleshooting guide unlocks it too. Admin accounts have no email address, so there is no lock email: the lock is in the audit log as **SignInLocked**.

## Manage Admin Users and Roles

1. Select **Admin Users** (Super Admin only).
2. **New Admin** creates an account with a username, full name, temporary password, and role.
3. Two roles exist:
   - **Super Admin** — full access, including Packages, Tax Rates, Company Details, PayPal Setup, Email Setup, Website Templates, Newsletter Templates, and Admin Users itself.
   - **Support** — day-to-day operations: Agents, Domains, Starter Content, Website Leads, and Reports. Billing/platform configuration screens are not accessible and return "Access Denied."
4. **Edit** an account to change its full name, role, or active status. An admin cannot deactivate their own account.
5. **Reset Password** sets a new password for an account; share it with that admin securely.

## View the Audit Log

Select **Audit Log** (Super Admin only, under **System**) to see every logged action across the portal, newest first — login success/failure, admin-account changes, and every meaningful change made on Agents, Packages, Promotion Codes, Tax Rates, Company Details, Domains, Newsletter Templates, Website Templates, Starter Content, and Support Tickets. Each entry shows when it happened, which admin did it, a short action code, and a human-readable detail.

Filter by acting admin, search action/detail text, or set a date range. Results are paginated 50 per page.

## Dashboard

The dashboard summarizes agents, active subscriptions, packages, revenue, and system activity. Use the sidebar to open operational areas.

## New Sign-Up Emails

iPro's mailbox gets an email for every self-registration, and a second when that adviser's subscription starts
(551; what each says is in `08_PUBLIC_REGISTRATION.md`, "iPro Is Told About Every Sign-Up"). Each has an **Open
in SuperAdmin** button to the adviser's page. They go to `Support:NotificationEmail` unless
`Signups:NotificationEmail` is set in the Web app's Azure App Settings (`Signups__NotificationEmail`).

## Find an Agent

1. Select **All Agents**.
2. Search by name, username, email, company, or domain.
3. Filter by status.
4. Open the agent record.

## Edit an Agent

1. Open the agent record.
2. Click **Edit**.
3. Update login, package, business type, registration, address, or phone information.
4. Save changes.

Optional fields such as fax, mobile phone, promotion code, designation, and company address may remain blank.

## Reset an Agent Password

1. Open the agent record.
2. Click **Reset Password**.
3. Give the temporary password to the agent securely.
4. The agent changes it at the next login when the force-change option is enabled.

A temporary password also unlocks a sign-in locked by wrong passwords.

## See and Clear a Sign-In Lock

Every sign-in (advisers, their team members, their clients, admins) locks for 15 minutes after 5 wrong passwords in a row; a sign-in that works starts the count again. The person whose sign-in it is gets one email when it locks (no more than one a day); admins get none, having no email address.

- **An agent:** the agent record's **Profile** card has a **Sign-in** line: "Locked until ..." with **Unlock**, or "Not locked" and how many wrong passwords since the last sign-in.
- **Their team members and clients:** a **Locked sign-ins** card under the Profile card lists each one locked right now, with **Unlock**.
- **Admins:** **Admin Users** shows a **Locked until** badge; **Edit** has a **Sign-in** card with **Unlock sign-in**.

Unlock is Super Admin only (Support admins see the button disabled). Each unlock is written to the audit log as **SignInUnlocked**, naming who was unlocked. Locks and unlocks are also written to the container log as warnings ("Sign-in locked for 15 minutes after 5 wrong passwords in a row: ..." and "Sign-in lock cleared (...)"). A new password unlocks at once: the agent's **Forgot password?**, **Reset Password** here or under **Admin Users**, an agent resetting a team member's password, a client's new portal invitation (**Send a New Invitation** on the client's profile).

## Activate, Deactivate, or Delete an Agent

- **Activate** restores access.
- **Deactivate** blocks access while retaining records.
- **Delete** permanently removes the agent and dependent information. Use only after confirming retention requirements.

## Review Agent Billing

The agent details screen shows:

- Active package
- Billing period and next billing date
- PayPal subscription ID
- Invoices
- Failed payments
- Retry history

Use **Email Invoice** to resend a paid invoice to the agent's current email address.

## Create a Package

1. Select **Packages**.
2. Click **New Package**.
3. Enter package name, description, monthly recurring price, annual recurring price, and one-time setup fee.
4. Set newsletter or other numeric limits when applicable. Use `-1` for unlimited.
5. Enable the required package functions. **Select all** can be used before removing excluded functions.
6. For numeric functions, set values such as contact count, file upload MB, or domain count.
7. Enable **Package is active**.
8. Save.

Only active packages appear during registration and billing.

## Edit Package Functions and Limits

1. Open **Packages**.
2. Click **Edit** beside the package.
3. Add or remove included functions.
4. Change contact, upload, domain, newsletter, or other limits.
5. Save before synchronizing PayPal.

Package feature changes control Agent Portal access and upgrade messages.

## Create or Replace PayPal Plans

1. Save the package prices first.
2. Confirm PayPal settings are configured for the current sandbox/live mode.
3. Click **Create / Replace PayPal Plans**.
4. Confirm monthly and annual plan IDs appear.

Replacing plans affects future subscribers. Existing PayPal subscriptions continue using their existing plan unless changed.

## PayPal Setup

Select **PayPal Setup** to verify:

- Sandbox or live mode
- Client ID and secret status
- Base URL
- Webhook ID
- Return/cancel URLs
- Active package plan IDs

Webhook URL: `https://ipro-prod-web.azurewebsites.net/Billing/Webhook`

## Manage Promotion Codes

1. Select **Promotion Codes**.
2. Click **New Code** to create a discount code prospective agents can enter at signup.
3. Set an optional expiry date and a maximum number of redemptions (blank means unlimited).
4. Configure a **recurring price discount** (percent or flat amount off) and how long it lasts:
   - Leave duration blank for a permanent discount.
   - Set duration to `1` for a discount on the first billing cycle only, then full price.
   - Set duration to any other number for that many discounted cycles, then full price.
5. A code with a recurring discount must be restricted to one specific package — PayPal plans are package-specific, so there is no way to discount "any package" with one plan.
6. Optionally add a **setup fee discount** (percent or flat amount off; 100% waives it completely). This does not require restricting the code to one package.
7. Save. The first time the code is actually used, IPRO creates the matching PayPal plan automatically (in whichever mode — sandbox or live — PayPal is currently configured for) and reuses it for every later signup with that code.
8. Open **Redemptions** on any code to see which agents used it, on which package, and the resulting discounted vs. original amounts.

Editing a code's discount amount, duration, or restricted package after it has already been used clears its cached PayPal plan, so the next redemption creates a fresh plan matching the new terms — agents who already redeemed the old terms keep their existing subscription unchanged.

Always verify a new promotion code against PayPal Sandbox before relying on it in live mode.

## Refer a Friend (Referrals)

**Give $50, Get $50** (532). Any adviser refers anyone; the friend gets $50 off at checkout, the adviser $50 plus its
tax back once the friend stays. It ships **OFF**.

1. Select **Referrals** (Billing section). Switch it **On** and keep or change the two amounts (the friend's gift; the
   referrer's reward, before tax). Saving is audit-logged. A change applies to new referrals: an amount already
   promised to a friend or a referrer stays as it was. Switched off, new sign-ups get no gift; referrals already made
   keep going.
2. **Codes are never Promotion Codes.** Each adviser gets one permanent code (`JANE-7K3Q`) the first time they open
   Refer a Friend; their link is `https://www.iproadvisers.com/Account/Register?ref=CODE`. A promotion code can never
   be saved with the same text. A closed account's code stops by itself; to stop one adviser's, open their
   referrals and **Pause their link** (audit-logged).
3. **The page shows** this month and all time (signed up, joined, gifts given, rewards earned, refunded), the top
   referrers, **Needs attention** (below), and the ledger: search by friend, adviser, email, business or code; filter
   by stage (Signed up, Joined, Earned, Paid, Not earned, Voided); 50 a page; **Export CSV**.
4. **Needs attention** lists only the exceptions: a *possible self-referral* (the friend shares a phone number, the
   sign-up network address or the business address with the referrer) -- **Looks fine** or **Void**; and an earned
   reward with *nothing to refund against yet* (the adviser's own payments inside PayPal's window are not enough).
   The dashboard shows the count. A friend who pays iPro from the referrer's own PayPal account is blocked
   automatically (voided, "automatic").
5. **Void** any referral that is not paid, with a reason (audit-logged). It earns nothing; the adviser's page reads
   "Not earned" and says nothing more about the friend.
6. **Paying a reward:** when the friend has stayed (their second monthly payment, or 30 days into a yearly plan) the
   reward appears under **Refunds -> Referral rewards to refund**, already worked out: $50 plus tax at the rate the
   adviser pays, against their own PayPal payments, newest first, split across two when one payment is smaller (PayPal
   refunds no more than a payment was). Refund each amount at PayPal against the transaction shown, then enter the
   refund id(s) and **Mark Refunded**: the reward is paid, its credit note (`CN-2026-0001`) is issued, and the adviser
   gets the "paid" email. Nothing on these pages moves money.

The adviser's side is in `01_AGENT_ACCOUNT_AND_DASHBOARD.md`, "Refer a Friend". Automatic refunds (a switch for the
rewards alone) are not built yet: manual for now, the owner's choice.

## Manage Trial Packages and Invite Codes

Trials are invitation-only and never appear in the normal public registration dropdown.

1. Create a trial package under **Packages** like any other package (typically priced at $0), and check **This is a trial package**. Set the trial length in days and, optionally, a comma-separated list of days-before-expiry to send reminder emails (e.g. `7,3,1`).
2. Select **Trial Invite Codes** and click **New Invite Code**.
3. Choose which trial package the code unlocks, an optional expiry date for the invitation link itself, and a maximum number of redemptions (blank means unlimited — this is the "how many people can use this link" control).
4. Save. The Index page shows a ready-to-copy link (`.../Account/Register?trialCode=CODE`) — share it directly with the prospect (email, chat, however).
5. Open **Redemptions** on any code to see which agents claimed it and when their trial ends.

A global **grace period** (default 1 day, applies to every trial) controls how long access continues after a trial's end date before the agent's portal locks down to the Billing page only. There's no dedicated settings screen for this yet — it lives in the `TrialSettings` table and can be adjusted directly if it ever needs to change.

## Email Setup

Select **Email Setup** to review SendGrid configuration, sender identity, event webhook information, and recent delivery events.

The sender address must be verified in SendGrid. Deferred email usually indicates recipient throttling rather than application failure.

## Edit Tax Rates

1. Select **Tax Rates**.
2. Update province/territory name, tax label, and percentage.
3. Save.

Tax changes apply to future invoice calculations.

## Company Details

Select **Company Details** (Super Admin only, under **Billing**) to set the supplier as it appears on every invoice to an adviser, on the paid-invoice email and on the Revenue print view: company name, address, GST/HST registration number, billing email and website.

1. Fill the fields and save. The card beside the form shows exactly what invoices print now.
2. A field left blank keeps the value the application settings supply (name, email, website and the one-line registered address). The GST/HST number has no setting: until it is saved here, no invoice prints one.
3. Saved values apply at once to every invoice, including invoices already issued. The customer's bill-to block is the snapshot frozen when the invoice was issued.

Every save is written to the Audit Log with the values before and after.

## Manage Website Templates

1. Select **Templates**.
2. Create, edit, duplicate, retire, restore, or set a default template.
3. Configure template family, colors (click a **Color Palette** swatch for a quick, coordinated accent and background, or set them individually), typography (including font family, heading font size, and body font size), header, hero, spacing, buttons, and preview image.
4. Set defaults by business type when appropriate.

Heading and body font size set the default for section headings and body text across an agent's site; agents may override both from their own **My Website** settings. The hero/banner title keeps its own large display size regardless of this setting. Background color, button style, section spacing, and hero style also set real per-template defaults that agents may override individually; Hero Style (Gradient, Clean, or Classic) changes the background treatment behind the hero banner.

The template list shows how many agent sites and package defaults use each template, and the "Make default" action is scoped per business type/vertical, not just one global default. **Duplicate** creates an inactive `v2`/`v3` draft for safely testing changes before activating. **Delete** is blocked with the affected agent/package names whenever a template is still in use. **Retire** deactivates a template (existing sites stay online, unchanged) and emails every affected agent that their template was retired and they can switch whenever they're ready from **My Website**; those agents also see an in-app "Your current template has been retired" notice on that page until they switch. Retiring is blocked if the template is currently a business-type default — choose another default first.

From an existing template's edit screen, **Preview on an agent's real site** opens the candidate template rendered against a chosen agent's actual pages and content in a new tab — nothing is saved or changed. This requires `AdminPreview:SharedSecret` to be set to the same value in both the Web and Admin apps' Azure App Settings; until then the button shows a configuration warning instead of a broken link.

## Manage Newsletter Templates

1. Select **Newsletter Templates**.
2. Create, edit, hide (deactivate), or delete a starter template agents can use when composing a newsletter.
3. Set the default subject, HTML body (same rich editor agents use), plain-text body, and sort order.
4. Hidden templates stay in the list here but no longer appear in the agent-facing picker.

A handful of starter templates (announcement, market update, thank-you note, seasonal greeting) are seeded automatically the first time the app starts, so agents never see a completely empty list.

## Manage Vertical Starter Content

1. Open **Starter Content**.
2. Create a starter page set for a business type and optional package.
3. Define page title, slug, navigation, SEO fields, publication, and order.
4. Add and configure content blocks.
5. Save.

New agents receive matching starter pages based on business type/package. Agents may edit their resulting content.

### Starter Articles

**Starter Articles** are the Resources articles a new agent receives for their business type. The list
groups them by business type and category.

- **Add to this group** on a group's header opens the form with that business type and category filled
  in and the next sort order. **New Starter Article** at the top opens a blank form.
- **Business Type** offers the verticals in use (All, Accountants, Insurance / Financial, Mortgage, and
  anything you have already typed). Pick one, or type a new name once to create a vertical; it is
  offered from then on. "All" goes to every vertical.
- **Category** blank gives the article its own Resources page; articles sharing a category are stacked
  on one page.
- Articles reach agents provisioned after you add them; agents who already have a Resources page keep
  what they have (see Rebuild an Agent's Resources Section below).

### A Did You Know block that arrives filled

A **Did You Know** starter block is the one block whose content lives somewhere else: it shows
teasers of the agent's own Articles, and a new agent has no Articles until their site is built. So
the starter block does not hold article ids; it holds **which starter articles** it should show.

1. Under **Starter Articles**, make sure the articles you want exist for that business type (or
   for **All**) and are active.
2. On the starter page, add a **Did You Know** block, or open the one that is there.
3. Tick the articles under **Articles in this block**, choose the **Teaser layout**, and press
   **Save Block**. The list offers only the active starter articles for this page's business type
   plus the **All** ones; anything else is ignored.

When an agent of that business type is provisioned, the ticked starter articles become their own
Articles first (an article they already have under the same title is reused, never duplicated;
the Resources section reuses the same rows a moment later), and the block is written with those
real ids. The agent sees a working block on day one and can change the selection on their own
site. The prospect preview shows the same teasers. This works for any business type you create
here; a starter article you deactivate later simply drops out of the block for future signups.

## Preview And Delete An Agent's Data

Deleting an agent is permanent and removes everything they own. Before doing it, use **Preview Erasure**
on the agent details screen.

The preview is read-only and shows exactly what deletion would remove: every table with its row count,
every uploaded file (logo, photo, media library, gallery images, documents, client-portal documents,
article images), and separately any **shared library artwork** the agent's records point at, which is
kept. It runs the same rules the deletion uses, counted instead of executed, so it cannot report
something different from what actually happens.

Deleting then does all of the following, in this order:

1. **Cancels the agent's PayPal subscription.** If the cancellation fails, **nothing is deleted** and
   you are told why. This is deliberate: an account deleted while its subscription is still live would
   keep charging with no record left to trace the charge back to.
2. Requires working file storage. If storage is unavailable the deletion is refused, because once the
   database rows are gone the file locations are gone with them and the files could never be found again.
3. Removes every row the agent owns, across all tables.
4. Deletes their uploaded files, leaving shared library artwork in place.
5. Writes the row and file counts into the audit log.

The agent's custom domain is not touched by deletion. Sites are hosted on Azure, and domains are managed
under **Domains** &mdash; remove the domain there if it should stop resolving.

Deletion requires **Super Admin**. After deleting, re-open the preview for the same agent id to confirm
it reports zero.

Background jobs already queued for a deleted agent (newsletter sends, e-cards, e-letters, queued article
emails) find no rows and stop on their own.

## Database Backups

**Backups** (Super Admin only, under **System**) lists the nightly database dumps: a gzipped SQL file of
the whole database, taken at 02:15 Eastern into a private storage container and kept for 30 days. The
page shows each file's name, when it was taken, its size and the change against the previous dump (a
swing of 25% or more is marked in red), and **Run a dump now** takes one immediately (a
moment, logged in the audit log). The files are private: download them from the storage account in the
Azure portal, never from a link. How to restore one is in the repository's backup checklist (DOCS/14).
Azure's own point-in-time backups (35 days) are separate and unaffected.

## Rebuild an Agent's Resources Section

New agents get their Resources section built automatically from the starter articles for their business
type. Agents who signed up before a change to that starter content keep whatever shape they were given —
the build only runs when the agent has no Resources page yet, so it will not reshape an existing one.

**Rebuild Resources** on the agent details screen deletes that agent's Resources page and everything
beneath it, so the section is rebuilt from current starter content the next time the agent opens their
Pages or Menu screen. Use it after adding starter articles or changing how they are categorized.

It affects only the Resources page tree. The agent's Articles themselves are not deleted, and the rebuild
reuses any article the agent already has under the same title — so an agent who edited a starter article
keeps their edits, and rebuilding twice does not leave duplicate articles behind. Any page the agent
created themselves *underneath* Resources is part of that tree and will be removed, so check with the
agent first if they may have added their own pages there. The action is recorded in the audit log.

## Start an Agent's Website Again

A website gets its starter pages once, when it has no page at all. Changing an agent's **Business Type**
under Edit therefore changes no page: a site built from the insurance pack keeps its insurance pages.
**Start the website again** on the agent details screen (next to the two Rebuild buttons) replaces every
page with the starter pack of the agent's *current* business type, exactly as a new sign-up of that type
gets it: the starter pages, Request Meeting with that business type's meeting form, Resources with its
articles and calculators, and Did You Know sections showing real articles. It is written at once, so the
public site is never empty. Use it for a demo account of another vertical, or when an agent's business
type really changes. SuperAdmin only; support admins see the button disabled.

1. Set the business type first: **Edit**, Business Type, Save. The details screen shows it under Profile.
2. Press **Start the website again**. A screen says what goes (every page, listed, with how many pages sit
   under each, and any link added by hand to the header button, menu or footer that will then find no page), what
   comes instead (the starter pages in order, Resources, the new meeting form, how many articles are new)
   and what stays as it is. If Template Builder work for the site is still out (a package waiting for
   review, or a hand-over nothing has come back from), it says so: such a package, brought in later,
   replaces the starter pages.
3. If some of the agent's published articles came from another business type's starter pack (for example
   insurance articles on a site that is becoming a mortgage site), they are listed with a tick box, on by
   default, to take them off the website. They are unpublished, never deleted, and stay in the agent's
   Articles. The agent's own articles are never listed. Articles of the new pack that the agent already has
   but are not published (for example after switching from insurance to mortgage and back) are listed too:
   they are published with the new pages, as a new sign-up's are, so no Resources page comes up empty.
4. Type the agent's username exactly as shown and press **Start the website again**. Pressing it twice does
   nothing more; the screen says so.

What stays: the design, colours, logo, header and footer; the website's title, tagline and domains; the
media library; the agent's forms (the new meeting form is added beside them; **Rebuild Request Meeting**
afterwards keeps the form the page shows); contacts, clients and leads; the agent's articles; the business
type. Visits and leads no longer show which of the old pages they came from (a lead keeps the address).

**Undo.** Before anything is written, the pages as they are are kept. A blue box on the agent details
screen then offers **Put the previous pages back**: the starter pages go (with any change made to them)
and the old pages return exactly as they were. The logo, design and business type stay as they are now;
articles the start-again took off the website go back on it, and those it added or published come off it
(they stay in Articles); the meeting form it added stays in Forms. A website that had no page before has
nothing to put back, so no box appears. Changes of a site's pages are put back newest first: if a
Template Builder package was brought in afterwards, put that back first (the Template Builder screen lists
the start-again too). If the website or the starter pack changed while the screen was open, nothing is
written and the screen shows the new state. Every start-again and put-back is recorded in the audit log
with its counts.

## Bring In a Site From the Template Builder

A site built in the Template Builder (the separate page builder; for now it is used internally, on the
owner's or his designer's machine) leaves the builder as one .zip file. **Template Builder** on the agent
details screen is where that file becomes the customer's site. SuperAdmin only; every step is recorded in
the audit log.

A package is one of two things: a site **built from scratch** in the builder, or a site iPro handed to
the builder that comes back edited (a **return**).

**To edit an existing site in the builder, hand it over first.** On the Template Builder screen press
**Make the package**. One .zip downloads: the site as it is now (its pages, sections, logo and images)
and the list of published articles and active forms the builder may place. In the builder, open it with
**Open iPro package**. Nothing on the site changes.

- A site is handed over whole or not at all. If a page still shows only the design's standard layout (it
  has no sections of its own), the screen lists the reason and makes nothing. Add a section to that page
  in the page editor first.
- **A section the builder does not have travels as a locked box:** a calculator, a map, a gallery, a
  price list, a poll, reviews and the rest. The package says where the section stands, what kind it is
  and its heading (so two of a kind can be told apart). Nothing else of it leaves iPro: not its text,
  its settings or its pictures. In the builder the designer sees a labelled box and can move it up or
  down its page, hide it or remove it. What the section says is changed in iPro's page editor, not in
  the builder. After a hand-over the screen says how many sections went out this way.
- Every press of **Make the package** records a new hand-over, which is why the button asks first. One
  package per round of work: make it, keep that file, and do not press again while a designer is working.
- **A menu label longer than 40 characters goes to the builder shortened.** The builder takes at most
  40; iPro's starter articles are named in the menu by their whole title (often 42 to 51 characters),
  so most sites have such a page. The builder shows the label cut after a whole word (inside a very
  long one), ending in "…" ("Questions Worth Asking Before You…"), and the screen lists each one after
  the hand-over. Left as it
  is in the builder, the page keeps its own label when the package comes back, as the site has it then
  (a label the customer changed meanwhile stays). Changed in the builder, the new label is used.
- A page's **title** is at most 120 characters. A longer one stops the hand-over, and the screen names
  the page and its length: shorten it in the page editor (its Page Title field).
- A picture taken from the page editor's own starter banners travels like any other picture.
- A site with more than 30 pages, a page with more than 40 sections, or pictures of more than 40 MB in
  all does not fit in a package; the screen says which.
- Only the package made from the **newest** hand-over can come back. Making a new one closes the door on
  the one before it.
- What is changed in iPro after the hand-over is replaced when the package comes back, apart from a
  locked box's section and a menu label the builder left as it went, which stay as the site has them.
  The review names
  the pages this affects, before anything is written.
- The screen lists what the hand-over tidied on the way out (for example a button with words and no
  link, which leads nowhere on the live site, is left out).

1. **Upload.** Choose the .zip and press **Check the package**. Nothing on the site changes. The package
   is checked against the export contract (`DOCS/TEMPLATE_BUILDER_CONTRACT.md`). If it breaks a rule it
   is not taken, and every reason is listed with its page, its section and the contract section, ready
   to send to the builder's developer.
2. **Review.** A package that passes shows what it would do, page by page:
   - a page with the same address is **replaced** (it keeps its search title and description, and
     whether it is published and in the menu);
   - a new address is a **new page**;
   - a page of the site that is not in the package **stays as it is**, unless you tick
     **Remove this page**;
   - the home page is always replaced by the package's home page.

   When the package is a **return**, the screen says so at the top, and reads the site against the
   hand-over:
   - a page that went to the builder and did not come back was **removed in the builder**, and is
     removed; tick **Keep this page after all** to keep it;
   - a page made in iPro after the hand-over was never in the builder, and **stays** (tick
     **Remove this page** to remove it);
   - a page **changed in iPro after the hand-over** is marked in red: the return replaces those changes.
     This is read from what the page says (its name, its address, its sections and their order), so a
     section the customer edited, added, removed or moved counts, whoever saved last. A page removed in
     the builder that the customer worked on since is marked the same way: keep it if in doubt.
   - a page **given another address in iPro** since is still the page that went to the builder: it is
     replaced where it is, and its newer address keeps arriving;
   - the **logo**, if it was changed in iPro after the hand-over, is marked too: the return carries the
     one from before;
   - a page whose **menu label went to the builder shortened** says so: "It came back unchanged, so the
     page keeps its own" with the label it keeps, or that the designer changed it, with the new label.
     A label the customer changed in iPro meanwhile, and the designer left alone, stays the customer's,
     and a page whose only change in iPro is that label is not marked red. A page the designer gave
     another address keeps its own label too, when its shortened label tells which page it is.
   - a page the designer gave **another address** in the builder is **moved**, not new, when iPro can
     tell which page it was: by its shortened menu label (above), or by its title, or by its menu label,
     when exactly one page that did not come back and exactly one new page share it. It takes that
     page's place: its search title and description, whether it is published and in the menu, its
     language, its old addresses, its visits and the pages under it all stay, and links to the old
     address keep arriving (they are sent on to the new one). The line says **Moved**, the address it
     had and how iPro knew, and, in amber, what it keeps that visitors notice: not published, not in the
     menu, a language of its own. It says in red when links to the old address will not arrive: the
     page is not published (they arrive once it is), another page has that address afterwards (the
     home page, say), or the page already lists 12 old addresses, the most a page answers to (remove
     one in the page editor afterwards and add the old address there). Every new page, and every moved
     one, has a choice **Is this a page of the site at a new address?** listing the pages that did not
     come back: pick one to say the new page is that page, or **No: a new page** to undo iPro's reading.
     Two pages cannot be the same page: the screen says so, shows both choices, and writes nothing.
   - a page **removed** says in red what goes with it that its sections do not show: its search title
     and description, its own language, the old addresses it answers to, and where links to its
     address go afterwards (nowhere, or the page that has that address then). A page removed in the
     builder that was hidden from the menu or not published says so when the package has new pages (a
     new page comes back published and in the menu), with how to keep all of it: choose the page on
     the new page's line.

   A replaced page's sections are replaced as a set.
   - **A locked box that came back** is its section again, as the site has it now, where the designer
     put the box. What the customer changed in that section in iPro meanwhile stays: nothing in a box
     could be changed in the builder, so there is nothing to put in its place. It is hidden or shown as
     the site has it, unless the designer changed that on the box. The screen says, in amber, when a
     box comes back SHOWN though its section is hidden on the site (or HIDDEN though it is shown), when
     it comes from another page, when it is a copy (the same box twice), when the site no longer has
     the section, so that it comes back as it was at the hand-over, and when a box was matched to a
     section by its kind alone because nothing else told which one it is.
   - **A section the package does not bring back is named in red, with a tick box of its own:** one
     whose locked box the designer removed in the builder; in a return, one the customer added
     in iPro after the hand-over; in a site built from scratch, every section of a replaced page
     that the builder cannot carry (a map, a gallery, a price list, reviews). Tick the box to keep that
     section as the site has it now: it goes to the end of its page. Left unticked, it goes with the
     page's old sections. A page cannot be kept past 40 sections: the screen says so and writes nothing.
   - **A page the designer moved** (above) is replaced where it stands, so its sections are named and
     kept like any replaced page's. **A page the designer gave another address that iPro could not
     tell, and you did not choose, or one made the home page,** is not replaced where it stands: the
     page under its old address shows as **removed in the builder** (with the kinds of section it has,
     and what else goes with it) and the new address as a **new page**, published and in the menu like
     any new page, also when the old one was not. Its locked boxes that came back are on the new page;
     the old page's line says so, in amber, for each. A box the designer removed from such a page has
     no tick box of its own: it goes with the old page. Choose the old page on the new page's line to
     keep that section (it is then a moved page), or keep the old page (tick **Keep this page after
     all**) and tidy the two pages up in the page editor afterwards. For a page made the home page that
     tick is refused (the home page takes its address): put the section back in the page editor
     afterwards, or ask the designer to put the box back.

   **Look** opens a page as it would be, in the customer's own design, in a new tab. A look works for an
   hour. Its menu, buttons and footer take you through the look's other pages (a page the look does not
   have shows the home page and says so); links to other sites leave it. The strip at the top of
   a look says which kind of page it is: one that comes from the package, a new one, or one that is not
   in the package and stays as it is. A page from the package can look much like the page it replaces:
   the header, the menu, the colours and the fonts are the site's own design and do not come from the
   builder. Its words, pictures and sections do.

   **The design:** leave the site's design as it is, or switch to another on the way in. Colours and
   fonts are the site's own (My Website); they do not come from the builder.

   After ticking a box, choosing which page a new page is, or changing the design, press **Update this
   page and the looks with these choices**.

   **Bring it in** writes exactly what this screen shows. Pressed after a box was ticked or cleared and
   not yet updated, or after the customer changed the site so that it would do something else, it writes
   nothing and says so: the review then shows what would happen now. Read it and press again.
3. **Bring it in.** The site's pages become the package's. On a published site this is live at once (the
   button says so before you confirm). The site as it was is kept.
4. **Put the previous site back,** if it is not right: on the list, beside the package that is on the
   site. Every page, section and the logo return to the moment before the package went in. Anything
   changed on the site since then is lost with it, so do it soon or not at all. The pictures the package
   brought leave the media library with it, unless something still uses them. Only the newest package
   on a site can be taken back out.

**Drop** removes a package that was never brought in, and takes back out of the media library the images
it added that nothing uses.

Good to know:

- A return of an older hand-over is refused, and the reason names the newest one.
- An article unpublished or a form switched off after the hand-over stops a return that uses it: when
  it is uploaded, and again when it is brought in. Publish it again, or take that section out in the
  builder.
- New images go into the customer's media library and count against their storage. Bringing the same
  site in again after an edit stores only the images that are new.
- A logo in the package replaces the site's logo. A package built from scratch without a logo leaves the
  site's logo alone; a **return** without one removes it, because the designer took it out. The logo is
  also what the customer's invoices and documents print.
- A package built from scratch cannot hold a custom form or an article section, so a page it replaces
  loses those: the review names them in red, as it does a map or a gallery.
- A damaged or half-downloaded file is refused with a reason, like any other package that breaks a rule.
- Article and custom-form sections cannot come from a from-scratch package: they point at one customer's
  own article or form. Add them in the page editor after the package is in.

## The New Builder for Customers (Beta)

The builder can be offered to a customer, who then edits their own website in it from **My Website**.
It is switched on one customer at a time, on the agent's **Template Builder** screen, in the panel
**The new builder (Beta) for this customer**:

- **On** shows them a **New website builder (Beta)** card on My Website with **Try the new builder
  (Beta)**. They get in with no second password: iPro gives their browser a pass that works once, for
  ten minutes, and the builder's server trades it for a session of at most twelve hours.
- **They publish what they send themselves** (off by default). Off: what they send arrives in the
  list on this screen as **Sent by the customer from the new builder**, waiting for review; iPro's
  mailbox (`Signups:NotificationEmail`, else `Support:NotificationEmail`) gets an email; review it
  and **Bring it in** as usual, and the customer gets an email that it is on their website. **Drop**
  it instead and they get an email saying it was not published (their draft stays in the builder). On:
  they review it themselves on a short page of their own (each page says what will happen, with a
  **Look**) and publish it.
- **Turning it off** ends their sessions: the builder closes at its next check with iPro, within five
  minutes (until then their open editor can still save their draft, but sends nothing). Their website
  is not touched, and their draft stays in the builder; a draft not opened for 90 days is deleted.
  What they sent and is still waiting stays in the list here, for you to bring in or drop.
- **While it is on, Hand over is refused for that customer.** Their site is handed over to them, in
  the builder; a hand-over made here would stop their open draft from being sent. Turn the Beta off
  first if iPro is to work on the site.

Good to know:

- The review of a customer's package opens with every section they did not remove in the builder
  ticked to stay: a section they added in the page editor while their draft was open is kept, at the
  end of its page. Their own review keeps those the same way.
- **Put my previous site back** is on their card as long as the builder's package is the newest on
  the site, also after the Beta is turned off. It is the same put-back as on this screen.
- While a customer has a draft open, their page list in the portal says that publishing it replaces
  what they change there in the sections the draft has.
- A customer's draft is never thrown away by the builder: when it cannot be sent as it is (its changes
  went in, the site was put back, or it was handed over again), the builder opens the site as it is now
  and keeps the draft as a "Kept draft" version the customer can restore (restoring it brings in what
  was added on the site since, at the end of its pages). A second send of a draft whose changes already
  went in is refused, and so is bringing in a package when the customer sent a newer one since.
- When the customer's billing needs attention or their package no longer includes the website, the
  builder refuses at once to show or send anything, and closes at its next check with iPro (within five
  minutes), as for the Beta switched off.
- "See it on my site" in the builder makes a look that works for an hour. Such looks are not listed
  here and go by themselves; their new pictures are never added to the customer's media library.
- Deleting the agent asks the builder to erase their draft (a job repeats the request until the
  builder confirms it).
- The builder's address is `Builder:BaseUrl` in the portal's settings and its shared key
  `Builder:ServiceKey` (at least 32 characters, the builder's `Hosting:ServiceKey`): every call between
  the two carries it, and without it the builder can neither open a session nor send. Without an
  address the card says the builder is not available yet.

## Monitor Domain Automation

1. Select **Domains**.
2. Review DNS, Azure binding, SSL, and last-check statuses.
3. Click **Recheck** after DNS or Azure permissions are corrected.
4. Use **Mark Bound** only when the Azure domain and SSL binding have been independently confirmed.

Automation settings require valid tenant ID, client ID, client secret, subscription ID, resource group, web app, App Service plan resource ID, and location. If a required setting is missing, the domain's error message names exactly which setting is blank.

Each domain row also shows the short address (**Root (...)**) when it differs from the `www` host, in the agent's own words: **Not set up**, **Forwarding OK**, or -- when it is pointed straight at the platform with an A record (553) -- **Connecting**, **Securing**, **Connected** or **Needs attention**, followed by the Azure binding and SSL states and, while it is not working, the sentence the agent is shown. A short address never blocks the `www` site. A domain that has failed repeatedly shows an "Auto-retry paused" badge once IPRO's background job stops automatically rechecking it; **Recheck** still works normally on a paused domain and resumes automatic checking if it succeeds.

Super Admin always sees the real underlying Azure/DNS error text for a failed domain (unlike the agent-facing view, which shows a plain-language translation) — use this for actual diagnosis.

## Review Website Leads and Blocked Attempts

1. Select **Website Leads**.
2. Use the **Leads** tab to review public website submissions across every agent, including whether the agent notification email was delivered. Filter to notification-not-delivered leads to spot email delivery problems early.
3. Use the **Blocked Attempts** tab to review submissions stopped by the honeypot, timing check, or captcha before they became leads. Filter by reason, domain, or IP address to spot abuse patterns.
4. Blocked attempts store only the reason, domain, page, and IP address — never the submitted name, email, or message.

## Manage Support Tickets

1. Select **Support Tickets**.
2. Review the cross-agent ticket queue — filter by status (Open/In Progress/Resolved/Closed) or search by subject/agent name/email.
3. Open a ticket to see the full message thread and reply. Replying automatically moves an Open ticket to In Progress, and reopens a Resolved/Closed ticket to Open.
4. Change status manually from the dropdown on the ticket detail page.
5. The agent is emailed automatically whenever Support replies.

To be notified by email when an agent opens a new ticket or replies, set `Support:NotificationEmail` in the Web app's Azure App Settings. If it isn't set, tickets still save normally — the notification is just skipped.

## Reports

Use the reporting screens to review:

- Agent statistics
- Revenue by period
- Subscription status

## Job Scheduler

**Job Scheduler** (Super Admin only, under **System**) opens the Hangfire dashboard — a view of the same background job queue the agent portal runs on (newsletter dispatch, drip campaigns, calendar reminders, subscription billing, domain automation, recurring invoices). It shows job history, lets you retry or delete failed jobs, and trigger jobs manually. Access is restricted to the Super Admin role; a Support-role admin cannot open it.

