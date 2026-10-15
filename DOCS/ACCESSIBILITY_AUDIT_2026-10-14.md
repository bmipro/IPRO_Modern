# iPro accessibility audit (WCAG 2.1 AA), 14 October 2026

Read-only audit for the government web lead's question "are iPro's sites accessibility capable?".
Target: WCAG 2.1 Level AA, with what WCAG 2.2 AA adds noted separately. No code was changed.
This file is a copy of the report page; it stays uncommitted until the owner decides.

## The answer in brief

- **The public websites are close to WCAG 2.1 AA, but do not meet it yet.** The foundations are
  right: every page has a title and a language, the page regions are marked, all public forms have
  real labels, pages reflow on a 320-pixel phone and at 200% zoom with no sideways scrolling,
  keyboard focus is always visible, and nothing moves on its own.
- **Seven template problems stand in the way, and each is fixed once for every customer.** The
  worst: on a desktop screen the drop-down menus open only with a mouse, so keyboard users cannot
  reach sub-pages (on www.4ipro.com, the ten pages under Resources). The others: four of the colour
  choices iPro offers make text unreadable, some fixed grey text is too faint (including the footer
  disclaimer), pictures cannot be described, a rejected form wipes what the visitor typed, the
  "Did you know?" Read More button loses keyboard users, and the page language is always English.
- **The portals are further away.** In the adviser portal (and the client portal's "My information"
  form) most fields show a label on screen that a screen reader never hears: about 210 fields on the
  31 screens I rendered, 122 of them in the page editor. The work is mechanical but wide.
- **Estimate:** about 2 weeks of development plus a screen-reader check to make the public websites
  defensible as WCAG 2.1 AA; about 4 to 5 weeks for everything, portals and marketing pages included.
- **Do not claim conformance to the government lead yet.** A suggested reply is at the end.

## Scorecard

| Area | Where it stands | What blocks AA | Fix size |
|---|---|---|---|
| Public websites (3 designs, 23 section types) | Close | Menus by keyboard, colour choices, picture descriptions, form errors | 8 to 10 days |
| Public forms and link pages (contact, booking, newsletter, testimonial, unsubscribe, email preferences, invoice link) | Mostly good | Rejected form wipes entries; two link pages need labels | in the line above |
| Client portal | Needs work | Unlabelled fields on My information, faint menu | about 2 days |
| Adviser portal | Needs work | About 200 unlabelled fields, no main region or skip link | 6 to 8 days |
| Marketing site and sign-in pages | Needs work | Orange buttons too pale, unlabelled sign-up forms, no page language | about 1 day |

Fix types used below: **Template** = fixed once in iPro's shared designs, every customer site gets it.
**Customer content** = needs guidance in the editor. **Portal** = adviser or client portal.
**Marketing** = iPro's own marketing and sign-up pages.

## Findings, worst first

Ranked by who they hurt and how much. "Blocker" means someone cannot complete the task.

### 1. Drop-down menus cannot be opened from the keyboard on desktop
- **Severity:** Blocker for keyboard users (people who cannot use a mouse; many screen-reader users).
- **Fix type:** Template (Professional Adviser, Editorial Visual, and Classic Sidebar whenever its top menu is used instead of the side menu).
- **WCAG:** 2.1.1 Keyboard (A).
- **What happens:** above 800 px wide a sub-menu shows only on mouse hover. The open/close buttons
  that work on phones are hidden on desktop. Seen live on www.4ipro.com (the 10 pages under
  Resources never receive keyboard focus) and on all three designs locally. On a phone the menu
  works with the keyboard and announces open/closed.
- **Fix:** show the sub-menu when its top item has keyboard focus and show the existing open/close
  button on desktop. Half a day to a day with checks in all three designs.

### 2. Portal form fields have no programmatic labels
- **Severity:** Blocker for screen-reader and voice-control users of the portals.
- **Fix type:** Portal (plus the sign-up and "See your site" forms on the marketing site).
- **WCAG:** 1.3.1 Info and relationships (A), 4.1.2 Name, role, value (A), 3.3.2 Labels or instructions (A).
- **What happens:** the label text sits next to the field but is not connected to it, so a screen
  reader says "edit text" with no name and voice users cannot say "click First name". Counted on
  rendered screens: page editor 94 fields and 28 drop-downs; My profile 20; Add client 15; adviser
  registration 15; website settings 11 (plus template cards with buttons nested inside them); new
  invoice 6; client portal My information 10; "See your site in 30 seconds" 3; smaller counts on
  clients, invoices, leads, documents, appointments and forgot password. Icon-only buttons with no
  name: client portal Send message, the adviser's "+" on a client's follow-ups, and Delete on Forms.
