# Legal pages — what changed and what needs a lawyer

Drafted 15 August 2026. Source of truth for the rewrite was the live agreement at
`src/IPRO.Web/wwwroot/content/ipro_agreement.txt`, shown inside the signup form at
`src/IPRO.Web/Views/Account/Register.cshtml:364`, plus verified platform behaviour.

**Neither document has been reviewed by a lawyer. Do not publish either without that
review.** What follows is what a reviewer should look at first.

---

## 1. THE ONE THAT MATTERS — clause 7 of the live agreement

Every agent who has signed up has agreed to this, verbatim, as it stands today:

> "By posting messages, uploading files, inputting data, or engaging in any form of
> communication on our system, you are hereby granting **to the public** an unrestricted
> licence to use, copy, modify, adapt or document in any form any communications,
> information or any underlying work in which you may possess proprietary rights, including
> but not limited to copyright rights. All users of the system are therefore deemed to have
> **disclaimed or waived all copyright ownership rights** in their messages or files, even if
> they contain copyright notices."

Read literally, an adviser who uploads a client list has granted the general public an
unrestricted licence to it, and waived copyright in everything they write on the platform.

Why this is urgent, in order:

1. **It contradicts what we are about to advertise.** The new homepage says "Your data leaves
   as easily as it arrives — no hostage-taking on the way out." Clause 7 says the opposite and
   worse. A prospect who reads both has been misled, and the marketing claim is the one that
   is easy to screenshot.
2. **It may put subscribers in breach of their own obligations.** Insurance advisers,
   accountants and mortgage brokers owe clients confidentiality under their regulators' rules
   and under PIPEDA. Purporting to licence client data to the public is not something they
   can validly agree to. A regulator that saw this would not be looking at us alone.
3. **It is almost certainly unenforceable, which does not help us.** Unenforceable does not
   mean harmless — it means we have collected signatures on something that will not hold, and
   that reads as either careless or predatory depending on who is reading.
4. **It is boilerplate.** Clause 6 lists "bulletin and message board services, chat areas,
   news groups, forums, communities, photo libraries." IPRO has none of those. The whole
   agreement appears to be a 1990s ISP subscriber contract, and clause 7 was written for
   public message boards, where a public licence at least made some sense. It was never
   written for a CRM holding client files.

**Section 4 of the new Terms replaces it entirely**, with: you own your content; iPro gets a
narrow licence to host and process it only to run the service; no public licence; no
copyright waiver; export whenever you like; we own the software and templates.

**Ask the reviewer specifically:** do existing subscribers need to re-accept, or is notice of
the change enough? Clause 3 of the old agreement lets us change terms on notice, which
probably covers it — but this change is materially in the subscriber's favour, and it is worth
being able to show that we corrected it deliberately and told people.

## 2. Other substantive changes from the old agreement

| Old | New | Why |
|---|---|---|
| §2 — we may "delete all program and data files associated with your account" at any time, no notice | Terms §8 — 30-day window after cancellation, export tools stay available, deletion on request or in course | The old clause contradicts the data-portability promise and is disproportionate |
| §4 — "opportunity to pay by credit card" | Terms §2 — PayPal only, card details never reach iPro | PayPal is the only payment method; the old text described something we do not do |
| §4 — silent on the setup fee | Terms §2 — setup fee, and the fact it may be waived | Waiver logic is real (`BillingRule.SetupFeeWaived`) and needs to be disclosed |
| §6 — bulletin boards, chat, news groups, forums, photo libraries | Terms §6 — acceptable use rewritten for what the platform is | Those features do not exist |
| §7 — public licence, copyright waiver | Terms §4 — subscriber owns content, narrow operational licence | See section 1 above |
| §8/§9 — unlimited liability exclusion | Terms §9 — exclusion **plus** a 12-month-fees cap **plus** carve-outs for fraud and personal injury | An exclusion with no cap and no carve-outs is weaker in Canada than one with them |
| §3 — changes effective "immediately upon notice" | Terms §11 — 30 days' notice for material changes | Immediate effect on a consumer-facing contract invites challenge |
| Nothing on AI | Privacy §3 | The AI assistant sends content to Anthropic. Undisclosed cross-border processing is a PIPEDA problem |
| Nothing on client data | Terms §5, Privacy throughout | The controller/processor split is the single most important thing to get right for this product |
| Nothing on CASL | Privacy §5 | The consent machinery is built and works — it should be described |
| No privacy policy at all | New document | PIPEDA requires one |

