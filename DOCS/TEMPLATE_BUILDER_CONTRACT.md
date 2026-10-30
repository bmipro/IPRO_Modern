# IPRO.TemplateBuilder export contract — v0.5

*v0.5 of 2026-10-13: the consolidated v0.4 text of 2026-10-12 (first issued 2026-09-29) plus host sections. This document is the **entire interface** between the builder and the host product.
It is deliberately self-contained: everything needed to produce a valid export, and to open a site
iPro hands over, is in this file. If something here is ambiguous, ask — do not guess and do not read
host code (you don't have it).*

Supersedes v0.4 and v0.3. It also supersedes the sample shapes in the host's two answer notes of 2026-09-29
(those followed an older draft: `template` instead of `theme`, `export.json` instead of
`template.json`, a Hero without settings, and missing required fields). Where anything disagrees,
this file and its fixtures (§8) win.

**What the v0.4 text under it is.** The 29 September text with every clarification iPro has given since
folded into the section it belongs to, so there is one document to read. That consolidation changed no
part of the schema, and the two v0.4 fixtures are unchanged, byte for byte. The changelog at the bottom
lists what was folded in. This file replaces the one at the root of the builder's repository.

**Where iPro's side stands (2026-10-12).** iPro's importer and its hand-over both exist and enforce
§1–§4, §7, §9 and §10 as written here. They are operated by iPro's staff for one customer at a time: a
package is made from the admin screens (§9) and the package that comes back is uploaded there. The
transport of §11 is built for the customers' Beta (2026-10-17); a builder on someone's own machine still works from files. §12.1 says what the importer does with a site.

**v0.5 (2026-10-13): host sections.** A customer's site holds sections the builder does not carry: a
calculator, a photo gallery, a price list, a poll and others, fifteen kinds today. Until now a site with
one of them could not be handed over at all, and iPro's own site has fourteen such pages. From v0.5 each
of those sections travels as a **locked box**: a ninth block type, `HostSection` (§4.10), that says which
section it is and carries none of its content. (The hand-over names each box by its kind and its
heading, §9.3; nothing else of the section leaves iPro.) The builder shows the box and lets the designer
move, hide or remove it; iPro puts the real section back where the box is. Nothing else changed: a site
with no host section is handed over, and comes back, as the v0.4 package it always was (§0).

---

## 0. Version policy

- `contractVersion` is the exact string `"0.4"` or `"0.5"`.
- **v0.5 is v0.4 plus host sections** (§4.10; `catalog.hostSections`, §9.3; `handoverVersion` `"1.1"`,
  §9.2) and nothing else. Whatever a `"0.4"` package may hold, a `"0.5"` package may hold on the same
  terms; a `"0.4"` package cannot hold a `HostSection` block.
- **Which of the two a package states:**
  - iPro's hand-over states `"0.5"` when the site has at least one host section and `"0.4"` when it has
    none, so a site made only of the eight export types is handed over exactly as before.
  - A return states the `contractVersion` of the hand-over it answers, also when every locked box was
    removed in the builder.
  - A template built from scratch states `"0.4"`.
- iPro's importer takes both. A builder built for v0.4 refuses a `"0.5"` hand-over by its version.
- During 0.x, **any** change (additive or breaking) bumps the minor, and each side accepts exactly the
  versions it was built for. The builder should refuse to export anything else.
- Change control: the host side owns this file; changes arrive as a new dated revision of
  CONTRACT.md with a changelog entry at the bottom. Never patch it locally.
- A clarification that changes no validation rule and no part of the schema is recorded in the
  changelog and folded into the text without a version bump (the two revisions of 2026-10-12 were).

### 0.1 Conventions used throughout

- **Lengths count UTF-16 code units:** what JavaScript's `.length` and .NET's `string.Length` return.
  An emoji outside the basic plane counts as two.
- **Patterns match the whole value.** A trailing line break is not part of a slug or a file name:
  anchor with `\A…\z` in .NET and use `fullmatch` in Python, not `^…$`, where `$` also matches
  before a final newline.
- **`null` is never a value,** with one exception: a top-level page's `parentSlug` (§3). An unused
  text field is `""`, an unused `settings` is `{}`. The importer refuses `null` anywhere else.
- **Every key is always written.** A missing key is an error exactly as an unknown one is (§2).

## 1. Export package format (ZIP)

Unchanged from v0.3. An export is **one ZIP file** (any name; suggested `<templateName>-<yyyymmdd>.zip`)
containing:

```
template.json          (required, at the ZIP root)
assets/                (required if any block or the logo references an image, else omit)
  hero-home.png
  adviser-logo.png
```

Rules the importer will enforce (build them into your export writer AND your tests):

1. `template.json` is UTF-8, **no BOM**, at the ZIP root (not inside a folder).
2. `assets/` is **flat** — no subdirectories.
3. Asset file types: `.jpg` `.jpeg` `.png` `.gif` `.webp` **only**. Nothing else may appear in
   the ZIP — no HTML, no CSS, no JS, no fonts, no SVG (SVG can script; it is not accepted).
4. Asset filenames match `^[a-z0-9][a-z0-9-]*\.(jpg|jpeg|png|gif|webp)$` (lowercase kebab).
   No spaces, no `..`, no path separators, no leading dots.
   At most **100 characters**, extension included.
5. Each asset file ≤ **8 MB** (hard host limit). Whole ZIP ≤ **40 MB**.
6. The importer verifies **magic bytes** match the extension (a PNG renamed `.jpg` is rejected),
   so write real files, not just correctly-named ones.
7. `assets[]` in the JSON and the files in `assets/` must match **exactly, both directions**:
   every declared ref exists as a file; every file is declared. Orphans either way fail import.

Three things that are **not** errors:

- **ZIP directory entries** (for example an entry named `assets/`) are allowed and ignored. Only file
  entries count.
- **An asset nothing references** (listed in `assets[]`, present in `assets/`, used by no block and
  not the logo). Neither side should send one. If one arrives it is ignored: iPro does not store it
  or count it against storage, and the builder must not refuse a package for it. Rule 7 still
  applies: the manifest and the files match both ways.
- **Large or unusual images.** The only image guarantees are the ones above: an allowed type, magic
  bytes matching the extension, at most 8 MB, and the file-name pattern. There is no pixel limit.
  Phone photos can be 48 MP or more, and many JPEGs carry bytes after the end marker. Open them; if
  one is too large for the builder's own processing, preview a downscaled copy and keep the original
  bytes for the return (§10.3).

iPro's importer also stops reading a package that unpacks to more than 120 MB or holds more than
2,000 entries. These are safety limits, not rules to design to: images do not compress, so no
package within rule 5 comes near them.

The **handover package** iPro sends the builder (§9) follows the same rules for `template.json` and
`assets/`, and adds `handover.json` and `catalog/`.

## 2. `template.json` — top level

```json
{
  "contractVersion": "0.4",
  "templateName": "prairie-advisor",
  "theme": "modern",
  "handoverId": "",
  "logoRef": "assets/adviser-logo.png",
  "logoAltText": "Prairie Advisor",
  "pages": [ ... ],
  "assets": [ { "ref": "assets/hero-home.png", "type": "image/png" } ]
}
```

| Field | Type | Rules |
|---|---|---|
| `contractVersion` | string | exactly `"0.4"` or `"0.5"` (§0 says which) |
| `templateName` | string | `^[a-z0-9]+(-[a-z0-9]+)*$`, 3–60 chars; the template's identity |
| `theme` | string | one of `"modern"`, `"classic"`, `"editorial"` — a **hint** for which host shell the template was designed against; the importer stores it, the agent can still switch |
| `handoverId` | string | **new in v0.4.** `""` for a template not started from an iPro handover (a designer template). Otherwise exactly the `handoverId` of the handover package the site came from (§9.2). Always emit the key |
| `logoRef` | string | `""` (no logo) or an exact `ref` from `assets[]` — same both-directions matching rule as `imageRef`. Always emit the key. The importer lands it on the site's header logo, rendered by all three host shells. Same file rules as any asset (§1; no SVG). Recommend a landscape mark ≤512KB — the host header renders it small |
| `logoAltText` | string | plain text ≤80 chars. REQUIRED non-empty when `logoRef` is set (accessibility); must be `""` when `logoRef` is `""`. iPro validates it; today its three shells write the logo's alternative text from the business name |
| `pages` | array | 1–30 pages |
| `assets` | array | see §1 and §10; `ref` is the exact string blocks use in `imageRef`; `type` is the MIME type matching the extension (`image/jpeg`, `image/png`, `image/gif`, `image/webp`) |

Unknown top-level keys: **do not emit any.** The importer treats unknown keys as an error in 0.x
(strict now is cheap; loose now is forever).

The same holds at every level: pages, blocks, `settings` and the entries of `assets[]`. And no key may
be missing (§0.1).

## 3. Pages — including hierarchy

Unchanged from v0.3.

```json
{
  "slug": "our-services",
  "parentSlug": null,
  "title": "Our Services",
  "navigationLabel": "Services",
  "isHomePage": false,
  "sortOrder": 1,
  "blocks": [ ... ]
}
```

| Field | Type | Rules |
|---|---|---|
| `slug` | string | `^[a-z0-9]+(-[a-z0-9]+)*$`, 1–80 chars, **unique across the whole template** (not just among siblings — host routing is single-segment `/{slug}`) |
| `parentSlug` | string \| null | `null` = top-level. Otherwise must equal the `slug` of another page in this export. **Maximum depth 3** (top → child → grandchild). No cycles. The home page must have `parentSlug: null` |
| `title` | string | 1–120 chars, plain text, not only spaces |
| `navigationLabel` | string | 1–40 chars, plain text, not only spaces (what the menu shows) |
| `isHomePage` | boolean | exactly **one** page `true` per export; conventionally slug `"home"` |
| `sortOrder` | integer | ≥ 0, unique **within its sibling group** (pages sharing the same `parentSlug`), ascending = menu order |
| `blocks` | array | 1–40 blocks |

Your builder UI may ship **flat pages first** — just always emit `parentSlug` (`null`) so the schema
never changes shape.

Reserved slugs: the importer rejects a small reserved list (host route names — e.g. `portal`,
`sitemap`, `health`). The exact list is enforced host-side at import; the builder only needs the
regex + uniqueness + depth rules above. Don't hardcode a reserved list.

The list as it stands on 2026-10-12, so a designer is told while editing and not when the site comes
back: `account`, `billing`, `publicwebsite`, `media`, `hangfire`, `health`, `error`, `clientportal`,
`clientportalaccount`, `clientportalappointments`, `clientportaldocuments`, `clientportalmessages`,
`clientportalpreferences`, `clientportalprofile`, `invoice`, `poll`, `testimonial`, `email-preferences`,
`t`, `portal`, `sitemap`, `robots`. The builder may warn about these in readiness. The importer stays
the authority: the list can grow without a new version of this document.

One address is worth knowing, because a designer can run into it: **`home` belongs to the home page.**
iPro's page editor keeps that address for the home page alone, so the importer refuses any other page
whose slug is `home`. (The home page itself may have any valid slug; `home` is the convention.)

## 4. Blocks — the locked palette, per-type rules

Common shape (unchanged):

```json
{
  "type": "Hero",
  "heading": "", "subheading": "", "body": "",
  "imageRef": "", "buttonText": "", "buttonUrl": "",
  "layoutVariant": "", "sortOrder": 0, "isVisible": true,
  "settings": { }
}
```

Common rules for **every** block:

- `type`: one of the **8 export types** — `Hero`, `Text`, `Services`, `CallToAction`,
  `ContactForm`, and new in v0.4 `ArticleContent`, `Blog`, `Form` — **verbatim, case-sensitive**.
  (Footer and Disclaimer remain **editor presets** that export as `type: "Text"`; see §4.6.)
  One more type exists that is **not** an export type: `HostSection` (§4.10, new in v0.5), a locked
  box that only iPro makes and only a `"0.5"` template from a handover holds.
  When the template comes from a handover, only the types in that handover's `allowedBlockTypes`
  (§9.2) may be used — the rest are outside the adviser's package. (A `HostSection` is never in
  that list and is not held to it: it is allowed by its `hostSectionId`, §4.10.)
