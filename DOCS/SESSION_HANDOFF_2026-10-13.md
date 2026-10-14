# Session handoff -- 2026-10-13

## Branches: read this first

- `main` is what production runs, plus notes. **Since this evening that includes the staff-only Template Builder
  part (571, 572, 574): the owner said yes after the three audits.** `feat/template-builder-beta` (local only)
  stays for what comes next, the builder for customers (sign-in, a draft per customer, the Beta switch): nothing
  new on it goes to production before he says so, and it is not to be offered.
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

## 574: the three audits of the builder work, and what was done about them

- **Three independent reviewers** (security, functionality, code quality) read 571 and 572: the code and the
  contract, not our conclusions. Each finding acted on was checked against the code first. The security reviewer's
  look outside the two commits is where 573 came from.
- **574 is built, gated 1728/1728 and committed on the branch as `062ada6`.** The TODO row lists every fix. The three
  that mattered most: a customer's section edits after a hand-over were replaced by the return with no warning (the
  rule read a time the page editor never moves); a logo replaced while a package was out was deleted and the return
  then linked a dead file; and "Bring it in" wrote a freshly worked-out plan, not the one the reviewer had read.
- **Found sound by all three:** staff-only access with anti-forgery on every change; each step all or nothing; the
  strict reading of a package; tenant checks on every id; the look's key (random, timed, compared in fixed time).
- **Left open, on purpose, for the owner to weigh:**
  - no cap on an image's pixel size (bytes only);
  - kept copies of replaced sites are held with no end date (each keeps its pictures from being cleaned up);
  - the look answers on every host, a customer's own domain included (the key is still needed);
  - a page with no sections of its own (it shows the design's standard layout) stops a hand-over: real sites may
    have such pages, and the first real hand-over will show how often;
  - two returns of the same hand-over: a page the first added and the second removed is kept (the safe side);
  - menu labels over 40 characters, more than 30 pages, or pictures over 40 MB in all do not fit the contract.
- **Nothing in the builder's own repository changed today.** The contract is unchanged.

## The owner's builder on his network, and his photos

- He asked for http://192.168.2.164:5087/ to open from his network. `Start-Builder.cmd` now listens on every
  address of the PC and `site\appsettings.json` answers to that name; the builder was restarted and his site
  checked intact (his last save 11:45). Windows already had a rule letting dotnet through on that network; the
  firewall was NOT touched (a security setting is his to change). He was told it has no password: anyone on the
  network who opens the address can change the site, and the port must not be forwarded on the router.
- His five photos in `Documents\iPro_Builder_Images` were replaced with the full-resolution files from his ZIP
  (cleaned copies, 1600 x 1200) and, for the sky, the 2000 x 1333 JPEG he sent last. He added `Blue_sky2.jpg` there
  himself. The licence check of the 69 stock images is still his.

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
- His answer: "lets finish with the audit first", then "Continue with the builder fixes and Deploy the 573 first".
  So: 573 is live; the audit fixes are 574; whether the staff-only part (571, 572, 574) goes to production before
  the customer-facing part was his to say, and that evening he said yes (see below). He also has two questions about Google waiting to be asked.

## 575 and two Google matters

- **The Workspace email** ("mark AI agent OAuth clients as agentic by Nov 11"): no action. iPro's code calls Google
  only for the Calendar connection (the ordinary Calendar service) and embedded maps; nothing in it is an AI agent
  calling Google's new servers. The owner was told to leave the setting OFF: it becomes permanent on 2026-11-12.
- **Search Console, "Alternative page with proper canonical tag"** on iproadvisers.com and ipromortgages.com: not
  an error. The bare name says the real address is the www one and Google agrees.
- **Search Console, "Duplicate without user-selected canonical"** on iproaccountants.com: the pages were
  `mail.iproaccountants.com/` and `/about.html`. That name points at the owner's OLD cPanel host (66.102.128.65),
  which still receives the email for iproaccountants.com and iproadvisers.com and was still showing the old
  website. He pasted redirect lines we wrote at the top of that host's `.htaccess` (his own hosting account; the
  files are in his Documents folder, `iPro_Old_Host_Redirect`). Checked from here afterwards: every `mail.` page a
  301 to its www site in one hop; `/.well-known/`, mail set-up and cPanel's own addresses left alone; the
  certificate valid to 2026-12-28; the mail server answering. He was shown where "Start new validation" is.
  **Never touch the `mail.` DNS records.** Incoming mail on port 25 cannot be tested from this network.
