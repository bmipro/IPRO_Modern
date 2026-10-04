# E-Cards

## What It Is

A quick way to send a client a pre-designed occasion card by email — pick a card design, add a short personal message, and it goes out with your own contact card (name, designation, company, phone, cell, fax, email, website, photo) attached underneath. Nothing to design or upload; the artwork is supplied, and your details are filled in from your profile.

Two kinds of card to pick from:

- **Simple** (4 designs) — a clean colour panel in your own accent colour: Birthday, Thank You, Season's Greetings, Congratulations. These suit any occasion and any tone, and they're listed first because most of the time they're all you need.
- **Illustrated** — artwork for specific occasions. The 2026 collection adds 40 designs across 20 occasions: **Birthday**, **Thank you**, **Congratulations**, **Welcome (new client)**, **Anniversary**, **New home**, **Retirement**, **New baby**, **Get well**, **Sympathy**, **Season's greetings**, **Christmas**, **New Year**, **Thanksgiving (Canada)**, **Canada Day**, **Easter**, **Diwali**, **Hanukkah**, **Lunar New Year** and **Eid** — alongside the original **Halloween** (7 designs), **Anniversary** (2) and **Birthday** (1). Nine more, including two for **Norooz**, are in the library but switched off until their picture licences are confirmed.

## Send an E-Card

1. Select **E-Cards** in the Agent Portal menu (under Marketing).
2. Click **Send an E-Card**.
3. Pick a card design from the gallery — the live preview on the right updates as you choose and shows the whole card, scaled down to fit beside the gallery. Use **Show** above the gallery to list one occasion's designs. Choosing a design fills in that card's stock greeting for you, replacing the previous card's stock greeting if you left it as it was; if you've typed your own wording it's left alone.
4. Edit the email subject and message if you want. Both show directly on the card itself, so keep them brief. Some designs have their words in the picture, and the page says so when you pick one:
   - **Lettering cards** ("THANK YOU", "WELCOME", "happy birthday") have the title drawn into the picture, so your subject is only the email's subject line and your message shows below the picture.
   - **Four cards have the whole greeting printed in the picture** (the songbird thank-you, the red-rose anniversary, the city-lights congratulations and the Christmas sleigh). That wording can't be changed, so there is no message box for them.
5. Select which clients should receive it. Only clients with an email address on file can be selected — use the search box to filter a long list.
6. Choose **Send now** or **Schedule for later** (uses your profile's time zone).
7. Click **Send E-Card**.

## What Recipients See

The card face, then your greeting on the card's own background, then your contact block — details on one side, your photo on the other. The greeting is never printed over the picture: text on top of artwork is hard to read, and how hard depends on the design and on how much you typed, so it gets its own clear space instead. The exception is a design whose words are part of the picture, drawn as one piece: a lettering card shows only your message below it, and a card with the greeting printed in the picture shows nothing below it, so nothing is said twice.

Every picture is shown whole, at most 620 pixels wide. On a phone the card fills the screen's width: the picture shrinks to fit, the words keep their full size, and your photo moves under your contact details. Outlook on Windows shows the card at its full width, as before. A small picture keeps its own size and sits on the card's background with a margin rather than being enlarged. The animated Norooz goldfish plays in most mail apps; older Outlook versions on Windows show only its first frame, which is the whole picture.

Every card carries the platform's own open pixel, and its links pass through a short redirect on app.iproadvisers.com so Email Activity can show Opened and Clicked (488); the unsubscribe link is left exactly as issued. A reply goes straight to your inbox.

## Notes

- Your **photo, designation, company, phone, fax, cell, email, and domain** come from your Profile — update them there, not per e-card. A missing field is simply left out rather than shown blank.
- Your **accent color** (Agent Portal → **My Website** → **Portal Accent Color**) colours the Simple cards and tints the links in the contact block.
- Illustrated artwork is served from the portal, so a recipient's mail client needs to load images to see it. Your greeting and contact details are real text, not part of the picture, so they still read if images are blocked. Where the words are part of the picture, the picture's description (what a mail app shows when images are blocked, and what a screen reader reads) is those words, and the plain-text version of the email carries them too.
- Sending is per-recipient (like Newsletter), so one bounced or invalid address never blocks the rest of the list.
- Scheduled e-cards are picked up within a minute of their scheduled time by the same background dispatcher that sends Newsletters and Did You Know follow-ups.

## For SuperAdmin

**Agent Portal Admin → Cards & Letters → E-Card Designs** manages the whole library.

- **New Design** — pick Artwork (an illustration you upload) or Simple (a colour panel with an emoji), set the occasion, name, default greeting, and sort order.
- **Occasion** groups the design in the agent's picker, so cards for the same occasion sit together.
- **Greeting background** should match the artwork — dark for a night scene, light for pale artwork — because the greeting sits below the picture on that colour.
- **Words in the picture** (under Artwork) says whether the picture carries its own words: **None** (the greeting goes below the picture), **the title is lettered in** (only the message goes below) or **the whole greeting is in the picture** (nothing goes below, and agents can't change the wording). For the last two, keep the default greeting matching the picture: it becomes the picture's description and the email's plain-text version.
- Uploading artwork records the picture's size, so the card is drawn at the picture's own width (620 pixels at most). Designs uploaded before October 2026 have no recorded size and are drawn 620 pixels wide; upload the picture again to record it.
- **The 2026 collection** arrived on its own: 49 designs added after the existing ones, never changing a design already in the library. The nine pictures the owner supplied (the three birthday lettering cards, the red-rose anniversary, the city-lights congratulations, the Christmas sleigh, the rose thank-you and both Norooz cards) arrive **Retired** because their licences are not in the designer's package; offer each one (the eye icon) once its licence is confirmed.
- **Retire** (the eye icon) stops offering a design to agents. It is never deleted: every e-card already sent finds its artwork through the design's key, so removing one would blank the picture in an agent's history. A card already scheduled with a retired design still goes out with that design.
- The **key** is permanent once saved. Changing it would orphan every card sent with the old one.

**Card & Letter Activity** shows the most recent 200 e-card and e-letter sends across all agents, with delivered and failed counts. It shows recipient counts only, never who an agent wrote to.