- `sortOrder`: integer ≥ 0, unique within the page, ascending = render order.
- `isVisible`: boolean (a hidden block imports but doesn't render — useful for staging content).
- Always emit **all** common fields; use `""` / `{}` for unused ones. No extra keys, in blocks
  or in `settings`.
- A field a block type does not use (each type below says which) is `""`. The importer **refuses** a
  value there rather than dropping it without a word: a heading on an `ArticleContent`, an image on a
  `Services`.
- Field length caps (importer-enforced): `heading` ≤ 200, `subheading` ≤ 300, `body` ≤ 10 000,
  `buttonText` ≤ 60, `buttonUrl` ≤ 500 chars.
- `imageRef`: `""` or an exact `ref` from `assets[]` (§10).
- `buttonUrl`: `""` **or** a site-relative path starting with `/` (e.g. `/contact`,
  `/our-services` — may reference a slug in this export) **or** an absolute `https://` URL
  **or** `mailto:`/`tel:`. Nothing else — no `http://`, no `javascript:`, no protocol-relative
  `//` (and not `/\`, which browsers read the same way).
- **The characters of `buttonUrl`:** no control character (a tab, a line break) and no backslash,
  anywhere. No whitespace, with one exception: inside a `tel:` link, where people write a number
  with spaces (`tel:(416) 555-0199`). The scheme is written in lower case: `https://`, `mailto:`,
  `tel:`. Both sides apply exactly this rule: no stricter and no looser.
- `layoutVariant`: `""` (host default) or one of the type's enum below, lowercase. Anything
  else is **rejected on import** (§7). iPro's renderer normalizes a value it already stores; that is
  how old data is treated, not a promise that an invalid export is accepted.

### ⚠ The most important rule in this file: every text field is PLAIN TEXT

Unchanged in v0.4. **No field in any palette block carries HTML.** The host renders `heading`,
`subheading`, `body`, `buttonText` HTML-encoded — a `<b>` you export would display to visitors as the
literal characters `<b>`. The three new blocks point at content iPro owns (articles and forms); that
content never enters the contract, so rich text still does not either.

### 4.1 `Hero` — required: `heading`

Full-width banner. `subheading` = lead line, `body` = optional second lead paragraph,
`imageRef` = banner image (omit ⇒ host renders a branded monogram), button optional
(empty `buttonUrl` with non-empty `buttonText` ⇒ host links to `/contact`).

`layoutVariant`: none (`""`) — Hero layout lives in `settings` (`ContactForm` is the other v0.3 block with a setting, §4.5):

```json
"settings": {
  "layout": "split",         // "split" | "image-left" | "centered" | "full-image" | "compact"
  "imagePosition": "center", // "center" | "left" | "right" | "top" | "bottom"  (crop focus)
  "textAlignment": "left",   // "left" | "center"
  "height": "standard",      // "compact" | "standard" | "tall"
  "overlayStrength": 45      // integer 0–85 (image dimming %, for text legibility)
}
```

Emit all five keys, always. A value outside the enums or the range is **rejected on import**; the
defaults shown are what iPro's renderer falls back to for data it already stores.

### 4.2 `Text` — required: at least one of `heading` / `body`

The general-purpose section; also how **Footer** and **Disclaimer** are built. `subheading` renders
as a small kicker above the heading. Line breaks in `body` are preserved as line breaks.

**A Text block may carry a button.** iPro renders a button on a Text block whenever `buttonText` is
non-empty, linking to `buttonUrl`, in all three themes. Send **both fields or neither**; the importer
refuses one without the other. The builder must accept and keep them when a handover has them;
offering them in the editor is optional.

`layoutVariant`: `"image-left"` | `"image-right"` (only meaningful when `imageRef` is set;
`""` ⇒ image-right). `settings`: `{}`.

### 4.3 `Services` — required: `body`

`body` is **one service per line** — the host splits on line breaks and renders each line as a
card / list item / icon tile. Keep each line short (≤ 80 chars); no empty lines.
`heading`/`subheading` introduce the section. `imageRef`, button: unused, emit `""`.

"≤ 80 characters, no empty lines" is **guidance**, not an import rule. iPro renders longer lines and
skips empty ones. The builder may point them out in readiness, but must not refuse to open or export
because of them.

`layoutVariant`: `"cards"` | `"list"` | `"icons"` (`""` ⇒ cards). `settings`: `{}`.

### 4.4 `CallToAction` — required: `heading`

`buttonText` empty ⇒ host renders "Get in touch"; `buttonUrl` empty ⇒ `/contact`.
`imageRef`: unused.

`layoutVariant`: `"banner"` | `"card"` | `"split"` (`""` ⇒ split). `settings`: `{}`.

### 4.5 `ContactForm` — required: nothing (all fields optional)

**Placeholder in the builder** — the host renders its own CRM-wired lead form here; the builder
shows a static mock of a form and must not attempt to build a working one. `heading`/
`subheading`/`body` = the copy beside the form. `imageRef`, button: unused.

`layoutVariant`: none (`""`). `settings`:

```json
"settings": { "showPhoto": true }   // whether the adviser's photo shows beside the form
```

`showPhoto` is **required**: a boolean, always emitted, like every settings key.

### 4.6 Footer and Disclaimer (editor presets, not export types)

Unchanged: builder-side presets that create a pre-filled `Text` block and export as `type: "Text"`
(§4.2). A template SHOULD end its home page with a `Text` disclaimer block; **its `body` must be
generic placeholder compliance text** — real compliance wording is the adviser's, and (host hard
rule) **no sample content anywhere in a template may contain dollar amounts, rates of return, or
subscription-package promises.**

### 4.7 `ArticleContent` — required: `settings.articleId` (new in v0.4)

Shows one of the adviser's **published** articles in full. The host renders the article's own image,
title and text; the block contributes only the reference. `heading`, `subheading`, `body`,
`imageRef`, `buttonText`, `buttonUrl`: unused, emit `""`.

`layoutVariant`: none (`""`). `settings`:

```json
"settings": { "articleId": 101 }   // integer ≥ 1: an `id` from the handover's catalog.articles (§9.3)
```

Only in a template that came from a handover (`handoverId` set). A designer template has no adviser
to own an article, so it may not contain this block.

### 4.8 `Blog` — required: nothing (new in v0.4)

Lists the adviser's published articles newest first; a visitor opens a post on the same page.
`subheading` = small kicker, `heading` = section title, `body` = lead paragraph (plain text).
`imageRef`, button: unused, emit `""`.

`layoutVariant`: none (`""`). `settings`:

```json
"settings": { "postCount": 6, "showImages": true }   // postCount: integer 1–50; showImages: boolean
```

Emit both keys, always. Allowed in designer templates too: it carries no IDs.

### 4.9 `Form` — required: `settings.websiteFormId` (new in v0.4)

One of the adviser's **active** custom forms, rendered and processed entirely by the host:
validation, spam protection, consent, storage of the answers and the adviser's notification.
`subheading` = small kicker, `heading` = section title (`""` ⇒ the form's own title), `body` = lead
paragraph (plain text). `imageRef`, button: unused, emit `""`.

`layoutVariant`: none (`""`). `settings`:

```json
"settings": { "websiteFormId": 45 }   // integer ≥ 1: an `id` from the handover's catalog.forms (§9.3)
```

Only in a template that came from a handover (`handoverId` set). The builder never produces a working
form: see §5.1 for what it draws instead.

### 4.10 `HostSection` — required: `settings.hostSectionId` (new in v0.5)

A section of the adviser's site that the builder does not carry: one of iPro's own kinds (a calculator,
a photo gallery, a price list, a map, a poll, a newsletter sign-up and others; the list grows on iPro's
side without a change to this file). It travels as a **locked box**. The block says *which* section it
is and nothing about what is in it:

```json
{
  "type": "HostSection",
  "heading": "", "subheading": "", "body": "",
  "imageRef": "", "buttonText": "", "buttonUrl": "",
  "layoutVariant": "", "sortOrder": 2, "isVisible": true,
  "settings": { "hostSectionId": 3 }   // integer ≥ 1: an `id` from the handover's catalog.hostSections (§9.3)
}
```

- Every text field, `imageRef` and `layoutVariant` are `""`. `settings` is exactly
  `{ "hostSectionId": n }`.
- **Only iPro makes one.** It exists only in a `"0.5"` template that came from a handover, and only
  with an `id` that handover lists. The builder never offers it in the palette, and it is not in
  `allowedBlockTypes`.
- **What a return may do with a box:** place it anywhere, on its page or on another; hide or show it
  (`isVisible`); leave it out. Nothing else: what the section says is changed in iPro's page editor.
  (The builder's editor moves a box within its page, hides and removes it; it has no move to another
  page for any section.)
- **The same `id` may appear more than once** (a page duplicated in a builder carries its boxes).
  iPro then makes that many copies of the section. The builder need not offer copying a box by itself.
- It is a block like any other for a page's limit of 40 and for `sortOrder`.
- What the builder draws for it: §5.1. What iPro does with it on the way back: §12.1.

## 5. "Pixel-equivalent preview" — defined

Unchanged from v0.3:

- The editor canvas and the preview route render through **one shared renderer inside the
  builder**. "Pixel-equivalent" = same DOM + CSS modulo editor chrome (selection outlines,
  drag handles). That's the testable claim.
- **Host-theme fidelity is explicitly out of scope.** You do not get the Modern/Classic/
  Editorial shell markup (deliberate isolation), so do not try to imitate them pixel-for-pixel.
  Build one clean, neutral render style. At integration, the host re-renders the imported
  blocks through its own shells — layout structure survives, exact styling is the host's.

### 5.1 Stand-ins for host-owned content (new in v0.4)

The three blocks new in v0.4, and the locked box of v0.5, show content the builder doesn't own, so it
draws **non-interactive stand-ins**
from the handover catalogue (§9.3):

- `ArticleContent`: the article's `title`, its image (`imageRef` in `catalog/`, when not `""`) and its
  `summary`, with a note such as "Full article shown on your site".
- `Blog`: the newest `postCount` catalogue articles, each with title, summary and (when `showImages`)
  image. With no handover, a neutral sample list clearly marked as sample.
- `Form`: the form's `title` (or the block's `heading`) and `description`, then the host's standard
  contact fields — **first name, last name, email, phone and a consent checkbox** — then the form's own
  `fields` in order, drawn as **disabled** controls, and the `submitButtonText` as an inert button.
  This is a picture of a form: no `<form>` element, nothing that submits, and none of it is exported.
- `HostSection`: a plain box that cannot be mistaken for content. It shows the catalogue entry's
  `label` ("Photo Gallery"), its `title` when it has one, and a line such as "This section stays as it
  is on your iPro site. Move, hide or remove it here; change what it says in iPro." No picture of the
  section, nothing to click inside it, no editing of its words, and it is not in the section library.
  A `hostSectionId` that is not in the catalogue never comes from iPro: refuse the handover (§9.4).
- An ID that isn't in the catalogue: a visible placeholder ("This article is no longer available" /
  "This form is no longer available"), and the export is blocked until the adviser picks another item
  or removes the block (the importer would reject it — §7).

