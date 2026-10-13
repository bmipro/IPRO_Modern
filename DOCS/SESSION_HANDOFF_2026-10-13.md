# Session handoff -- 2026-10-13

## Branches: read this first

- `main` is what production runs, plus notes. `feat/template-builder-beta` (local only, not pushed) holds the
  Template Builder work: 571, 572 and everything after. Nothing of it goes to production before the owner says so.
  The full rule and the story so far are in `SESSION_HANDOFF_2026-10-12.md` on this branch.

## 573: a hole in the live site, found by the builder's security audit

- **What it was:** a signed-in customer could send a crafted request to My Website and have another customer's
  picture deleted from storage. The save took the logo's address from the request; a file is found by the path of
  its address alone; and the "still used?" check compares addresses as text, so the same file under another
  spelling (`?z` on the end) looked unused. Two other forms (a new contact, a new newsletter) saved the posted
  record whole, so a request could add related rows it named.
- **Proven, not assumed:** the new test, run against the code as it stood, fails with both files in the delete
  list.
- **Fixed in `175d350`, gated 1653/1653, and LIVE** (the owner: "Deploy the 573 first"): build `cd54996` on both
  hosts at 11:53, both Healthy, the public sites answering, the three forms it touches asking for a sign-in and
  answering no 5xx to a bare request, a picture on a live site still loading. The TODO row (573) has the three
  parts of the fix.
- **Recoverable:** production's storage keeps deleted files and old versions for 30 days.
- **Not known:** whether anyone used it. Listing deleted files needs access this sign-in does not have (asked for
  and refused by the platform, so not worked around): the owner's say. After the fix is live, a scan of the public
  sites for broken pictures is the other check.
- `main` is merged into the builder branch.

## The owner's builder site moved out of the TEMP folder

- His word, this morning: "the data on port 5087 is saved".
- **Where it lives now: `C:\Users\admin\Projects\ipro-builder-site`.**
  - `build\` is the current build, published from the builder's `ipro/beta` (`6ffed14`).
  - `site\` is his data (`App_Data`: the database, his images, the key ring) and the two settings files.
  - `before-move\` is a copy of the database taken with SQLite's own backup while the old copy was still running.
  - `Start-Builder.cmd` starts it at http://127.0.0.1:5087 in a minimized console window. Closing that window stops
    it; a double-click starts it again. Never two processes against the same `site` folder.
- **How it was done:** the database was copied safely first; the current build was rehearsed on that copy at
  another port (it opened his four pages and said "Ready to export"); only then was the old process stopped (after
  checking it was still the one in the developer's note), the whole `App_Data` copied (16 files, same bytes, same
  database hash) and the current build started at the same address. Looked at in a tab of ours: same four pages,
  same revision, no errors. Nothing was saved in it.
- **His last save on record: Saturday 2026-10-10, 4:29 pm** (template "gbslogoooo": Home, Contact, About,
  Services; 20 sections). He was told: if he changed anything after that in a browser tab, it is not saved, and he
  should not reload that tab before saying so.
- The old folders under his TEMP folder (`ipro-gallery-20261010`, `ipro-reference-20260930`) were left exactly as
  they were. They are a fallback, not a home: nothing durable belongs in TEMP.
- A zip of the site's data is in both backup folders (`iPro_TemplateBuilder_owner-site_20261013-090631.zip`; no
  key ring, no generated previews).
- It is still his work. Do not save in it, and do not point tools or tests at it. To look, use a tab of our own and
  close it afterwards: a second open editor tab can stop his saves.
- To put a newer build under it: stop it, publish over `build\`, start it. Rehearse on a copy at another port first.
- Port 5090 (the developer's synthetic review copy) was not touched.

## The images he is checking

- **`C:\Users\admin\Documents\iPro_Builder_Images`**: copies of the 69 stock images from the builder's library
  (21 PhotoDune, 48 Vivozoom, each checked against the library's recorded hash), the five photos he pasted this
  morning under the names he suggested (`CNTower1` to `CNTower4`, `Blue_sky`; the files as pasted, about 1,450
  pixels wide), and `_list.csv` (each file, where it came from, its original file name, its size).
- He is confirming the licences. Until he says which are cleared, none of them ships and none enters a repository.
  Many of the Vivozoom files are small web-size copies (864 pixels wide): thin for a full-width picture.

## His question on the path, and the answer given

- He asked whether the suggestion is to deploy this version now with a switch per agent, to test in production
  without touching the others.
- Answered: that is the destination, in two stages.
  1. *Staff-only (what 571 and 572 are):* the SuperAdmin screens that hand one agent's site to the builder and take
     it back. No customer sees anything; it works on one agent at a time by its nature; the previous site can be
     put back. This could go to production once it has had its audits and he says so, and would let him test the
     whole trip on a real site with the builder still on this machine.
  2. *Customers use the builder themselves:* needs the builder running somewhere on the internet (a hosting
     decision and cost that are his), sign-in so each agent sees only their own site, and the switch itself. None
     of the three exists yet; they are the next build work.
- His decision is awaited. Nothing was pushed or offered for a push.

## Still his

- Which of the 74 images are licensed for customers' sites.
- Stage 1 to production first, or everything together later.
- From 10-12, unchanged: SES step 8.2; his GST/HST number for 570.