- **Fix:** connect each label to its field (mostly `asp-for` on the label) and name the icon buttons.
  Mechanical; about 4 to 5 days across roughly 40 screens.

### 3. Colour choices iPro offers that make text unreadable
- **Severity:** Serious for people with low vision or colour-vision differences, older visitors,
  anyone on a phone in sunlight.
- **Fix type:** Template.
- **WCAG:** 1.4.3 Contrast minimum (AA): 4.5:1 for normal text.
- **What happens:** the accent colour sits behind white button text, colours the active menu item,
  small section labels and the form Send button. 4 of the 14 looks and palettes iPro offers fail:

| Choice | White text on it | Verdict |
|---|---|---|
| Look: Modern Minimal #0EA5E9 (used on www.4ipro.com, 9 failures on its home page) | 2.77:1 | Fails all text |
| Palette: Amber Gold #B8892B | 3.16:1 | Fails all text |
| Palette: Sunset Orange #D9541F | 4.01:1 | Fails normal text |
| Palette: Teal #0F8C8C | 4.08:1 | Fails normal text |
| Ocean Blue #1457D9 (Professional Adviser default) | 6.19:1 | Passes |
| Classic Sidebar default #315B46 | 7.73:1 | Passes |
| Editorial Visual default #B42318 | 6.57:1 | Passes |
| Heritage Green #1F6B4D, Classic Navy #1B3A5C, Forest Green #1F7A4D, Slate Gray #4B5563, Burgundy #7A1F3D, Royal Purple #5B2F9E | 5.3 to 11.6:1 | Pass |

  The free colour picker accepts any colour, so customers can create new failures.
- **Fix:** keep customers free to pick any colour. Have the designs choose dark or white text on the
  accent automatically and darken the accent a little where it is used as small text; correct the
  four presets. About 1 day.

### 4. Fixed grey text that is too faint in every design
- **Severity:** Serious (one of these is the regulatory disclaimer financial firms must show).
- **Fix type:** Template. **WCAG:** 1.4.3 (AA).
- **What happens:** footer disclaimer in Professional Adviser 3.16:1; "Powered by iPro" line 2.5:1
  to 3.7:1; calculator notes under results 3.08:1; "not available" price-list items 2.56:1; default
  blue links on tinted bands 4.08:1 and 4.31:1 (Professional Adviser contact section, Editorial);
  the 404 page's "Or try one of these" about 3.7:1.
- **Fix:** a handful of colour values. Half a day.

### 5. No way to describe a picture
- **Severity:** Serious for blind and low-vision visitors when a picture carries information (a team
  photo, a chart, a storefront sign).
- **Fix type:** Template and editor, then customer content.
- **WCAG:** 1.1.1 Non-text content (A).
- **What happens:** hero, text-with-picture, article and blog pictures are always published as
  decorative (empty description) and the editor has no field to describe them. The rich-text editor
  cannot insert pictures at all. Gallery photos use their caption as the description, which is good,
  so a photo without a caption is only "Open photo 2 of 3". Logos are described ("company logo") and
  price-list photos use the item name.
- **Fix:** an optional "Describe this picture" field on picture blocks (empty means decorative), used
  by all three designs, with a one-line hint in the editor. About 2 days.

### 6. A rejected form throws away what the visitor typed
- **Severity:** Serious for everyone, hardest on people who type slowly or use assistive technology.
- **Fix type:** Template and server.
- **WCAG:** 3.3.1 Error identification (A), 3.3.3 Error suggestion (AA), 3.3.7 Redundant entry (WCAG 2.2, A).
- **What happens:** tested locally on the contact form: a wrong answer to the security sum reloads
  the page with "Please complete the security check and try again." and every field empty. The
  message is announced, but focus stays at the top of the page; where the form sits lower on a long
  page, the message is out of sight. On custom forms a required checkbox question is not marked
  required, so the only error is "Please fill in all required fields" without saying which one.
- **Fix:** show the form again with what was typed (never the security answer), name the field, move
  focus to the message. About 1 to 1.5 days.

