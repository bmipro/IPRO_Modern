# IPRO Advisers — marketing site design system

Written so the remaining twelve pages can be built by someone who did not design these three.
Everything here is implemented in `home.html`, `preview-show.html` and `pricing.html`; the token
block at the top of each file's `<style>` is byte-identical and is the source of truth.

The audience is a fifty-year-old licensed adviser in Ontario reading on a laptop in a bright room.
Every rule below exists to serve that person. When a rule and a nice idea disagree, the rule wins.

---

## 1. The direction in one paragraph

A **document with exhibits.** The page is a light, warm, near-white ground carrying navy serif
headings and left-aligned prose at a readable measure; the product screens break out of that prose
as captioned plates on white. Navy is punctuation, not wallpaper — exactly two full-width navy bands
plus the footer on the home page. Brass is a rule, a tab and a price, never a body colour. The
emotional target is relief: *somebody has finally organised this.* Nothing bounces.

---

## 2. Colour

| Token | Hex | Role |
|---|---|---|
| `--paper` | `#f7f5f1` | Page ground. Warm near-white, low chroma, biased very slightly toward the brass. |
| `--paper-deep` | `#ece7dd` | The closing band and the founding-offer band. One step down from paper, never a second brand colour. |
| `--surface` | `#ffffff` | Cards, exhibit plates, product screens. Screenshots always sit on white so they read as screens. |
| `--line` | `#d9d2c6` | Hairline on paper. |
| `--line-soft` | `#e7e2d8` | Hairline inside a white surface. |
| `--navy` | `#1a3a6b` | Brand navy. Headings, the primary button, the wordmark. |
| `--navy-deep` | `#10254a` | Full-width bands and the footer. Deliberately darker than the button so a band never reads as one. |
| `--brass` | `#a9812f` | Brand brass. Plate tabs, the Platinum top rule, the signature rule, the current-page underline. |
| `--brass-deep` | `#8a6820` | Brass **as type**: eyebrows, the `$0`, the founding code. |
| `--brass-wash` | `#f3ead6` | Badge and code-chip grounds. |
| `--ink` | `#1c2a42` | Body text. **13.2:1** on paper. |
| `--ink-soft` | `#41506b` | Secondary text, captions, struck prices. **7.5:1** on paper. |
| `--on-navy` | `#ffffff` | Text on navy bands. 15.2:1 on `--navy-deep`. |
| `--on-navy-soft` | `#cfdaef` | Secondary text on navy. 10.8:1. |

**Semantic colours** — `--ok #1f6b45`, `--warn #8a5100`, `--alert #a5251c` with matching washes.
These exist **only inside product reconstructions** (a due badge, an overdue pill). They are not
part of the marketing palette and must never be used for site chrome.

### Colour rules

1. **No dark mode.** Single light theme. No `prefers-color-scheme` block anywhere. Every colour is
   declared explicitly, including `body { background: var(--paper) }`.
2. **7:1 minimum for anything that is read.** There is no grey-on-grey and no third, lighter text
   colour. If something needs de-emphasis, make it smaller or set it in `--ink-soft`; do not fade it.
3. **Brass is never body text.** The one place brass carries type at a small size is the eyebrow —
   13px, 700 weight, uppercase, in `--brass-deep`.
4. **One documented contrast exception:** the `$0` setup figure is `--brass-deep` at 28px/800, which
   is 5.1:1 on white. Section I asks specifically for the saving to read in brass, one size up. At
   that size it clears AA-large comfortably; it is a display numeral next to its own label, not
   prose. Do not extend the exception to anything smaller.
5. **Navy band budget.** Two per long page plus the footer, and never two adjacent. On the home page:
   *your first week* and *four things we don't do*. The closing band is `--paper-deep` precisely so
   it does not stack against the navy footer.

---

## 3. Typography

**Display:** `Georgia, "Iowan Old Style", "Palatino Linotype", "Book Antiqua", "Times New Roman", serif`
**Body/UI:** `-apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif`

