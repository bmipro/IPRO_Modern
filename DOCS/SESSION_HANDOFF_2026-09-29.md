# Session handoff — 2026-09-29

## What happened

- **A backup before the day** (the owner asked): two zips of `6766459`, the version on both hosts.
- **The owner's concern:** an adviser's clients get email from "IPRO Advisers", a name they do not know. He asked
  for the adviser's name on every email a client receives.
- **The research that followed:** HostPapa (his mailboxes and the iproadvisers.com DNS) sends nothing and is not
  in the way. The bigger finding: Microsoft retires Azure Communication Services, email included, on
  2028-09-30, is in maintenance mode, and has no replacement for emailing clients. So the sender name per email
  cannot come on ACS, and iPro has to move its email within two years anyway (TODO 531).
- **530 step one built and deployed:** every email to an adviser's client names the business in its subject and
  replies to the adviser (TODO 530 has the list). Until today, invoices, reminders, polls, the portal invite,
  appointment emails and the testimonial request sent client replies to support@iproadvisers.com.
- **Provider research, weighted as the owner asked** (people's ratings and how accounts are treated; SendGrid
  dropped iPro with no explanation during development): `DOCS/EMAIL_PROVIDER_RESEARCH_2026-09-29.md`.
  Recommendation: Amazon SES in Montreal with a tenant per adviser and the paid support plan; Postmark as a
  standby. The decision is his.
- **AWS set up with the owner (531):** the account, the support plan, a budget, the region; the two sending
  addresses created and verified; ten DNS records at HostPapa, checked at both name servers and publicly (his
  mailbox MX untouched); production access requested. AWS asked for the use case and he posted our reply
  (support case 179071698900992; the text is in `Documents\iPro_AWS_SES_Case_Reply_2026-09-29.txt`).
- **The template builder reviewed** (his local build at 127.0.0.1:5086; saved and exported on the test copy
  with his permission): a real export passes the v0.4 checker. The review and iPro's contract clarifications
  went to the developer (`Documents\iPro_TemplateBuilder_Review_2026-09-29.md`). The big open question is his:
  the colours, fonts and design an adviser picks don't reach iPro yet.
- **532 recorded:** Refer a Friend, give $50 / get $50, with his decisions in the row (manual payouts through
  the Refunds queue, a switch to automatic later; no cap).
- **533 built and deployed:** found while answering AWS: marketing emails lacked the mailing address Canada's
  anti-spam law asks for, and the newsletter footer said "your IPRO adviser". Every marketing email now closes
  with the business, its address, a way out, and "Sent with iPro on behalf of" the business.

## Pushed today

| Code | Item | Gate |
|---|---|---|
| `245d08f` | **530a** client emails name the adviser's business and reply to the adviser | 1289/1289 |
| `fc33138` | **533** every marketing email closes with the business, its mailing address, a way out and "Sent with iPro on behalf of" | 1295/1295 |

## Do this first tomorrow

1. Both health endpoints, the Job Scheduler (`certificate-expiry`, the follow-ups, the invoice reminders at
   9:00 a.m. Eastern), Email Activity, Reports -> Visitors.
2. **The owner's glance:** send an invoice to a test client with his own email address: the subject reads
   "Invoice INV-… from <his business>", and replying goes to his own inbox.
   Then an e-letter or a poll to that test client: the foot shows his company name and address and "Sent with
   iPro on behalf of" his company (533).
3. **531:** watch for AWS's answer on case 179071698900992; on approval the build starts (a limited IAM login
   from a policy I write and he creates, configuration sets, bounce and complaint events, a tenant per
   adviser). **Decide:** TODO 520 (bring-your-own-website
   package); 524 (dictation, with the owner's three conditions in its row); 528 and 529 after the template builder.
4. **The new customer:** his first renewal is 25 October; PayPal's payment notice settles it on its own.
5. **Open:** 506 (an adviser's domain with CAA records); retention for the two page-view tables; an adviser's
   own icon and logo; the client-invoice email's look; the comped plans' renewal date (8 July 2027); 519 when
   the owner returns to it; the Girard demo if he wants it; the client Details page's three-collection query
   (`AsSplitQuery`); a Sage file for the accountant's statement the day an adviser brings a real template;
   the Squarespace template tour, paused after Common Tongue.
6. **The builder:** the developer has the review and the clarifications; when the fixes arrive, repeat the
   walkthrough and run `validate_return.py` on a real export. Queued after the email move: 532 (Refer a Friend).
7. **Calendar:** clear `Email__TrackingSigningKeyPrevious` around 17 October; the two unbound Let's Encrypt
   certificate resources lapse 19 October (nothing to do); ACS closes to NEW customers on 23 October (ours
   keeps working); the Microsoft quota re-application in mid-October only if the email move (531) is not under way; .NET 10 in October; ACS retires 2028-09-30.

Related: `DOCS/TODO.md` 530 to 533; `DOCS/EMAIL_PROVIDER_RESEARCH_2026-09-29.md`; `DOCS/SESSION_HANDOFF_2026-09-28.md`.
