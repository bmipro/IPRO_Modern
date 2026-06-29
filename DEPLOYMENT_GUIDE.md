# IPRO Modern — Complete Deployment Guide
**Target: Azure App Service + Azure Database for MySQL + Azure Blob Storage**

---

## What You'll Need Before Starting

- An **Azure account** (free trial at portal.azure.com — $200 free credit)
- A **GitHub account** (free at github.com)
- A **SendGrid account** (free tier = 100 emails/day — sendgrid.com)
- A **PayPal Developer account** (free — developer.paypal.com)
- **Git** installed on your computer (git-scm.com)
- **Visual Studio 2022** or **VS Code** (optional but helpful)

Estimated time: **45–60 minutes** for first deployment.

---

## PART 1 — Set Up Your Azure Account

### Step 1.1 — Create a free Azure account
1. Go to **https://portal.azure.com**
2. Click "Start free" — you get $200 credit for 30 days
3. Sign in with your Microsoft account (or create one)

### Step 1.2 — Install Azure CLI on your computer
This lets you deploy from your terminal.

**Windows:**
```
winget install Microsoft.AzureCLI
```
Or download from: https://aka.ms/installazurecliwindows

**Mac:**
```
brew install azure-cli
```

**Then log in:**
```bash
az login
```
A browser window will open — sign in with your Azure account.

---

## PART 2 — Create Azure Resources (One Command)

We've pre-built infrastructure-as-code for you using Azure Bicep.

### Step 2.1 — Create a Resource Group
A resource group is like a folder that holds all your IPRO resources.

```bash
az group create --name ipro-production --location canadacentral
```

> We recommend `canadacentral` since you're in Toronto — lowest latency.

### Step 2.2 — Deploy All Azure Resources at Once

This single command creates your App Service, MySQL database, and Storage account:

```bash
az deployment group create \
  --resource-group ipro-production \
  --template-file infra/azure/main.bicep \
  --parameters \
    environment=prod \
    dbPassword="Gbs2iProGbs2" \
    sendGridKey: "SG.REDACTED" \
    paypalClientId="AceWhYutuCQZCgjzyjQPEVdRocxuegPxYMaakzKqgc_7DLg7Tu_TQmD3ev-9guh9oBE8jOJJ8eLlQEAp" \
    paypalSecret="EPUl-PqRZ7-dZaTMqxEqX_5W8uZqdTixZVWtjrGLAFk7J_r_4mxdz-pjH2cWfwnSIXm-SFBE-0pvZDMq"
```

**It takes about 5 minutes.** When done, you'll see output like:
```json
{
  "webAppUrl": "https://ipro-prod-web.azurewebsites.net",
  "dbHostName": "ipro-mysql-prod.mysql.database.azure.com",
  "storageAccountName": "iproprodstorage"
}
```
**Save these values — you'll need them.**

---

## PART 3 — Set Up SendGrid (Email)

### Step 3.1 — Create a SendGrid account
1. Go to **https://sendgrid.com** → Sign up free
2. Verify your account via email

### Step 3.2 — Get your API Key
1. In SendGrid dashboard → Settings → API Keys
2. Click **"Create API Key"**
3. Name it "IPRO Production"
4. Choose **"Full Access"**
5. Copy the key (starts with `SG.`) — **you only see it once!**

### Step 3.3 — Verify your sender email
1. Settings → Sender Authentication → Single Sender Verification
2. Add your email address (e.g. noreply@yourdomain.com)
3. Click the verification link in your inbox

---

## PART 4 — Set Up PayPal

### Step 4.1 — Create a PayPal Developer account
1. Go to **https://developer.paypal.com**
2. Log in with your PayPal business account

### Step 4.2 — Create an App
1. Dashboard → My Apps & Credentials
2. Click **"Create App"** under REST API apps
3. Name it "IPRO Production"
4. Copy the **Client ID** and **Client Secret**

### Step 4.3 — Create Subscription Plans
For each package you offer, you'll create a PayPal plan:

1. Go to Catalog → Products → Create Product
   - Name: "IPRO Basic Package"
   - Type: Service
2. Go to Subscriptions → Plans → Create Plan
   - Select your product
   - Set billing cycle (Monthly / Annual)
   - Set price
3. Copy the **Plan ID** (starts with `P-`)

### Step 4.4 — Set Up Webhooks
1. Dashboard → My Apps → your app → Webhooks
2. Add webhook URL: `https://ipro-prod-web.azurewebsites.net/billing/webhook`
3. Subscribe to these events:
   - `BILLING.SUBSCRIPTION.ACTIVATED`
   - `BILLING.SUBSCRIPTION.CANCELLED`
   - `PAYMENT.SALE.COMPLETED`
   - `PAYMENT.SALE.DENIED`
4. Copy the **Webhook ID**

---

## PART 5 — Configure Your Application Settings

In the Azure Portal:
1. Go to **portal.azure.com**
2. Search for "ipro-prod-web" → App Service
3. Click **Configuration** → Application Settings
4. Add/verify these settings:

| Setting Name | Value |
|---|---|
| `Email__SendGridApiKey` | Your SendGrid API key |
| `Email__FromEmail` | noreply@yourdomain.com |
| `PayPal__ClientId` | Your PayPal Client ID |
| `PayPal__ClientSecret` | Your PayPal Client Secret |
| `PayPal__IsSandbox` | `false` (for production) |
| `PayPal__WebhookId` | Your PayPal Webhook ID |
| `PayPal__ReturnUrl` | `https://yourdomain.com/billing/success` |
| `PayPal__CancelUrl` | `https://yourdomain.com/billing/cancel` |
| `App__AdminDomain` | `admin.yourdomain.com` |

Click **Save** after adding all settings.