- **575**: the nine "Not found" addresses he sent. Three have a successor and now go to it; five have none and
  keep answering "not found"; one already went to the front page. Committed on `main` as `c524ad6`, gated 1669/1669,
  and LIVE as build `8c3f86d` on his word: the three addresses checked on the live site.
- **Offered, not done** (his to ask for): the bare names answering with a redirect to www instead of the same page
  (507 was his choice that the name stays, so this is a question, not a fix); the "real address" tag on Privacy and
  Terms; real pages again for About, Features, Support and a blog, which the old site had and Google still showed.

## The staff-only builder part is live

- **His words.** Asked what "whether the staff-only builder part goes to production next" meant, he got it in plain
  terms (the SuperAdmin screens: make the package, upload, review, bring in, put back; no customer sees anything;
  the builder stays on his PC) and answered: "I am glad u did not do it on your own and asked my approval as it
  should be ... I agree with your suggestions and lets do it".
- **What went:** the tip of the builder branch, which already held all of `main`. It was gated as one tree first
  (1744/1744: the builder work and the day's two live fixes had not been tested together). Build `5858ff8`.
- **Checked live:** both hosts on the build and Healthy; the public sites answering; the builder screens on the
  admin site asking for a sign-in; the look on the portal answering "not found" without its key AND with a
  well-formed wrong one (which reads the new table, so the table is there); 573's forms and 575's addresses as
  before; both apps' own logs read for failures since the push. Before the push: the admin app has file storage
  and the portal's address set (names and length only), and a starter banner is reachable on the live portal.
- **Not seen signed in.** The screens were rendered by tests. His first use is the first real one.
- **For his first try, as he was told:** start with his own site. Opening an iPro package in the builder REPLACES
  what the builder holds, and the builder on port 5087 holds his own design. So a second builder for packages:
  `C:\Users\admin\Projects\ipro-builder-site\Start-Builder-For-Handovers.cmd`, http://127.0.0.1:5088, its own
  empty data. His design was backed up again just before (his save of 12:35; the zip is in both backup folders).
- **Known limits he was told:** a page with no sections of its own stops a hand-over; "Bring it in" on a published
  site is live at once (one click puts it back); nobody but SuperAdmin can reach any of it.
- **Next, on the branch, and again only on his word:** the builder for customers (it needs a place on the internet,
  which is his decision and cost; sign-in with a draft per customer; the per-agent Beta switch).

## 576: locked boxes, after his first real try (PARKED)

- **What he hit.** The evening the staff-only part went live he tried it on his own site. "Make the package" refused:
  fourteen pages hold sections the builder does not carry. He then uploaded a sample built from scratch in the
  builder; the review said his Did You Know teaser would go; and "Look" showed him, in his words, "my own site with
  no changes". **The sample package is still waiting on his live site (import 1): nothing was brought in.** He was
  told what "Bring it in" would do (replace his Home and Contact pages, live at once) and that "Drop this package"
  leaves the site untouched. His call.
- **The look was right, and unreadable.** The live server's own record (App Insights `requests` on
  `ipro-prod-web-insights`, read-only) shows his one click answered with the package's Home page, and his exact file
  on a local copy draws the sample's page ("Planning with a steady hand"). It looked like his site because the sample
  has the same kind of sections as his Home page and because colours, fonts, header and menu are the site's own
  design. The look now says at the top which kind of page it is. **He may expect the builder's design to travel. It
  does not** (the contract, since v0.2: words, pictures, sections and their order travel; styling is the site's). He
  was told plainly; if he wants the design to travel, that is the larger piece the 10-10 handoff called "the look",
  and it is not started.
- **576 is built and PARKED: `47e986d` on the local branch `hold/576-locked-boxes`.** Not on `main`, not gated
  on its final tree, not pushed. The TODO row has the whole of it. In one line: a section the builder does not
  carry goes out as a locked box and comes back as the section the site has then, and every section a package
  would drop is named with a tick box to keep it.
- **Why parked and not held on `main`:** he closed the day while the last full run was still to be started. The
  full suite ran on two earlier states of the work and passed as far as it got; each time a review finding
  superseded the tree before the run ended. On the final tree: its own 24 tests, and 111 around it.
- **Reviewed the way he asked:** three independent readers (security, function, code quality), given the code and
  not our conclusions, then two re-checks of the answers. No hole, no path to another customer's data. The last
  reader's verdict on the state before the final change: nothing that should stop it going to production, one
  weak spot after a put-back or a second return (answered in the final change, by his own suggestion), and tests
  that did not pin the heart of the rework (six added; each fails when its piece is switched off).
- **The builder's side is `ba49da9` on its `ipro/beta`** (local, not pushed; four commits today). It was
  written by a second session from the contract text and checked here before it was believed.
- **Both of his builders (ports 5087 and 5088) still run the build from before this.** They open and export v0.4
  packages as before. To open a package that has locked boxes, the build under
  `C:\Users\admin\Projects\ipro-builder-site\build` has to be replaced with one published from `ba49da9`: stop both,
  publish, start both (the steps are in the section on the move out of TEMP, above). Not done: it means stopping
  his builder, so it waits for his word together with the deploy.
- **The order of a release, when he says so:** push `main` (iPro then makes and takes v0.5 packages; nothing
  changes for a v0.4 one), verify both hosts, then put the new build under his builders. In that order: a new
  builder returning "0.5" to an iPro that does not know it yet would be refused on the version.

## Still his

- Which of the 74 images are licensed for customers' sites.
- The sample package waiting on his own live site (import 1): bring it in, or drop it.
- His word to finish and deploy 576 (locked boxes), and with it the new build under his two builders.
- Whether the builder's DESIGN should travel to iPro (today only words, pictures and sections do).
- From 10-12, unchanged: SES step 8.2; his GST/HST number for 570.

## Close-out 2026-10-13

| What | Where | State |
|---|---|---|
| 573 (posted records, SECURITY) | `main`, live as build `cd54996` | deployed, verified on both hosts |
| 575 (three old addresses) | `main`, live as build `8c3f86d` | deployed, verified on both hosts |
| 571, 572, 574 (staff-only Template Builder) | `main`, live as build `5858ff8` | deployed, verified on both hosts |
| check-marks and these notes | `main`, local only | held: a push restarts the site, so it waits for his word |
| 576 (locked boxes) | `hold/576-locked-boxes` `47e986d`, local only | PARKED: reviewed, not gated on its final tree |
| the builder, contract v0.5 | `C:\Users\admin\Projects\ipro-builder`, `ipro/beta` `ba49da9`, local only | 375 and 37 tests green |

- **Production is build `5858ff8` on both hosts,** as it was after the last deploy. Nothing was pushed since.
- **Backups (20261013-223718),** in `OneDrive\Codex_Code_Bkup` and `Documents\IPRO_Backups`: a zip of `main`; a bundle of
  `main`, the builder branch and the parked branch; the parked work as a `.patch`; a bundle of the builder's
  `ipro/beta`. His builder site's data was not copied again: its saved design is the one in the 17:34 zip.
- **Stopped:** the local file-storage emulator and the build servers. **Left running, his:** the two builders on
  ports 5087 and 5088 (`ipro-builder-site`), and MySQL (a Windows service).

## Do this first tomorrow

1. Ask what he decided about the sample package waiting on his live site (import 1).
2. Finish 576: `git checkout main`, `git merge --no-ff hold/576-locked-boxes` (or cherry-pick `47e986d`), pin check,
   `gate_576.sh prepare`, the four parts, `sum`. Expect 1,768 tests. Commit on `main`, HELD.
3. Tell him in plain words what the release does and ask for his word. On yes: push `main`, verify both hosts,
   then publish `ba49da9` under his two builders (stop, publish, start; rehearse on a copy first).
4. Then have him make the package of his own site again. If something else stops it (a page with no sections of
   its own, a picture that cannot be read, a field longer than the contract carries), the screen says which.