No webfonts, no network requests, no `@font-face`. Georgia ships on every machine this audience uses
and a serif headline over navy reads as an institution rather than an app.

Georgia has **two real weights, 400 and 700**. Never ask for 600 — the browser synthesises it and it
looks smeared at display sizes.

| Token | px | Used for |
|---|---|---|
| `--t-label` | 13 | Uppercase labels only: eyebrows, badges, table heads. Never prose. |
| `--t-sm` | 16 | Captions, microcopy, footer links, legal. The floor for anything read. |
| `--t-base` | 17 | Body. |
| `--t-lg` | 19 | Lead paragraphs and hero sub. |
| `--t-xl` | 22 | `h4`, commitment headings, timeline anchors. |
| `--t-2xl` | 28 | `h3`, plan names, sidebar card headings. |
| `--t-3xl` | 34 | Spare step in the scale. |
| `--t-4xl` | 44 | `h2` and price amounts. |
| `--t-5xl` | 54 | `h1`. |

- `h1` / `h2`: Georgia **400**, line-height 1.08–1.15, `letter-spacing: -0.012em`, `text-wrap: balance`.
- `h3` / `h4`: Georgia **700**.
- Body: 17px / 1.6, `--ink`, measure capped at **66ch** via `.prose`. Wide variant `.prose--wide` = 78ch.
- Eyebrow: `--t-label`, 700, `letter-spacing: .11em`, uppercase, `--brass-deep`.
- **Numbers:** `font-variant-numeric: tabular-nums` on prices, counts, dates and any column of digits.
  The `.num` utility applies it.
- **Nothing below 16px is prose.** 13px is permitted only for uppercase labels at 700 weight.

### Product-UI type is a separate scale

Type inside `.shot` is 13–14px. That is deliberate and it is not a violation of the 17px floor: a
reconstruction of the dashboard is a *picture of an interface*, not text the visitor reads. Keep the
two scales strictly separated — never let `.shot` typography leak into page copy, and never enlarge
`.shot` type to "match" the page, because that is what makes a screenshot look drawn rather than
captured.

---

## 4. Space and geometry

Space scale, 4px base: `--s1 4 · --s2 8 · --s3 12 · --s4 16 · --s5 24 · --s6 32 · --s7 48 · --s8 64 · --s9 96 · --s10 128`.

- Band padding: `--s9` (96) top and bottom; `.band--tight` uses `--s8` (64). Below 860px both drop to `--s8`.
- Gap between the three home-page pillars: `--s10` (128). They need to read as separate arguments.
- Page frame `.wrap`: max-width **1140px**, side padding `--s5` (24), `--s4` (16) below 560px.
  `/Preview/Show` overrides to **1340px** — see §7.
- Radii: `--r-sm 3px` (inputs, badges) · `--r-md 5px` (buttons, cards) · `--r-lg 7px` (exhibit plates).
  Small radii on purpose. Pill-shaped everything reads as a consumer app.
- Shadows: two only. `--shadow-card` for cards, `--shadow-plate` for exhibit plates. Both are
  low-opacity navy, not black.
- **Lay out with flex/grid `gap`.** Do not add per-element margins; the `.stack-*` helpers exist so
  siblings space themselves.

---

## 5. Components

### Buttons

One `.btn` component, four variants, minimum height **52px** (`.btn--quiet` 46px), verb labels,
never icon-only, never dependent on hover to be discoverable.

| Variant | Appearance | Use |
|---|---|---|
| `.btn--primary` | Navy fill, white label | The one action the screen exists for. |
| `.btn--secondary` | Navy 2px outline on transparent | A real but subordinate action. |
| `.btn--onNavy` | White fill, navy label | Primary sitting on a navy band. |
| `.btn--quiet` | Hairline border, navy label | Masthead CTA, the email-capture submit. |

`.btn--block` stretches to its container. Below 560px all buttons go full width.

