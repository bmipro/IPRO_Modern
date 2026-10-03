# Amazon SES go-live runbook (TODO 531)

*2026-09-30. The code is in place and switched off: with `Email__Ses__Streams` empty, every email goes
through Azure Communication Services exactly as before. This is the order for switching it on, and the
way back. The owner does every console step and pastes every secret himself; nothing secret goes into
chat, a file or the repo.*

**Status, 2026-09-30:** steps 1 to 6 done with the owner; step 7's pilot on since about 1:40 p.m. Eastern
(adviser 12, `notify`): INV-1003 went From "Global Business Solution via iPro", and Amazon's delivery report
landed on the invoice a minute later. Left: the simulator's bounce and complaint, then step 8.
**2026-10-02:** the owner asked for a few tests on his own account to decide when everyone moves; they are
section 7a (the test sheet), which also puts the marketing stream's trial on his account BEFORE step 8.

**What exists in AWS already.** Account 354245663230, region Canada (Central) `ca-central-1`, Business
Support+. Production access granted 2026-09-30: 50,000 emails a day, 14 a second. Two verified identities:
`notify.iproadvisers.com` (transactional) and `news.iproadvisers.com` (marketing), Easy DKIM 2048-bit,
custom MAIL FROM `bounce.notify` / `bounce.news`. The account-level suppression list is on for bounces and
complaints. DNS: ten records at HostPapa (`Documents\iPro_AWS_DNS_Records_2026-09-29.md`).

**What the code does once switched on.** An email an adviser sends to their own client carries two tags
(`AdviserSender.Tags`): its stream and its adviser. `RoutingEmailService` sends it through SES when that
stream is listed in `Email__Ses__Streams`, the keys are present, and (during a pilot) the adviser is in
`Email__Ses__PilotAgentIds`. Everything else, including iPro's own mail to advisers (sign-in, billing,
notices), stays on ACS.

- From: `"<business> via iPro" <mail@notify.iproadvisers.com>` (or `@news.`). Reply-To: the adviser.
- Configuration set `ipro-notify` or `ipro-news`; tenant `adviser-<id>`, created on the adviser's first
  send with both addresses and both configuration sets associated.
- Amazon's delivery reports come back through SNS to `/SesEmailEvents`. A permanent bounce suppresses
  the client everywhere; a complaint unsubscribes them from everything; deliveries show in Email Activity.

---

## 1. Two configuration sets (SES console, Canada Central)

SES -> Configuration -> Configuration sets -> Create set:

- Name `ipro-notify`. Tick **Reputation metrics** (the console leaves it off; about a dollar a month for both
  sets); leave the rest at the defaults (suppression: account level).
- Name `ipro-news`. Same.

No open or click tracking: iPro counts those itself.

## 2. The report topic (SNS console, Canada Central)

SNS -> Topics -> Create topic: type **Standard**, name `ipro-ses-events`. Its ARN will be
`arn:aws:sns:ca-central-1:354245663230:ipro-ses-events`.

Edit its access policy to let SES publish to it (add this statement to the default policy):

```json
{
  "Sid": "AllowSesToPublish",
  "Effect": "Allow",
  "Principal": { "Service": "ses.amazonaws.com" },
  "Action": "SNS:Publish",
  "Resource": "arn:aws:sns:ca-central-1:354245663230:ipro-ses-events",
  "Condition": { "StringEquals": { "AWS:SourceAccount": "354245663230" } }
}
```

The console's default statement came with an empty `Resource` on 2026-09-30; set it to the topic's ARN as
well. Leave encryption off: SES cannot publish to a topic locked with the AWS-managed key.

Do **not** create the subscription yet: iPro confirms it automatically, but only once step 5's settings
are in place.

## 3. Event destinations (SES console)

For **each** configuration set -> Event destinations -> Add destination:

- Event types: **Rendering failures, Rejects, Deliveries, Hard bounces, Complaints, Delivery delays**.
  Not Sends, Opens, Clicks or Subscriptions.