The exact host rendering is available only at integration, through the host preview (§11.4).

## 6. Sanitizer profile — the standing appendix

Unchanged from v0.3 and still **moot by design**: no exported field in v0.4 passes through the HTML
sanitizer. Keep the conformance harness green; it activates the day a rich-text field enters the
contract. Below is the host's **exact** production profile (host package: **HtmlSanitizer (Ganss)
9.2.995**).

**Conformance is defined as canonical-output comparison** (one definition, for both the vectors
below and future exports):

- **Vector tests:** assert `Sanitize(input) == Sanitize(expected)`. Comparing both sides after
  a sanitizer pass absorbs the serializer's whitespace/quoting normalization, which is not part
  of the security claim.
- **Future rich-text exports:** the builder runs `Sanitize` once at export time and exports the
  **result** (canonical form). Because the sanitizer is idempotent, the importer's check —
  `Sanitize(body) == body`, now genuinely byte-for-byte — passes exactly when the content is
  canonical and profile-clean. "Survives unchanged" therefore means: *canonicalization changes
  bytes; sanitization must not change meaning* — no element, attribute, declaration or text the
  author wrote may be dropped.
- Add one harness test pinning idempotence itself: `Sanitize(Sanitize(x)) == Sanitize(x)` over
  the vector set.

### 6.1 Architecture: two passes, in this order

1. **XSS pass** — stock `HtmlSanitizer` defaults (default tag/attribute/scheme allow-lists),
   `KeepChildNodes = false`: a dangerous element is deleted **with** its contents
   (`<script>`/`<style>` bodies are code, not prose).
2. **Unwrap pass** — a second `HtmlSanitizer` with `KeepChildNodes = true` and the form-control
   tags removed from `AllowedTags`, so `<button>Book a call</button>` becomes the bare text
   `Book a call` (prose preserved, control gone).

Both passes are additionally hardened as follows.

### 6.2 Removed beyond the library defaults

- **Tags (unwrapped, pass 2):** `form input button select textarea option optgroup label
  fieldset legend datalist output`
- **Attributes (both passes):** `formaction formmethod formtarget action method`

### 6.3 CSS: allow-list, not deny-list

`AllowedCssProperties` is **cleared** and replaced with exactly this set — any property not
listed is dropped from `style` attributes. Note what is *absent*: `position`, `top/right/
bottom/left/inset`, `z-index`, `transform` and friends, `clip/clip-path`, `filter`,
`animation/transition`, `pointer-events`, `visibility`, `content`. Absence is the security
property (anti-phishing-overlay), so never ask for an addition that lets a box escape normal flow.

```
color direction font font-family font-size font-style font-variant font-weight
letter-spacing line-height quotes tab-size text-align text-align-last text-decoration
text-decoration-color text-decoration-line text-decoration-style text-decoration-thickness
text-indent text-overflow text-shadow text-transform unicode-bidi vertical-align
white-space word-break word-spacing word-wrap overflow-wrap hyphens
background background-color background-image background-position background-repeat
background-size background-clip background-origin opacity
margin margin-top margin-right margin-bottom margin-left
padding padding-top padding-right padding-bottom padding-left
border border-width border-style border-color
border-top border-top-width border-top-style border-top-color
border-right border-right-width border-right-style border-right-color
border-bottom border-bottom-width border-bottom-style border-bottom-color
border-left border-left-width border-left-style border-left-color
border-radius border-top-left-radius border-top-right-radius
border-bottom-left-radius border-bottom-right-radius
border-collapse border-spacing box-shadow box-sizing
width min-width max-width height min-height max-height
object-fit object-position aspect-ratio
display float clear overflow overflow-x overflow-y
gap column-gap row-gap align-items align-content align-self
justify-content justify-items justify-self
flex flex-basis flex-direction flex-grow flex-shrink flex-wrap order
list-style list-style-type list-style-position list-style-image
table-layout caption-side empty-cells
```

### 6.4 Value-level guards (run after the property filter, per declaration)

1. **Negative margins / text-indent:** any `margin*` or `text-indent` declaration containing a
   negative numeric component anywhere in its value is dropped (`margin: 10px -900px` dies
   exactly like `margin-top: -900px`). Detection: a `-` at a token start (start of value, or
   after space/comma/`(`/`:`) followed by a digit or `.`.
2. **Viewport units:** any declaration whose value contains a viewport-relative unit is dropped.
   Regex (case-insensitive): `(^|[\s,(])[-+]?\d*\.?\d+(vw|vh|vmin|vmax|svw|svh|lvw|lvh|dvw|dvh)($|[\s,;)])`
   — `width:100%` is the legitimate spelling; `100vw` is only ever the exploit's.

A dropped declaration removes only itself; the rest of the `style` attribute survives. An
emptied `style` attribute is removed.

### 6.5 Canonical conformance vectors