**Kept deliberately:** Ontario governing law and jurisdiction; the downtime credit structure
(first 60 minutes free, capped at one month's fee); 9–5 Eastern support hours; the
substance of the acceptable-use list; entire-agreement and acknowledgement.

## 2b. Two questions from Bahman's 15 Aug edits — please answer these

**(a) The Privacy Commissioner sentence.** Bahman's replacement wording for Privacy s.8 was:

> "If you are unhappy with how we have handled your information, tell us first — we try our best to
> help you out. You could also cancel your services with us under 'Cancel anytime'"

His wording is in, and **the Office of the Privacy Commissioner sentence is out** — I had initially
retained it, raised the point with him, and he asked for it removed. Decision made; recorded here so
a reviewer knows it was considered rather than overlooked.

The argument that was put to him, for the reviewer's benefit: PIPEDA gives individuals a statutory
right to complain to the Commissioner. That right exists whether or not the policy mentions it, so
omitting it removes nothing legally — the question is presentation. Offering cancellation as the
alternative route is the part worth a second opinion, since cancelling a subscription is not a
privacy remedy, and it is not available at all to the *other* group this policy covers: a
subscriber's own clients are data subjects here but have no subscription to cancel.

**If counsel wants it back, it is one line in `Views/Shared/_LegalPrivacy.cshtml` section 8.**

**(b) Which company is the legal entity?** Both documents say **iPro Advisers Inc.** throughout.
The registered address Bahman supplied begins **iPro Accountants**:

> iPro Accountants, 3230 Yonge Street, Suite 2005, Toronto, ON M4N 3P6

It is rendering exactly as given, so the contact block currently reads "iPro Advisers Inc. /
iPro Accountants, 3230 Yonge Street…", which looks like a care-of. That may be right if the office
is shared, or the contracting entity may actually be iPro Accountants. **A contract naming the wrong
entity is worth catching now.** Confirm the registered name, and whether the address line should
keep "iPro Accountants" on it.

## 3. Placeholders that must be filled before publishing

- **Legal entity name.** The old agreement uses "iPro advisers Inc.", "Ipro Advisers" and
  "Ipro advisers" interchangeably. I standardised on **iPro Advisers Inc.** — see §2b(b), which
  is now a live question rather than a tidy-up.
- **Azure region.** Now reads "Eastern Canada" per Bahman. The repo shows `"Location": "Canada East"`.
  Still worth confirming *every* resource sits there — if any does not, the table must say so.
- **Discontinuation notice period.** Terms §3 offers 90 days. Confirm that is acceptable.

Resolved 15 August 2026: registered address, privacy email, support email (one inbox, his call),
data region, and the Anthropic training question (the caveat marker is removed from Privacy §3).
**Only the effective date is still blank**, which is what keeps the draft banner honest.

## 4. Wiring the pages up — DONE 15 August 2026

Implemented; the markdown files in this folder are now the reviewer's copy, and the shipped text
lives in Razor partials. **If the reviewer edits the markdown, the change must be carried into the
partials — they are what the site actually serves.**

| File | Role |
|---|---|
| `Views/Shared/_LegalTerms.cshtml` | **Canonical Terms text.** Rendered by /terms *and* by the signup box |
| `Views/Shared/_LegalPrivacy.cshtml` | **Canonical Privacy text.** Rendered by /privacy |
| `Views/Shared/_LegalLayout.cshtml` | Page shell, draft banner, typography |
| `Views/Home/Terms.cshtml` / `Privacy.cshtml` | Thin wrappers |
| `HomeController.Terms()` / `.Privacy()` | `[HttpGet("terms")]`, `[HttpGet("privacy")]` |

- The signup box and `/terms` render **the same partial**, so the text a subscriber accepts and the
  text we publish cannot drift. The old `wwwroot/content/ipro_agreement.txt` read is gone.
- The signup checkbox now links out to both `/terms` and `/privacy` (new tabs), in addition to the
  full text shown inline above it.
- Footer links added to `Home/Index.cshtml`.
- Superseded agreement archived at
  `DOCS/legal/archive/2026-08-15-superseded-online-subscription-agreement.txt`. **Keep it.** If a
  subscriber ever disputes what they agreed to, you need the text as it stood on the day they
  ticked the box. Archive each future version the same way before replacing it.

### The draft banner and the placeholders

Both pages show an amber **"Draft — pending legal review"** banner and carry `noindex`, and every
unfilled value renders as a yellow highlight. This is deliberate: the pages can go live today
without pretending to be finished.

Fill these Azure app settings, then set `Legal__ReviewComplete=true` to clear the banner:

Four of the six are now filled in `src/IPRO.Web/appsettings.json` (they are public business details,
not secrets, so they live with the code rather than in Azure config). **Two remain:**

All six are now set. **Nothing is left blank**, and `Legal__ReviewComplete` stays `false`, so both
pages carry the "Pending legal review" banner and `noindex` until counsel reports back.

Note the banner wording changed when the effective date went in on 15 August 2026. It previously
said the documents were "not yet the operative agreement" -- that stopped being true the moment they
acquired an effective date and became the text people accept at signup. Claiming otherwise on the
same page as an effective date would have been a contradiction a reviewer should not have to catch.

Already set, 15 August 2026:

```
Legal__RegisteredAddress  iPro Accountants, 3230 Yonge Street, Suite 2005, Toronto, ON M4N 3P6
Legal__PrivacyEmail       privacy@iproadvisers.com
Legal__SupportEmail       privacy@iproadvisers.com  -- Bahman confirmed one inbox serves both
Legal__DataRegion         Eastern Canada
Legal__ReviewComplete     false  -- set true LAST, once the two above are filled and counsel
                          has answered section 2b
```

### Two things noticed while wiring this up

1. **`Support:NotificationEmail` in `src/IPRO.Web/appsettings.json` is still the literal
   `CHANGE_THIS_SUPPORT_EMAIL`.** It may be overridden in Azure — worth confirming, because if it
   is not, support notifications are going nowhere.
2. **`BillingCompany` has a name, email and website but no postal address.** Invoices and the legal
   pages both want one. Consider making `Legal__RegisteredAddress` the single place it lives and
   having invoices read from it too, rather than adding a second copy.