- Destination: Amazon SNS, topic `ipro-ses-events`. Name: `ipro-events`.

## 4. iPro's own AWS login (IAM console)

IAM -> Users -> Create user `ipro-app-ses`, **no** console access. Permissions -> Create inline policy
(JSON), name `ipro-ses-send`:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "SendOnlyFromTheTwoAddresses",
      "Effect": "Allow",
      "Action": "ses:SendEmail",
      "Resource": "*",
      "Condition": { "StringLike": { "ses:FromAddress": [ "*@notify.iproadvisers.com", "*@news.iproadvisers.com" ] } }
    },
    {
      "Sid": "OneTenantPerAdviser",
      "Effect": "Allow",
      "Action": [ "ses:CreateTenant", "ses:GetTenant", "ses:CreateTenantResourceAssociation", "ses:TagResource" ],
      "Resource": "*"
    }
  ]
}
```

It can send only from the two iPro addresses and manage advisers' tenants: it cannot read mail, change
DNS, touch billing or create other logins.

Then Security credentials -> Create access key -> "Application running outside AWS". **Copy the two
values straight into step 5's settings**; never into chat, a file or an email. AWS shows the secret once.
The description tag takes letters, digits, spaces and `_ . : / = + - @` only (no brackets). In the desktop
app's browser pane Amazon's copy icons do nothing (the pane blocks websites from writing to the clipboard):
select each value and press Ctrl+C, and keep the page open until both are pasted. A key closed before it was
pasted is deactivated, deleted and made again; nobody holds its secret.

## 5. App Service settings (Azure portal, `ipro-prod-web` only; saving restarts the app)

The owner's go is needed for this step (a production restart).

| Setting | Value |
|---|---|
| `Email__Ses__AccessKeyId` | from step 4 |
| `Email__Ses__SecretAccessKey` | from step 4 |
| `Email__Ses__AccountId` | `354245663230` |
| `Email__Ses__EventTopicArn` | `arn:aws:sns:ca-central-1:354245663230:ipro-ses-events` |
| `Email__Ses__EventSecret` | a long random string the owner makes (PowerShell: `-join ((48..57)+(65..90)+(97..122) | Get-Random -Count 40 | ForEach-Object {[char]$_})`); it goes here and in step 6's URL, nowhere else |
| `Email__Ses__Streams` | leave **empty** for now |

The admin app sends only iPro's own mail and needs none of these.

## 6. The subscription (SNS console)

Topic `ipro-ses-events` -> Create subscription: protocol **HTTPS**, endpoint
`https://app.iproadvisers.com/SesEmailEvents?secret=<the EventSecret>`, raw message delivery **off**.
Within a minute it should read **Confirmed** (iPro verifies Amazon's signature and confirms it). If it
stays "Pending confirmation", the log names the reason (secret, topic ARN or signature).

## 7. A pilot on the owner's own account, with Amazon's test mailboxes

Settings: `Email__Ses__PilotAgentIds` = the owner's adviser id (12, the grey "#12" in the admin app's
Agents list) **first**, then `Email__Ses__Streams` = `notify`. Each save restarts the app; in the other order,
every adviser's mail would move for the minute between the two. In his adviser account, three test clients:

| Client email | Send them | Expect |
|---|---|---|
| `success@simulator.amazonses.com` | an invoice | Delivered on the invoice and in Email Activity |
| `bounce@simulator.amazonses.com` | an invoice | Bounced; the client suppressed on every channel |
| `complaint@simulator.amazonses.com` | an invoice | the client unsubscribed from everything |

Then one invoice to his own mailbox: From "Global Business Solution via iPro", replying goes to him, the
headers show DKIM `pass` for `notify.iproadvisers.com`. The mailbox simulator doesn't count against the
quota or the reputation.

## 7a. The pilot test sheet (2026-10-02; the owner: "set up few tests with my bahmanmotamed account and aws so we could decide when to port it to aws for the rest of the system")

