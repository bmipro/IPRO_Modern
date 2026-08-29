# IPRO.TemplateBuilder — new-developer onboarding guide

*Written 2026-08-29 by the IPRO_Modern engineering side. This document is self-contained on
purpose: the builder developer should be able to start — and finish the prototype — without any
access to the main repository, production, or customer data.*

---

## 1. What you are building

A **standalone, local-first WYSIWYG page builder** (`IPRO.TemplateBuilder`) that prototypes a
Wix-style editing experience for adviser websites. It runs entirely on your machine, saves drafts
locally, and proves the editing model. **It will later be stitched into the IPRO portal — but not
by sharing code. It integrates through a JSON contract** (section 5). Get the contract right and
integration is a bounded importer; get it wrong and the prototype is a demo we throw away.

**Non-goals for the prototype:** authentication, multi-tenancy, Azure, payments, email,
production data. None of it. Local drafts, local preview, local export.

---

## 2. The host product you are integrating with (context, not dependencies)

IPRO_Modern is a multi-tenant SaaS for Canadian financial/insurance advisers. Each adviser
("agent") gets a portal plus a **public website** on their own subdomain or custom domain. The
part you care about is the website system:

- A site is an `AgentWebsite` (template choice, domain, published flag) with `WebsitePage` rows
  (slug, navigation label, parent page, sort order) and, per page, ordered
  `WebsiteContentBlock` rows.
- **Everything a visitor sees is rendered server-side from blocks** through one of three themed
  shells (Modern / Classic / Editorial). There is no client-side page framework.
- A block has: `BlockType`, `Heading`, `Subheading`, `Body`, `ImageUrl`, `ButtonText`,
  `ButtonUrl`, `LayoutVariant`, `SortOrder`, `IsVisible`, and a `SettingsJson` string holding a
  small per-type settings object (defensive parse: bad JSON falls back to defaults).
- There are **20 block types** today: Hero, Text, Services, CallToAction, ContactForm,
  NewsletterSignup, TestimonialForm, PollResults, LeadMagnet, Reviews, AgentInfo, Maps, Form,
  DidYouKnow, ArticleContent, Video, Gallery, Calculator, SectionIndex, Blog.
- Several block types are **dynamic**: forms write leads into the CRM with spam/consent checks,
  Blog lists the agent's published articles, PollResults show live results, some blocks are
  gated by the agent's subscription package. **A static HTML export cannot reproduce these** —
  which is why the contract exports structure, not markup.

### Hard rules the host enforces (your design must respect them)

1. **All agent-authored rich text passes a sanitizer** with an *allow-list* of ~120 CSS
   properties and value guards (no `position/transform/z-index/clip-path`, no negative margins,
   no viewport units). This is a phishing/overlay defence that took real incidents to get right.
   The builder must never produce content that only looks right when the sanitizer is bypassed.
2. **Routing is single-segment `/{slug}` with site-wide-unique slugs**, navigation at most three
   levels deep. Do not design URL schemes that need nested routes.
3. **Prices and package features are never hardcoded** anywhere — they render from the database.
   Sample content in the builder must not contain dollar amounts or package promises.
4. Pages in the host go **live immediately on save** (no draft system yet). Your draft/version
   model is genuinely new ground — design it well and it may become the host's first draft
   system.

---

## 3. The host's exact stack (match idioms, not references)

| Layer | What the host uses | What the builder should use |
|---|---|---|
| Runtime | .NET 8 (`net8.0`), ASP.NET Core MVC + Razor views | Same — .NET 8, MVC + Razor (SDK 10.x installed is fine; target `net8.0`) |
| ORM | EF Core 8 via Pomelo MySQL `8.0.2` | EF Core 8 via `Microsoft.EntityFrameworkCore.Sqlite` |
| Database | MySQL 8.0.36 (Azure Flexible Server in prod, local MySQL for dev) | **SQLite** — zero-install, file-based; same EF idioms |
| Front-end | Bootstrap 5.3 + Font Awesome (CDN), vanilla JS, CSP nonces, **no SPA framework** | Same base + **GrapesJS** (pin a version; BSD-3 licensed) |
| Files | Azure Blob Storage | Local folder under the app (e.g. `App_Data/assets/`) |
| Email/Jobs/Payments | SendGrid `9.28.1`, Hangfire `1.8.23`, PayPal | **None** — out of scope |
| HTML hygiene | HtmlSanitizer (Ganss) `9.2.995` | Same package, same allow-list philosophy, at import/preview |
| Tests | xUnit `2.9.2`, integration-first against a real DB | xUnit; test the export/import round-trip above all |
| Versioning | Central package management (`Directory.Packages.props`) | Same pattern |
| Hosting (later) | Azure App Service Linux, region Canada East | Irrelevant until integration |