**One primary per screen.** The rule is one primary *action*, not one primary element: the home page
repeats the same preview button in the hero and in the closing band, and nothing else on the page is
filled navy. On `/pricing` the primary set is the three plan buttons — one per card, identical
weight — and no other filled button appears; Platinum is lifted by its brass top rule, never by a
heavier button. On `/Preview/Show` the only primary is **Claim this site**.

Text links use `.qlink`: navy, 600, underlined at 1px with 3px offset, thickening to 2px on hover.
`.qlink--onNavy` for navy bands. Focus is a 3px brass outline at 2px offset, everywhere.

### Exhibit plates

Every product image on the site is a `.plate`:

```
figure.plate
  .plate__frame     — white, 1px --line, 3px --brass top border, --r-lg, --shadow-plate, overflow hidden
    .browser__chrome (optional)  — three dots plus a URL slot with a padlock
    .shot | .site | inbox chrome — the reconstruction
  figcaption.plate__cap          — 16px --ink-soft, 62ch, 2px left rule in --line
```

Use `.plate__cap--narrow` (44ch) whenever an overlapping inset shares the caption's row.
An inset uses `.stage` on the figure plus `.stage__inset` (a second card, offset bottom-right) or
`.phone` (a bordered narrow viewport). Both go static and stack below 1040px.

**Captions carry the argument.** A plate without a caption is decoration.

### Product reconstruction primitives

`.shot` and its children mirror the real application: `.shot-rail` (the 208px dark sidebar with its
real section labels), `.shot-topbar`, `.shot-card`, `.shot-stat`, `.shot-table`, `.shot-pill`
(`--alert` / `--warn` / `--info` / `--ok`), `.shot-btn` (`--solid` / `--ghost` / `--grey`),
`.shot-input`. `.site` and its children mirror the customer's own Modern template.

Rules for building the remaining shots (5, 6, 7 on `/how-it-works`, and the per-vertical variants):

1. **Read the Razor view first.** Reproduce its real headings, real column names, real button labels
   and its real empty/alert states. Do not improve the interface while depicting it.
2. **Use the real catalogue content.** `MockDailyInsightCatalog` supplies the morning-list line per
   vertical — Jennifer Walsh for insurance, David Park for mortgage, Robert Kim for accounting, each
   with its own counts (3/5/4, 4/6/3, 3/4/5) and its own reason line. The vertical pages must use
   their own entry; swapping the noun is the failure mode Section I names.
3. **Ontario-plausible example data.** Names, cities and phone formats should look like a Halton or
   Hamilton book of business.
4. **Never a grey box, never a fake photograph.** These are honest stand-ins and are labelled as such
   by the prototype strip; when real captures exist they replace the reconstruction inside the same
   `.plate__frame` and nothing else changes.

### Plan card — one repeatable unit

`.plan-card` is designed to be emitted in a loop over live package rows. Nothing in it is
hand-tuned per plan:

```
article.plan-card[.plan-card--lifted]
  p.plan-card__badge      ← badge text
  h2.plan-card__name      ← PackageName
  p.plan-card__price      ← MonthlyPrice + "/ month"
  p                       ← Description
  ul.plan-card__limits    ← one <li> per limit label (Contacts, MultiDomainSupport, FileUploadCapacity, TeamMemberLogins)
  p.plan-card__setup      ← "Setup:" + <s>SetupFee</s> + .plan-card__free
  div.plan-card__cta      ← .btn--primary + .qlink to /Preview?package=
```

- `--lifted` swaps the top border from `--line` to `--brass` and the badge to the brass wash. It is
  the **only** visual difference between the featured card and the others: same width, same padding,
  same button weight.
- Cards stretch to equal height (`align-items: stretch` on `.plan-grid`), and `.plan-card__cta` is
  pushed down with `margin-top: auto` so the buttons line up regardless of description length.
- The struck setup fee is `--ink-soft` with a 1px strike; the `$0` is `--brass-deep`, 800, one size
  up, with its qualifier beneath it in `--ink`.
