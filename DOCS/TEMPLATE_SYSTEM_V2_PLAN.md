# Website Template System — Next-Generation Plan

Working notes from the 2026-07-30 discussion about replacing dissatisfaction with the current
template system with a deliberate plan, before any code is written. This is a living planning
document, not a shipped-feature manual — update it as decisions are made or reversed.

## Why this is being revisited

Two separate complaints, not one:

- **The editing process** feels like confusing redundancy — multiple ways to reach the same
  outcome, no single clear model for "how a page is built."
- **The visual output** is inconsistent — quality varies by template (Modern/Classic/Editorial)
  and by whether a sidebar is present, rather than every combination looking deliberately designed.
  (Item 31/44/60/61/205/206/207 in the roadmap chased specific instances of this — hero fallback
  and section rhythm — template by template. That is treating the symptom; this document is about
  the cause.)

A third issue surfaced during this same discussion, underneath the two above: **the vertical
content population was never actually done.** Agents get a working page builder, but nothing
ships pre-populated per vertical (accountant, financial planner, mortgage broker, etc.) the way
the legacy predecessor platform did.

## What the legacy predecessor platform showed us

Two archived reference sites (found via web.archive.org, screenshots reviewed directly) confirmed
this is the same underlying platform family as IPRO_Modern (both carry "Powered by I Pro Advisers"
branding):

- **Accountant vertical**: `4ipro.com` (2019 snapshot)
- **Financial vertical**: `girardfinancial.com` (2022 snapshot)

Both are built around an **expandable multi-level sidebar navigation tree** — that sidebar *is*
the site's real information architecture, not a decorative rail. Both also share cross-vertical
widget types that our current block library doesn't have equivalents for:

- A "Did you know?" widget (we now have an equivalent — item 174-189).
- Embedded **calculators** (mortgage, retirement, loan, tax — see inventory below).
- A real "Get a Quote" lead-capture form (we have generic lead forms; not vertical-specific yet).
- A BBB trust-badge widget.
- Shared **articles** content, organized per topic.

This confirms the sidebar-as-navigation pattern and the calculator/article content types are a
real, repeated platform mechanism from the previous product generation — not a one-off design
choice we'd be inventing from scratch.

## Content inventory already on hand

