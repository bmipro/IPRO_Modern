# Session handoff -- 2026-10-13

## Branches: read this first

- `main` is what production runs, plus notes. `feat/template-builder-beta` (local only, not pushed) holds the
  Template Builder work; that branch's copy of this file has the rest of the day. After anything lands on `main`,
  merge `main` into the branch.

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
  NOT pushed: a push deploys and waits for his word.
- **Offered, not done** (his to ask for): the bare names answering with a redirect to www instead of the same page
  (507 was his choice that the name stays, so this is a question, not a fix); the "real address" tag on Privacy and
  Terms; real pages again for About, Features, Support and a blog, which the old site had and Google still showed.

## Still his

- **The word to deploy 575** (old addresses of iproadvisers.com; committed on `main`, not pushed).
- From 10-12, unchanged: SES step 8.2; his GST/HST number for 570.
