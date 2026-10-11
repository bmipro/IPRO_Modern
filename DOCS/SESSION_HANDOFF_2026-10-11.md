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

## Invoice tax and discounts (open, next)

- The owner reported that tax on client invoices "worked before" and that a tax check box "is GONE", after the
  bakery sent an invoice with no tax. Read in the code and its history: there was never a tax check box on the
  invoice screens, and the tax code has not changed since it was built. Tax is charged when the CLIENT's country is
  Canada and the client's province has an active rate (admin, Tax Rates); a client with no province is charged
  nothing, silently, and the editor shows no subtotal, tax or total. His own invoice to an Ontario client shows
  `ON 13% HST` on every line, which is that rule working.
- **What he asked for, with a screenshot (2026-10-11):** in the Tax column each item needs only a check box, the
  column thinner; the Tax summary box is enough to carry the tax's name and total.
- **Planned as 563:** a "taxable" tick per line (stored on the line; ticked by default), a narrow Tax column with a
  check mark on the document, the taxable amount in the Tax summary from the ticked lines, the subtotal, tax and
  total shown in the editor as it is filled in, and the business's own province used when the client has none.
  Carried through Duplicate, recurring invoices, the Xero export and the statement.
- **Discounts:** he is asking the bakery whether a discount applies to one item or to the whole invoice. Not built.

## Still his

- SES step 8.2: `Email__Ses__Streams` = `notify,news` on `ipro-prod-web` (today it is `notify`, six characters;
  eleven after). Seven days have passed since 8.1 and the log shows no SES failures. He reads Amazon's reputation
  figures first; we check by length and health after.
- The bakery: type the phone number again in the three call buttons. The Open-now line shows twice on their home
  page (two banner blocks); showing it once per page was offered and is unanswered.
- Refer a Friend is still OFF; "Shown in your emails as" for www.4iPro.com and www.LAvenueBakery.com.

## Not seen signed in

Email Activity's invoice rows and detail page (562); the leads page with its Delete buttons (434); the reviews rows
in the block editor (560); the Business hours card on My Website (559). The owner's first visit to each is its
first real look: fix whatever he finds.

Related: `DOCS/TODO.md` 561, 562, 467; `DOCS/SESSION_HANDOFF_2026-10-10.md`.
