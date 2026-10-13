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

## Still his

- From 10-12, unchanged: SES step 8.2; his GST/HST number for 570.
