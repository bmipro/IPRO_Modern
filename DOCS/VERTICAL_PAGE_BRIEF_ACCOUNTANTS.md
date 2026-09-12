# Design brief: the Accountants landing page

`app.iproadvisers.com/accountants` -- one page inside the IPRO platform site, aimed at accountants
and bookkeepers in Canada. `iproaccountants.com` will redirect to it. Written 2026-09-11 for the
designer; Bahman approves the design, the page is then built from it as drawn.

## What this page is for

One job: an accountant lands here, sees that this was built for their practice, and clicks
**Register** (or **See an accountant's site** first, then Register). Everything on the page serves
that click.

Two more pages follow the same design with their own words and images: `/mortgage` and
`/insurance`. Design a template with slots, not a one-off. Note where the Mortgage and Insurance
versions would differ (headline, the three or four "what you get" blocks, the images).

## Who it is for

Sole practitioners and small accounting or bookkeeping firms, one to ten people, in Canada. Not
designers, not technical. They want more clients, a professional website without running a web
project, and one place to keep client follow-ups and documents. Tone: plain, confident, specific.
Leave behind lines like "Discover the secret of attracting customers" from the old site; say what
the product does.

## The page, section by section, in this order

1. **Hero.** A headline for accountants. The platform home's is "Be the ONE they find first."; this
   one says it for accountants (for example: the practice a client finds, trusts, and stays with).
   One sub-line of at most 25 words. Two buttons: **Register** (primary) and **See an accountant's
   site** (secondary; opens the preview of the starter site). Visual: an accountant's site on a
   laptop and a phone (we supply the screenshots).
2. **Built for accountants.** Three or four short blocks, each an icon, a title of at most six
   words, and one sentence:
   - A website with accountant starter pages and a Resources section already written (personal
     financial planning, personal tax preparation, what to bring to your first meeting).
   - A client portal where clients upload their slips and documents and message you.
   - Follow-ups and reminders, so tax season is a list, not a memory test.
   - Newsletters and "Did you know" teasers built from the same articles, sent from your own name.
3. **See it.** The starter site the accountant gets on day one, framed as a screenshot or an
   embedded preview. Caption: "Every new account starts with this site, already written for
   accountants. Change anything."
4. **How it works.** Three steps in a row: Register (five minutes) -> your site is live at
   yourname.247advisers.com -> point your own domain when you are ready.
5. **Pricing.** The same package table as the platform home. The rows come from the database
   (package names, prices, features); the designer styles the frame and the row style, never the
   content. No prices or feature lists in the mockup, use placeholders.
6. **Trust.** One provider, one login, one number to call. Canadian, data stored in Canada (Azure
   Canada East), nightly backups, support by ticket from inside the product. Short.
7. **Final call to action.** Register, one line above it. Whether to say "no card needed for the
   trial": confirm with Bahman before writing it.
8. **Footer.** The platform's shared footer; not part of this design.

## Content sources

- The current `iproaccountants.com`: reuse the substance ("Everything you need under one umbrella",
  "professional websites for professional accountants", leads, revenue, professional outlook),
  rewrite the words.
- The platform home (`app.iproadvisers.com`): its sections and their headings ("Start with a
  version that already understands your business", "From a stranger on your website to a client
  you keep", "Nothing starts from a blank page", "Pay for the level of relationship management you
  need", "Look at it first. Decide after."). The accountants page is that story told for one
  audience.
- The accountant starter site: pages Home, About, Services, Testimonials, Contact, Free Newsletter,
  Request a Meeting, Resources (Personal Accounting: Personal Financial Planning, Personal Tax
  Preparation; plus How to Get the Most Out of Your First Meeting, Questions Worth Asking Before
  You Choose an Advisor). Real content the preview shows; use real titles in the mockup.

## Constraints, so the design can be built exactly as drawn

- The page shares the platform header and footer. The body is yours.
- Bootstrap 5 grid. No new JavaScript libraries; effects in CSS only.
- Palette from the platform home: navy `#173b55`, near-black text `#1b2733`, light blue-greys
  `#c6d5df`, `#d7e5ee`, `#d2dfe8`, white panels. Accent for buttons: the platform's orange (take
  the exact value from the Register button on the home page). Fonts: the system stack (Segoe UI on
  Windows); one Google Fonts display face is acceptable for headings, with a fallback.
- Three widths: desktop 1200 px and wider, tablet 768 px, phone 375 px. The phone layout matters as
  much as the desktop one; most first visits are on a phone.
- Images: SVG or PNG at 2x. Screenshots we supply. No stock photos of handshakes or towers.
- Copy is real, by section, in a separate document. No lorem ipsum. Headings of at most eight
  words, paragraphs of at most 40 words.
- Accessibility: AA contrast, buttons at least 44 px tall, one H1, visible focus states.
- Search: page title "Websites and Client Management for Accountants in Canada | IPRO Advisers" (or
  better), meta description of at most 155 characters, the word "accountants" in the H1.
- Links we wire: Register -> `/Account/Register?businessType=Accountants` (the form pre-selects the
  business type); See an accountant's site -> `/Preview/Show?businessType=Accountants`; Pricing ->
  the pricing section on the same page.

## Deliverables

- Desktop and phone mockups (Figma or HTML). Tablet optional.
- All copy in a document, by section.
- Exported assets (SVG, or PNG at 2x).
- A short note of what changes for the Mortgage and Insurance / Financial versions.

## Handover package: what to deliver, and in what shape

The designer never touches the product's code, servers or accounts. The page is built into the
platform from the package below; anything not in the package will be asked for, so the flow is:
design -> Bahman approves -> the designer packages -> the page is built and deployed.

One zip (or a shared folder), named `accountants-page-v1`, containing:

1. **`page.html`** -- the page as static HTML and CSS, responsive (one file, not one per width),
   using Bootstrap 5.3 classes where they fit and a single `page.css` for the rest. It must open
   and look right from a folder on a laptop with no server and no internet. No JavaScript
   (animations in CSS). Placeholders where the platform fills content in: `{{HEADER}}`,
   `{{FOOTER}}`, `{{PRICING_TABLE}}`, `{{PREVIEW}}`.
2. **`page.css`** -- every colour as a variable at the top (`--navy: #173b55;` and so on), a
   spacing scale, the fonts, and no `!important`.
3. **`copy.md`** -- all text by section, final, proofread. Headings marked as H1/H2/H3. Link text
   marked with the destination from the list above.
4. **`assets/`** -- SVG for icons and logos, PNG or WebP at 2x for pictures, each under 300 KB,
   descriptive file names (`hero-laptop-phone.png`, not `image3.png`), and an `alt-text.md` giving
   the alt text for each image.
5. **`README.md`** -- fonts used (Google Fonts name and weights, or "system"), the palette, the
   breakpoints designed for, and a list of anything that differs for the Mortgage and Insurance
   versions.
6. **Screenshots** of the approved design at 1440 px and 375 px, so the built page can be checked
   against them.

If the design was made in Figma, include the Figma link with Dev Mode on, but the six items above
are still the package; the Figma file is a reference, not the deliverable.

Not accepted: a WordPress, Wix, Webflow or other builder export; server-side code of any kind;
external scripts or trackers; fonts that are not Google Fonts or the system stack; images over
300 KB; text in images.

**Acceptance check before handover** (the designer runs this, then Bahman):
- `page.html` opens from a folder with no missing images and no console errors.
- Every section from the list above is present, in order.
- Phone width 375 px: nothing overflows sideways, buttons are at least 44 px tall, text is
  readable without zooming.
- Every image has an entry in `alt-text.md`.
- The copy in `copy.md` matches the mockup word for word.

## What happens next

Bahman approves the design; the designer delivers the package; the page is built into the platform
from it in about a day (the shared header and footer, the live pricing table, the preview and the
links wired, SEO tags, tests), deployed, and `iproaccountants.com` lands on it on the domain-switch
day. Questions about the package go through Bahman.

**Delivered so far (2026-09-11):** `/accountants` from accountants-page-v2-font-revision, and
`/mortgage` from mortgage-page-v1 (whose corrected four-column footer is now the footer of every
vertical page). Both live at app.iproadvisers.com on the same template. The footer, the pricing
cards and the stylesheet are shared in the platform, so a package for the next vertical
(`/insurance`) needs only its own sections, copy and screenshots in the shape above; its footer
links and package table come from the platform.

**Brand domains (484, 2026-09-12):** a vertical's own domain shows its landing page under its own
name (ipromortgages.com shows the mortgage page; iproaccountants.com will show the accountants page
from the switch day). Register, Sign in, the preview and the legal pages still open on
app.iproadvisers.com, and the page's canonical tag names the platform address, so the designer's
package needs nothing extra for a domain: relative links to the page's own files are fine, links to
sections of the platform home must be absolute.

**Brand domains (484, 2026-09-12):** a vertical's own domain shows its landing page under its own
name (ipromortgages.com shows the mortgage page; iproaccountants.com will show the accountants page
from the switch day). Register, Sign in, the preview and the legal pages still open on
app.iproadvisers.com, and the page's canonical tag names the platform address, so the designer's
package needs nothing extra for a domain: relative links to the page's own files are fine, links to
sections of the platform home must be absolute.

**Brand domains (484, 2026-09-12):** a vertical's own domain shows its landing page under its own
name (ipromortgages.com shows the mortgage page; iproaccountants.com will show the accountants page
from the switch day). Register, Sign in, the preview and the legal pages still open on
app.iproadvisers.com, and the page's canonical tag names the platform address, so the designer's
package needs nothing extra for a domain: relative links to the page's own files are fine, links to
sections of the platform home must be absolute.

**Brand domains (484, 2026-09-12):** a vertical's own domain shows its landing page under its own
name (ipromortgages.com shows the mortgage page; iproaccountants.com will show the accountants page
from the switch day). Register, Sign in, the preview and the legal pages still open on
app.iproadvisers.com, and the page's canonical tag names the platform address, so the designer's
package needs nothing extra for a domain: relative links to the page's own files are fine, links to
sections of the platform home must be absolute.
