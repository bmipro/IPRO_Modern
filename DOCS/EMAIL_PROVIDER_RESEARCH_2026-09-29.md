# Email provider research — 2026-09-29

*For TODO 531. The decision is the owner's; nothing has been opened or signed up anywhere.*

## Why this is needed

Every email iPro sends goes through Microsoft's Azure Communication Services (ACS). Microsoft's
"Retirement and breaking changes guide for Azure Communication Services" (updated 2026-09-24) says:

- ACS, email included, retires on **2028-09-30**; after that date the API stops working.
- New customers cannot sign up from 2026-10-23; iPro's existing resource keeps working until retirement.
- The service is in maintenance mode (security and critical fixes only), so a sender name set per email
  will never be added. Today the sender name is fixed per registered sender, 100 per domain.
- Quota increases are handled case by case during the retirement period.
- Microsoft has no first-party replacement for emailing clients: Microsoft 365 High Volume Email sends only
  inside the organisation's own Microsoft 365, and Exchange Online is for person-to-person mail. The guide
  points to Marketplace partners (Infobip, Telesign).

## The owner's criteria

The owner, 2026-09-29: "when doing research please consider the People rating and reputation. I did not like
or appreciated when sendgrid dropped us with ZERO explanation leaving us hanging. Back then it was development
so I did not care as much but now we are dealing with production and live clients." SendGrid is excluded.

So the order of weight: how the provider treats an account when something goes wrong (warning, reason,
appeal, a human who answers), then people's ratings, then Canadian data location (the privacy policy says
data stays in Eastern Canada), then price. Technical fit is table stakes: a sender name and Reply-To per
email, delivery events (delivered, bounced, complained, opened, clicked), suppression lists, DKIM on our own
(sub)domain, and a .NET or plain HTTPS API.

## Recommendation, for the owner's decision

**Amazon SES in Montreal (ca-central-1), with one SES "tenant" per adviser, paid AWS support, a dedicated
sending subdomain, ACS kept as the fallback until 2028, and a warm standby provider for invoices and
reminders.**

Checked on AWS's own pages on 2026-09-29:

- **Warn-first enforcement, in writing.** A bounce rate of 5% or a complaint rate of 0.1% puts the account
  *under review* while it keeps sending; 10% or 0.5% may pause it. The account is always notified by email,
  a support case is opened with a summary, a review can be requested by replying, and a refused request
  comes back with reasons. SES can pause without a review first for very serious or repeated problems, and
  its diagnosis is high-level only. <https://docs.aws.amazon.com/ses/latest/dg/faqs-enforcement.html>
- **Tenants, built for software that sends for many customers.** Each adviser can be a tenant with its own
  reputation, policy and suppression list; SES and AWS Trust & Safety can pause one tenant instead of the
  whole account. There is a small extra charge per tenant email.
  <https://docs.aws.amazon.com/ses/latest/dg/tenants.html>
- **Canada.** Montreal (ca-central-1), and Calgary (ca-west-1, API only).
  <https://docs.aws.amazon.com/general/latest/gr/ses.html>

The weak spot people report: slow human replies on the free support tier when an account is paused (1–2
weeks in 2025 forum cases). The fix is the paid support plan (Business Support+, from $29 a month, 24/7).

## Comparison (as reported by the research on 2026-09-29; sources below)

| | Amazon SES | Postmark | Mailgun | Brevo | Resend |
|---|---|---|---|---|---|
| People's ratings | G2 4.3 (204); Capterra 4.7 (123) | G2 4.6 (~30); Capterra 4.7 (35); Trustpilot 2.2 (45) | G2 4.2 (322); Capterra 4.3 (197); Trustpilot 4.1 (1,532) | Capterra 4.6 (3,522); G2 4.5 (whole platform) | G2 4.8 (9); Trustpilot mostly 2026 ban complaints |
| When something goes wrong | Published thresholds, review period, notice, support case, appeal with reasons | Emails owner and emergency contacts, queues mail; terms for new customers allow cancellation for threshold breaches | Sinch's shared rules (Mailgun and Mailjet): may suspend with no warning and no reason; reports of disables without warning (Sep 2025, Jul and Sep 2026) | 2nd suspension can be permanent; ~4–5% of recent Trustpilot reviews are suspension complaints | Terms: suspend "for any reason… without prior notice", no appeal |
| Sending for many customers, incl. newsletters | Explicitly supported (tenants) | Supported with separate newsletter streams; "no contact in 3 months" rule | Resellers and agencies expected; but Sinch requires express, provable opt-in for anything not transactional, stricter than CASL's implied consent | Resale needs a written agreement; get written OK | Explicit multi-tenant guidance |
| Support | Free tier slow; paid plan 24/7 | Weekdays only, email and chat, no paid tier | 24/7 tickets; chat and phone from the $90 Scale plan; no response-time commitment on self-serve plans | Tickets; phone on higher plans | Weekday hours; Slack on Scale |
| Data in Canada | **Yes** (Montreal) | No (US only) | No (US or EU) | No (EU) | No (US storage) |
| Price at 50k / 200k emails a month | about $5 / $20, plus support | about $66 / $246 (Platform plan) | $35 / about $185–230 | about $39–97 / $239–599 | $20 / $125–190 |