Confirmed to exist under `X:\ipro_related\` (external to the git repo):

### Calculators (PHP)

- **`calculators/cal1/calculators/`** — ~48 plain-text, portable PHP calculators with matching
  JS files: mortgage, refinance, rent-vs-buy, retirement, Roth, credit score, APR, amortization,
  investment, estate tax, down-payment assistance, ARM-vs-fixed, bi-weekly, blend, and more. No
  encoding — directly readable and rewritable into whatever the eventual calculator block type
  looks like. **Treat this as the source of truth.**
- **`calculators/cal/`** — a separate, smaller bundle, **ionCube-encoded** (confirmed by reading
  `calcus.php` — it opens with the ionCube loader stub, not plain PHP). Requires the exact matching
  ionCube Loader binary to even execute; not portable or rewritable without that runtime. The
  folder bundles loader binaries for multiple PHP versions/platforms, so it is technically runnable
  in isolation, but redundant with `cal1` and should not be the basis for the port.

### Per-vertical content (Word documents)

| Folder | Contents | Count |
|---|---|---|
| `Client Paragraphs/` | Short blurbs per product, written for existing clients (Investment, RRSP, RESP, all insurance types) | 13 |
| `Prospect Paragraphs/` | Same product set, pitched at prospects instead | 11 |
| `Edited Articles/` | Longer-form polished articles, same ~13 topics — the deep-read counterpart to the short paragraphs | 17 |
| `IPro_accountants/` | The accountant vertical's actual legacy content: `Did_U_Know/`, `Articles/` (tax records, audits, corp tax rates), plus `side_menu.doc`/`top_menu.doc` (the literal sidebar structure), `faq.doc` | ~15 |
| `Paul_Words/` | Marketing/site copy — Home, About Us, Why Us, per-professional-type pitches (insurance/mortgage/finance professionals), pricing, taglines, ad banners; an `iproadvisers/` subfolder looks like the finalized copy set for iproadvisers.com specifically | ~30 |
| `Ideas/` | `crm_ideas.docx` — product/feature notes, not site content | 1 |

Between `Client Paragraphs` + `Prospect Paragraphs` + `Edited Articles`, most financial-product
verticals are covered at three depths (short client-facing, short prospect-facing, long-form
article) — a clean fit for a content model with a short-blurb field and a long-article field per
topic. `IPro_accountants` is structured differently (whole legacy site: menus + articles +
did-you-know) because it *is* a distinct vertical, not a product topic.

Not yet reviewed in detail: individual document contents (only file listings confirmed so far),
and the `Mortgage_site` / `web_template` folders (present but contained no Word docs on first pass).

## Tooling options considered

The user researched page-builder libraries independently and shared a comparison (credited to
"Gem" / Gemini) of four options:

| Library | Frontend required | License | Notes |
|---|---|---|---|
| **Puck** | React | MIT, free | Modern, actively developed drag-drop page builder |
| **Craft.js** | React | MIT, free | Lower-level React framework for building custom editors |
| **GrapesJS** | Framework-agnostic (vanilla JS) | MIT, free | Built specifically for WYSIWYG page/email builders; only option that doesn't require adopting a new frontend framework |
| **Vue-Grid-Layout** | Vue | MIT, free | Grid-layout library, not a full page-builder |

IPRO_Modern is 100% server-rendered ASP.NET Core Razor MVC with vanilla JS (no React/Vue anywhere
in the stack). Three of the four options would mean introducing a full new frontend framework
just for the page editor — a much bigger architectural bet than it first appears, since it would
sit next to, not replace, the existing Razor rendering used by every other screen in both portals.
GrapesJS is the only option that fits the current stack without that step.

No tooling decision has been made yet. This table is recorded for reference, not as a conclusion.

## Decision: build in place, not a separate project

Question raised: should the new template system be prototyped in a brand-new separate project,
to avoid any risk of breaking the live, already-deployed IPRO_Modern solution while experimenting?

**Recommendation adopted: no — build on a feature branch inside the existing solution, as
strictly additive code, not a separate project.**

Reasoning:

- A page builder is not a standalone tool. It is deeply coupled to things already built: agent and
  theme entities, the `--site-theme-soft` / `--site-theme-deep` CSS token system, the Admin/Web
  portal split, and the existing hero/sidebar rendering conventions.
- A separate project would allow free experimentation, but "porting it back" would really mean
  re-integrating all of that from scratch later — trading the risk of breaking production today for
  a second, harder integration project down the line, with drift bugs that surface only at merge
  time.
- Building in the same repo keeps that shared data model for free. Production safety comes instead
  from discipline: new work stays additive (new files/routes/tables, not edits to the live
  `_ModernManagedPage.cshtml` / `_ClassicManagedPage.cshtml` render path), validated visually via
  the existing SuperAdmin "preview on an agent's real site" tool, and only merged to `main` /
  cut over once proven.

**One exception noted:** if a frontend-framework swap (e.g., adopting React specifically to use
Puck or Craft.js) becomes a serious option, that is a bigger, separate bet that wouldn't reuse the
existing Razor views anyway — prototyping that in isolation would make more sense. That decision
has not been made and is out of scope for now.

## Content model design (2026-07-30)

Before designing anything new, checked what's actually already built rather than assuming a blank
slate.

### Finding: the page tree already exists

The "page tree" from the next-steps list below turned out not to be missing — it exists end-to-end
and ships today:

- `WebsitePage.ParentPageId` / `ChildPages` — a real FK relationship (`ON DELETE SET NULL`).
- Agents already assign a page's parent from the **Pages > Navigation** screen or the per-page
  **Edit** screen ("Parent Menu" dropdown).
- The public top navigation (`_PublicNavigation.cshtml`) already renders it as a real two-level
  menu: top-level pages, each with a dropdown of child pages.
- Depth is deliberately capped at two levels — `WebsitePagesController` only allows a page to become
  a parent if it is itself top-level (`ParentPageId == null`), so grandchildren are rejected.

What's actually missing is not the tree — it's a **sidebar rendering of that same tree**. Today
"sidebar" (the Position feature, item 157-164) always renders `_WebsiteSidebarRail.cshtml`, a
contact card (logo, photo, name, company, phone/email) — never the page list. That is the real gap
versus the legacy platform, where the sidebar *was* the site menu.

**Design:** add a "Site Menu" element to the sidebar rail, reusing the exact same query
`_PublicNavigation.cshtml` already runs (top pages + their children), rendered as a vertical
expandable list instead of a dropdown nav. Recommended default: stack it with the existing contact
card (menu plus card in one rail), no new settings toggle yet — add a contact-only / menu-only /
both toggle later only if agents actually ask for it. Keep two-level depth as-is; revisit only if
reviewing the legacy side-menu Word docs (`IPro_accountants/files/side_menu/side_menu.doc`) shows a
real three-level requirement. **No new entities or schema needed for this piece.**

### Calculator block type

New block type following the existing pattern (`WebsiteContentBlock.BlockType` plus a `*Settings`
class serialized into `SettingsJson`, same shape as `WebsiteAgentInfoSettings` /
`WebsiteDidYouKnowSettings`):

- `WebsiteBlockTypes.Calculator`
- `WebsiteCalculatorSettings { string CalculatorKind; string Heading; }` — the kind selects which
  calculator renders; heading lets the agent relabel it.
- A `CalculatorKind` catalog (static class, same shape as `WebsiteBlockTypes.All`) enumerating
  supported calculators.

**Porting approach:** the `cal1` PHP scripts are pure math with no data dependency (mortgage
payment, amortization schedule, APR, etc.) — a natural fit for **client-side vanilla JS**, matching
this app's existing no-framework/CSP-nonce convention, rather than a new server-side API per
calculator. Each becomes a self-contained JS module (inputs → computed outputs, optional small
chart/table) embedded by the block partial in all three templates.

48 calculators is too much to port in one pass. Recommended first phase — highest-value, broadest
audience, matches what appeared in both legacy vertical sites' sidebars: **Mortgage Payment,
Refinance, Rent vs. Buy, Retirement Savings, RRSP/Roth, Loan Amortization, APR.** The rest follow in
later phases once the block type and rendering pattern are proven.

### Glossary / Info-Centre content

The `Article` entity (`Title`, `Summary`, `Content`, per-agent, publish flag) already exists and is
already embeddable in a page (`ArticleContent` block) and listable (`DidYouKnow` teaser block). A
glossary is the same shape of problem — a library of short entries, listable and linkable — so the
recommendation is to **extend `Article`, not add a fourth parallel content entity.**

**Each vertical needs its own glossary** (explicitly called out — an accountant's glossary and a
financial planner's glossary should differ). The platform already has exactly this mechanism, and it
should be reused rather than reinvented: `AgentUser.BusinessType` is the existing per-agent vertical
field (required at registration, e.g. "Accountants", "Insurance / Financial"), and
`WebsiteStarterPage`/`WebsiteStarterBlock` already implement "a library of starter content scoped by
`BusinessType` (+ optional `BillingRuleId`), copied once into the agent's own rows the first time
their website is provisioned" (`WebsiteStarterPagesHelper.EnsureStarterPagesAsync`) — after that
one-time copy, it is the agent's own content, independently editable, no live link back to the
starter row. SuperAdmin already manages that library through `StarterContentController` /
`Views/StarterContent`.

**Design:** mirror that exact pattern for glossary terms instead of inventing a new one:

- New `WebsiteStarterArticle` entity (`BusinessType`, `Title`, `Summary`, `Content`, `Category`,
  `SortOrder`) — the seeded-per-vertical glossary library, managed by SuperAdmin the same way
  starter pages are.
- Add `Category` to `Article` itself (e.g. `"Glossary"`), so copied entries are distinguishable from
  an agent's other articles.
- Extend the starter-provisioning step (alongside `EnsureStarterPagesAsync`) to copy the matching
  `BusinessType` (falling back to `"All"`) starter articles into the agent's own `Articles` table
  once, the same way starter pages are copied — agents then see their vertical's glossary
  pre-populated, and can edit, add to, or delete entries freely afterward.
- New `Glossary` block type that lists the agent's `Category == "Glossary"` Articles alphabetically
  with jump letters, linking each to its existing Article detail rendering.

No source content resembling a term/definition glossary has turned up yet in the reviewed Word-doc
folders (the closest match, "Did you know?", is already built) — the `WebsiteStarterArticle` table
starts empty and gets populated per vertical once real glossary content is written or sourced; the
mechanism does not depend on having that content today.

### Build order

1. **Site Menu sidebar element — done, 2026-07-30.** Shipped and verified live on a real
   sidebar-right production site. Full detail: roadmap item 70.
2. Calculator block type + first 7 calculators. In progress.
3. Glossary category field + Glossary block, per-vertical via `WebsiteStarterArticle`.

Each phase: build, verify against real data, commit, push, deploy, update docs — the same
discipline used for every other feature this session. (Verification for phase 1 used the live
public site directly rather than the SuperAdmin preview tool, since no admin session was available
this session — the public sidebar rail needs no login at all.)

## Open next steps

- Review a same-topic sample across `Client Paragraphs` / `Prospect Paragraphs` / `Edited Articles`
  (e.g. Term Life) to see exactly how the three depths differ, before finalizing the content model
  shape.
- Design the content model itself: page/category tree (sidebar navigation), an embeddable
  calculator/tool block type, and a glossary/Info-Centre content type — needed regardless of which
  editing UI is eventually chosen.
- Only after the content model is settled, revisit the editing-UX tooling question (GrapesJS
  integration vs. improving the current Razor-form-based block editor).
- Rewrite the `cal1` calculator formulas from PHP into whatever the new tool/block type ends up
  being; do not depend on the ionCube-encoded `cal` bundle.
