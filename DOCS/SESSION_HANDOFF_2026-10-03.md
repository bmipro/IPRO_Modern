# Session handoff -- 2026-10-03

## What happened

- **541 settled** (his answers: "40 ok, December first by Nov 15, assignment + waiver, English only"; the fee
  stays out of the brief, his to agree with the designer). The PDF he forwards
  (`Documents\iPro_ECard_Design_Brief_2026-10-02\iPro_ECard_Design_Brief.pdf`), its text, the Claude doc and
  `DOCS/ECARD_DESIGN_BRIEF.md` carry them: a Dates section (part 1, the 9 December designs, finished by
  15 November 2026; part 2 on a date agreed with the designer), the rights as an assignment plus a waiver of
  moral rights in a signed agreement, English greetings, one zip per part. The comment on the doc about the 40
  designs is answered and resolved.
- **The SES pilot, test 2 passed:** INV-1014 reached his Gmail Inbox, SPF, DKIM and DMARC all PASS (DMARC is
  monitor-only, `p=none`), delivered in 0 seconds from Amazon's Canada servers; INV-1015 reached his business
  mailbox (not Junk) with Reply-To him; both opened on his Android phone too. He has no Yahoo or Outlook.com
  mailbox he reads.
- **Found on the way: his iPro adviser account's email is a Yahoo address he does not read.** That address is
  the Reply-To on every client email and where iPro's notices, password resets and billing mail go, so his
  clients' replies were going unseen. He was told to change it on his Profile to his business address,
  and to look in the Yahoo mailbox for replies already there.
- **Test 3, first send:** EST-1003 to a test client at that Yahoo address landed in Yahoo's **Spam** with its
  links disabled; iPro showed it delivered, then viewed three times and approved. He marked it Not spam. Not a
  verdict alone (his own address, now trained), but the likely factors are real for clients: a free-mail
  Reply-To under a business From, a body that was one button, HTML with no text part, a days-old domain.
- **542 built and gated, HELD for the next push** (his word: "go, fix it with the next push"): every email gets
  a plain-text part, and the client emails read as a letter from the adviser (TODO 542). A sample rendered from
  the built code looks right (greeting, details, button, sign-off with phone, email and address).

## Pushed today

Nothing yet. Held locally for the next push: the 541 and pilot docs commits, and 542.

## Do this first when the owner is back

1. **542 goes out with the next push** (with the held docs commits). After the deploy: one invoice to his Gmail
   test client should read as the letter, and Gmail's Show original should list a text/plain part.
2. **His Profile email:** if it still shows the Yahoo address, his clients' replies and iPro's notices go there.
3. **The pilot:** test 3's other three (**Send reminder** on an overdue invoice, **Invite to Portal**, **Request
   Testimonial**) to his Gmail test client -- Bob Moore holds the Yahoo address since the estimate test, so it
   goes back to the Gmail one first. Then Part 2 (his setting change, `Email__Ses__Streams` = `notify,news`) and
   the decision (runbook 7a).
4. **541:** ready to send; the fee is his to agree with the designer. Part 1 is due 15 November: load it the week
   it arrives.
5. **539, his two to decide** (`friends_group.jpg`, `wall.JPG`) and the stock-image licences, as in 10-02's list.
6. **536, Monday 2026-10-05:** his follow-ups mail around 7 a.m. Eastern.
7. **Carried, and the calendar:** as in `DOCS/SESSION_HANDOFF_2026-10-02.md`, items 7 and 8.

Related: `DOCS/TODO.md` 541 and 542; `DOCS/SES_GO_LIVE_RUNBOOK.md` 7a (tests 2 and 3); `DOCS/SESSION_HANDOFF_2026-10-02.md`.
