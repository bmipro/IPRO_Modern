# iPro E-card Design Brief

*Written 2026-10-02 for an outside graphic designer (TODO 541). The owner: "can u come up with the requirement and
the contract so I could pass it on to the other graphic/template designer for our ecards, so we could focus on more
important stuff", and "include a current sample that we have so he could visualize it better". Everything from
"What we need" to "How the work flows" is the text the designer receives; the last two sections are ours.*

## What we need

iPro needs new artwork for its e-cards: one picture per design, delivered as a package that goes straight into the product.

iPro is a Canadian platform for financial advisers, accountants and mortgage brokers. An adviser picks a card, adds a short greeting and emails it to clients. You design the picture at the top of the card. iPro adds the greeting and the adviser's contact details underneath, so the picture itself carries no words.

## How a card is put together

An e-card is an email of three stacked panels, and you design only the first.

1. **The card face**: your artwork, shown full width, 620 pixels on a desktop screen and narrower on a phone.
2. **The greeting**: a title and a short message from the adviser, on a plain band directly under the picture. The band is near-black (#111111) or white (#ffffff), chosen per design to suit the artwork.
3. **The adviser's contact block**: name, company, phone, email, website and photo. iPro fills this in.

The greeting is never printed over the picture. Text on artwork is hard to read, and how hard depends on the design, the mail app and how much the adviser typed.

*(The sample picture of two current cards is in the owner's copy of this brief, not in the repository; see the end.)*

Two current cards as a client receives them, with a sample adviser. The roses were drawn for an older layout that printed the greeting on the white card in the picture; today that space stays empty, which new designs must avoid.

The library today holds 10 illustrated designs, all older and smaller than the new specification, plus 4 plain colour cards with no artwork.

| Occasion | Designs today | Artwork size |
| --- | --- | --- |
| Halloween | 7 | 700 x 525 |
| Anniversary | 2 | 467 x 311 |
| Birthday | 1 | 540 x 396 |

## What to design

The proposed first set is 40 designs across 20 occasions. Bahman confirms the list before work starts.

| Occasion | When advisers send it | Designs |
| --- | --- | --- |
| Birthday | All year | 4 |
| Thank you | All year | 3 |
| Congratulations | All year | 2 |
| Welcome (new client) | All year | 2 |
| Anniversary | All year | 2 |
| New home | All year | 2 |
| Retirement | All year | 2 |
| New baby | All year | 1 |
| Get well | All year | 1 |
| Sympathy | All year | 1 |
| Season's greetings (winter, not religious) | December | 4 |
| Christmas | December | 2 |
| New Year | Late December, January | 2 |
| Thanksgiving (Canada) | Early October | 2 |
| Canada Day | July 1 | 2 |
| Easter | Spring | 2 |
| Diwali | October or November | 2 |
| Hanukkah | December | 1 |
| Lunar New Year | January or February | 2 |
| Eid | Date moves each year | 1 |

The sender is a professional writing to a client, and the client may be 30 or 80. That sets the style:

- Warm, calm and adult. Nothing childish, gory or jokey.
- One clear subject per picture. It has to read at the size of a playing card on a phone.
- Illustration or photography are both welcome, but keep one look within an occasion so its designs sit together.
- Within an occasion, make the designs different in mood, not variations of one picture.
- For a religious or cultural occasion, use its own well-known symbols respectfully. Season's greetings stays free of them.

Not wanted in any picture: brand names, logos or trademarks (cars, teams, cartoon characters), recognisable real people, flags other than Canada's, alcohol, money shown as cash, or anything political.

## Artwork specification

Every design is one landscape picture, 1240 x 930 pixels, with no words in it.

| Item | Requirement | Why |
| --- | --- | --- |
| Shape | Landscape, 4 : 3 | The card picker shows every design as a 4 : 3 thumbnail |
| Size | 1240 x 930 pixels | Shown 620 pixels wide in the email; twice that keeps it sharp on phones and high-density screens |
| Format | JPEG, sRGB colour, no transparency | Every mail app shows it the same way |
| Weight | 300 KB or less per file | It is downloaded inside an email, often on mobile data |
| Words | None: no greeting, no lettering, no dates | The adviser's own greeting sits under the picture and can be changed; words in a picture cannot |
| Space for a message | None | The greeting has its own band; an empty area in the picture stays empty |
| Edges | The picture fills the whole rectangle: no border, frame, shadow or rounded corners | The card rounds the top corners itself |
| Safe margin | Keep anything important 30 pixels inside every edge | Rounded corners and some mail apps trim the edge |
| Lower edge | Must sit well on the design's ground: near-black (#111111) or white (#ffffff) | The greeting band starts directly under the picture in that colour |
| Small size | The subject still reads at 335 pixels wide, and as a thumbnail 150 pixels wide | That is a phone, and the adviser's card picker |
| Master file | One layered or vector source per design, at 2480 x 1860 or larger | A later size change should not need a redraw |

Choosing the ground is part of the design. A night scene or a rich dark picture takes the near-black ground; a pale or airy picture takes white. A bright picture cut straight onto black, or a dark one onto white, reads as two unrelated pieces.

## What comes with each design

Each picture comes with six facts, which iPro types into the product exactly as given.

| Fact | Rule | Example |
| --- | --- | --- |
| Key | Lower-case letters, digits and hyphens. It is permanent and is also the file name | `birthday-balloons` |
| Occasion | One from the list above, spelled the same way | Birthday |
| Name | Two to four words describing the picture. It is read aloud to people who cannot see images | Balloons at dusk |
| Ground | `dark` or `light` | dark |
| Greeting title | 30 characters or fewer. Shown large, in italics, under the picture | Happy Birthday |
| Greeting message | One or two sentences, 140 characters or fewer | Wishing you a wonderful year ahead. |

The title and message are only the starting wording. The adviser can change both before sending, so write them to suit any client.

## Rights

iPro must be free to use every delivered picture in its product for good, for all of its customers, with no fee per use and no credit line.

- Each design is your own original work, or is built only from material you are licensed to use this way.
- A standard stock licence usually does not allow this: the cards are handed to every iPro adviser to send as their own. If you use a stock element, its licence must cover use in templates or products for resale, and a copy goes in the package.
- Say in the package if any part was made with an AI image tool, and which one. Use only tools whose terms give you commercial rights to the result.
- No brand names, logos, trademarks or recognisable real people, as listed above.
- On payment the rights in the delivered artwork pass to iPro. The exact wording, an assignment or an exclusive licence, is agreed with Bahman before work starts.

## Handover package

The deliverable is one zip, named `ecards-set-1`, holding five things. It maps one-to-one onto the product's design list, so nothing has to be reworked on our side.

1. **`art/`**: one JPEG per design, named by its key (`birthday-balloons.jpg`), 1240 x 930, 300 KB or less.
2. **`designs.csv`**: one row per design with the six facts, saved as UTF-8. The first row is the header shown below.
3. **`sources/`**: the layered or vector master of each design, named by the same key.
4. **`RIGHTS.md`**: one line per design saying it is original, or naming each outside element, where it came from and its licence. Licence copies go beside it.
5. **`contact-sheet.jpg`**: every design on one sheet with its key under it, for approval and for checking the delivery.

```csv
key,occasion,name,ground,title,message
birthday-balloons,Birthday,Balloons at dusk,dark,Happy Birthday,Wishing you a wonderful year ahead.
thank-you-wildflowers,Thank you,Wildflower bunch,light,Thank You,Thank you. It is a pleasure working with you.
```

Not accepted: words or lettering in a picture; a border, frame, shadow or watermark; a size other than 1240 x 930; a file over 300 KB; a PNG with transparency; stock material without its licence copy; a file whose name is not its key.

## Check before handover

Run this list before sending the package; Bahman runs it again on arrival.

- [ ] Every file in `art/` is 1240 x 930, JPEG, sRGB, and 300 KB or less.
- [ ] No picture contains words, a logo or a recognisable person.
- [ ] Every picture fills its rectangle: no border, no frame, no empty space kept for a message.
- [ ] Each picture sits well on its ground. Test it by placing a band of #111111 or #ffffff directly under it.
- [ ] Each picture still reads at 335 pixels wide and at 150 pixels wide.
- [ ] `designs.csv` has one row per file, each key matches its file name, titles are 30 characters or fewer and messages 140 or fewer.
- [ ] `RIGHTS.md` covers every design.

## How the work flows

The designer never touches iPro's code, servers or accounts. Questions go through Bahman.

1. You send a contact sheet of roughs for the whole set, one per design, with the ground marked on each.
2. Bahman approves each one or asks for changes.
3. You finish the artwork and deliver the package.
4. iPro loads the designs, sends itself a test card of each, and they appear in every adviser's card picker.

A correction after delivery is a new file with the same key; it replaces the old picture on every card sent from then on. A key is never reused for a different picture, because cards already sent find their artwork by it.

## For Bahman to settle before sending

Five points are open, and none of them is in the designer's copy.

- The occasions and the number of designs. The proposed set is 40.
- The dates: roughs, and the final package.
- The fee and how it is paid.
- The rights wording: an assignment to iPro or an exclusive licence.
- Whether the starting greetings are wanted in French as well as English.

## When the package arrives (build notes, not for the designer)

- **Where the brief is:** the owner's copy is `Documents\iPro_ECard_Design_Brief_2026-10-02\` (the PDF he forwards, this
  text, and `ecard-current-sample.jpg`); the same brief is a Claude doc he can edit and comment on, https://claude.ai/code/artifact/eb534620-59c6-412b-a884-311150fc9fb1.
  `make_sample.py` and `make_brief_pdf.py` in the session scratchpad made the sample and the PDF.
- **Check it first** against "Check before handover": the size, format and weight of every file in `art/`, one row
  per file in `designs.csv`, keys equal to file names, the title and message lengths, and `RIGHTS.md`.
- **Each row is one `ECardDesign`:** `Key`, `Occasion`, `Name`, `Kind` = `image`, `IsDark` = (ground is `dark`),
  `DefaultHeaderText` = title, `DefaultMessage` = message, `Width` 1240, `Height` 930, `SortOrder` after the
  designs already there. `SendAfterUnsubscribe` stays off; the owner ticks it per design (birthday, anniversary).
- **Where the artwork goes:** `src/IPRO.Web/wwwroot/images/ecard-art/<key>.jpg` with a site-relative `ImageUrl`, like
  the ten that shipped, or uploaded one at a time in SuperAdmin -> Cards & Letters -> E-Card Designs.
  `ECardDesignSeeder` only fills an EMPTY table, so a set this size needs its own one-time insert by key, which
  must never touch a key that already exists: the table is SuperAdmin's to manage (the seeder's own comment).
- **Why 1240 x 930:** `ECardHtmlComposer` shows artwork at `min(Width, 620)` pixels, so a 1240-wide file is 620 on
  screen at twice the density; the adviser's picker (`Views/ECards/Create.cshtml`) crops every thumbnail to 4 : 3.
- **The old designs** (700 x 525, 467 x 311, 540 x 396) stay as they are; retiring any of them is the owner's choice,
  in SuperAdmin (a retired design keeps rendering on cards already sent).
- The designer has no access to the repository, the servers or any account; questions go through the owner (the
  same rule as `DOCS/VERTICAL_PAGE_BRIEF_ACCOUNTANTS.md` and the template builder's developer).
