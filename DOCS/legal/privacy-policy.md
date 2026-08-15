# Privacy Policy

**iPro Advisers Inc.**
Effective date: **15 February 2012** · Last updated: 15 August 2026

> **Not yet legal advice.** Drafted from what the platform verifiably does, not from a
> template. Every third-party processor named below was confirmed in the codebase. Not
> reviewed by a lawyer. See `DOCS/legal/README-review-notes.md` before publishing.

---

## The short version

iPro Advisers Inc. builds websites and business tools for Canadian professional-service
firms. This policy explains what we do with personal information.

There are two different relationships here and they matter:

- **You, our subscriber.** We hold your account and billing contact information, and we are
  responsible for it.
- **Your clients.** You upload information about them. It stays yours. We hold and process
  it on your behalf, to run the service for you — nothing else. We do not sell it, we do not
  market to your clients, and we do not use it to build anything for anyone else.

We do not sell personal information to anyone, ever.

## 1. Who to contact

iPro Advisers Inc.
3230 Yonge Street, Suite 2005, Toronto, ON M4N 3P6
Privacy questions and requests: **privacy@iproadvisers.com**

If you are a client of one of our subscribers and you want your information corrected or
removed, contact the adviser or firm you deal with — the information is theirs, and they
decide. If you cannot reach them, write to us and we will help you get to the right place.

## 2. What we collect, and why

### From you, when you subscribe

| What | Why |
|---|---|
| Name, business name, email, business phone, address, country | To create and administer your account, and to contact you about it |
| Username and password (stored hashed, never in readable form) | To sign you in |
| Business type and chosen package | To provision the right starter content and features |
| Time zone | To show dates and times correctly and to schedule sends |
| Profile photo, logo, designation, contact details you publish | To render your public website, newsletters and greeting cards |
| Promotion or trial invite code | To apply the offer you were given |

### About your billing

Your subscription is charged through **PayPal**, which is our only payment method. **We never
see, receive or store your card or bank details.** PayPal gives us a subscription identifier
and the status and history of payments, which we use to keep your access correct and to issue
your invoices.

### The information you upload about your clients

Client names, contact details, account types, notes, meetings, life events, documents,
invoices, testimonials and portal messages — whatever you choose to record. **We treat this as
yours.** We handle it to operate the platform for you, to deliver the email you send, and to
provide support when you ask. We do not access it for any other purpose except where we must
to fix a fault, prevent abuse, or comply with the law.

### From visitors to your public website

When someone fills in one of your website forms — an enquiry, a meeting request, a newsletter
signup, a testimonial, a "Did You Know" unlock — we record what they submitted and pass it to
you as a lead. We also record their IP address and the time, to rate-limit and to block
automated abuse. Your website sets no advertising or tracking cookies.

### Automatically, when the platform is used

Sign-in times, IP addresses, browser and device type, pages visited within the portal, and
error diagnostics. We use this to keep the service secure and working, to investigate
problems, and to detect abuse. Diagnostic telemetry is scrubbed of tokens and credentials
before it is stored.

### Email delivery and engagement

For email sent through the platform, we record what was sent, to whom, when, and whether it
was delivered, bounced, opened or reported as spam. You need this to know your mail is
arriving. We need it to keep our sending reputation intact.

## 3. Artificial intelligence features

If your package includes the AI daily assistant or AI drafting tools, and you use them, the
relevant content — for example the record you asked for a suggestion about, or the topic you
asked for a newsletter draft on — is sent to **Anthropic PBC** (United States) for processing,
using the Claude API. The generated text comes back to your portal for you to review, edit or
discard.

- These features run **only when your package includes them and you use them.** They are not
  applied to your data in the background.
- Content sent to the API is processed to produce your result. Under our API terms with
  Anthropic, it is not used to train their models.
- We record token counts per day for billing and capacity, not the content of your prompts.
- **AI output is a draft, not advice.** Review it before it reaches a client. See section 9
  of our [Terms of Service](/terms).

If you would rather no client information reached an AI provider, do not use these features,
and ask us to disable them on your account.

## 4. Who else handles the information