---

## 4. The one architectural decision that is not negotiable

**The builder's saved artifact and export format is the block model — JSON — never raw HTML.**

Configure GrapesJS with **custom components locked to a fixed palette** that maps 1:1 onto the
host's block types (start with: Hero, Text/adviser-bio, Services, CallToAction, ContactForm
placeholder, Footer-as-Text, Disclaimer-as-Text). The editor manipulates those components'
settings; dragging, reordering and editing all happen on structured components. Freeform HTML
elements are disabled.

Why: the host renders blocks through themed shells with sanitization, package gating, CRM-wired
forms, SEO and analytics. Raw HTML bypasses all of it — no leads, no gating, no safety. A
block-JSON export keeps every integration option open and makes the eventual importer a small,
testable piece.

---

## 5. The contract (start here, evolve carefully)

Create `CONTRACT.md` in the builder repo containing the export schema. Version it from day one.
Draft v0.1:

```json
{
  "contractVersion": "0.1",
  "template": "modern | classic | editorial",
  "pages": [
    {
      "slug": "home",
      "title": "Home",
      "navigationLabel": "Home",
      "isHomePage": true,
      "sortOrder": 0,
      "blocks": [
        {
          "type": "Hero",
          "heading": "…", "subheading": "…", "body": "…",
          "imageRef": "assets/hero-1.jpg",
          "buttonText": "…", "buttonUrl": "/contact",
          "layoutVariant": "standard",
          "sortOrder": 0,
          "isVisible": true,
          "settings": { }
        }
      ]
    }
  ],
  "assets": [ { "ref": "assets/hero-1.jpg", "file": "hero-1.jpg", "type": "image/jpeg" } ]
}
```

Rules: `type` must be one of the host's block-type names, verbatim. `settings` is per-type and
starts empty except where the palette needs it. Images travel as files in an `assets/` folder
next to the JSON, referenced by `ref` — the importer uploads them and rewrites URLs. Rich-text
`body` is HTML **that must survive the host sanitizer unchanged** — test with the same
HtmlSanitizer package and the allow-list philosophy in section 2.

The host-side importer (built later, on the host side, under its test gates) will: validate
types, run the sanitizer, enforce package gating, create pages/blocks, upload assets.

---

## 6. Where to put the project on this machine

```
C:\Users\admin\Projects\IPRO.TemplateBuilder\
```

- **A short path, outside any existing tree.** Not inside `IPRO_Modern`, and NOT under
  `C:\Users\admin\Documents\Codex\...` — that deep working tree has been lost to a machine reset
  before; only OneDrive-synced and GitHub-pushed content survived.
- `git init` on day one; create a **private GitHub repo** (suggest `bmipro/IPRO.TemplateBuilder`)
  and push before writing the second file. GitHub is the real backup.
- Solution layout:

```
IPRO.TemplateBuilder/
  CONTRACT.md
  IPRO.TemplateBuilder.sln
  src/Builder.Web/          (ASP.NET Core app: editor + preview + export)
  src/Builder.Core/         (page/block model, export writer — no web references)
  tests/Builder.Tests/      (xUnit; round-trip: model -> JSON -> model, sanitizer conformance)
  App_Data/                 (SQLite db + assets; gitignored)
```

---

## 7. Definition of done for the prototype

1. Create/edit/reorder pages built from the locked palette in GrapesJS, WYSIWYG.
2. Drafts and versions stored in SQLite; restore any prior version.
3. Preview renders the page pixel-equivalent to the editor.
4. **Export** produces the contract JSON + assets folder; a round-trip test proves
   export → parse → identical model.
5. Every rich-text body in the export passes the Ganss sanitizer configured with the host's
   allow-list philosophy **unchanged** (a conformance test, not a manual check).
6. Zero references to any `IPRO.*` assembly; zero cloud credentials anywhere in the repo.

---

## 8. What the builder developer does NOT need (deliberately)

- **No access to the IPRO_Modern repository.** The contract is the interface.
- **No production credentials, connection strings, Azure or PayPal anything.** If a task seems
  to need them, the task is out of scope for the prototype.
- **No customer data, ever** — not even "just to test with something real". Sample content only.

This is not distrust; it is scope hygiene. It also means onboarding needs no security review,
and nothing the prototype does can touch production.

## 9. When integration day comes (host-side notes, for the record)

- Preferred shape: an **area inside the portal** (inherits auth; note the auth cookie is
  host-scoped, so a separate subdomain would need real SSO work).
- The importer lands in IPRO_Modern under its own rules: test-first, red-before-green, full
  suite gate before merge, no exceptions.
- The template system currently sits under a standing pause pending an external consultant;
  the owner lifts or scopes that pause explicitly before integration work begins.
