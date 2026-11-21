# E-Letters

## What It Is

A personalized letter sent by email on your own letterhead — longer and more personal than an [E-Card](18_ECARDS.md), shorter and simpler than a [Newsletter](03_NEWSLETTERS_AND_CAMPAIGNS.md). You start from a ready-written letter, edit the wording, and each recipient gets their own copy addressed to them by name.

| | Best for |
|---|---|
| **[E-Card](18_ECARDS.md)** | A short occasion greeting — birthday, thanks, holidays |
| **E-Letter** | A written message to a person — welcome, review request, renewal notice |
| **[Newsletter](03_NEWSLETTERS_AND_CAMPAIGNS.md)** | Regular publication with full articles, banner, and edition |

## Write and Send a Letter

1. Select **E-Letters** in the Agent Portal menu (under Marketing).
2. Click **Write a Letter**.
3. Pick a starting point — **Welcome / new client**, **Annual review request**, **Policy renewal reminder**, or **Thanks for the referral**. Each one loads a full draft you can rewrite however you like. (Choosing a different one replaces what's in the subject and body, so pick before you start editing.)
4. Edit the subject and body.
5. Select which clients receive it. Only clients with an email address on file can be selected — use the search box to filter a long list. A client who has unsubscribed from your emails can still be ticked, but is skipped when the letter goes out: Email Activity lists them as Failed, with the issue "Recipient has unsubscribed from these emails."
6. Choose **Send now** or **Schedule for later** (uses your profile's time zone).
7. Click **Send Letter**.

## Personalized Fields

The letter body can include fields that fill in differently for each recipient. Click any chip under the body to insert one at your cursor:

| Field | Fills in with |
|---|---|
| `[First Name]` | The client's first name |
| `[Last Name]` | The client's last name |
| `[Full Name]` | The client's first and last name |
| `[Company]` | The client's company, if recorded |
| `[Advisor Name]` | Your own name |
| `[Advisor Company]` | Your company name |
| `[Advisor Phone]` | Your business phone |
| `[Advisor Email]` | Your email address |

So `Dear [First Name],` arrives as *Dear Sarah,* for one client and *Dear Michael,* for the next. Fields are matched loosely — capitalization and extra spaces inside the brackets don't matter.

If a field has nothing to fill in (for example `[Company]` for a client with no company recorded), it comes out blank rather than printing the bracket text — worth keeping in mind when writing a sentence around it.

**Preview:** tick a client in the list and the preview pane shows the letter exactly as that person will receive it, with their real name filled in. With several clients ticked, it uses the first one ticked in the list (the list runs by last name), and the line above the preview says **Previewing as** that client's name. With nobody selected, the preview shows the raw fields instead.

## What Recipients See

Your letter on a clean letterhead — a thin accent bar in your brand color, your company name and address at the top, the letter body, and a signature block with your name, designation, phone, cell, email, and website. Replies go straight to your inbox.

The website is your custom domain once it is live, written the way you typed it in **Shown in your emails as** under
**The address on your emails** on the **Domain** page; until then, your temporary domain. If you keep your own
website, it is the address saved under **Website address** on **My Website**.

Every letter ends with the same closing lines as an e-card: that the person is receiving it as your client, your business name and mailing address (from your Profile), a link to unsubscribe or change what they receive, and "Sent with iPro on behalf of" your business. While your Profile has no street address, the page warns you (**Add your business street address.**) and letters show only your city, province and postal code.

The email's subject starts with your business name, for example "YourCompany: Time for your annual review, Sarah". The business name is the company name on your Profile, or your own name if there is none; a subject that already names it is left as it is.

On a phone the letter fills the screen's width and the text keeps its full size, so nobody has to zoom in to read it. Outlook on Windows shows it 620 pixels wide, as before.

## Notes

- Your **accent color** is the portal colour you pick with the coloured dots at the bottom of the left menu, above your name. It sets the letterhead bar. It's the same color used on Newsletter and E-Card emails.
- Your **photo, designation, company, address, phone, cell, email, and domain** come from your Profile — update them there, not per letter.
- Each recipient gets an individually addressed copy, so one bad address never blocks the rest of the list.
- Scheduled letters are picked up within a minute of their time by the same background dispatcher that handles Newsletters and E-Cards.

<!-- staff-only -->
## For SuperAdmin

**Agent Portal Admin → Cards & Letters → E-Letter Templates** manages the starter letters.

- Templates are **starting points, not locked content**. An agent's letter takes its own copy of the subject and body at the moment they open the composer, so editing a template here changes what the *next* agent starts from and never rewrites a letter already written or sent.
- The **merge-field chips** in the editor insert at your cursor. Anything else in square brackets is rejected when you save — an unrecognised token like `[Advisor Mobile]` would reach the client as literal text rather than being filled in.
- **Retire** (the eye icon) stops offering a template to agents. Letters already sent keep their own wording and are unaffected.

**Card & Letter Activity** covers e-letters alongside e-cards — see [E-Cards](18_ECARDS.md).

<!-- /staff-only -->
