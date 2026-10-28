# Agent Account, Profile, and Dashboard

## Sign In

1. Open the Agent Portal.
2. Enter the assigned username and password.
3. Tick **Remember me** only on a private device: it keeps you signed in on it for 7 days from signing in, even after closing the browser (otherwise 8 hours).
4. Click **Sign In**.
5. If this is the first login, create a new password when prompted.

If the login fails, verify the username and password. Super Admin can reset the password and require another password change.

### Five Wrong Passwords Lock Sign-In for 15 Minutes

After 5 wrong passwords in a row, signing in with that name is locked for 15 minutes, even with the right password. The sign-in page then says:

> Sign-in with this name is locked for 15 minutes after 5 wrong passwords in a row. To get in sooner, reset your password with "Forgot password?" below. A team member asks the account owner to reset theirs.

- A sign-in that works starts the count again. Your username and your email address are the same account here: wrong passwords with either count together.
- **Forgot password?** unlocks it at once, and the reset signs you straight in (with two-step sign-in on, after your code).
- You get one email when it locks ("Your iPro sign-in was locked for 15 minutes"), and no more than one a day however often it locks again. If it was not you, reset your password.
- **Change Password** checks your current password the same way: 5 wrong in a row lock your sign-in too.
- Super Admin can see and clear a lock on your record.
- A name that is not an account gets the same answers, so the lock does not tell anyone which names exist.

## Forgot Your Password

1. On the login page, click **Forgot password?**.
2. Enter the email address on the account.
3. Click **Send Reset Link**.
4. Check email for a message from IPRO Advisers and click **Reset My Password**.
5. Enter and confirm a new password.

### Important Behavior

- The confirmation screen always shows the same message ("If that email is registered...") whether or not an account exists for that email, so no one can use this form to discover which emails have an account.
- The reset link expires **1 hour** after the request and can only be used once. Request a new link if it expires.
- Successfully resetting the password signs the agent in immediately and clears any pending forced-password-change flag. With two-step sign-in on, the code page comes first: a reset never skips the code.

### Troubleshooting

- No email arrives: check spam/junk, confirm the email matches the one on file in **My Profile**, and try again — each request issues a fresh link and invalidates the previous one.
- "Link Invalid or Expired": request a new reset link; the old link can no longer be used once consumed or after 1 hour.

## Change the Password

1. Open the user menu in the upper-right corner.
2. Select **Change Password**.
3. Enter the new password and confirmation.
4. Save the change.

A new password signs you out of every other browser where you were signed in (this one stays signed in). So does **Forgot password?**, and a temporary password from Super Admin.

## Two-Step Sign-In

Your clients' personal and financial information is in iPro. With two-step sign-in on, iPro asks for a code after your password, so someone who learns your password still cannot get in. It is optional; the Dashboard shows a reminder until you turn it on. Every team member turns on their own.

### Turn On Two-Step Sign-In

1. Open the user menu in the upper-right corner and select **Two-Step Sign-In** (or click **Set up two-step sign-in** on the Dashboard).
2. Click **Set up two-step sign-in** and choose how you will get your codes:
   - **Use an authenticator app** (recommended). Google Authenticator, Microsoft Authenticator, or a password manager that shows codes.
   - **Use email codes**, if you do not want an app. This is safer than a password alone, but only as safe as that mailbox.
3. Click **Email me a code**. Either way, iPro first emails a 6-digit code to the address on your account, to check it is you (it works once, for 10 minutes; at most one a minute and 10 a day).
4. With an app: in the app, add an account and scan the QR code (if you cannot scan it, type the key shown beside it), then type the code the app shows.
5. Type the code from the email, and click **Turn on two-step sign-in**.
6. Save the ten **recovery codes** shown next. This is the only time iPro shows them. Each one signs you in once if you cannot get a code (a lost phone, for example). Keep them in a password manager, or print them and put them away.

iPro emails you that two-step sign-in is on. Any other browser where you were signed in is signed out, and asks for a code the next time.

### Sign In With Two-Step Sign-In

1. Sign in with your username (or email) and password as usual.
2. On the **Two-step sign-in** page, type the code your app shows now, or the code iPro just emailed you. An emailed code works once, for 10 minutes; **Email me a new code** sends another (at most one a minute and 5 an hour).
3. **Don't ask for a code on this device for 30 days** is ticked for you: untick it on a shared computer. Your password is still needed every time; a new password, or turning two-step sign-in off, ends it.
4. Click **Verify**.

- Lost your phone? Type one of your recovery codes in the same box (it looks like `abcde-fghjk`). iPro emails you each time one is used and says how many are left.
- **Forgot password?** still works, but it does not skip the code: after choosing a new password you are asked for your code.
- **5 wrong codes in a row lock sign-in for 15 minutes**, the same rule as wrong passwords, and you get the same one-a-day email ("Your iPro sign-in was locked for 15 minutes"). That email means someone typed your password correctly: change it. A new password does not end a lock caused by wrong codes; wait 15 minutes, or ask iPro support (Super Admin can unlock it).
- **20 wrong codes without a right one** (four of those locks) keep sign-in locked until iPro support unlocks it: someone has your password and is guessing codes. Change your password with **Forgot password?**, then call support.
- Lost your phone **and** your recovery codes? An adviser calls iPro support at 1-416-363-2220; Super Admin can reset two-step sign-in, and you get an email when they do. A team member asks the account owner, who can reset it on **My Team**.

