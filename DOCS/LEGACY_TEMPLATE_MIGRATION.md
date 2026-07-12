# Legacy Website Template Migration

## Archive inventory

Source archive: `skins.zip`

- 45 numbered template folders were found.
- Skin `6` is absent; the archive contains `1-5` and `7-46`.
- 1,612 files expand to approximately 69.5 MB.
- Much of the archive is duplicated Bootstrap, jQuery, fonts, administrative icons, and backup files rather than unique public-site design.

## Technical generations

### Legacy table and image layouts (13)

Skins: `2, 7, 11, 16, 17, 18, 19, 20, 21, 26, 27, 28, 29`

These use old table-oriented master pages, jQuery 1.3, image slices, and fixed-width backgrounds. Their visual ideas can be preserved, but their markup and scripts should not be migrated.

### Bootstrap 2015 layouts (25)

Skins: `1, 3, 4, 5, 8, 9, 10, 12, 13, 14, 15, 22, 23, 24, 25, 30, 31, 32, 33, 34, 35, 36, 37, 38, 46`

These share Bootstrap 3-era structure and jQuery 1.11. Most differences are styling, header imagery, background treatments, and navigation placement rather than genuinely different rendering systems.

### Bootstrap 2016 layouts (7)

Skins: `39, 40, 41, 42, 43, 44, 45`

These are the newest legacy group but still rely on Bootstrap 3 and jQuery 1.12. They should be treated as visual references only.

## Migration recommendation

Do not create 45 independent Razor templates. Build a smaller collection of modern responsive layout families and represent the legacy variety through editable presets.

Recommended layout families:

1. Modern professional hero
2. Classic sidebar
3. Editorial and content-led
4. Minimal professional
5. Bold image-led
6. Local service and lead-generation

Each template record should select a renderer and carry presentation settings such as:

- typography pairing
- header and navigation style
- content width and spacing density
- hero composition and image treatment
- section background treatments
- button and card style
- starter color palette

The managed page and block data remains independent from these presentation settings. Agents can therefore switch templates without losing pages, navigation, or content.

## What to preserve

- recognizable composition ideas
- useful business-oriented hero imagery
- sidebar versus horizontal navigation choices
- restrained palette options
- strong lead-generation layouts

## What not to preserve

- ASP.NET Web Forms master-page markup
- table-based layout
- image-sliced borders and backgrounds
- bundled jQuery and Bootstrap copies
- fixed pixel widths
- duplicated portal/admin assets
- obsolete social widgets and third-party scripts

## Suggested conversion sequence

1. Create one polished representative for each of the six layout families.
2. Add configurable template presets in Super Admin.
3. Render the same managed pages and blocks through every family.
4. Verify desktop and mobile views on temporary and custom domains.
5. Map each legacy skin to its nearest modern family and preset.
6. Import only useful, licensed imagery after visual review.
7. Retire redundant legacy skins rather than exposing 45 near-duplicates.

## Deferred functional work

After the template migration:

- submit public contact forms directly into CRM leads
- store newsletter signups as subscriber records
- provision starter pages during registration
- add a dedicated drag-and-drop navigation manager
