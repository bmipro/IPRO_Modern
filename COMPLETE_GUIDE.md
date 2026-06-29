# IPRO Modern — Complete Project Guide
### Cloud Recommendation · What's Built · Installation Steps

---

## PART A — WHAT WAS BUILT (Complete Summary)

### Project Structure

```
IPRO_Modern/
├── IPRO.sln                          ← Open this in Visual Studio
├── src/
│   ├── IPRO.Entities/                ← 21 data models (Agent, Client, Billing…)
│   ├── IPRO.DataAccess/              ← EF Core 8, Repository, Unit of Work
│   ├── IPRO.Business/                ← Agent, Client, Newsletter, Website services
│   ├── IPRO.Email/                   ← SendGrid email + newsletter dispatcher
│   ├── IPRO.Billing/                 ← PayPal REST API v2 subscriptions + webhooks
│   ├── IPRO.Scheduler/               ← Hangfire background jobs (newsletters, drip, reminders)
│   ├── IPRO.Utility/                 ← Blob storage, Plesk REST, CSV importer, encryption
│   ├── IPRO.Web/                     ← Main agent portal (login, dashboard, clients, website, billing, newsletter)
│   └── IPRO.Admin/                   ← Super-admin panel (agents, packages, reports, Plesk provisioning)
├── infra/
│   ├── azure/main.bicep              ← One-command Azure deployment
│   └── docker/                       ← Dockerfile + docker-compose for local dev
└── .github/workflows/                ← CI/CD: auto-deploy to Azure on git push
```

### Every Upgrade Made

| Old (2010) | New (2026) | Status |
|---|---|---|
| ASP.NET WebForms | ASP.NET Core 8 MVC | ✅ |
| CRASharp custom ORM | EF Core 8 + Repository pattern | ✅ |
| PayPal SOAP API v60 | PayPal REST API v2 | ✅ |
| Windows Service scheduler | Hangfire (cloud-native) | ✅ |
| FCKeditor 2.2 | TinyMCE 6 | ✅ |
| Flash banners (.swf) | HTML5 banner slider + video | ✅ |
| Direct SMTP email | SendGrid API | ✅ |
| Plesk SOAP XML API | Plesk REST API | ✅ |
| Outlook 2003 importer | CSV + vCard modern importer | ✅ |
| web.config | appsettings.json + env vars | ✅ |
| System.Web dependencies | Pure ASP.NET Core | ✅ |
| ViewState | Model binding | ✅ |
| No security headers | Full CSP, HSTS, X-Frame headers | ✅ |
| No rate limiting | IP rate limiting on login | ✅ |
| Manual IIS deployment | Docker + GitHub Actions CI/CD | ✅ |
| Local only | Azure cloud-ready | ✅ |

---

## PART B — CLOUD PLATFORM RECOMMENDATION

### My Recommendation: Microsoft Azure ⭐

**Why Azure for IPRO specifically:**

1. **Best .NET support** — Azure was built alongside .NET. Deployment is seamless.
2. **Custom domain per agent** — Azure App Service handles wildcard domains perfectly.
3. **Azure Database for MySQL** — Drop-in replacement for your existing MySQL, fully managed, auto-backups.
4. **Azure Blob Storage** — Perfect for agent logos, images, documents.
5. **You're in Canada** — `Canada Central` region (Toronto) means lowest latency for your agents.
6. **Cost** — Cheapest option for .NET apps vs AWS or GCP.

### Azure vs AWS vs GCP Comparison

| | Azure | AWS | GCP |
|---|---|---|---|
| Best for .NET | ⭐⭐⭐ | ⭐⭐ | ⭐ |
| MySQL managed | ✅ Flexible Server | ✅ RDS | ✅ Cloud SQL |
| Canada region | ✅ Canada Central | ✅ ca-central-1 | ⚠️ Limited |
| Monthly cost (this app) | ~$49 CAD | ~$65 CAD | ~$60 CAD |
| Easiest for .NET team | ✅ | Medium | Hard |
| Free tier | $200 credit | $300 credit | $300 credit |

**Winner: Azure**, especially since your original app was Windows/.NET — the ecosystem fits perfectly.

---

## PART C — WHAT AZURE PLAN TO GET

### Recommended: Azure App Service — Basic B2

| Plan | vCPUs | RAM | Cost/mo (CAD) | Good for |
|---|---|---|---|---|
| Free F1 | Shared | 1 GB | $0 | Testing only — no custom domain |
| Basic B1 | 1 | 1.75 GB | ~$20 | Very small load (<10 agents) |
| **Basic B2** ⭐ | **2** | **3.5 GB** | **~$38** | **50–100 agents — START HERE** |
| Standard S2 | 2 | 3.5 GB | ~$115 | 100–500 agents, auto-scale |
| Premium P1v3 | 2 | 8 GB | ~$185 | 500+ agents, high traffic |

