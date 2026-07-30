# Template System & Navigation — Brief for Outside Review

**Date:** July 30, 2026
**Purpose:** Get an outside business/marketing perspective on IPRO's website template and
navigation system before more engineering work continues on it.

## What IPRO is

IPRO is a business-growth platform for independent professionals — financial advisers, insurance
agents, accountants, mortgage brokers — bundling a website, CRM, email marketing, billing, and a
client portal in one product. This brief is about one piece: the website template and page-building
system agents use to build their own public-facing site.

## The problem, plainly

The agent-facing website builder works, but hasn't felt right for a while — both the process of
building a page (redundant, unclear choices) and the visual result (inconsistent quality depending
on which options an agent picks). A same-day attempt to improve one piece of it (site navigation)
surfaced how unresolved the underlying design actually is, which is why we're pausing for outside
input before writing more code.

## What exists today

- **Three template "families":** Modern Professional, Classic Sidebar, Editorial Visual. Each has
  its own visual identity (colors, type, hero style). An agent picks one, then can further
  customize accent color, font, section spacing, and button style.
- **A block-based page builder:** each page is a stack of content "blocks" an agent adds and
  arranges — Hero, Services, Text, Contact Form, Testimonials, Reviews, Photo Gallery, Video, "Did
  You Know" articles, and (as of today) a Calculator block with 7 built-in financial calculators.
  Editing is form-based, not drag-and-drop.
- **A sidebar option:** independent of template choice, an agent can add a persistent left or right
  sidebar rail to every page. Until today it only ever showed a small contact card (logo, photo,
  name, company, phone, email).
- **A top navigation bar:** present on every site regardless of sidebar choice, listing the site's
  pages.

## What's broken or unresolved, honestly

1. **Visual inconsistency across combinations.** Template, sidebar, and header-style options can
   combine in ways nobody has actually looked at together. One example found today: with a specific
   header style, the very top of the page's content renders partly hidden behind the header instead
   of below it.
2. **Top nav vs. side nav — the central open question.** Two older IPRO-family reference sites we
   still have access to (below) show a *working top menu and a working, deep side menu at the same
   time* — clearly serving different purposes, not duplicating each other. A same-day attempt to
   bring a side menu into the current system got this wrong twice: first by showing the identical
   page list in both places (visually redundant), then — trying to fix that — by removing the top
   menu's links entirely and leaving only the side menu. Neither is what was actually wanted.
   **There's no answer yet for what these two navigation surfaces should each be responsible for,**
   and that needs deciding before any more work happens here.
3. **Vertical content was never populated.** Agents pick a business type at signup (accountant,
   financial planner, mortgage broker, etc.), but the site they get is generic — no starter content
   specific to their vertical. Real content already exists and is unused: short client-facing
   blurbs, prospect-facing blurbs, and long-form articles for most financial/insurance product
   topics, plus roughly 50 working calculators (mortgage, retirement, loan, tax). None of it is
   connected to the "which business type did you pick" choice yet.
4. **Too many independent dials, no guardrails.** Template family, sidebar position, header style,
   hero layout, per-block layout variants, and theme color are all independently choosable. Nothing
   currently prevents — or even flags — a combination that just doesn't look good together.

## Reference material worth looking at

- **4ipro.com** (accountant vertical) and **girardfinancial.com** (financial-planner vertical) —
  archived snapshots of an earlier IPRO-family product, both showing the top-menu + side-menu
  pattern referenced above.
- The current live template gallery (can be shown directly on request).

## What we're asking for

Not a rebrand. Specifically:

- A recommendation for how top navigation and side navigation should relate — the same information
  shown two ways, or genuinely different jobs (and if so, what each one's job actually is).
- A gut check on whether "many independent dials" is the right model for template customization, or
  whether a smaller set of complete, pre-composed looks would serve agents better.
- Input on how the per-vertical content already on hand (client/prospect paragraphs, articles,
  calculators) should actually surface on a page — starter content an agent can accept and edit, a
  standing library, or something else.