| # | Input | Expected output |
|---|---|---|
| 1 | `<p style="color:#336699; text-align:center">Hi</p>` | unchanged |
| 2 | `<script>alert(1)</script><p>Hi</p>` | `<p>Hi</p>` (script body must NOT leak as text) |
| 3 | `<button>Book a call</button>` | `Book a call` (unwrapped, no element) |
| 4 | `<form action="https://evil.example"><p>Real text</p></form>` | `<p>Real text</p>` |
| 5 | `<div style="position:fixed; top:0; color:red">x</div>` | `<div style="color: red">x</div>` |
| 6 | `<div style="margin: 10px -900px">x</div>` | `<div>x</div>` |
| 7 | `<div style="width:100vw; font-weight:bold">x</div>` | `<div style="font-weight: bold">x</div>` |
| 8 | `<img src="x" onerror="alert(1)">` | `<img src="x">` |
| 9 | `<a href="javascript:alert(1)">x</a>` | `<a>x</a>` |
| 10 | `<td style="width:50%; background-color:#eee">x</td>` (in a table) | unchanged |

(Compare per the canonical-output definition above: `Sanitize(input) == Sanitize(expected)`.)

## 7. Importer validation summary (what "valid" means, in one list)

The host importer will reject an export for any of: wrong `contractVersion`; unknown keys
anywhere; ZIP structure violations (§1); slug regex/uniqueness/reserved violations; missing or
duplicated home page; `parentSlug` unknown, cyclic, on the home page, or exceeding depth 3;
duplicate `sortOrder` within a sibling group or page; unknown block `type`; invalid
`layoutVariant`/`settings` values; field length overruns; `imageRef` (or `logoRef`) not in `assets[]`;
`logoAltText` missing when `logoRef` is set, or set when it is not; `buttonUrl` scheme violations;
asset/manifest mismatch, bad magic bytes, oversize files.

**Added in v0.4:**

- a block `type` not in the handover's `allowedBlockTypes` (templates from a handover; a
  `HostSection` is held to its `hostSectionId` instead, see "Added in v0.5");
- `ArticleContent` or `Form` in a template whose `handoverId` is `""`;
- `handoverId` that isn't the one the session was opened with (at integration);
- an `articleId` or `websiteFormId` that isn't in that handover's catalogue, or is no longer the
  adviser's published article / active form at the moment of import;
- `Blog` settings missing a key, or `postCount` outside 1–50;
- new images whose total size exceeds the handover's `uploads.remainingBytes` (§10).

**Stated since 2026-09-29 and enforced by iPro's importer** (each is a rule of the section named, not
a new one):