Everything here happens in the owner's own adviser account (12); no other adviser's mail moves. He clicks
(the account is his sign-in) and reports or screenshots; the server side is read from the web container log
(only warnings and errors are logged, so "no line" means "no failure") and the settings' names and lengths.

**Starting point, read 2026-10-02:** `Email__Ses__PilotAgentIds` holds 2 characters and `Email__Ses__Streams`
6 (what `12` and `notify` measure); the five keys are present; the web log since the pilot began (09-30,
17:40 UTC) has no failed SES send, no bounce and no complaint. Its only SES lines are three refused event
calls on 09-30: 13:48 and 13:54 UTC, before the settings existed, and 18:07 UTC, the close-out's own check
that a call without the secret is refused.

### Part 1: the invoice-type mail (`notify`), no setting changes

| # | What he does | What must be true |
|---|---|---|
| 1 | Three test clients (`success@`, `bounce@`, `complaint@simulator.amazonses.com`), one small invoice **Sent to Client** each | `success@`: the invoice reads "delivered" within a minute or two. `bounce@`: it reads "bounced (reason)", the client shows the red **Unsubscribed** badge, and the log carries "SES reported a HARD BOUNCE ... suppressed". `complaint@`: the client shows **Unsubscribed**. The simulator costs no quota and no reputation. |
| 2 | One invoice to each mailbox he owns (Gmail, Yahoo, Outlook/Hotmail, his business mailbox) | In the Inbox, not Spam/Junk. From "Global Business Solution via iPro". **Reply** addresses his own adviser email. The link opens the invoice. The invoice reads "delivered". In Gmail, **Show original**: SPF, DKIM and DMARC all PASS. |
| 3 | The other client emails, once each, to one of his mailboxes: an estimate; **Send reminder** on an invoice past its due date; **Invite to Portal**; **Request Testimonial** | Each arrives with the same sender name and replies to him. (Appointment scheduled/declined needs a request made from the client portal: optional.) |
| 4 | Nothing: his normal client mail keeps going through Amazon for a few days | No SES failure in the log; SES -> Reputation metrics: bounces under 2%, complaints under 0.05%. |

**Test 1, run by the owner 2026-10-02, about 2:30 p.m. Eastern: passed, and it found TODO 538.** `success@`: the
invoice read Delivered. `bounce@` (an estimate): Bounced, the client suppressed, the log's hard-bounce line at
18:31:06 UTC, the notice to him a minute later. `complaint@`: the complaint recorded ("complaint: abuse"), the
client suppressed, the notice to him. What iPro SAID about the last two was wrong (the complaint read "could not
be sent" and "Send failed", and both notices said "unsubscribed"); that, a bounced address the adviser could
not correct, and a delivery delay shown as a final failure are 538. Two invoices to his own mailboxes read
Viewed; which mailbox, the Inbox, the sender name and the reply address are still his to report (test 2).
After 538: a complaint reads "delivered ..., then reported as spam by the recipient" / "Spam complaint", and
the notices say "An email to <client> bounced" and "<client> reported one of your emails as spam". His two
test clients, suppressed before 538 kept a reason, read Unsubscribed until 540 (deployed 10-02) found their
reasons in the email history: Reported spam and Email bounced.

**Test 2 (reported by the owner 2026-10-03, with screenshots and Gmail's raw headers):** INV-1014 (sent 10-02,
2:48 p.m.) reached his Gmail Inbox from "Global Business Solution via iPro" <mail@notify.iproadvisers.com>,
delivered in 0 seconds from Amazon's Canada Central servers over TLS 1.3. Gmail's **Show original**: SPF PASS
(the return path is `bounce.notify.iproadvisers.com`), DKIM PASS for `notify.iproadvisers.com` (Amazon signs a
second time as `amazonses.com`), DMARC PASS; Reply-To is the adviser's own address under his business name.
The DMARC record is monitor-only (`p=none`, `sp=none`): enough for Gmail's and Yahoo's sender rules; tightening
it is a later step, after every service that sends as iproadvisers.com is checked. INV-1015 (3:02 p.m.) reached
his business mailbox, read in Thunderbird: the same sender, and Reply-To is him. Both read Viewed in iPro, so
the link opened the invoice. Invoice mail carries no List-Unsubscribe header (it is not marketing); the news
stream adds the one-click pair, to be seen in Part 2. Still open: the business mailbox's folder (Inbox or
Junk), and Yahoo (his adviser address is a Yahoo one) and Outlook.com.

