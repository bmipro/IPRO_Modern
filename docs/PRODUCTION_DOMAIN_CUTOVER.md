# Production Domain Cutover Checklist

When IPRO moves from Azure default hostnames to the final production domain, update every external provider that calls back into the application.

## Current Development URLs

- Web app: `https://ipro-prod-web.azurewebsites.net`
- Admin app: `https://ipro-prod-admin-fhaydtemgeetbycm.canadaeast-01.azurewebsites.net`
- SendGrid newsletter event webhook: `https://ipro-prod-web.azurewebsites.net/Newsletter/SendGridEvents`
- PayPal billing webhook: `https://ipro-prod-web.azurewebsites.net/Billing/Webhook`

## Items To Change At Domain Cutover

- SendGrid Event Webhook URL:
  - From: `https://ipro-prod-web.azurewebsites.net/Newsletter/SendGridEvents`
  - To: `https://www.iproadvisers.com/Newsletter/SendGridEvents` or the final chosen web domain.
- PayPal Webhook URL:
  - From: `https://ipro-prod-web.azurewebsites.net/Billing/Webhook`
  - To: `https://www.iproadvisers.com/Billing/Webhook` or the final chosen web domain.
- Azure App Service settings:
  - `PayPal__WebhookId`
  - `PayPal__ReturnUrl`
  - `PayPal__CancelUrl`
  - `App__AdminDomain`
  - Any app setting that still references `azurewebsites.net`.
- PayPal developer app:
  - Confirm sandbox/live mode.
  - Confirm webhook URL.
  - Confirm return and cancel URLs.
  - Confirm active package plan IDs still match the intended PayPal environment.
- SendGrid:
  - Verify the final sender domain.
  - Verify the final billing sender, likely `billing@iproadvisers.com`.
  - Verify the final no-reply/reply-to address, likely `no-reply@iproadvisers.com`.
  - Confirm Open Tracking and Event Webhook events are still enabled.
- Azure custom domains and SSL:
  - Web domain.
  - Admin domain.
  - Any future agent wildcard or custom-domain routing.

## Smoke Tests After Cutover

- Register a new agent and receive the welcome email.
- Send a newsletter to a test client and confirm delivered/opened events reach IPRO.
- Complete a PayPal sandbox or live test payment, depending on environment.
- Confirm PayPal webhook activates the subscription and creates invoices.
- Confirm invoice email delivery.
- Confirm admin login works on the final admin domain.