- `null` anywhere but a top-level page's `parentSlug` (§0.1);
- a missing key, at any level (§0.1, §2);
- a field the block's type does not use that is not `""` (§4, §10 rule 7);
- a `Text` block with `buttonText` but no `buttonUrl`, or the reverse (§4.2);
- a page other than the home page whose slug is `home` (§3);
- a `title` or `navigationLabel` of only spaces (§3);
- an image file name longer than 100 characters (§1 rule 4);
- a `buttonUrl` starting `/\`, holding a control character or a backslash, holding whitespace outside
  a `tel:` number, or with its scheme not in lower case (§4);
- a return whose `handoverId` is not the newest hand-over of that site (§9.2).

**Added in v0.5:**

- a `HostSection` block in a `"0.4"` template, or in a template with no `handoverId` (§4.10);
- a `hostSectionId` the hand-over did not list (§4.10, §9.3);
- a `HostSection` with a text field, an `imageRef` or a `layoutVariant` that is not `""`, or with
  `settings` other than exactly `{ "hostSectionId": integer ≥ 1 }` (§4.10);
- a template built from scratch (no `handoverId`) that states `"0.5"` (§0);
- a return that does not state the `contractVersion` of the hand-over it answers (§0).

**Not a reason to reject** (§1, §4.3): a ZIP directory entry; an asset nothing references; a Services
line longer than 80 characters, or an empty one; an image of any pixel size within §1's rules.

Every rejection names the page (its `slug`), the block (its `sortOrder`) and the section of this
document it comes from, and the importer reports all of them, not only the first (the first hundred,
then the number that remain).

Your round-trip test suite should cover every line of that list — a red test per rule, then the
export writer makes it green.

## 8. The authoritative fixtures

Two ZIP files accompany this revision. Both pass every rule in this file; use them as golden fixtures.
They are the fixtures issued on 2026-09-29, unchanged: SHA-256
`13daa3a87a60519ef7a9b41999dadf063b13645dfdf46ab21fd299664e336b72` (handover) and
`d15bf2c119f877e0b115c2c4db9a26afa093864a37bd12830186d351098602d6` (return).

- **`ipro-v0.4-handover-fixture.zip`** — what iPro sends: `handover.json`, `template.json` (the
  adviser's current site, 3 pages, 7 of the 8 types), `assets/` (3 images) and `catalog/` (1 image).
- **`ipro-v0.4-return-fixture.zip`** — what the builder sends back after an editing session on that
  site: the hero heading changed, the About image replaced by a new upload, a Resources page added with
  an `ArticleContent` block, and the blog count raised. `assets/` holds the two unchanged images
  byte-for-byte and the one new image; the replaced image is gone from both the manifest and the ZIP.

**The v0.5 pair (2026-10-13).** Two more files are kept in both repositories, made by a real trip and
not by hand: `roundtrip-v05-ipro-handover.zip`, a hand-over made by iPro of a site with three host
sections (a calculator on Home, a gallery and a hidden map on About), and
`roundtrip-v05-builder-return.zip`, what the builder's own editor exported after the calculator's box
was moved below the blog, the gallery's box hidden and the map's box removed. §4.10 and §9.3 show
their shapes; their JSON is not printed here.

The JSON of the two v0.4 fixtures, in full:

**`handover.json`** (in the handover ZIP):

```json
{
  "handoverVersion": "1.0",
  "handoverId": "h_2f6c9a41d0b84e5f",
  "contractVersion": "0.4",
  "issuedAt": "2026-09-29T15:00:00Z",
  "sessionToken": "fixture-session-token-not-a-real-secret-0000000000",
  "sessionExpiresAt": "2026-09-30T03:00:00Z",
  "adviser": {
    "businessName": "Sample Planning Group",
    "vertical": "advisers"
  },
  "allowedBlockTypes": [
    "Hero",
    "Text",
    "Services",
    "CallToAction",
    "ContactForm",
    "ArticleContent",
    "Blog",
    "Form"
  ],
  "uploads": {
    "maxFileBytes": 8388608,
    "remainingBytes": 52428800
  },
  "catalog": {
    "articles": [
      {
        "id": 101,
        "title": "Five questions to ask before renewing your mortgage",
        "summary": "A short checklist to go through when the renewal letter arrives.",
        "imageRef": "catalog/renewal-checklist.png",
        "publishedAt": "2026-09-15T14:00:00Z"
      },
      {
        "id": 102,
        "title": "Choosing between an RRSP and a TFSA",
        "summary": "The questions that decide which account to fund first.",
        "imageRef": "",
        "publishedAt": "2026-09-01T13:00:00Z"
      }
    ],
    "forms": [
      {
        "id": 45,
        "title": "Book a planning review",
        "description": "Tell us a little about what you would like to review.",
        "submitButtonText": "Send",
        "fields": [
          {
            "id": 1,
            "type": "Section",
            "label": "Your review",
            "placeholder": "",
            "helpText": "",
            "required": false,
            "options": []
          },
          {
            "id": 2,
            "type": "Dropdown",
            "label": "Topic",
            "placeholder": "",
            "helpText": "Pick the closest match.",
            "required": true,
            "options": [
              "Insurance",
              "Investments",
              "Mortgage"
            ]
          },
          {
            "id": 3,
            "type": "DateTime",
            "label": "Preferred time",
            "placeholder": "",
            "helpText": "",
            "required": false,
            "options": []
          },
          {
            "id": 4,
            "type": "CheckboxGroup",
            "label": "How should we reach you?",
            "placeholder": "",
            "helpText": "",
            "required": false,
            "options": [
              "Phone",
              "Email"
            ]
          },
          {
            "id": 5,
            "type": "Textarea",
            "label": "Anything else?",
            "placeholder": "Optional",
            "helpText": "",
            "required": false,
            "options": []
          }
        ]
      }
    ]
  }
}
```

**`template.json`** (in the handover ZIP):

```json
{
  "contractVersion": "0.4",
  "templateName": "sample-planning-group",
  "theme": "modern",
  "handoverId": "h_2f6c9a41d0b84e5f",
  "logoRef": "assets/adviser-logo.png",
  "logoAltText": "Sample Planning Group",
  "pages": [
    {
      "slug": "home",
      "parentSlug": null,
      "title": "Home",
      "navigationLabel": "Home",
      "isHomePage": true,
      "sortOrder": 0,
      "blocks": [
        {
          "type": "Hero",
          "heading": "Plan with confidence",
          "subheading": "Insurance and investment advice for families and business owners.",
          "body": "",
          "imageRef": "assets/hero-home.png",
          "buttonText": "Book a review",
          "buttonUrl": "/contact",
          "layoutVariant": "",
          "sortOrder": 0,
          "isVisible": true,
          "settings": {
            "layout": "split",
            "imagePosition": "center",
            "textAlignment": "left",
            "height": "standard",
            "overlayStrength": 45
          }
        },
        {
          "type": "Services",
          "heading": "How we can help",
          "subheading": "",
          "body": "Retirement planning\nInsurance review\nEducation savings",
          "imageRef": "",
          "buttonText": "",
          "buttonUrl": "",
          "layoutVariant": "cards",
          "sortOrder": 1,
          "isVisible": true,
          "settings": {}
        },
        {
          "type": "Blog",
          "heading": "From the blog",
          "subheading": "Latest",
          "body": "Short reads on planning topics.",
          "imageRef": "",
          "buttonText": "",
          "buttonUrl": "",
          "layoutVariant": "",
          "sortOrder": 2,
          "isVisible": true,
          "settings": {
            "postCount": 3,
            "showImages": true
          }
        },
        {
          "type": "Text",
          "heading": "",
          "subheading": "",
          "body": "This is sample template content. Replace with your own compliance wording.",
          "imageRef": "",
          "buttonText": "",
          "buttonUrl": "",
          "layoutVariant": "",
          "sortOrder": 3,
          "isVisible": true,
          "settings": {}
        }
      ]
    },
    {
      "slug": "about",
      "parentSlug": null,
      "title": "About",
      "navigationLabel": "About",
      "isHomePage": false,
      "sortOrder": 1,
      "blocks": [
        {
          "type": "Text",
          "heading": "About the practice",
          "subheading": "Who we are",
          "body": "We help families plan for the years ahead.\nEvery plan starts with a conversation.",
          "imageRef": "assets/office-team.png",
          "buttonText": "",
          "buttonUrl": "",
          "layoutVariant": "image-left",
          "sortOrder": 0,
          "isVisible": true,
          "settings": {}
        },
        {
          "type": "CallToAction",
          "heading": "Ready to talk?",
          "subheading": "",
          "body": "",
          "imageRef": "",
          "buttonText": "Get in touch",
          "buttonUrl": "/contact",
          "layoutVariant": "banner",
          "sortOrder": 1,
          "isVisible": true,
          "settings": {}
        }
      ]
    },
    {
      "slug": "contact",
      "parentSlug": null,
      "title": "Contact",
      "navigationLabel": "Contact",
      "isHomePage": false,
      "sortOrder": 2,
      "blocks": [
        {
          "type": "ContactForm",
          "heading": "Let's talk",
          "subheading": "",
          "body": "Send a note and we will get back to you within one business day.",
          "imageRef": "",
          "buttonText": "",
          "buttonUrl": "",
          "layoutVariant": "",
          "sortOrder": 0,
          "isVisible": true,
          "settings": {
            "showPhoto": true
          }
        },
        {
          "type": "Form",
          "heading": "",
          "subheading": "Planning review",
          "body": "Tell us what you would like to cover.",
          "imageRef": "",
          "buttonText": "",
          "buttonUrl": "",
          "layoutVariant": "",
          "sortOrder": 1,
          "isVisible": true,
          "settings": {
            "websiteFormId": 45
          }
        }
      ]
    }
  ],
  "assets": [
    {
      "ref": "assets/adviser-logo.png",
      "type": "image/png"
    },
    {
      "ref": "assets/hero-home.png",
      "type": "image/png"
    },
    {
      "ref": "assets/office-team.png",
      "type": "image/png"
    }
  ]
}
```

**`template.json`** (in the return ZIP):

```json
{
  "contractVersion": "0.4",
  "templateName": "sample-planning-group",
  "theme": "modern",
  "handoverId": "h_2f6c9a41d0b84e5f",
  "logoRef": "assets/adviser-logo.png",
  "logoAltText": "Sample Planning Group",
  "pages": [
    {
      "slug": "home",
      "parentSlug": null,
      "title": "Home",
      "navigationLabel": "Home",
      "isHomePage": true,
      "sortOrder": 0,
      "blocks": [
        {
          "type": "Hero",
          "heading": "Plan with confidence, at every stage",
          "subheading": "Insurance and investment advice for families and business owners.",
          "body": "",
          "imageRef": "assets/hero-home.png",
          "buttonText": "Book a review",
          "buttonUrl": "/contact",
          "layoutVariant": "",
          "sortOrder": 0,
          "isVisible": true,
          "settings": {
            "layout": "split",
            "imagePosition": "center",
            "textAlignment": "left",
            "height": "standard",
            "overlayStrength": 45
          }
        },
        {
          "type": "Services",
          "heading": "How we can help",
          "subheading": "",
          "body": "Retirement planning\nInsurance review\nEducation savings",
          "imageRef": "",
          "buttonText": "",
          "buttonUrl": "",
          "layoutVariant": "cards",
          "sortOrder": 1,
          "isVisible": true,
          "settings": {}
        },
        {
          "type": "Blog",
          "heading": "From the blog",
          "subheading": "Latest",
          "body": "Short reads on planning topics.",
          "imageRef": "",
          "buttonText": "",
          "buttonUrl": "",
          "layoutVariant": "",
          "sortOrder": 2,
          "isVisible": true,
          "settings": {
            "postCount": 4,
            "showImages": true
          }
        },
        {
          "type": "Text",
          "heading": "",
          "subheading": "",
          "body": "This is sample template content. Replace with your own compliance wording.",
          "imageRef": "",
          "buttonText": "",
          "buttonUrl": "",
          "layoutVariant": "",
          "sortOrder": 3,
          "isVisible": true,
          "settings": {}
        }
      ]
    },
    {
      "slug": "about",
      "parentSlug": null,
      "title": "About",
      "navigationLabel": "About",
      "isHomePage": false,
      "sortOrder": 1,
      "blocks": [
        {
          "type": "Text",
          "heading": "About the practice",
          "subheading": "Who we are",
          "body": "We help families plan for the years ahead.\nEvery plan starts with a conversation.",
          "imageRef": "assets/meeting-room.png",
          "buttonText": "",
          "buttonUrl": "",
          "layoutVariant": "image-left",
          "sortOrder": 0,
          "isVisible": true,
          "settings": {}
        },
        {
          "type": "CallToAction",
          "heading": "Ready to talk?",
          "subheading": "",
          "body": "",
          "imageRef": "",
          "buttonText": "Get in touch",
          "buttonUrl": "/contact",
          "layoutVariant": "banner",
          "sortOrder": 1,
          "isVisible": true,
          "settings": {}
        }
      ]
    },
    {
      "slug": "contact",
      "parentSlug": null,
      "title": "Contact",
      "navigationLabel": "Contact",
      "isHomePage": false,
      "sortOrder": 2,
      "blocks": [
        {
          "type": "ContactForm",
          "heading": "Let's talk",
          "subheading": "",
          "body": "Send a note and we will get back to you within one business day.",
          "imageRef": "",
          "buttonText": "",
          "buttonUrl": "",
          "layoutVariant": "",
          "sortOrder": 0,
          "isVisible": true,
          "settings": {
            "showPhoto": true
          }
        },
        {
          "type": "Form",
          "heading": "",
          "subheading": "Planning review",
          "body": "Tell us what you would like to cover.",
          "imageRef": "",
          "buttonText": "",
          "buttonUrl": "",
          "layoutVariant": "",
          "sortOrder": 1,
          "isVisible": true,
          "settings": {
            "websiteFormId": 45
          }
        }
      ]
    },
    {
      "slug": "resources",
      "parentSlug": null,
      "title": "Resources",
      "navigationLabel": "Resources",
      "isHomePage": false,
      "sortOrder": 3,
      "blocks": [
        {
          "type": "Text",
          "heading": "Resources",
          "subheading": "",
          "body": "Articles we wrote for our clients.",
          "imageRef": "",
          "buttonText": "",
          "buttonUrl": "",
          "layoutVariant": "",
          "sortOrder": 0,
          "isVisible": true,
          "settings": {}
        },
        {
          "type": "ArticleContent",
          "heading": "",
          "subheading": "",
          "body": "",
          "imageRef": "",
          "buttonText": "",
          "buttonUrl": "",
          "layoutVariant": "",
          "sortOrder": 1,
          "isVisible": true,
          "settings": {
            "articleId": 101
          }
        }
      ]
    }
  ],
  "assets": [
    {
      "ref": "assets/adviser-logo.png",
      "type": "image/png"
    },
    {
      "ref": "assets/hero-home.png",
      "type": "image/png"
    },
    {
      "ref": "assets/meeting-room.png",
      "type": "image/png"
    }
  ]
}
```

## 9. Handover package (host → builder) — new in v0.4

### 9.1 ZIP layout

```
handover.json          (required, at the ZIP root)
template.json          (required, at the ZIP root: the adviser's current site as a valid template
                        of the version handover.json states, with handoverId set)