---

## PART 6 — Deploy the Code

### Step 6.1 — Push code to GitHub
```bash
# In the IPRO_Modern folder:
git init
git add .
git commit -m "Initial IPRO Modern deployment"
git branch -M main
git remote add origin https://github.com/YOURUSERNAME/ipro-modern.git
git push -u origin main
```

### Step 6.2 — Link GitHub to Azure (one-time setup)
1. In Azure Portal → your App Service → Deployment Center
2. Source: **GitHub**
3. Authorize Azure to access your GitHub
4. Select your repository and `main` branch
5. Click **Save**

Azure will now auto-deploy every time you push to `main`. 🎉

### Step 6.3 — First deployment
The first deployment takes about 3–5 minutes.

To watch the progress:
```bash
az webapp log tail --name ipro-prod-web --resource-group ipro-production
```

Or in Azure Portal → App Service → Log stream.

---

## PART 7 — Set Up Your Custom Domain

### Step 7.1 — Add your domain to Azure
```bash
az webapp config hostname add \
  --webapp-name ipro-prod-web \
  --resource-group ipro-production \
  --hostname yourdomain.com
```

### Step 7.2 — Configure DNS (at your domain registrar)
Add these DNS records at wherever you bought your domain (GoDaddy, Namecheap, etc.):

| Type | Name | Value |
|---|---|---|
| CNAME | www | ipro-prod-web.azurewebsites.net |
| A | @ | (Azure IP — shown in portal) |
| TXT | asuid | (verification code — shown in portal) |

### Step 7.3 — Enable free SSL certificate
```bash
az webapp config ssl create \
  --name ipro-prod-web \
  --resource-group ipro-production \
  --hostname yourdomain.com
```

---

## PART 8 — Test Your Deployment

### Step 8.1 — Open the app
Go to: `https://ipro-prod-web.azurewebsites.net`

You should see the **IPRO Agent Portal login page**.

### Step 8.2 — Create your first admin user
For now, register via the registration page, then we can lock registration down later.

1. Go to `/Account/Register`
2. Create your account
3. Log in → you'll see the dashboard

### Step 8.3 — Test email
1. Go to Newsletter → Create a newsletter
2. Schedule it for 1 minute from now
3. Wait and check your inbox

### Step 8.4 — Test PayPal (sandbox first)
1. Keep `PayPal__IsSandbox` = `true` for testing
2. Go to Billing → choose a package → Subscribe
3. You'll be redirected to PayPal sandbox
4. Use PayPal sandbox test credentials to complete payment

---

## PART 9 — Running Locally (for development)

If you want to test changes on your own computer before deploying:

### Prerequisites
- .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8
- Docker Desktop: https://docker.com/products/docker-desktop

### Option A — Docker (easiest)
```bash
cd IPRO_Modern/infra/docker

# Copy and fill in your secrets
cp .env.example .env
# Edit .env with your SendGrid/PayPal keys

# Start everything
docker-compose up -d

# App runs at: http://localhost:8080
```

### Option B — Visual Studio
1. Open `IPRO.sln` in Visual Studio 2022
2. Right-click `IPRO.Web` → Set as Startup Project
3. Update `appsettings.json` with your local database connection
4. Press **F5** to run

---

## PART 10 — Setting Up Per-Agent Domains

Each agent can have their own custom domain pointing to their IPRO website.

### How it works
When a visitor goes to `johnadvisor.com`, Azure routes them to your app, which reads the domain, looks up which agent owns it, and serves their website. This is handled automatically by `DomainTenantResolver.cs`.

### For each agent's domain
The agent needs to add a CNAME at their domain registrar:
```
CNAME  @  ipro-prod-web.azurewebsites.net
```

Then in the Azure Portal → App Service → Custom Domains → Add Custom Domain → enter the agent's domain.

> **Note:** This can be automated via Azure Management API. We'll add this in Phase 2.

---

## Troubleshooting

### App won't start
Check logs: `az webapp log tail --name ipro-prod-web --resource-group ipro-production`

### Database connection error
- Make sure the MySQL firewall rule allows Azure services (done in Bicep)
- Double-check the connection string in App Settings
- Connection string format: `server=HOSTNAME;port=3306;database=ipro_crm;user=iproadmin;password=PASS;SslMode=Required;`

### Emails not sending
- Check SendGrid dashboard for delivery logs
- Make sure sender email is verified in SendGrid
- Check the API key has Full Access

### PayPal not working
- Make sure `IsSandbox=false` for production
- Webhook URL must be HTTPS and publicly accessible
- Check PayPal developer dashboard for webhook delivery logs

---

## Cost Estimate (Azure, Toronto region, CAD)

| Resource | Tier | Monthly Cost |
|---|---|---|
| App Service (B2) | Basic | ~$25/mo |
| MySQL Flexible (B1ms) | Burstable | ~$20/mo |
| Blob Storage | Standard LRS | ~$2/mo |
| Bandwidth | First 5GB free | ~$2/mo |
| **Total** | | **~$49/mo CAD** |

> You can scale up/down anytime in the Azure Portal.
> The B2 App Service handles ~50–100 concurrent agents comfortably.

---

## Next Steps After Deployment

1. ✅ Lock down the registration page (require invite code or admin approval)
2. ✅ Add the Admin portal (`IPRO.Admin`) for super-admin management
3. ✅ Set up Azure DNS automation for agent domain provisioning
4. ✅ Add monitoring with Azure Application Insights (free tier available)
5. ✅ Configure daily database backups (already set to 7-day retention in Bicep)

---

*Generated for IPRO Modern — ASP.NET Core 8 on Azure*
*Need help? Every step above can be done together — just share what you see and we'll fix it.*
