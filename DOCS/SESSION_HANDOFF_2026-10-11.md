# Session handoff -- 2026-10-11

## What happened

- **The template builder (467).** The owner agreed to two stages (the developer's computer first, a hosted Beta
  later; never straight to production), the look in two steps, and a go-ahead on the fixes. He asked for the
  communication to the developer in writing and for a hands-on pass of the packaged copy ("why dont you start the
  ps1 file"). Done on the packaged copy only, started and stopped by its own script. Findings are in the owner's
  Documents folder as `iPro_TemplateBuilder_Review_Findings_2026-10-11.md` (he forwards it): line breaks in body
  text are not rendered, the site as opened is not in the version history, the desktop view shows the phone menu at
  1024 px. Fixed since September: the nested page menu, and "Use this design" keeps the customer's content.
- **561, found while reading the live log before the SES step.** The rate limiter trusted a header any visitor can
  type. See the row in `DOCS/TODO.md`. The owner: "deploy what you have build".
- **562, from his two screenshots of Email Activity.** An invoice email now shows whether the client viewed the
  invoice, and the explanation under the table is rewritten.
- **The gate for 561 and 562 together.** Part 1's first run had ONE failure,
  `RetiredFeaturesSeptemberTests.The_four_withdrawn_rows_are_deleted_from_a_database_that_already_sells_them`: the
  local MySQL connection dropped mid-save ("The I/O operation has been aborted"). Parts 2, 3 and 4 passed; part 1
  was run again on the unchanged tree and passed. A test that touches neither change; if it drops again, look at
  the local database service before the code.

## Pushed

| Commit | What | Gate |
|---|---|---|
| `73a8297` | **566 and 567** a discount on a client invoice, on the whole of it or on each item; the invoice's figures sit together (live on build `b3a3e72`) | 1632/1632 |
| `fc59382` | **565** Email Activity opens on the last 30 days, 25 to a page, with a Needs attention tab and a search (live on build `1c57274`) | 1621/1621 |
| `dc366f7` | **563 and 564** tax is a tick on each invoice line; the invoice's dates are the dates typed (live on build `61cd45b`) | 1611/1611 |
| `7eced19` | **561** the rate limiter knows a visitor by the connection, never by a header (live on build `a551e51`) | 1597/1597 |
| `dd4ba34` | **562** an invoice email shows whether the client viewed the invoice (live on build `a551e51`) | 1597/1597 |

## Invoice tax (563, 564) and discounts

- The owner reported that tax on client invoices "worked before" and that a tax check box "is GONE", after the
  bakery sent an invoice with no tax. Read in the code and its history: there was never a tax check box on the
  invoice screens, and the tax code has not changed since it was built. Tax is charged when the CLIENT's country is
  Canada and the client's province has an active rate (admin, Tax Rates); a client with no province is charged
  nothing, silently, and the editor shows no subtotal, tax or total. His own invoice to an Ontario client shows
  `ON 13% HST` on every line, which is that rule working.
- **What he asked for, with a screenshot (2026-10-11):** in the Tax column each item needs only a check box, the
  column thinner; the Tax summary box is enough to carry the tax's name and total.
- **Built as 563 the same afternoon:** a Tax tick per line (ticked by default), a narrow Tax column with a box per
  line on the document, the taxable amount in the Tax summary from the ticked lines, the subtotal, tax and total
  shown in the editor as it is filled in (with the reason, in red, when no tax will be charged), and the business's
  own province used when the client has none. Carried through Duplicate, recurring invoices and the Xero file. Both
  editors got a phone layout (each line two rows) so the tick is not a sideways scroll away.
- **564, found on the way:** the invoice page showed the issue date and the due date one day early (calendar dates
  read as UTC moments). Fixed in the same commit; new documents are dated the adviser's own today. Invoices already
  sent now show dates one day later than their page did, which is what their emails always said: tell the owner.
- **Lesson:** a Razor view with a style block needs `@@media`; a single `@` builds as C#. The solution build before the
  last view was added passed, and only the local run of the app caught it. Build after the LAST edit, always.
- **Discounts:** built the same evening as 566 (below), once the bakery had answered: one discount on the whole invoice.
- **Offered, not built:** a GST/HST registration number on the invoice (there is no field for one; Canadian
  registrants are expected to show it), and a one-time "we do not charge tax" setting for a business that never does.

## Email Activity (565)

- The owner scrolled the list back to August and asked how to keep it manageable. Recommended four things; he said
  "30 days is good, build all four": a period (30 days by default), 25 to a page, a **Needs attention** tab, and a
  search by subject or recipient. Built the same evening; see the row in `DOCS/TODO.md`.
- Not built, offered: collapsing the one-at-a-time automatic emails into one row per day.

## Discounts (566) and the invoice's columns (567)

- The bakery wants one discount on the whole invoice; the owner wanted both kinds for other businesses and asked to be
  shown before any deploy. Built, shown as pictures from the local app, and changed once on his word: a discount on the
  items is a line of the totals ("Discounts"), not a small note under the lines. Then "looks good".
- One choice under the lines: None, Whole invoice, Each item. Tax after the discount; a whole-invoice discount is
  shared between taxed and untaxed lines. The two editors (invoice, recurring) now share one lines editor.
- 567: his two-line invoice had wide gaps between the figures; the description takes the spare width now.
- **568, built the same evening:** the editor remembers items. Clicking or typing in a line's Description offers the
  items this business has invoiced before, with the last price and Tax tick; picking one fills the line. No setup,
  no new screen, nothing new stored. A managed "My items" list was offered as the alternative and held back; a way
  to forget a mistyped item is the first thing to add if anyone asks.

## Still his

- SES step 8.2: `Email__Ses__Streams` = `notify,news` on `ipro-prod-web` (today it is `notify`, six characters;
  eleven after). Seven days have passed since 8.1 and the log shows no SES failures. He reads Amazon's reputation
  figures first; we check by length and health after.
- The bakery: type the phone number again in the three call buttons. The Open-now line shows twice on their home
  page (two banner blocks); showing it once per page was offered and is unanswered.
- Refer a Friend is still OFF; "Shown in your emails as" for www.4iPro.com and www.LAvenueBakery.com.

## Not seen signed in

The remembered items under a line's Description (568); the discount choice in the invoice and recurring editors (566); the Email Activity list with its period, pages, Needs attention tab and search (565); the invoice editor with its Tax ticks and live totals, and the recurring editor (563); Email Activity's invoice rows and detail page (562); the leads page with its Delete buttons (434); the reviews rows
in the block editor (560); the Business hours card on My Website (559). The owner's first visit to each is its
first real look: fix whatever he finds.

Related: `DOCS/TODO.md` 561, 562, 467; `DOCS/SESSION_HANDOFF_2026-10-10.md`.
