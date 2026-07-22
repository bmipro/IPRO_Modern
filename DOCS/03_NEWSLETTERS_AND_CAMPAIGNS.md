# Newsletters and Campaigns

## Create a Newsletter

1. Select **Newsletter** in the Agent Portal.
2. Click **Create Newsletter**.
3. Optionally choose **Start from a template** to pre-fill the subject and body from one of Super Admin's starter templates, or start blank.
4. Enter the subject line.
5. Optionally enter an **Edition** (e.g. "November 2026 Newsletter") and choose a **Banner Image** from the shared starter-banner gallery — the same stock photo library used for website Hero blocks. Leave Edition blank to default to the current month automatically.
6. Compose the HTML newsletter body in the rich editor (formatting toolbar, or switch to raw HTML source).
7. Add a plain-text version for email clients that do not display HTML.
8. Save as a draft.

Every newsletter sends inside a branded wrapper: your banner (if chosen), a colored title bar showing the Edition and your website link (using your own accent color from the portal color picker), your content, then a footer with your name, company, phone, and email. You only ever compose the middle part — the wrapper is added automatically at send time, so **Preview** and **Test Send** both show the exact finished email, banner and footer included. This wrapper applies to newsletters only, not drip campaign steps (see below).

Super Admin manages the library of starter templates agents can choose from (see `07_SUPER_ADMIN.md`).

## Edit, Duplicate, or Reuse a Newsletter

1. Open **Newsletter**.
2. Locate the newsletter.
3. Click **Edit** to change the reusable content.
4. Click **Duplicate** to create a separate version without overwriting the original.
5. Save before previewing or sending.

The newsletter is reusable. Each send creates a separate send record with its own audience, schedule, and tracking.

## Preview a Newsletter

1. Open the newsletter.
2. Click **Preview**.
3. Review the subject, formatting, links, images, and mobile readability.
4. Return to edit if changes are needed.

## Send a Test

1. Open the newsletter preview.
2. Click **Test Send**.
3. Check the agent's current profile email, including spam or junk folders.
4. Correct any formatting or links before sending to clients.

## Choose the Audience

Click **Send** and choose one audience:

- **All newsletter subscribers** sends to all opted-in clients.
- **Account type / group** sends to opted-in clients assigned to one account type.
- **One individual client** sends to that client only if they are currently opted in.

Only clients with usable email addresses are included. All three audience choices respect each client's **Newsletter subscribed** setting on their client profile — a client who has unsubscribed (or was never opted in) is skipped by every audience type, not just "All newsletter subscribers."

## Send Now

1. Choose the audience.
2. Select **Send now**.
3. Confirm the send.
4. The send enters the dispatch queue and then records recipients and delivery events.

## Schedule for Later

1. Choose the audience.
2. Select **Schedule for later**.
3. Choose a future date and time in the agent's profile time zone.
4. Confirm the schedule.

To stop a future send, open the newsletter's send history and select **Cancel** before dispatch begins.

## Review Delivery Tracking

Open the newsletter preview and review the send history, which now shows open rate and click rate percentages alongside the raw counts:

- Recipients, sent, opened, and open rate
- Clicked and click rate
- Delivered, failed, deferred, bounced, or rejected (recipient-level detail below the send history table)
- Provider response or issue

SendGrid event webhooks update these results. Open tracking can be affected by privacy protection, image blocking, and email client behavior.

## Subscribers and Unsubscribe

1. Open **Newsletter**.
2. Select **Subscribers** to review opted-in CRM clients.
3. Newsletter emails include an unsubscribe path.
4. An unsubscribe updates the CRM client's newsletter preference.

## Create a Drip Campaign

1. Select **Campaigns**.
2. Create a campaign name and description.
3. Add steps in the order they should be sent.
4. For each step, set the subject, content (same rich editor used for newsletters), and delay in days.
5. Alternatively, reuse an existing newsletter as a campaign step.
6. Edit, replace, reorder, or delete steps as needed.

Each campaign's **Performance** section shows sent, delivered, opened, and clicked counts plus open/click rate percentages per step, based on the same SendGrid delivery tracking used for newsletters.

## Enroll Recipients in a Campaign

1. Open the campaign.
2. Enroll either an account type/group or one individual client.
3. Activate the campaign.
4. Review enrollment and step progress.
5. Cancel an enrollment if the client should stop receiving the sequence.

Campaign access is controlled by package features.

Every drip campaign email includes an unsubscribe link scoped to that specific campaign. If a client clicks it, only their enrollment in that one campaign is cancelled (their status changes to **Cancelled** and future steps stop) — it does not affect their newsletter subscription or any other campaign they may be enrolled in.