assets/                (the site's images, exactly as in §1)
catalog/               (catalogue article images, flat; omit if none)
```

- Every rule in §1 applies to `template.json` and `assets/`.
- `catalog/` follows §1 rules 2–6 (flat; image types only; the filename regex; ≤ 8 MB each; real magic
  bytes). Every file in `catalog/` is referenced by exactly one `catalog.articles[].imageRef`, and every
  such reference exists as a file — both directions, like `assets[]`.
- `handover.json` is UTF-8, no BOM. Whole ZIP ≤ **40 MB**.
- Nothing else appears in the package.

### 9.2 `handover.json`

| Field | Type | Rules |
|---|---|---|
| `handoverVersion` | string | `"1.0"` with `contractVersion` `"0.4"`; `"1.1"` with `"0.5"`. Nothing else |
| `handoverId` | string | `^h_[0-9a-f]{16}$`. The export must carry it in `template.json` → `handoverId` |
| `contractVersion` | string | `"0.4"` or `"0.5"` (§0): the version of the enclosed `template.json`, and the version the export must use |
| `issuedAt` | string | ISO 8601 UTC, e.g. `"2026-09-29T15:00:00Z"` |
| `sessionToken` | string | opaque, 32–200 characters; the only credential the builder ever holds (§11). In fixtures, a dummy value |
| `sessionExpiresAt` | string | ISO 8601 UTC; normally 12 hours after `issuedAt`. Use the value given as the deadline; don't check the arithmetic |
| `adviser` | object | exactly `{ "businessName": string ≤ 120, plain text, "vertical": "advisers" \| "accountants" \| "mortgage" }` — for display and brand defaults only |
| `allowedBlockTypes` | array | the export types this adviser's package includes: always the five v0.3 types, plus any of `ArticleContent`, `Blog`, `Form`. Offer only these in the palette |
| `uploads` | object | exactly `{ "maxFileBytes": 8388608, "remainingBytes": integer ≥ 0 }` — `remainingBytes` is the adviser's remaining storage (§10) |
| `catalog` | object | `"1.0"`: exactly `{ "articles": [...], "forms": [...] }`. `"1.1"`: exactly `{ "articles": [...], "forms": [...], "hostSections": [...] }` (§9.3). Any array may be empty |

No other keys. Treat the file as strictly as the importer treats yours: reject unknown keys.
It carries nothing else about the adviser — no email, phone, internal account IDs, client data or
billing. Business details that appear on the site are already in its blocks.

**The same package opened twice.** Every issuance has a new `handoverId`. Opening the same package
again must reopen it, not fail: an adviser double-clicks, or opens the file twice. Different content
under a known `handoverId` never comes from iPro, and may be refused.

**Only the newest hand-over of a site can come back.** Each hand-over carries the site as it was at
that moment; once iPro makes a newer one, a package made from an older one is refused.

### 9.3 The catalogue

`catalog.articles[]` — only the adviser's **published** articles, newest first:

| Field | Type | Rules |
|---|---|---|
| `id` | integer | ≥ 1; the value an `ArticleContent` block puts in `settings.articleId` |
| `title` | string | ≤ 200, plain text |
| `summary` | string | ≤ 500, plain text |
| `imageRef` | string | `""` or `"catalog/<file>"` (§9.1) |
| `publishedAt` | string | ISO 8601 UTC |

`catalog.forms[]` — only the adviser's **active** forms:

| Field | Type | Rules |
|---|---|---|
| `id` | integer | ≥ 1; the value a `Form` block puts in `settings.websiteFormId` |
| `title` | string | ≤ 200, plain text |
| `description` | string | ≤ 1 000, plain text |
| `submitButtonText` | string | ≤ 60, plain text |
| `fields` | array | in display order; each exactly `{ "id": integer ≥ 1, "type": "Text" \| "Textarea" \| "CheckboxGroup" \| "Dropdown" \| "Section" \| "DateTime", "label": string ≤ 200, "placeholder": string ≤ 200, "helpText": string ≤ 500, "required": boolean, "options": [string ≤ 200, ...] }` — `options` is empty unless the type is `CheckboxGroup` or `Dropdown`; a `Section` is a heading inside the form, never `required` |

`catalog.hostSections[]` (`handoverVersion` `"1.1"` only) — the sections of the site that travel as
locked boxes (§4.10), in the order they stand on the site:

| Field | Type | Rules |
|---|---|---|
| `id` | integer | ≥ 1, used once in the list; the value a `HostSection` block puts in `settings.hostSectionId` |
| `kind` | string | iPro's own name for the kind of section: matches `[A-Za-z][A-Za-z0-9]{0,39}` as a whole value (`Gallery`, `Calculator`, `PriceList`). For an icon at most; do not rely on a fixed list |
| `label` | string | 1–60, plain text: what to call the box (`Photo Gallery`) |
| `title` | string | ≤ 200, plain text: the section's own heading, `""` when it has none. It tells two boxes of one kind apart |

Every `HostSection` block of the hand-over's `template.json` has its `id` here, and every entry here is
used by exactly one block of it. The `title` is the one thing of a host section that travels: its
text, its settings and its pictures stay in iPro.

The catalogue is for **picking and stand-ins only** (§5.1). It is never exported: only an `id` travels
back, inside a block's `settings`. The host's standard contact fields (first name, last name, email,
phone, consent) are not listed in `fields`; every form has them (§5.1).

### 9.4 When the builder refuses a handover

Refuse a handover only for a rule this contract states, and name the rule in the message. Anything
else the builder would like to be stricter about (image dimensions, line lengths) belongs in
readiness, where the adviser can fix it.

## 10. Image references — all the rules in one place (new in v0.4)

1. In `template.json`, **every image is a file** in `assets/`, named by a block's `imageRef` or by
   `logoRef`, and declared in `assets[]`. There are no image URLs anywhere in `template.json`.
2. The handover delivers the adviser's current site images the same way: iPro writes them into
   `assets/` with names that match §1's regex. Keep a file's **bytes unchanged** unless the adviser
   replaces that image.
3. The return carries **every image the site uses**, old and new. iPro recognizes a file that is
   byte-identical (SHA-256) to one it delivered in this handover and links the image it already
   stores, so unchanged images cost no storage. Re-encoding, resizing, re-compressing or stripping
   metadata from an unchanged image breaks the match and uses the adviser's storage. Matching is by
   content, not by name: a renamed file with identical bytes still matches.
4. **New images** are the files that aren't byte-identical to a handover file. Each is within
   `uploads.maxFileBytes` and §1's rules, and together they fit within `uploads.remainingBytes`.
   Each distinct new image counts once, by SHA-256; identical new images should be one file anyway.
5. An image the adviser removed or replaced is simply absent from the return — manifest and ZIP both.
6. **Catalogue images** live in `catalog/` and are referenced only from `catalog.articles[].imageRef`,
   for stand-ins. They never appear in `template.json`, in `assets[]`, or in the return.
7. Blocks that don't take an image — `Services`, `CallToAction`, `ContactForm`, `ArticleContent`,
   `Blog`, `Form`, `HostSection` — emit `imageRef: ""`.
8. A host section's own pictures (a gallery's photos) are **not in the package** at all. They stay in
   iPro's storage, and the locked box shows none of them (§5.1).

## 11. Transport: the builder for iPro's customers (Beta) — rewritten 2026-10-17

Until 2026-10-17 this section was placeholders. It is now what the two sides do when an adviser opens
the builder from iPro (the Beta, TODO 585). A builder used on someone's own machine (the owner's, a
designer's) still opens a hand-over from a file and writes the return to a file, as before: nothing in
this section applies to it, and no package changes because of it.

**Who talks to whom.** The adviser's browser carries one thing from iPro to the builder: a one-time
pass, in a form post. Everything else is the builder's **server** talking to iPro's, with the pass or
the session token as a bearer token (`Authorization: Bearer <token>`) **and** the secret the two servers
share in the header `X-Ipro-Service-Key` (at least 32 characters; iPro's `Builder:ServiceKey`, the
builder's `Hosting:ServiceKey`). Without that header, or with a wrong one, iPro's `builder-api`
addresses answer `404`: they exist for the builder's server alone. Neither token ever goes into an
address, a log or the browser. iPro keeps only the SHA-256 of each. The builder follows no redirect
from iPro, and takes nothing but iPro's own answer, as described below, for an answer.

1. **Open.** On My Website the adviser presses "Try the new builder (Beta)", "Continue my draft" or
   "Start again from my live site". iPro answers with a page whose form posts itself to
   `POST <builder>/open` with one field, `pass`: 32 random bytes, base64url without padding (43
   characters). A pass works **once**, for **10 minutes**, and not at all once the Beta was switched
   off after it was issued. The builder takes the post only from iPro's own page: the browser's
   `Origin` (or, without one, its `Referer`) must be iPro's address or one of the other names iPro's
   portal answers on (the builder's `Hosting:IproOrigins`); anything else is refused before iPro is
   asked.
2. **Session.** The builder's server sends `POST <ipro>/builder-api/session` with the pass as bearer
   token. iPro answers `200` and:

   | Field | Meaning |
   |---|---|
   | `tenant` | `t_` and 32 lower-case hex digits: the builder's name for this adviser, the same every time, pointing at nothing in iPro. Keep the adviser's draft and files under it |
   | `sessionToken` | 43 characters; the only credential the builder holds from here on |
   | `sessionExpiresAt` | ISO 8601 UTC, 12 hours after issue: the latest the session lasts |
   | `current` | `{ handoverId, state, at }`: the site's newest hand-over (`""` when there is none), and where it stands: `"open"` (a package of it can still be sent), `"published"` (a package of it is on the website since `at`) or `"putBack"` (one went on and was taken back off at `at`) |
   | `startFresh` | `true`: the adviser chose "Start again from my live site" |
   | `adviser` | the business name, for the editor's bar when `site` is missing ("<name>'s website") |
   | `publishing` | `"staff"` (iPro's staff review what is sent) or `"adviser"` (the adviser reviews and publishes it) |
   | `returnTo` | the address of My Website on iPro's host, for the bar's "My Website" |
   | `look` | `{ accent, background, font, headingSize, bodySize, buttons }`: the site's own colours (`#rrggbb`), font list, sizes in pixels and button shape (`rounded`, `square`, `pill`), so the canvas can draw the site as it looks. Display only: nothing of it travels back (614, optional, all of them) and what draws the page as the site's own design does: `design` (`modern-professional`, `classic-sidebar` or `editorial-visual`, the shell iPro's public site uses), `vars` (the custom properties that shell sets in its `:root`: `--site-theme`, `-on`, `-band`, `-ink`, `-deep`, `-soft`, `--section-space`, `--button-radius`, `--heading-font-size`, `--body-font-size`, and `--ink`, `--muted`, `--line` where the shell has them; each a colour or whole pixels), `bodyClass` (`header-*`, `hero-*`, `hero-style-*`, `buttons-*`), `headerClass` (`style-*`, `logo-*`, `logo-size-*`, `is-sticky`), `siteFont` (the font the live page really draws with; empty when it keeps the browser's), `company` (written over the hero), `monogram` (its first letter, where the hero has no picture), `contact` (`{ name, phone }`, beside the contact form) and `copyright` (the footer's name) and `headerActions` (`{ phone, email, button }`: the phone the header shows, whether it shows its Email link, its button's words). The builder drops any value outside its shape, and without `design` draws the page in its own layout. |
   | `site` | (613, optional) the site's own name, for the editor's iPro bar; at most 120 characters, control characters dropped |
   | `address` | (613, optional) the address the site's clients are shown (its own domain while it is serving, with the adviser's capitals, else the free one); a host name only, dropped otherwise |
   | `accent` | (613, optional) the adviser's own portal colour (`#rrggbb`, one of the portal's six; iPro's blue when unset), for the editor's main button, darkened by the builder until white writing on it reads at 4.5:1 |
   | `help` | (613, optional) the builder's section of iPro's guide; followed only when it is on `returnTo`'s host |
   | `notInMenu` | (iPro TODO 621, optional) the slugs of the pages the site's own menu leaves out (not shown in the menu, or not published): the builder leaves them, and whatever hangs under them, out of the menu it draws and keeps them in its Pages list. Display only: "show in menu" stays on iPro's side (section 12) |

   `401 { "message": … }` for a pass that is unknown, used or expired, or when the Beta is no longer on
   for the adviser, the account is closed, its billing needs attention or its package no longer
   includes the website. The builder shows the message and a link back.

   **What the builder opens.** It decides, from what it holds and from `current`: it **continues** the
   adviser's draft when `startFresh` is `false`, the draft is of `current.handoverId` and
   `current.state` is `"open"`. Otherwise (no draft, the adviser chose to start again, the draft's
   changes went onto the website or were put back, or the site was handed over again since) it makes a
   **fresh** draft from a new hand-over (step 3), and the draft it had is kept among the adviser's
   versions (the three newest such "kept drafts" stay beyond the ten that roll away): nothing the
   adviser made is thrown away. Restoring a kept draft carries it onto the new hand-over, so it can be
   sent: its `handoverId` and `contractVersion` follow the new one; each locked box becomes the new
   hand-over's box of the same kind and title (on the same page first, anywhere after), else is
   removed; and what the new hand-over has that the draft's own hand-over and the draft do not (a page,
   or a section, added on the website meanwhile) is added to it, at the end of its page, within §3's
   limits.
   The editor says which of these happened. The builder records the adviser (`tenant`) before it writes
   any of their files.
   A session starts bound to `current.handoverId` while `current.state` is `"open"` (the draft the
   builder continues is of that one), and to none otherwise.