### Manage Two-Step Sign-In

On the **Two-Step Sign-In** page (user menu):

- **Make new recovery codes**: needs your current password and a code. Ten new codes; the old ones stop working, every other signed-in browser is signed out, and no device stays remembered.
- **Turn off**: needs your current password and a code. Your password alone signs you in again, and every other signed-in browser is signed out. iPro emails you that it is off.
- To change how you get codes (a new phone, or email instead of an app), turn it off, then set it up again.
- With email codes, click **Email me a code** first to get the code these forms need.

If iPro requires two-step sign-in for your account (Super Admin can require it), the portal opens the set-up page until it is on, for you and for each of your team members. You can still change your password and sign out.

## Edit the Agent Profile

1. Open the user menu in the upper-right corner.
2. Select **My Profile**.
3. Update the required contact or business information.
4. Confirm the email address is correct and lowercase.
5. Select the correct time zone. Scheduled newsletters and displayed times use this setting.
6. Click **Save Changes**.

Profile data is also used on the public website, invoices, lead notifications, and billing tax calculations.

The **Login Email** is where **Forgot password?** links and emailed sign-in codes go, so changing it needs your **Current password** (and a two-step code, when two-step sign-in is on; with email codes, click **Email me a code** under the box). Team members cannot change it. iPro emails the old address that it changed.

Your company name is how your clients recognize your emails. It leads the subject of every email IPRO sends to your clients (invoices, reminders, newsletters, drip campaigns, e-cards, e-letters, polls, appointment and portal emails), and when a client replies, the reply comes to your profile email. With no company name on file, your own name is used.

Your company address closes every marketing email you send (newsletters, drip campaigns, e-cards, e-letters, polls, Did You Know emails and testimonial requests), together with your company name, an unsubscribe link and the line "Sent with iPro on behalf of" your company. Canada's anti-spam law asks every commercial email for a mailing address, so keep the street address filled in on your profile. While it is missing, the send pages remind you, and your emails show only your city, province and postal code.

## Personalize the Agent Portal Color

Click the small palette icon beside the "IPRO Agent Portal" logo (top-left of every page) to pick one of 6 colors for your own portal's top bar — Ocean Blue, Sunset Orange, Forest Green, Slate Gray, Burgundy, or Royal Purple. This only changes how your own portal looks to you; it's separate from your public website's theme color (set under My Website).

## Use the Dashboard

The dashboard summarizes work that needs attention:

- Total clients
- Overdue follow-ups
- Follow-ups due today
- Upcoming follow-ups
- New and unread website leads
- Recent website inquiries
- Subscription and website status

### Open a Dashboard Item

1. Click the appropriate count or action button.
2. Review the filtered list.
3. Complete the task from the related client, lead, billing, or website screen.

### Quick Actions

Use **Quick Actions** to:

- Add a client
- Search clients
- Open the follow-up queue
- Open the follow-up calendar
- Create a newsletter
- Review website leads
- Open billing

If a function is outside the active package, the link remains visible and displays an upgrade message.

## Refer a Friend

**Give $50, Get $50.** Anyone you know who could use iPro -- a colleague, another business, whatever edition they
need -- gets $50 off when they sign up with your link, and you get $50 back once they stay. Open it from the
**Refer a Friend** card on your Profile (just above Calendar Source) or on the Dashboard.

1. **Share your link.** Copy it, or select **Share by email** to open your own email app with the link in a
   ready-made note. iPro never emails your friend. Your code (for example `JANE-7K3Q`) works too: a friend can type
   it in the sign-up's **Promotion Code** box.
2. **Your friend's gift.** The sign-up page opens with "A $50 gift from you". The $50 comes off their setup fee at
   checkout; when the setup fee is less (or waived), the rest comes off their first month, then their second. One
   code per sign-up: a different promotion code typed instead means no gift.
3. **When they join,** we email you to say so, and when your reward is earned: at their **second monthly payment**,
   or **30 days after their first payment** on a yearly plan, if they are still subscribed.
4. **When it is earned,** your $50 plus its tax is refunded to the card or PayPal account you pay iPro with, and we
   email you the day it goes. PayPal shows it right away; a card can take a few business days. Each reward has a
   **credit note** on the Refer a Friend page, to keep with your invoices.

The page lists every referral and where it stands: signed up, joined (with the date your reward is expected),
earned, paid. One that is never earned -- the friend left before their second payment -- reads **Not earned**.
There is no limit: "You have referred N and earned $X" keeps count. If you do not pay iPro yourself yet, a reward
waits until you do (a refund needs a payment of yours to go back to).

## Get Help or Contact Support


Every page of the portal has a **Help** icon (a question mark) at the top right. It opens the guide for the page you are on, at the right section, in a new tab so you keep your place. Pages without a guide of their own open the full help library.
1. Select **Support** in the top navigation.
2. Browse **Help Documentation** for guides on clients, newsletters, the website builder, domains, and billing.
3. Under **My Tickets**, click **New Ticket** to describe an issue and submit it to Support.
4. Support replies appear in the same ticket thread; reply there to continue the conversation.
5. Mark a ticket **Resolved** once your issue is handled. Replying to a resolved or closed ticket automatically reopens it.

Support is available to every agent regardless of package.