We use a small number of service providers. Each gets only what it needs to do its job. None
of them may use it for their own purposes.

| Provider | What they handle | Where |
|---|---|---|
| **Microsoft Azure** | Hosting, database, uploaded files, diagnostic telemetry (Application Insights) | Eastern Canada |
| **PayPal** | Subscription payments and card details | Canada / United States |
| **SendGrid (Twilio)** | Delivering newsletters, campaigns, cards, letters and system email, and reporting on delivery | United States |
| **Anthropic PBC** | AI drafting and daily-assistant content, when you use those features | United States |
| **Google** | Calendar synchronisation — only if you connect your Google Calendar | United States |
| **Let's Encrypt** | Issuing SSL certificates for custom domains (domain name only) | International |

Some of these are outside Canada. While information is held in another country it is subject
to that country's laws, and may be accessible to its courts and law enforcement. We keep the
list short deliberately, and we will update this table whenever it changes.

We may also disclose information where we are legally required to, to protect our rights or
someone's safety, or to a buyer or successor if the business is sold — in which case we will
tell you, and this policy will continue to apply until replaced.

## 5. Email consent and unsubscribing

Canada's Anti-Spam Legislation governs commercial email. The platform is built for it:

- Every commercial message carries a working unsubscribe link and standard one-click
  unsubscribe headers.
- One opt-out applies across every kind of message the platform sends — newsletters,
  campaigns, greeting cards, letters, polls. There is one suppression list, honoured by every
  sender, so an opt-out cannot leak through a channel that missed it.
- Suppression is checked at send time, so an opt-out takes effect immediately.
- Subscribers can see who has opted out.

**Transactional messages are different.** Password resets, invoices, billing notices and
account alerts are not marketing, and you cannot unsubscribe from them while you hold an
account.

**Consent for your own lists is yours to obtain.** We provide the machinery; you are
responsible for having permission to email the people you upload.

## 6. How long we keep things

- **While you subscribe** — for as long as you need it.
- **After you cancel** — 30 days, so you can reactivate or export. Sooner if you ask.
- **After deletion** — the account and its data are removed, including uploaded files. This
  is thorough and cannot be undone, so export first.
- **Invoices and payment records** — kept as long as tax and accounting law requires, even
  after deletion.
- **Backups** — expire on their own cycle, after which deleted data is gone from those too.
- **Unsubscribe records** — kept after deletion of the underlying contact where needed, so we
  do not accidentally start emailing someone again.

## 7. How we protect it

Encryption in transit (HTTPS everywhere, including custom domains); passwords stored hashed,
never recoverable; access to production restricted; content security policy, anti-forgery
protection, rate limiting on public forms, and signature verification on incoming webhooks;
tokens and credentials scrubbed from diagnostic logs; regular security review of the
codebase.

No system is perfectly secure, and we do not claim otherwise. **If a breach occurs that
creates a real risk of significant harm, we will notify affected subscribers and the Privacy
Commissioner of Canada as PIPEDA requires**, and give you what you need to notify your own
clients.

## 8. Your rights

You may **see** the personal information we hold about you, **correct** it, **export** it,
**delete** it, and **withdraw consent** to optional features. Most of this you can do yourself
in the portal; for anything else, write to **privacy@iproadvisers.com** and we will respond within
**30 days**. We may need to verify who you are first. Withdrawing consent to something the
subscription depends on may mean we can no longer provide it.

If you are unhappy with how we have handled your information, tell us first — we try our best to
help you out. You could also cancel your services with us under “Cancel anytime”.

## 9. Cookies

We use cookies that are necessary for the service to work: keeping you signed in, protecting
forms against cross-site request forgery, and remembering interface preferences. We do not
use advertising cookies, and we do not run third-party ad or social tracking pixels — on our
site or on the public websites we host for subscribers.

## 10. Children

The platform is a business tool and is not intended for anyone under 18. We do not knowingly
collect information from children. If you believe a child's information has reached us
through one of our subscribers' websites, contact us and we will help get it removed.

## 11. Changes to this policy

We will post any change here and update the date at the top. If a change materially affects
how we handle personal information, we will notify subscribers by email or in the platform
before it takes effect.