3. **Package.** `GET <ipro>/builder-api/handover` with the session token: `200` with
   `Content-Type: application/zip`, the §9 hand-over package of the site **as it is now**, made for
   this session. From then on the session works on that hand-over: §9's "only the newest hand-over of a
   site can come back" applies, and a package of any other hand-over is refused at steps 5 and 6. What
   the adviser sent or looked at of an earlier hand-over, and is still waiting, is withdrawn (it could
   no longer be brought in). `422 { "message", "reasons": [ … ] }` when the site cannot be handed over;
   each reason is a sentence for the adviser.
4. **Still valid?** `GET <ipro>/builder-api/session` with the session token: `200 { "sessionExpiresAt" }`
   while the session stands, `401` once it is over, ended, the Beta was switched off, the account
   closed, its billing needs attention or its package no longer includes the website. The builder asks
   at least every **5 minutes** and ends its own session on `401`. Only iPro's `200` with
   `sessionExpiresAt` confirms a session; anything else (unreachable, busy, a redirect, a page that is
   not iPro's answer) confirms nothing: the builder carries on, asks again within the minute, and ends
   the session once iPro has given no answer of its own for **15 minutes** (counted from its first
   missed answer; a Save arriving at that moment is still kept). The builder also ends a
   session unused for 2 hours (its editor keeps it in use while the adviser works, and saves their
   changes when they step away or before the session's end).
5. **See it on my site.** `POST <ipro>/builder-api/preview[?page=<slug>]` with the session token and a
   return-format ZIP (§1) as the body: `200 { "previewUrl", "expiresAt" }`. The address shows the page
   (`page`, else the home page) through the site's real design, with every section publishing would
   keep, works for an hour, writes nothing to the site (its new pictures are files of its own, never
   rows of the media library, and go with it) and is hidden from search engines. Each preview replaces
   the one before it.
6. **Send.** `POST <ipro>/builder-api/return` with the session token and the return ZIP: `202` and
   `{ "publishing": "staff", "message" }`, or `{ "publishing": "adviser", "reviewUrl", "message" }`
   for an adviser who reviews it (the builder sends them there). Only that `202` with `publishing` is a
   send that reached iPro. Nothing on the site changes at this step; publishing always happens in
   iPro, never in the builder. A send replaces the adviser's earlier send that is still waiting (only an
   earlier one: two arriving at once never drop each other). `422 { "message", "errors": [ { "page",
   "message" } ] }` when the package breaks a rule of §7 (`page` names where, in iPro's words), is not
   of the session's hand-over, or is of a hand-over whose changes already went onto the website (step 5
   is refused the same way): the adviser opens the builder again, which opens the site as it is now.
7. **Erase.** When an adviser's account is deleted, iPro calls `POST <builder>/internal/erase` with
   `{ "tenant": "t_…" }` and the header `X-Ipro-Service-Key` (a secret the two share, at least 32
   characters). The builder deletes that adviser's draft, versions, files and sessions and answers
   `200`, also when it has nothing of them. iPro repeats the call until it gets `200`.

Rules for the builder: it never asks the browser who the adviser is (the tenant comes from step 2
only); a request without a session it opened gets nothing of any adviser; it keeps an adviser's draft
for 90 days after their last visit and then deletes it (iPro counts the same 90 days from the same
visit, for "Continue my draft"); it offers no picture collection whose licence the owner has not
confirmed; addresses iPro gives (`returnTo`, `previewUrl`, `reviewUrl`) are followed only when they are
on iPro's own host; it loads no script or style from another server.

Rules for iPro: while the Beta is on for an adviser, iPro's staff make no hand-over of that site
themselves (the adviser's open draft could no longer be sent); the builder's server reaches iPro from
one address for every adviser, so iPro's limit per address does not count `builder-api` (the service
key decides there, and the builder limits each adviser's calls).

## 12. Round trip — what the host keeps and what it refuses (new in v0.4)

- iPro hands over a site whose every section is either one of the adviser's allowed export types or
  travels as a locked box (§4.10, from v0.5), and whose every page and field is within §3–§4. What
  still stops a hand-over is said to iPro's staff instead: a page with no sections of its own, a section
  type the adviser's package no longer includes, a picture that cannot be read.
- Page settings the contract doesn't carry — SEO title, SEO description, "show in menu", page
  published — are kept by iPro for every page whose `slug` is unchanged, and (from 2026-10-15) for a
  page given a new `slug` that iPro can tell, or iPro's staff say, is that page (§12.1). New pages get
  iPro's defaults.
- `theme` in the return is the hint it has always been; the adviser confirms the theme on iPro's
  review page.

### 12.1 What iPro's importer does today (2026-10-15)

A package is brought in by iPro's staff for one customer. It is either a template built from scratch
(`handoverId: ""`) or the return of that site's newest hand-over.

- **Nothing changes at upload.** The package is checked (§7) and its new images are stored. iPro's
  staff then see what it would do, page by page, and each page rendered through the customer's real
  shell, before anything is replaced.
- **The home page is the home page.** The package's home page replaces the site's home page, whatever
  either side calls it; if its address changes, the old address keeps arriving. Every other page is
  matched by `slug`: a match is replaced and keeps the page settings listed above; a page with a new
  slug is added, unless it is a page of the hand-over moved (next).
- **A page given a new `slug` in the builder is moved, not new,** when iPro can tell which page of the
  hand-over it is: by its shortened menu label (below), when the hand-over gave that label to one page
  only and no other page of the return carries it; else by its `title`, else by its `navigationLabel`,
  each only when exactly one page that did not come back and exactly one page at a new `slug` share it
  (capitals and spaces aside). It then replaces that page where it stands and keeps the page settings
  listed above, its language, its old addresses and the pages under it in iPro, and its old address
  keeps arriving once the page is published (unless another page has that address afterwards). iPro's
  staff see each such page on the review, with how iPro knew, and may say otherwise, or say which page
  a new page is where iPro could not tell. A page that does go (removed in the builder, or not chosen)
  is named on the review with what goes with it that its sections do not show, and where links to its
  address go afterwards. Nothing changes for the builder: a page's `slug` may be changed freely, and
  keeping its `title` while doing so is what lets iPro tell without asking.
- **A page the package does not have:**
  - *built from scratch:* it is kept as it is, after the package's pages in the menu, unless iPro's
    staff choose to remove it;
  - *a return:* a page that was in the hand-over and did not come back was removed in the builder, and
    is removed (iPro's staff may keep it); a page made in iPro after the hand-over was never in the
    builder, and is kept.
- **What was changed in iPro after a hand-over is replaced by its return.** iPro's review names the
  pages this affects before anything is written.
- **A menu label longer than 40 characters** (§3) goes to the builder shortened (see below), and iPro
  keeps the page's own. A `navigationLabel` that comes back exactly as it went is the page's own label
  again, as the site has it then: a label the adviser changed in iPro meanwhile stays changed (an
  exception to the line above, as a locked box is, below). A label changed in the builder is written.
  iPro's review says which, page by page. (iPro's starter articles are named in the menu by their whole
  title, so most sites have such a page; until 2026-10-14 every one of them stopped the hand-over.)
- **A replaced page's sections are replaced as a set,** and for a section of a kind the builder cannot
  carry (a map, a gallery, a price list) that means one of these:
  - *a locked box in the return* (§4.10) becomes the section it stands for **as the site has it now**,
    at the place the box has. Nothing in a box could be changed in the builder, so a change the adviser
    made to that section in iPro meanwhile is kept, not undone (an exception to the line above).
    It is hidden or shown as the site has it, unless the return changed that on the box. Only when the
    site no longer has the section is it written as it was at the hand-over. iPro's review says when
    that happens, and when a box comes back shown though its section is hidden on the site (or hidden
    though it is shown), from another page, or as a copy.
  - *a locked box that did not come back* was removed in the builder. When the site still has that
    section, iPro's review names it and iPro's staff may keep it.
  - *a section the package does not have at all:* in a template built from scratch, every such section
    of a replaced page; in a return, one added in iPro after the hand-over. It does not come back with
    the package. iPro's review names each one, and iPro's staff may keep it.
  - *a kept section* stays as the site has it now, at the end of its page.
- **Articles and forms are asked about twice:** when a return is uploaded, and again when it is
  brought in. One unpublished or switched off in between stops it.
- **The logo:** a `logoRef` replaces the site's logo. `logoRef: ""` in a template built from scratch
  says nothing about the site's logo, which stays; in a return it means the adviser removed it.
- **Images are matched by content** (§10.3): against what the hand-over delivered, and against the
  images earlier packages brought into the site, whatever name they travel under.
- **The previous site is kept** and can be put back in one step.

What iPro's hand-over does on the way out, so the builder is not surprised: a field a section type
does not use is written `""` whatever the site holds there; a Text section's button with words and no
link is left out (on the live site it leads nowhere); line breaks are written as LF; a menu label longer
than 40 characters is written on one line and, if still too long, cut after the last whole word that fits
(inside a very long word) and ended with `…` (one character, U+2026), so it is at most 40 (what becomes
of it is above); page and section
`sortOrder` are renumbered from 0; a scheme is written in lower case; image files are named
`<readable name>-<8 hex of its SHA-256>.<type>`; `adviser.vertical` is `advisers` for a business that is
none of the three (until this document has a value for it).

---

## Changelog

- **v0.5, third doc-only revision (2026-10-17)** — §11 is no longer placeholders: it is the transport
  the builder for iPro's customers (Beta, TODO 585) uses. The pass travels in a form post, not an
  address; the session comes from `POST /builder-api/session`, which also says who the adviser is to
  the builder (an opaque name), who publishes, and how the site looks; the hand-over is fetched with
  the session; a preview can name its page; iPro can ask the builder to erase an adviser. The paths
  changed from the placeholders (`/builder/...` became `/builder-api/...`). No package changes: every
  package the builder makes or opens is what it was, and a builder working from files is unaffected.
  Revised the same day after three independent reviews: every `builder-api` call carries the servers'
  shared key; iPro says where the site's newest hand-over stands (`current`, in place of `handoverId`)
  and the builder decides whether a draft is continued, keeping a draft it does not continue as a
  version; only iPro's own answers count, and an unconfirmed session ends after 15 minutes; `/open`
  takes a post from iPro's page only; a hand-over whose changes went onto the website cannot be sent
  again; a look writes no row to the media library; iPro makes no staff hand-over while the Beta is on.
- **v0.5, second doc-only revision (2026-10-15)** — a page given a new `slug` in the builder came back
  as a new page with iPro's defaults, and the page it was went with its SEO title and description, its
  "show in menu" and "published" settings and its old address, without a word on the review. iPro now
  takes such a page for the page it was when it can tell (§12, §12.1), and its review names what goes
  with any page that is removed. Nothing changes for the builder: every package it makes or opens is
  what it was.
- **v0.5, doc-only revision (2026-10-14)** — the owner's own site was refused a hand-over: four starter
  articles are named in the menu by titles of 42 to 51 characters. iPro now sends such a label shortened
  at a word, and a label that comes back as it went is the page's own again (§12.1, and the list of what
  the hand-over does on the way out). Nothing changes for the builder: §3's 40 characters stand, and
  every package it makes or opens is what it was.
- **v0.5 (2026-10-13)** — host sections, after the first real hand-over was refused: iPro's own site
  has fourteen pages with sections the builder does not carry, and so will most customers'. New block
  type `HostSection`, the locked box (§4.10), and what the builder draws for it (§5.1);
  `catalog.hostSections` and `handoverVersion` `"1.1"` (§9.2, §9.3); the validation additions
  (§7); what iPro does with a box on the way back, and that a section a package does not have can be
  kept (§12, §12.1). Additive, and stated only where it is used (§0): a site without a host
  section is handed over and returned as a v0.4 package as before, and the two v0.4 fixtures are
  unchanged, byte for byte. Three independent reviews of both sides the same day tightened it: the two
  version rules are import rules (§7); only a host section's heading travels (§9.3); a kept section,
  and a section whose box comes back, is the one the site has now (§12.1).
- **v0.4, second doc-only revision (2026-10-12)** — after two independent audits compared the builder,
  iPro's importer and this document, and once iPro's hand-over existed. `contractVersion` stays `"0.4"`;
  fixtures unchanged. One rule for the characters of `buttonUrl`, applied identically by both sides
  (§4, §7): the builder had refused a phone number written with a space, and iPro had let a tab
  through. The reserved addresses are listed (§3), so a designer is told while editing. Only a site's
  newest hand-over can come back (§9.2, §7). §12.1 rewritten now that a return is taken: what happens
  to a page removed in the builder, to a page made in iPro since, to changes made in iPro after the
  hand-over; and what the hand-over tidies on the way out. The importer reports the first hundred
  problems and counts the rest (§7). Checked by a real trip: a hand-over made by iPro, opened, edited
  and exported by the builder's own code, and taken back by iPro with every image recognized.
- **v0.4, consolidated revision (2026-10-12, doc-only — no validation rule and no part of the schema
  changed, `contractVersion` stays `"0.4"`, fixtures unchanged)** — the clarifications iPro gave on
  2026-09-29 (review note, section 6) are folded into their sections: rejection versus fallback for
  `layoutVariant` and `settings` (§4, §4.1); a Text block's button (§4.2); `showPhoto` required
  (§4.5); Services line guidance (§4.3); unreferenced assets, ZIP directory entries and the image
  guarantees (§1); when to refuse a handover (§9.4); `sessionExpiresAt` and reopening a package
  (§9.2); counting new images (§10); UTF-16 lengths and whole-value patterns (§0.1); file names of at
  most 100 characters (§1); the two wording fixes (§4, §4.1). Added from building iPro's importer,
  each a consequence of a rule already stated: `null` is never a value and no key is missing (§0.1);
  an unused field is `""` and a value there is refused (§4); `home` belongs to the home page and a
  title is not only spaces (§3); `/\` is refused like `//` (§4); the importer's two safety limits
  (§1); what iPro does with `logoAltText` (§2); the restated list in §7; what the importer does
  with a site (§12.1). Checked against the builder's six exports of 2026-10-12: all pass.
- **v0.4 (2026-09-29)** — articles, forms and the handover, on the builder developer's questions of
  2026-09-29. Palette 5 → **8 export types**: `ArticleContent`, `Blog`, `Form` (§4.7–4.9), each carrying
  only a reference or display settings — host content never enters the contract, so the plain-text rule
  and §6 stand unchanged. New top-level key `handoverId` (§2). New: stand-ins (§5.1), the handover
  package (§9), image-reference rules (§10), transport (§11), round trip (§12), validation additions
  (§7), authoritative fixtures (§8). Per policy this is a schema change, so `contractVersion` is now
  exactly `"0.4"`. The sample shapes in the host's two answer notes of 2026-09-29 are superseded.
- **v0.3 (2026-08-30)** — template-level logo, on the builder developer's request: `logoRef` +
  `logoAltText` (rules in §2). Approved after confirming the host renders a site logo
  (`AgentWebsite.LogoUrl`, all three shells), so the importer has a real landing spot — the
  builder's upload/replace/remove/preview/export flow is green-lit end to end. Per policy this
  is a schema change, so `contractVersion` is now exactly `"0.3"`.
- **v0.2 clarifications (2026-08-29, doc-only — no validation rule changed, `contractVersion`
  stays `"0.2"`)** — three developer-raised points: (1) confirmed there are **5 export types**;
  Footer/Disclaimer are editor presets exporting as `Text` (§4, §4.6); (2) sanitizer
  conformance unified to one definition — canonical-output comparison
  `Sanitize(input) == Sanitize(expected)`, plus an idempotence pin (§6); (3) ONBOARDING §7
  item 5 rewritten: the sanitizer harness is preparation for a future rich-text contract,
  since v0.2 exports plain text only. Policy note: doc-only clarifications are recorded here
  without a version bump; anything touching a validation rule or the schema bumps the minor.
- **v0.2 (2026-08-29)** — first self-contained revision. Added: ZIP package spec (§1);
  `templateName`; `parentSlug` + depth-3 hierarchy; per-block required fields, variant enums and
  settings shapes; plain-text rule for all palette fields (corrects v0.1, which wrongly implied
  rich-text HTML in `body`); exact sanitizer profile as standing appendix; validation summary;
  preview definition; field length caps; URL scheme rules.
- **v0.1 (2026-08-29)** — draft sketch in ONBOARDING.md §5. Superseded.