- **No "Most Popular" badge, ever.** There is no popularity data. Platinum's badge is *Most complete*,
  which is factual.
- The home page's summary cards are the same component with the limits, setup and CTA blocks omitted;
  the section carries a single `.btn--secondary` to the pricing page instead of three per-card CTAs.

### Bands

`.band` + one of `--paper`, `--surface`, `--paperDeep`, `--navy`. `.band--navy` re-colours headings
and `.muted` / `.fine` automatically; do not set colours on children inside it.

Two named band layouts are reusable:
- `.week` — a two-column schedule (190px anchor / description) with a brass rule on top and hairlines
  between rows. Use for any "what happens when" sequence.
- `.commitments` / `.commit` — two columns, each item topped by a hairline, heading at `--t-xl`. Use
  for promises. It should read as a signature, not a feature list.

### Forms

`.field` = label (16px/700) above the input. Inputs are 48px minimum, 1px `--line`, `--r-sm`, 17px
text, and always carry a real `<label>` — a placeholder is not a label. Placeholder text is
`--ink-soft`, never lighter.

### Footer

`.sitefoot` on `--navy-deep`: four columns (Product / Who it's for / Company / Account), column
headings as 13px uppercase brass-tinted labels, links white at 16px, a hairline rule above the legal
line, and the tagline set in Georgia at the right.

---

## 6. Motion

The licensed list is short and closed.

1. **The three counts on the AI Daily Assistant card count up once when the card scrolls into view.**
   520ms, cubic ease-out, `IntersectionObserver` at 0.6 threshold, `unobserve` immediately so it
   never replays. This runs on the **home page only** — the card on `/Preview/Show` is static,
   because the motion Section I licenses on that screen is the frame's own page transitions.
2. **The preview frame's page transitions** — a 200ms opacity fade when a frame-navigation button is
   pressed. The product moving is the motion.

Both are skipped entirely under `prefers-reduced-motion: reduce`, and the count-up is skipped when
`IntersectionObserver` is unavailable; in both cases the final values are already in the markup, so
nothing is ever hidden behind an animation.

**Nothing else moves.** No fade-up on scroll for body copy — a skeptical reader who has to wait for
text to appear reads less of it.

---

## 7. Page rules

**Every page:** masthead (wordmark with brass underline, all top-level links visible, one `.btn--quiet`
CTA) → bands → footer. Mark the current page with `body[data-page="…"]`, which brasses the matching
nav link's underline.

**`/Preview/Show`** is the exception to the page frame. It replaces the masthead with a thin navy
top bar (*Live preview* / *Start over*), widens `.wrap` to 1340px, and runs a
`minmax(0,1fr) / 330px` grid so the frame is roughly **3:1** against the sidebar — a sidebar, not a
partner. The frame is 800px tall minimum and the site inside is rendered at full size; never scale it
down to fit. Sidebar order is fixed and load-bearing: **plan → morning list → primary action → email
capture**, with the email capture visually the quietest object on the screen (dashed border, no
shadow, `.btn--quiet`) so it cannot cannibalise the signup. Below 1100px the frame goes full width
first and the sidebar stacks beneath it.

**Frame navigation buttons** sit directly above the frame, are 46px tall with a 1.5px navy border,
and the current page is filled navy with `aria-current="true"`. They must look clickable at rest.
Most visitors will not otherwise discover the rest of the site is real.

---

## 8. Responsive rules

Three breakpoints, and no more.

| Width | What changes |
|---|---|
| **≤ 1100px** | `/Preview/Show` collapses to one column: frame first, sidebar beneath as a two-up grid. |
| **≤ 1040px** | Plan grid → one column. Footer → two columns. Home pillars → one column. Overlapping insets (`.phone`, `.stage__inset`) become static blocks below their plate. Founding band and commitments → one column. |
| **≤ 860px** | Display sizes step down (`h1` 54→38, `h2` 44→32). Band padding 96→64. Product reconstructions drop the sidebar rail, collapse their internal grids and scroll horizontally **inside** their own card. Masthead nav wraps under the wordmark, every link still visible. |
| **≤ 560px** | Page padding 24→16. Footer → one column. Buttons go full width. |

Two structural rules hold at every width:

- **The page body never scrolls sideways.** Wide content — comparison tables, reconstructed screens —
  scrolls inside its own `overflow-x: auto` container. Flex and grid children are pinned with
  `min-width: 0` so a wide table cannot push its parent out.
- **No hidden navigation.** No hover-only menus and no hamburger that conceals the link set; the
  masthead wraps to more rows rather than hiding anything.

---

## 9. Anti-patterns — the standing list

Mistakes for this audience regardless of execution quality. This is Section I's list, and it is the
first thing to check a new page against.

Dark mode · scroll-jacking or parallax · carousels · auto-playing video with sound · hover-only
navigation · modal popups on entry or exit · countdown timers · chat bubbles nobody is behind ·
"AI" glow effects, purple gradients, neural-network motifs · abstract 3D renders · isometric
illustration · emoji in headings · body text below 16px · grey text on grey backgrounds · icon-only
buttons · accordions hiding FAQ answers · progress bars that don't measure anything ·
testimonial-shaped placeholders with no testimonials in them.

Two clarifications the list invites:

- **The pricing comparison table is allowed to be collapsed** (`<details class="compare">`), because
  Section I asks for it. FAQ answers are not: every question on `/faq` and the three on `/pricing`
  renders open, as prose.
- **No founder photo placeholder.** Section I requires one real photograph of Bahman on `/about`.
  Until that asset exists, the home page's *Me, mostly* band is set as a signed note — prose, a brass
  rule, a signature — rather than a framed grey rectangle. When the photograph arrives it takes a
  left column at 5:6 and the letter moves right; nothing else changes.

---

## 10. Porting to Razor

- Class names are semantic and framework-free. Nothing here needs Bootstrap; if the port keeps
  Bootstrap for the app, scope this stylesheet so the two do not fight over `.card`, `.btn` or
  `.table` — none of those names are used above.
- **Escape at-rules in `.cshtml`:** `@@media`, `@@keyframes`, `@@font-face`.
- **Strict CSP:** no inline event handlers. Both scripts here are already listener-based and go in an
  inline `<script nonce="@Context.GetCspNonce()">` or a file under `wwwroot/`.
- **Keep the data-driven parts data-driven.** Plan cards, limit labels, setup fees and the comparison
  table all read live package rows. The values in these prototypes are today's seeded numbers, shown
  for layout only.
- **Delete `.proto-note`** — the navy strip at the top of each prototype page is a note to the owner,
  not part of the design.

### Three fixes that must land alongside the port

These are from Section K of the strategy document and they change what these pages render:

1. **`SmsReminder` must be corrected in `PackageEntitlementSeeder.cs`** before the pricing page ships.
   It currently seeds "Mobile SMS reminder" as included on all four packages, so a data-driven table
   renders a checkmark for something that is not built — directly contradicting the honesty band.
   The comparison table in `pricing.html` deliberately omits the row; the fix belongs in the seed data.
2. **Every plan CTA must carry `?package=`.** `Register` already accepts it and preselects the plan;
   today the home page's cards link to a bare `/Account/Register` and the buyer has to choose again
   after they already chose. In the port, `.plan-card__cta` renders
   `/Account/Register?package=@Uri.EscapeDataString(package.PackageName)`. The prototype's hrefs are
   inert placeholders.
3. **No temporary address appears anywhere in these pages.** `GenerateUniqueDomainAsync` still issues
   `firstnamelastname.247advisers.com` while the marketing says `iproadvisers.com`. Until the owner
   decides, the preview frame's URL slot shows *Live preview · [name]* rather than an address, and
   the customer-site plate on the home page shows a **custom** domain, which is the claim the copy
   actually makes.