### Part 2: the marketing mail (`news`), on his account only

The pilot list limits BOTH streams, so marketing mail can be tried on his account only while
`Email__Ses__PilotAgentIds` is still set. Once it is cleared, adding `news` moves every adviser's at once.
So this comes before step 8: `Email__Ses__Streams` = `notify,news` (his change; a restart), then:

| # | What he does | What must be true |
|---|---|---|
| 5 | A newsletter to an account type holding only his own mailboxes | From "Global Business Solution via iPro" at `mail@news.iproadvisers.com`; Inbox; the foot names his business and mailing address; the **Unsubscribe** link in the email works (re-subscribe from the same page afterwards); Email Activity shows Delivered, then Opened. |
| 6 | An e-card (or e-letter) to one of his mailboxes | Arrives; Email Activity shows it. |
| 7 | Two NEW test clients, `bounce+news@simulator.amazonses.com` and `complaint+news@simulator.amazonses.com` (test 1's two are unsubscribed for good, and only the client can undo that; Amazon's test mailboxes accept a `+label`), in an account type of their own; a newsletter to it | Email Activity shows the first Failed with Amazon's reason; both clients end **Unsubscribed**; the log carries the hard-bounce line and no error. |

### The decision

- **Everyone's invoice-type mail (step 8.1)** when 1 to 3 pass and the log has stayed clean for a few days.
  If Part 2 ran first: set `Email__Ses__Streams` back to `notify` (restart), THEN clear
  `Email__Ses__PilotAgentIds` (restart); the other order moves every adviser's marketing mail for a minute.
- **Everyone's marketing mail (step 8.2)** a week later, when 5 to 7 passed and the reputation figures hold.
- **Not part of this switch:** iPro's own mail to advisers (sign-in, password resets, billing, the morning
  follow-up email, support). It is untagged and stays on ACS; moving it is a build item of its own, due well
  before ACS retires on 2028-09-30.

## 8. Everyone, stream by stream

1. Clear `Email__Ses__PilotAgentIds` (restart): every adviser's invoices, reminders, portal invites,
   appointment emails and testimonial requests go through SES. Watch a week: SES -> Reputation metrics;
   bounces under 2%, complaints under 0.05% (SES reviews at 5% and 0.1%).
2. `Email__Ses__Streams` = `notify,news` (restart): newsletters, drips, cards, letters, polls, Did You Know.
3. CloudWatch alarms on `Reputation.BounceRate` > 0.02 and `Reputation.ComplaintRate` > 0.0005, emailing
   the owner.

## The way back

Clear `Email__Ses__Streams` (restart). Every email goes through ACS again at once; nothing else changes.
ACS stays in place until it retires on 2028-09-30.

## Optional

Replies that ignore Reply-To land at `mail@notify.iproadvisers.com` / `mail@news.iproadvisers.com`, which
have no mailbox. A HostPapa forwarder from each to support@ would catch them.

## Where it lives in the code

`RoutingEmailService` (the switch), `SesEmailService` (the send, sender name, tags, retry contract),
`SesTenants` and `SesPacer`, `SesEmailEventsController` + `SnsTrust` (the reports), `EmailEventCorrelation`
(shared with the ACS endpoint), `AdviserSender.Tags` / `EmailStreams`. Tests: `Ses531Tests`.
