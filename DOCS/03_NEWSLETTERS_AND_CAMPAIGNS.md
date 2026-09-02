# Newsletters and Campaigns

## Create a Newsletter

1. Select **Newsletter** in the Agent Portal.
2. Click **Create Newsletter**.
3. Optionally choose **Start from a template** to pre-fill the subject and body from one of Super Admin's starter templates, or start blank.
4. Optionally enter a topic and click **Draft with AI** to get a starting subject line and body written for you — review and edit before saving, it's a starting point, not a finished newsletter. This is a Platinum/Broker package feature; on Silver or Gold the button explains the upgrade needed instead of drafting.
5. Enter the subject line.
6. Optionally enter an **Edition** (e.g. "November 2026 Newsletter") and choose a **Banner Image** from the shared starter-banner gallery — the same stock photo library used for website Hero blocks. Leave Edition blank to default to the current month automatically.
7. Compose the HTML newsletter body in the rich editor (formatting toolbar, or switch to raw HTML source).
8. Add a plain-text version for email clients that do not display HTML.
9. Save as a draft.

Every newsletter sends inside a branded wrapper: your banner (if chosen), a colored title bar showing the Edition and your website link (using your own accent color from the portal color picker), your content, then a footer with your name, company, phone, and email — plus your profile photo next to your name if you've uploaded one (Agent Portal → **My Profile** → **Photo**). You only ever compose the middle part — the wrapper is added automatically at send time, so **Preview** and **Test Send** both show the exact finished email, banner and footer included. This wrapper applies to newsletters only, not drip campaign steps (see below).

Super Admin manages the library of starter templates agents can choose from (see `07_SUPER_ADMIN.md`).

## Add Extra Articles to a Newsletter

Beyond the main subject/body, a newsletter can carry extra article cards (each with its own title, image, and content) that render below the main body inside the same branded wrapper. From the **Edit** page:
- **Insert from Articles**: pick one of your existing library Articles from the dropdown and click Insert — copies its title, image, and content in as a new article card. Fastest way to reuse something you already wrote for Did You Know or a Drip Campaign.
- **Add Article**: write something fresh, specific to this one newsletter issue, right there in the form (own rich-text editor, own optional image).

Either way, remove one with the trash icon next to it; order follows the order you added them in.

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

## Write a Reusable Article

1. Select **Articles** (under the Marketing section of the left nav).
2. Create an article: title, a short summary (used as the teaser line on the website's Did You Know block), and content with the full rich-text editor (bold, italic, underline, lists, links).
3. Optionally attach an image and mark it Published.
4. Articles aren't sent anywhere on their own — pick published articles directly on a Did You Know website block, attach one to its own page with the Article Page block, pull one into a Drip Campaign step, or insert one into a newsletter. Write once, reuse everywhere.

## Create a Drip Campaign

1. Select **Campaigns**.
2. Create a campaign name and description.
3. Add steps in the order they should be sent.
4. For each step, set the subject, content (same rich editor used for newsletters), and delay in days.
5. Alternatively, pull in an existing newsletter, form, or **Article** as a step instead of writing fresh content.
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

### When Each Step Goes Out

- **Step 1** is sent within a minute of enrolling the recipients (since 2026-09-02). Before that, the first step waited for the hourly run.
- Later steps are sent by the hourly run once their delay has passed.
- Each step's delivery is under **Email Activity → Campaigns**: one row per step, with the recipients and their delivered / opened status.