### Full Monthly Cost Breakdown (B2 — recommended start)

| Resource | Plan | CAD/month |
|---|---|---|
| App Service (Web portal) | Basic B2 | $38 |
| App Service (Admin portal) | Basic B1 | $20 |
| Azure Database for MySQL | Burstable B1ms | $22 |
| Azure Blob Storage | Standard LRS | $3 |
| Bandwidth (first 5GB free) | Pay as you go | ~$2 |
| **Total** | | **~$85/month CAD** |

> You can start on **Basic B1 (~$49/mo total)** and upgrade anytime with zero downtime.

### Scale-up trigger points
- More than 50 active agents → upgrade to Standard S2
- More than 200 agents → move to Premium P1v3 + consider Azure CDN
- More than 500 agents → add Azure Redis Cache and load balancer

---

## PART D — WHAT ELSE YOU NEED (External Services)

| Service | What For | Cost | Sign Up |
|---|---|---|---|
| **SendGrid** | All emails — newsletters, drip, notifications | Free (100/day), $20/mo (50K emails) | sendgrid.com |
| **PayPal Business** | Agent subscription payments | 2.9% + $0.30 per transaction | paypal.com |
| **TinyMCE** | Rich text editor in newsletter builder | Free (essential plan) | tiny.cloud |
| **Plesk** | Agent hosting management (keep your existing server) | You already have this | — |
| **GitHub** | Code storage + CI/CD pipeline | Free | github.com |
| **Domain registrar** | Your main domain (iprosystem.com etc.) | ~$15/yr | namecheap.com |

---

## PART E — STEP-BY-STEP INSTALLATION

### Prerequisites — Install These First

1. **.NET 8 SDK** — https://dotnet.microsoft.com/download/dotnet/8
   - Windows: run the installer
   - Mac: `brew install dotnet@8`

2. **Git** — https://git-scm.com/downloads
   - Windows: run the installer
   - Mac: `brew install git`

3. **Azure CLI** — https://docs.microsoft.com/cli/azure/install-azure-cli
   - Windows: `winget install Microsoft.AzureCLI`
   - Mac: `brew install azure-cli`

4. **Visual Studio 2022** (optional but recommended for Windows)
   - Community edition is free: visualstudio.microsoft.com
   - Make sure to select "ASP.NET and web development" workload

5. **Docker Desktop** (for local testing) — https://docker.com/products/docker-desktop

---

### Step 1 — Set Up Azure (15 minutes)

```bash
# Log in to Azure
az login
# A browser window opens — sign in with your Microsoft account

# Create a resource group (folder for all IPRO resources)
az group create --name ipro-rg --location canadacentral
```

---

### Step 2 — Deploy Cloud Infrastructure (5 minutes)

This single command creates everything: App Service, MySQL, Storage Account.

```bash
cd IPRO_Modern

az deployment group create \
  --resource-group ipro-rg \
  --template-file infra/azure/main.bicep \
  --parameters \
    environment=prod \
    dbPassword="YourStrongPassword123!" \
    sendGridKey="SG.your_sendgrid_key" \
    paypalClientId="your_paypal_client_id" \
    paypalSecret="your_paypal_secret"
```

When done (about 5 minutes) you'll see:
```
"webAppUrl": "https://ipro-prod-web.azurewebsites.net"
"dbHostName":  "ipro-mysql-prod.mysql.database.azure.com"
```
**Save these — you'll need them.**

---

### Step 3 — Set Up SendGrid (10 minutes)

1. Go to **sendgrid.com** → Sign up free
2. Verify your email
3. Settings → API Keys → Create API Key → Full Access
4. Copy the key (starts with `SG.`) — you only see it once!
5. Settings → Sender Authentication → verify your from-address

---

### Step 4 — Set Up PayPal Developer (15 minutes)

1. Go to **developer.paypal.com** → log in with your PayPal business account
2. My Apps → Create App → name it "IPRO"
3. Copy **Client ID** and **Client Secret**
4. Go to Catalog → Products → Create a product (Service type)
5. Subscriptions → Plans → Create a plan for each package you sell
6. Copy each **Plan ID** (starts with `P-`)
7. Webhooks → Add webhook URL: `https://ipro-prod-web.azurewebsites.net/billing/webhook`
8. Subscribe to: `BILLING.SUBSCRIPTION.ACTIVATED`, `BILLING.SUBSCRIPTION.CANCELLED`, `PAYMENT.SALE.COMPLETED`
9. Copy the **Webhook ID**

---

### Step 5 — Configure App Settings in Azure (5 minutes)

Go to portal.azure.com → App Services → `ipro-prod-web` → Configuration → Application Settings

