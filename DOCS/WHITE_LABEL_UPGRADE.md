# White-label upgrade — sizing and plan (2026-09-16)

Written for the owner on 2026-09-16, five days before launch, from an inventory of the shipped code.
The July design (roadmap, "Broker/team/white-label model", TODO 378) still holds; this document
re-sizes it against what exists today and records what a partner brand would and would not cover.

## 1. Two different products hide under "white label"

| | What the partner's advisers see | Who bills, supports, contracts | Size |
|---|---|---|---|
| **(A)-lite: partner brand inside the platform** | The partner's logo, colours and name inside app.iproadvisers.com and in the mail the platform sends them | IPRO | about **1 week** |
| **(A): partner brand with the partner's own domain** | As above, plus sign-in and a partner dashboard at the partner's domain (team.partner.com) | IPRO | **10–11 build days**, 2–2.5 weeks with the usual gates |
| **(B): full reseller** | IPRO invisible: the partner's brand end to end | The partner (partner-level invoicing, partner-scoped admin, support routing, partner-branded contract) | **2–3 months** plus legal |

Decision: which of the three. (A) is designed so nothing is thrown away if a real partner later
justifies (B): the `Broker` row is the tenant record (B) needs.

## 2. Estimate for (A), by piece

| Piece | Days |
|---|---|
| `Broker` table (name, logo, accent colour, custom domain, enforce-branding flag), `AgentUser.BrokerId` + `IsBrokerAdmin`, schema repair in both apps, SuperAdmin **Brokers** page (CRUD + assign agents) on the ECardDesigns pattern | 1.5 |
| A per-request brand resolver (by the signed-in agent's broker; by host for anonymous pages such as sign-in) driving the portal layout: logo, page titles, accent colour default, the picker locked when the broker enforces branding | 2 |
| Transactional mail through the brand: welcome, password reset, the three trial reminders, invoice and plan-change texts, PayPal `brand_name`/plan name; the newsletter footer's "your IPRO adviser" | 1.5 |
| The partner domain: host → broker lookup, sign-in and dashboard on it, binding and managed certificate through the existing `AgentDomain` automation | 2 |
| Broker admin dashboard: the broker's agents, branding form, invite an adviser under the broker | 1.5 |
| Tests red-first, docs, deploys | 2 |
| **Total** | **10–11** |

Owner's time: half a day per partner (logo, colours, one CNAME, optionally the sender-domain DNS).
Infrastructure: none new. A partner domain is one more custom hostname on the same App Service.

## 3. Why it is that cheap: what is brand-bound today

The inventory (2026-09-16) found the platform already mostly brand-free:

- **82 brand strings in 26 views.** Landing pages 42 (they stay IPRO in every scenario), Shared layouts 13,
  Account 11, Billing 7, Preview 6, PublicWebsite 4, Newsletter 3, Error 1, PollVote 1. Every other folder
  (clients, follow-ups, calendar, cards, letters, polls, campaigns, activity, website builder) is brand-free.
- **One global email identity** (`EmailSettings`: From "IPRO Advisers" &lt;no-reply@iproadvisers.com&gt;,
  Reply-To support@). The card, letter, newsletter and poll composers are already agent-branded (company name,
  accent colour, photo, contact block); the shared unsubscribe footer names no brand.
- **Branded transactional mail:** welcome and password reset (AccountController), three trial reminders,
  PayPal texts (`brand_name`, plan name, invoice subject, plan-change notices), the error page, the admin
  invoice view.
- **Legal:** the Terms are a contract with iPro Advisers Inc., shown at sign-up. Correct for (A).

Reusable as they are: the per-agent accent colour (`--portal-accent`, one line to default it per broker),
the host-header routing and `AgentDomain` managed-certificate automation, `PlatformAliasHosts` (484) for
brand hosts, the SuperAdmin CRUD shape, `TeamMember` (379) as the login-under-an-owner precedent.

## 4. What (A) does not cover — changed since July

- **The sender.** Mail still leaves as "IPRO Advisers" from iproadvisers.com. A partner sender identity is
  about 2 more days plus the partner's DNS (SPF, DKIM, an ACS-verified domain), and the sending cap is per
  Azure **subscription** (491: 30 a minute, 100 an hour until Microsoft relents), shared by every partner.
- **Google Calendar.** The consent screen, the authorised domain and the callback are welded to
  app.iproadvisers.com and are under Google's review (DOCS/27). Connect always shows "IPRO Advisers"; a
  partner domain cannot host it. A partner-branded connect means a Google project per partner (B territory).
- **Links in mail.** Open/click tracking (488), unsubscribe and preference links name the platform host.
  Fine for (A); a (B) partner would want their own.
- **Help guides.** About thirty guides are IPRO-voiced prose; a brand-name substitution covers most of it.
- **PayPal checkout** shows the account's brand name unless the partner has its own PayPal (B).

## 5. Onboarding a partner under (A): the checklist

1. Partner provides: name, square logo (PNG, 120–240 px), one accent colour, the domain for sign-in
   (team.partner.com) and who their broker admin is.
2. SuperAdmin → Brokers: create the broker, upload the logo, set the colour, tick enforce-branding if the
   partner wants a uniform look, assign the advisers (or let the broker admin invite them).
3. Partner adds one CNAME (their sign-in host → the platform). The automation binds it and issues the
   certificate, as it does for adviser websites.
4. Optional sender identity: partner adds the SPF/DKIM/verification records; owner verifies the domain on
   the ACS resource; the broker's From becomes the partner's.
5. Advisers under the broker sign in at the partner host (or at app.iproadvisers.com) and see the partner's
   brand everywhere the portal used to say IPRO.

## 6. Recommendation and timing

- Nothing before launch (21 September) and nothing in the first two weeks after.
- If a named partner is waiting: start (A) in the week of 5 October; ships by the end of October.
- If nobody is waiting: let the first real partner conversation choose between (A) and (B), because the
  answer changes what gets built, not just how much. (B) is a spring project.
- Decisions to pin before any code, even for (A): who may create brokers (SuperAdmin only, as designed);
  whether a broker's advisers keep individual packages (as today) or share one; whether enforce-branding is
  per broker (as designed) or per adviser.

Related: `DOCS/IPRO_Project_Status_And_Roadmap.md` (the July design and the 2026-09-16 re-sizing),
`DOCS/TODO.md` 378, 379, 484, 488, 491; `DOCS/27_GOOGLE_OAUTH_VERIFICATION.md`.