Dropped:
- **Mailjet:** the same Sinch rules as Mailgun, so not an independent backup; its 2025–26 reviews put sudden
  blocks and demands for proof of consent at the top of the complaints, including a financial-services firm
  emailing its existing clients.
- **SparkPost/Bird:** SparkPost is now Bird, mid-migration to a new API; 2025–26 reports of suspension
  without warning; its terms bar building Bird into a service sold to others without a negotiated
  contract; no Canada region.
- **Infobip:** named by Microsoft and a good technical fit, with published enforcement that lifts itself (a
  warning at 3% bounces; the sending domain paused at 5% for 24 hours). But it has no Canadian email region,
  it is sold only through a negotiated private offer, and its Trustpilot rating is 1.6 with reports of weeks
  of support silence.
- **Telesign:** named by Microsoft, but its documented email is template-only and transactional, with one
  fixed From name and no per-email Reply-To; its policy allows suspension without notice with no appeal;
  no Canadian data location.
- **Zoho ZeptoMail** (renamed Zoho CPaaS on 2026-09-23): transactional only, so it cannot carry newsletters,
  drips or e-cards. Very cheap, with published thresholds and up-front vetting, and there are live but
  undocumented signs of a Canadian data centre. A possible transactional-only standby, only after Zoho
  confirms Canadian hosting, a Reply-To outside the verified domain, and the multi-tenant use in writing.
- **Microsoft 365 High Volume Email:** internal recipients only.
- **SendGrid:** the owner's decision.

## Risks with SES, and how to reduce them

1. **Slow replies when paused:** buy the paid support plan; premium support can reach the SES review team.
2. **Missed notices** (they go to the AWS account's main email): set AWS alternate contacts, alarm on the
   "SES sending paused" health event and on tenant status changes, alarm on bounce and complaint rates.
3. **One adviser's list pausing everyone:** a tenant per adviser with the Standard policy and per-tenant
   suppression; check an adviser's imported list before their first newsletter.
4. **Newsletters hurting invoices:** send invoices and reminders from one subdomain and newsletters from
   another, each with its own DKIM and configuration set.
5. **Staying under the thresholds:** keep bounces under 2% and complaints under 0.1%; register the sending
   domains in Google Postmaster Tools (Gmail does not report complaints to SES); keep one-click unsubscribe.
6. **Stolen keys:** no long-lived access keys; a narrowly scoped role that can only send.
7. **A backup:** keep ACS as the fallback until 2028-09-30, at its current 100 emails an hour, enough for
   invoices and reminders in an emergency but not for newsletters; keep Postmark warm on its own subdomain for
   invoices and reminders, with a switch in iPro to fail over. Mailgun is the second choice for the
   standby; its consent rule rules it out for adviser newsletters sent on implied consent.
8. **Getting out of the SES sandbox:** apply early, describe the use case in detail.

## What moving would take

iPro already sends through one seam (`IEmailService`), so the work is a new provider behind it: sending with a
tenant and a configuration set, the delivery events feeding the same Email Activity screens the ACS events
feed today, suppression kept in step, and the DNS records for the sending subdomain(s) (one-time, at HostPapa,
or once as a delegation to Azure DNS so HostPapa is never touched again). About a week of building, then two
to four weeks of gradually rising volume, invoices first and newsletters last. The per-email sender name
("Global Business Solution via iPro") comes with it (TODO 530, step two).

## Sources

- ACS retirement guide: <https://learn.microsoft.com/en-us/azure/communication-services/acs-retirement-and-breaking-changes-guide>
- SES enforcement: <https://docs.aws.amazon.com/ses/latest/dg/faqs-enforcement.html>
- SES tenants: <https://docs.aws.amazon.com/ses/latest/dg/tenants.html>
- SES regions: <https://docs.aws.amazon.com/general/latest/gr/ses.html>
- SES pricing: <https://aws.amazon.com/ses/pricing/>
- AWS support plans: <https://aws.amazon.com/premiumsupport/plans/>
- SES paused-account forum cases (2025): <https://repost.aws/questions/QUJiVxpIJ1Tf-qF-jYdqsNIQ/ses-sending-paused-and-support-request-unanswered-for-several-days-how-can-i-escalate>
- Ratings: <https://www.g2.com/products/amazon-simple-email-service-amazon-ses/reviews>, <https://www.capterra.com/p/179662/Amazon-SES/reviews/>, <https://www.trustpilot.com/review/postmarkapp.com>, <https://www.capterra.com/p/254711/Postmark/reviews/>, <https://www.trustpilot.com/review/mailgun.com>, <https://www.capterra.com/p/159630/Mailgun/reviews/>, <https://www.capterra.com/p/132996/brevo/>, <https://www.trustpilot.com/review/resend.com>, <https://www.trustpilot.com/review/infobip.com>
- Postmark terms, pause process and data location: <https://postmarkapp.com/terms-of-service/>, <https://postmarkapp.com/support/article/does-postmark-have-a-daily-send-limit>, <https://postmarkapp.com/eu-privacy>
- Brevo enforcement: <https://help.brevo.com/hc/en-us/articles/209408165>
- Resend terms and data location: <https://resend.com/legal/terms-of-service>, <https://resend.com/docs/dashboard/domains/regions>