### 7. "Did you know?" Read More loses the keyboard user
- **Severity:** Serious for keyboard and screen-reader users. On www.4ipro.com's home page.
- **Fix type:** Template. **WCAG:** 2.4.3 Focus order (A).
- **What happens:** Read More hides the article cards and shows a sign-up form, but focus stays on the
  now-hidden button and drops to the top of the page. The button says "Read More" but opens a
  sign-up form, not the article. The cards use level-4 headings under a level-2 heading.
- **Fix:** move focus to the form, rename the button (for example "Email me this article"), use
  level-3 headings. Under half a day.

### 8. iPro's marketing pages: orange buttons too pale, sign-up forms unlabelled
- **Severity:** Serious. **Fix type:** Marketing.
- **WCAG:** 1.4.3 (AA), 1.3.1 and 4.1.2 (A), 3.1.1 Language of page (A).
- **What happens:** white on orange #D06A35 is 3.62:1 on www.iProAdvisers.com (28 failures,
  including both "See a live site" buttons); white on #F36B21 is 3.02:1 on www.iProAccountants.com
  and www.iProMortgages.com (23 each). The registration page has no page language and 15 unlabelled
  fields; the adviser login page has no page language and its tagline is 3.57:1.
- **Fix:** a slightly deeper orange (for example #C2410C gives 5.2:1 with white text), labels, page
  language. About 1 day.

### 9. Page language is always English
- **Severity:** Serious for French-language sites (a screen reader reads French with English
  pronunciation); moderate otherwise.
- **Fix type:** Template. **WCAG:** 3.1.1 Language of page (A), 3.1.2 Language of parts (AA).
- **What happens:** every design writes `lang="en"`. There is no site language setting and no way to
  mark a French section on an English site. Canadian government buyers often need both official
  languages.
- **Fix:** a site language setting (English or French) and a "this section is in French" switch on
  blocks. 1 to 1.5 days.

### 10. Client portal menu too faint
- **Severity:** Serious. **Fix type:** Portal. **WCAG:** 1.4.3 (AA).
- **What happens:** menu links pale green on green, 2.72:1, on every client portal screen. Also no
  main region and no level-1 heading; the phone-size menu button has no name (from the markup).
- **Fix:** half a day.

### 11. Checkbox questions on custom forms are not grouped
- **Severity:** Moderate. **Fix type:** Template. **WCAG:** 1.3.1 (A).
- **What happens:** each checkbox is announced without its question (labels are nested and there is
  no group), and help text under a field is not linked to it.
- **Fix:** group with a fieldset and legend, link the help text. Part of the estimate in #6.

### 12. The photo viewer lets focus escape
- **Severity:** Moderate. **Fix type:** Template. **WCAG:** 2.4.3 (A).
- **What happens:** opening a photo moves focus to Close and Escape returns to the photo (good), but
  two presses of Tab later focus is behind the dark overlay.
- **Fix:** keep focus inside the viewer while it is open. Small.

### 13. Calculator results arrive silently
- **Severity:** Moderate for screen-reader users. **Fix type:** Template. **WCAG:** 4.1.3 Status messages (AA).
- **Fix:** announce the results area. Tiny.

### 14. No "skip to content" link; most pages have no main heading
- **Severity:** Moderate. **Fix type:** Template and portal.
- **WCAG:** 2.4.1 Bypass blocks (A); 1.3.1 and 2.4.6 for headings.
- **What happens:** the websites meet 2.4.1 through their marked regions, but a tester will look for
  a skip link, and the adviser portal has neither a skip link nor a main region. Contact, Request a
  meeting, calculators, articles and Resources start at level-2 headings; only a hero gives a level-1.
- **Fix:** a skip link in all designs and both portals; the first heading on a page becomes level 1.
  Half a day.

### 15. A sticky header can hide the focused item (WCAG 2.2)
- **Severity:** Moderate. **Fix type:** Template. **WCAG:** 2.4.11 Focus not obscured, minimum (2.2 AA).
- **What happens:** only when a customer turns on the sticky header. Tabbing backwards up a page,
  6 of 38 items went partly or fully under the header ("Book now" fully hidden).
- **Fix:** one line of CSS (scroll padding). Tiny.

### 16. Sign-in pages have no page language
- **Severity:** Moderate. **Fix type:** Portal. **WCAG:** 3.1.1 (A).
- **Where:** adviser login, registration, forgot password, client portal login, activation and the
  related account pages. Tiny.

### 17. Faint form field edges and focus ring
- **Severity:** Moderate for low vision. **Fix type:** Template and portal.
- **WCAG:** 1.4.11 Non-text contrast (AA).
- **What happens:** field borders #CBD5E1 are 1.48:1 against white (3:1 needed); on the website
  forms, focus shows as a 20% tint. Small.

### 18. Smaller portal items
- Four adviser screens scroll sideways on a 320-px phone (website settings, leads, requests, page
  editor). 1.4.10 Reflow (AA).
- "Saved" and error banners are not announced; the current menu item is not announced. 4.1.3, 1.3.1.
- Rich-text editor: no heading buttons, so customers cannot structure long articles; the editing
  area has no name. 1.3.1, 4.1.2.
- Sign-in fields lack autocomplete hints that help password managers. 1.3.5 (AA).
- Grey outline buttons at 4.48:1, just under.

### 19. Public link pages judged from markup only
- Emailed poll page: the question is not grouped with its answers (the website poll does this right). 1.3.1.
- Testimonial request page: the comment box has only a placeholder, no label. 3.3.2.
- Invoice link page (rendered): well structured; the orange "Prepared for" label is 3.17:1. 1.4.3.

## Customer content: guidance needed in the editor

Not iPro's to fix, but iPro gives customers the tools and a short hint where they edit.
- Describe pictures that carry information (after #5); caption gallery photos.
- Captions on videos they embed (automatic captions are often wrong). 1.2.2 (A), 1.2.5 (AA).
- Accessible PDFs for downloads (lead magnets, documents shared with clients).
- Button and link wording that says where it goes.
- Headings in long articles (after the editor gets heading buttons).
- Mark French text (after #9).
- Colour choice stays theirs; #3 keeps any choice readable.

## What already works

- Page titles on every page ("Page | Site name"); `lang="en"` on every website page.
- Header, navigation, main and footer regions marked on every website page.
- Visible keyboard focus everywhere (the browser's focus ring is kept, never removed).
- Reflow at 320 px and at 200% zoom with no sideways scrolling on every public page tested; the
  WCAG text-spacing test clips nothing.
- Nothing moves on its own: the slideshow is manual and honours "reduce motion".
- Public forms: real labels, required fields marked and enforced, name/email/phone autocomplete, a
  plain-text security question (no picture puzzle), success announced, errors announced.
- Phone menu: a named Menu button that announces open/closed; sub-menu toggles reachable.
- Photo gallery: every photo is a button with a name, the viewer closes with Escape and returns focus.
- Social icons, review stars, reviews and polls on the website (question and answers grouped) are named.
- Invoice link page, email preferences and unsubscribe pages are well structured.
- No WCAG 2.2 target-size failures found.

## What WCAG 2.2 AA adds

| New criterion | Level | Status |
|---|---|---|
| 2.4.11 Focus not obscured (minimum) | AA | Fails only when the sticky header is on (#15) |
| 2.5.7 Dragging movements | AA | Passes: no drag-only controls found; gallery swipe has buttons |
| 2.5.8 Target size (minimum) | AA | Passes on every page tested |
| 3.2.6 Consistent help | A | Passes: contact details in the same footer place; portal help icon in the same place |
| 3.3.7 Redundant entry | A | Fails: a rejected form must be filled in again (#6) |
| 3.3.8 Accessible authentication (minimum) | AA | Likely passes: username and password, pasting allowed, no puzzle; add autocomplete hints (#18) |

WCAG 2.2 also drops 4.1.1 Parsing, so stray markup no longer counts. Moving from 2.1 AA to 2.2 AA
costs about half a day on top of the 2.1 work.

## Estimate

Working days for one developer, with the usual gate and review steps.

| Phase | Work | Days |
|---|---|---|
| 1. Public websites and public forms | Menus by keyboard (1); readable text on any colour, presets, greys (1.5); picture descriptions (2); form errors keep entries, name the field, group checkboxes, field borders (1.5); Read More, photo viewer, calculator, sticky header, skip link, main heading (1); English/French language (1 to 1.5); poll and testimonial link pages (0.5) | 8 to 10 |
| 2. Client portal | Labels, menu contrast, Send button, page language on sign-in pages, main region, headings | about 2 |
| 3. Adviser portal and marketing | Labels on about 40 screens (4 to 5); icon buttons, regions, skip link, headings, announced banners, template cards (1); reflow on 4 screens (0.5 to 1); editor headings (0.5); marketing orange, sign-up and preview forms (1) | 7 to 9 |
| Check | Screen-reader passes (NVDA on Windows, VoiceOver on iPhone) of each design and the main portal tasks; tests that pin the fixes | 2 to 3 |
| **Total** | | **19 to 24** |

Recommendation: build Phase 1 now (it is what the government lead asked about), check it with a
screen reader, then write the conformance report for the public websites. Portals follow.

## What a government buyer's Accessibility Conformance Report needs

- **Format:** ITI's VPAT template (version 2.5 or later). Its "INT" edition covers WCAG, the US
  Section 508 and the European EN 301 549 in one document. Canadian federal buying increasingly
  cites CAN/ASC-EN 301 549:2024, Canada's adoption of EN 301 549, whose web chapter is WCAG 2.1 AA;
  Ontario's AODA asks public bodies for WCAG 2.0 AA.
- **Scope:** product name, build and date; exactly what is covered (the three website designs, public
  forms and link pages, client portal, adviser portal) and what is not (content customers add:
  pictures, videos, PDFs; third-party pieces: YouTube, Google Maps, PayPal).
- **Method:** the tools, the manual checks, the screen readers and browsers used, and who tested
  (internal or independent).
- **One row per criterion** (50 for WCAG 2.1 A and AA; 55 for 2.2) marked Supports, Partially
  supports, Does not support or Not applicable, with a plain remark. "Partially supports" with an
  honest remark and a fix date reads better to buyers than a blanket "Supports".
- **EN 301 549 extras:** chapter 9 (web) is WCAG; chapter 10 covers documents the product produces
  (PDF invoices, emails); chapter 11 software (mostly the same answers as chapter 9 for a web app);
  chapter 12 asks that help guides and support be accessible and say how to get accessible support.
- **An accessibility statement page and a feedback contact** on www.iProAdvisers.com.
- **Keep it current:** date it, and update it when a release changes what it says.

## Suggested reply to the government lead (your call)

> Thanks for asking. Every iPro website is built from shared templates designed to WCAG 2.1
> Level AA, the standard Canadian governments use. We completed an accessibility review of those
> templates in October 2026 and are fixing the items it found at the template level, so every site
> receives the improvements at once. Once that work is verified we can provide an Accessibility
> Conformance Report (VPAT format, covering WCAG 2.1 AA and EN 301 549).

## How this was checked

- **Websites:** www.4ipro.com live, 12 public pages, read only (it uses the Classic Sidebar design
  with the Modern Minimal look). The three designs on a local copy of the current build, 10 pages
  each, including a test page holding every section type; 21 of the 23 section types rendered (Map
  needs a Google key locally and Poll results needs 10 votes; both judged from markup), plus a
  three-level menu, all form types, calculators, articles and the 404 page.
- **Marketing and sign-in pages:** 9 live public pages (three home pages, privacy, terms, "See your
  site", a sample preview site, registration, login).
- **Portals:** 31 screens rendered by a test harness on a local copy under a made-up signed-in
  identity: no password, no real sign-in, nothing on the live site. The live portals were not seen.
  The emailed poll page and testimonial request page were judged from markup (no local link existed).
- **Tools,** installed only in the session's scratchpad, not in the repository: axe-core 4.10.2
  (WCAG 2.0 and 2.1 A and AA rules, plus the 2.2 AA rules) and puppeteer-core 23.11.1 driving
  Microsoft Edge without a window; my own scripts for keyboard walks, 320 px and 200% zoom, the
  text-spacing test, the contrast of every colour choice, and interaction checks.
- **Not done:** a real screen reader (NVDA, JAWS, VoiceOver), voice control, and a person looking at
  every page. An Accessibility Conformance Report needs these.
- **Not seen:** the colours of the production-only designs "Modern Professional Blue/Green/Red"
  (same design code as Professional Adviser, so #3 covers them); emails, newsletters, e-cards and PDFs
  (worth a short separate check, since government staff would receive them); the SuperAdmin app.
- **Side effects:** on the local database my test page, poll and settings were removed and every
  touched row put back. iPro's visitor count skips headless browsers, so my automated visits to
  www.4ipro.com are not counted; two plain page requests (home and /article) may show as two visits.
