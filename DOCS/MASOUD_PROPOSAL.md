# Masoud Proposal

Working notes for a prospective corporate customer (contact: Masoud), opened 2026-09-08. To be
revisited after the software demo. Internal document: it names what the platform does not have
yet, so it is not the version that goes to the customer.

## What was asked

- About **20 insurance agents** under one company.
- A package that is **not on the public list**: all the AI features, e-cards, e-letters,
  newsletters, client invoicing ("billing"), teammates, "and a few more".
- They have their **own corporate domain**, which will **not** be pointed at our servers. They
  want to use it for the communication features, that is, to send from it.

## What works today, with no development

| need | how |
|---|---|
| A private package | SuperAdmin > Packages: build the package from feature toggles and limits (AI Daily Assistant, e-cards, e-letters, newsletters, client invoicing, team-member seats, client cap, newsletter cap, storage). Tick **hidden test package** so it never appears on public signup. |
| Controlled signup for 20 people | A trial invite code tied to that package, max 20 redemptions, with an expiry. Confirm during setup that a code can carry a non-trial hidden package straight into paid billing. |
| 20 agents with their own client lists | 20 separate accounts on the corporate package. Teammates are extra logins that share one account's data, right for an assistant, wrong for a producing agent. Seat count is a package limit. |
| No DNS pointing at us | Nothing is needed. If they never use the website builder, their DNS is untouched. The portal, the client portal and every link in our emails live on app.iproadvisers.com (the address rule from item 457). |
| Each agent pays for themselves | PayPal subscription per account, as for everyone else. |

## What has to be built

1. **Sending as their corporate domain** (the real item). Today every email leaves as
   no-reply@iproadvisers.com with the agent as reply-to. To send from their domain:
   - they add DNS records at their registrar: a verification TXT, an SPF include for Azure
     Communication Services, and DKIM records. No record points at our servers;
   - we verify and link the domain on ACS;
   - the platform gains a per-organization sender identity (from address, display name, verified
     status) that every dispatcher uses when mailing on behalf of one of their agents, with an
     admin screen to manage it.
   Estimate: about two days with tests, plus the customer's DNS turnaround. DMARC alignment works
   because ACS signs with their DKIM once verified.
2. **One invoice for 20 seats**, if the company wants to pay centrally. The platform has only
   PayPal subscriptions per account. A "billed by invoice" mode (SuperAdmin marks the account
   paid through a date, no PayPal) is about a day. The prepaid-value design in
   `22_PREPAID_VALUE.md` is the other route.
3. **A company view**, if a manager wants to see all 20 agents (usage, sends, leads). There is
   no organization object above accounts today; this is a feature, not a setting.

## The dependency that decides it

The Azure email sending quota is **per ACS resource and shared by every agent** (item 442). At the
current 100 per hour, 20 new senders on top of the launch advisers would queue behind each other
all day. Two ways through:

- the quota increase on ticket 2608310040012537 is granted (reply sent 2026-09-08); or
- this customer gets a **dedicated ACS email resource** with their own domain and their own limits.
  That also makes their sending reputation theirs, not ours, which is the right shape for a
  corporate customer sending from a corporate domain.

Put this in the contract as a condition, not a discovery in week one.

## Sequence

1. Demo the software (pending).
2. Quote the package and the 20 accounts; that costs nothing to set up.
3. Make the corporate sending domain a paid onboarding step; start the DNS work with them the day
   the contract is signed.
4. Decide the billing mode once it is known whether they want one invoice or twenty.
5. Decide whether they get a dedicated ACS resource (recommended if the quota is still at default).

## Questions to settle at or after the demo

- Exactly which features: the AI set, e-cards, e-letters, newsletters, client invoicing, teammates,
  polls, forms, Did You Know, the client portal, the website builder (unused if no domain points
  at us), Google Calendar.
- One invoice or twenty subscriptions.
- The corporate domain name, who controls its DNS, and whether they run DMARC with a strict policy.
- Whether a manager needs a company-level view, and of what.
- Seats: agents versus assistants (teammates).
- Timing relative to the 21 September launch.

## Pricing

Left blank on purpose. Prices are set by the owner in SuperAdmin > Packages and are never written
into code or documentation.

## Status

- 2026-09-08: assessed; waiting for the demo.