Add these values:

```
Email__SendGridApiKey          = SG.your_key_here
Email__FromEmail               = noreply@yourdomain.com
PayPal__ClientId               = your_paypal_client_id
PayPal__ClientSecret           = your_paypal_secret
PayPal__IsSandbox              = false
PayPal__WebhookId              = your_webhook_id
PayPal__ReturnUrl              = https://yourdomain.com/billing/success
PayPal__CancelUrl              = https://yourdomain.com/billing/cancel
App__AdminDomain               = admin.yourdomain.com
Admin__Username                = your_admin_username
Admin__Password                = your_strong_admin_password
```

Click **Save**.

---

### Step 6 — Deploy the Code (5 minutes)

```bash
# Inside the IPRO_Modern folder:
git init
git add .
git commit -m "IPRO Modern — initial deployment"
git branch -M main
git remote add origin https://github.com/YOURUSERNAME/ipro-modern.git
git push -u origin main
```

Then in Azure Portal → `ipro-prod-web` → Deployment Center:
- Source: GitHub
- Authorize → select your repo → branch: main
- Save

Azure will auto-deploy now and on every future code push. ✅

---

### Step 7 — Set Up Your Domain (10 minutes)

**At your domain registrar** (GoDaddy, Namecheap, etc.), add:

| Type | Name | Value |
|---|---|---|
| CNAME | www | ipro-prod-web.azurewebsites.net |
| CNAME | admin | ipro-prod-web.azurewebsites.net |

**In Azure Portal → App Service → Custom Domains:**
```bash
az webapp config hostname add \
  --webapp-name ipro-prod-web \
  --resource-group ipro-rg \
  --hostname yourdomain.com
```

**Enable free SSL:**
```bash
az webapp config ssl create \
  --name ipro-prod-web \
  --resource-group ipro-rg \
  --hostname yourdomain.com
```

---

### Step 8 — First Login & Test (10 minutes)

1. Open `https://ipro-prod-web.azurewebsites.net`
2. You should see the **IPRO Agent Portal login page**
3. Go to `/Account/Register` to create your first agent account
4. Log in → you'll see the dashboard

**Test Admin Panel:**
- Go to `https://ipro-prod-web.azurewebsites.net/Admin/Login`
- Use the username/password you set in Step 5 (`Admin__Username` / `Admin__Password`)

**Test PayPal (sandbox mode first):**
- Set `PayPal__IsSandbox = true` temporarily
- Register as an agent → Billing → choose a package → Subscribe
- Complete with PayPal sandbox test account
- Check that subscription shows Active in your dashboard

**Test Newsletter:**
- Add a few clients with newsletter subscription enabled
- Newsletter → Create → write content → Schedule for 2 minutes from now
- Wait and check inbox

---

### Step 9 — Run Locally (for development)

**Option A — Docker (easiest, no setup needed):**
```bash
cd IPRO_Modern/infra/docker
cp .env.example .env
# Edit .env with your keys
docker-compose up -d
# Visit http://localhost:8080
```

**Option B — Visual Studio:**
1. Open `IPRO.sln`
2. Right-click `IPRO.Web` → Set as Startup Project
3. Update `appsettings.json` with your local MySQL connection
4. Press F5

---

## PART F — WHAT'S LEFT FOR FUTURE PHASES

The core platform is complete and deployable. These items are the next logical phase:

| Feature | Priority | Notes |
|---|---|---|
| Agent public website templates | High | The public-facing sites agents serve to their clients |
| Google Analytics per agent | Medium | Track visitor stats on each agent's site |
| Azure Application Insights | Medium | Error monitoring and performance tracking |
| Two-factor authentication (2FA) | Medium | Add TOTP for agent and admin login |
| Coupon/discount code UI | Medium | Backend is built, needs front-end |
| Drip campaign builder UI | Medium | Backend is built, needs front-end |
| Calendar/event manager UI | Low | Backend is built, needs front-end |
| Agent testimonials UI | Low | Backend is built, needs front-end |
| Automated domain provisioning | Low | Auto-add Azure DNS when agent sets domain |
| Mobile app | Low | React Native using the same backend |

---

## PART G — QUICK REFERENCE

| URL | What it is |
|---|---|
| `https://yourdomain.com` | Agent portal login |
| `https://yourdomain.com/Admin/Login` | Super admin panel |
| `https://yourdomain.com/hangfire` | Background job monitor |
| `https://portal.azure.com` | Azure management |
| `https://app.sendgrid.com` | Email delivery stats |
| `https://developer.paypal.com` | PayPal subscription management |

---

*Built with ❤️ — ASP.NET Core 8 · EF Core 8 · Azure · SendGrid · PayPal REST API v2*
