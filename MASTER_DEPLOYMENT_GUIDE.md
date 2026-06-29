# IPRO Modern — Master Deployment Guide
### From Zero to Live on Azure — Step by Step

---

## WHAT YOU HAVE

A complete modernised SaaS platform for insurance/financial advisers built on:
- **ASP.NET Core 8** (replaced WebForms)
- **EF Core 8 + MySQL** (replaced CRASharp ORM)
- **PayPal REST API v2** (replaced dead SOAP API)
- **SendGrid** (replaced direct SMTP)
- **Hangfire** (replaced Windows Service)
- **TinyMCE 6** (replaced FCKeditor)
- **HTML5 banners** (replaced Flash .swf)
- **Plesk REST API** (replaced SOAP XML)
- **Azure Blob Storage** (file uploads)
- **Security headers + rate limiting** (new — was not in original)
- **Docker + GitHub Actions CI/CD** (new)

---

## BEFORE YOU START — ACCOUNTS TO CREATE

Create all of these first (all free to start):

| Service | URL | Cost |
|---|---|---|
| Microsoft Azure | portal.azure.com | $200 free credit |
| GitHub | github.com | Free |
| SendGrid | sendgrid.com | Free (100 emails/day) |
| PayPal Developer | developer.paypal.com | Free (use existing PayPal account) |
| TinyMCE | tiny.cloud | Free (essential plan) |

---

## STEP 1 — INSTALL TOOLS ON YOUR COMPUTER

### Install .NET 8 SDK
- Go to: https://dotnet.microsoft.com/download/dotnet/8
- Download and run the installer for your OS (Windows/Mac)
- Verify: open a terminal and type `dotnet --version` — should show `8.x.x`

### Install Git
- Go to: https://git-scm.com/downloads
- Download and run the installer
- Verify: `git --version`

### Install Azure CLI
- **Windows:** Open PowerShell as Administrator and run:
  ```
  winget install Microsoft.AzureCLI
  ```
  Or download from: https://aka.ms/installazurecliwindows

- **Mac:** Open Terminal and run:
  ```
  brew install azure-cli
  ```

- Verify: close and reopen terminal, then type `az --version`

### Log in to Azure
```bash
az login
```
A browser window opens. Sign in with your Microsoft account. When done, return to terminal — you'll see your subscription listed.

---

## STEP 2 — CREATE AZURE RESOURCES

### Create a Resource Group
Think of this as a folder that holds everything for IPRO.
```bash
az group create --name ipro-rg --location canadacentral
```
> We use `canadacentral` (Toronto) for lowest latency since you are in Canada.

### Deploy All Infrastructure with One Command
This creates your App Service, MySQL database, and Storage Account automatically.

```bash
az deployment group create \
  --resource-group ipro-rg \
  --template-file infra/azure/main.bicep \
  --parameters \
    environment=prod \
    dbPassword="YourStrongDbPassword123!" \
    sendGridKey="SG.placeholder" \
    paypalClientId="placeholder" \
    paypalSecret="placeholder"
```

This takes about 5 minutes. When finished you will see output like:
```json
{
  "webAppUrl": "https://ipro-prod-web.azurewebsites.net",
  "dbHostName": "ipro-mysql-prod.mysql.database.azure.com",
  "storageAccountName": "iproprodstorage"
}
```
**Copy and save these values.**

### What Was Created
- **Azure App Service (B2)** — runs your web application (~$38/month)
- **Azure Database for MySQL** — your database (~$22/month)
- **Azure Blob Storage** — stores logos and uploaded files (~$3/month)
- **Total: ~$63 CAD/month** to start

---

## STEP 3 — SET UP SENDGRID (EMAIL)

1. Go to **https://sendgrid.com** and click Sign Up Free
2. Fill in your details and verify your email
3. Once logged in, go to **Settings → API Keys**
4. Click **Create API Key**
5. Name it: `IPRO Production`
6. Select **Full Access**
7. Click Create & View
8. **COPY THE KEY NOW** — it starts with `SG.` and you will only see it once
9. Store it safely (e.g. Notepad, password manager)

### Verify Your Sender Email
1. In SendGrid: **Settings → Sender Authentication**
2. Click **Verify a Single Sender**
3. Enter: `noreply@yourdomain.com` (use your actual domain)
4. Check your email inbox and click the verification link

---

## STEP 4 — SET UP PAYPAL

1. Go to **https://developer.paypal.com**
2. Log in with your existing PayPal business account
3. Click **My Apps & Credentials** in the top menu
4. Under **REST API apps**, click **Create App**
5. App Name: `IPRO Production`, Type: Merchant
6. Click **Create App**
7. You will see **Client ID** and **Secret** — copy both

### Create Subscription Plans (one per package you sell)
1. Go to **Catalog → Products → Create Product**
   - Name: e.g. "IPRO Basic"
   - Type: Service
   - Click Save
2. Go to **Subscriptions → Plans → Create Plan**
   - Select the product you just created
   - Billing cycle: Monthly, Price: your price (e.g. $49.00 CAD)
   - Click Activate
   - Copy the **Plan ID** (starts with `P-`)
3. Repeat for each package and for Annual billing

### Set Up Webhooks
1. In your app settings → **Webhooks → Add Webhook**
2. Webhook URL: `https://ipro-prod-web.azurewebsites.net/billing/webhook`
3. Tick these events:
   - `BILLING.SUBSCRIPTION.ACTIVATED`
   - `BILLING.SUBSCRIPTION.CANCELLED`
   - `BILLING.SUBSCRIPTION.EXPIRED`
   - `PAYMENT.SALE.COMPLETED`
   - `PAYMENT.SALE.DENIED`
4. Click Save. Copy the **Webhook ID**.

---

## STEP 5 — SET UP TINYMCE (RICH TEXT EDITOR)

1. Go to **https://tiny.cloud** and sign up free
2. Once logged in, go to **Dashboard**
3. Copy your **API Key** (a long string of letters and numbers)
4. Open this file in the project:
   `src/IPRO.Web/Views/Shared/_Layout.cshtml`
5. Find `YOUR_TINYMCE_KEY` and replace it with your key
6. Do the same in `Views/Newsletter/Create.cshtml` and `Views/Newsletter/Edit.cshtml`

---

## STEP 6 — GET YOUR PLESK API KEY

1. Log in to your Plesk control panel
2. Go to **Tools & Settings → REST API**
3. Click **Add Key**
4. Name: `IPRO Integration`
5. Copy the API key

---

## STEP 7 — ADD ALL SECRETS TO AZURE

Go to **https://portal.azure.com**
1. Search for `ipro-prod-web` in the top search bar
2. Click on it (it is an App Service)
3. In the left menu, click **Configuration**
4. Click **Application Settings**
5. Click **+ New application setting** for each of the following:

| Name | Value |
|---|---|
| `ConnectionStrings__DefaultConnection` | `server=ipro-mysql-prod.mysql.database.azure.com;port=3306;database=ipro_crm;user=iproadmin;password=YourStrongDbPassword123!;SslMode=Required;` |
| `Email__SendGridApiKey` | Your SendGrid key (starts with `SG.`) |
| `Email__FromEmail` | `noreply@yourdomain.com` |
| `Email__FromName` | `Your Company Name` |
| `PayPal__ClientId` | Your PayPal Client ID |
| `PayPal__ClientSecret` | Your PayPal Secret |
| `PayPal__IsSandbox` | `false` |
| `PayPal__WebhookId` | Your PayPal Webhook ID |
| `PayPal__ReturnUrl` | `https://yourdomain.com/billing/success` |
| `PayPal__CancelUrl` | `https://yourdomain.com/billing/cancel` |
| `Azure__StorageConnectionString` | Your Azure storage connection string (from portal → Storage Account → Access Keys) |
| `Azure__StorageAccountName` | `iproprodstorage` (or whatever was in the output from Step 2) |
| `Plesk__ApiUrl` | `https://your-plesk-server.com:8443` |
| `Plesk__ApiKey` | Your Plesk API key |
| `Admin__Username` | A username you choose for the admin panel |
| `Admin__Password` | A strong password you choose |
| `App__AdminDomain` | `admin.yourdomain.com` |

6. Click **Save** at the top. Click **Continue** when asked to restart.

---

## STEP 8 — UPLOAD CODE TO GITHUB

### Create a GitHub repository
1. Go to **https://github.com** and log in
2. Click the **+** button top right → **New repository**
3. Name: `ipro-modern`
4. Set to **Private**
5. Click **Create repository**
6. Copy the repository URL shown (e.g. `https://github.com/yourname/ipro-modern.git`)

### Push the code
Open a terminal, go to the IPRO_Modern folder, and run:
```bash
git init
git add .
git commit -m "IPRO Modern - complete modernised platform"
git branch -M main
git remote add origin https://github.com/YOURNAME/ipro-modern.git
git push -u origin main
```
It will ask for your GitHub username and password (use a Personal Access Token from GitHub Settings → Developer Settings → Personal Access Tokens).

---

## STEP 9 — CONNECT GITHUB TO AZURE FOR AUTO-DEPLOY

1. In Azure Portal → your App Service (`ipro-prod-web`)
2. Left menu → **Deployment Center**
3. Source: **GitHub**
4. Click **Authorize** and log in to GitHub
5. Organization: your GitHub username
6. Repository: `ipro-modern`
7. Branch: `main`
8. Click **Save**

Azure will now deploy automatically. The first deployment takes 3–5 minutes.

### Watch the deployment
```bash
az webapp log tail --name ipro-prod-web --resource-group ipro-rg
```

---

## STEP 10 — SET UP YOUR CUSTOM DOMAIN

### Add DNS records at your domain registrar (GoDaddy, Namecheap etc.)

| Type | Host | Value |
|---|---|---|
| CNAME | www | `ipro-prod-web.azurewebsites.net` |
| CNAME | admin | `ipro-prod-web.azurewebsites.net` |
| TXT | asuid | (shown in Azure Portal → Custom Domains) |

### Add the domain in Azure
```bash
az webapp config hostname add \
  --webapp-name ipro-prod-web \
  --resource-group ipro-rg \
  --hostname yourdomain.com
```

### Enable free SSL (HTTPS)
```bash
az webapp config ssl create \
  --name ipro-prod-web \
  --resource-group ipro-rg \
  --hostname yourdomain.com
```

DNS changes can take up to 48 hours to fully propagate worldwide.

---

## STEP 11 — TEST EVERYTHING

### 1. Agent Portal
- Open: `https://ipro-prod-web.azurewebsites.net`
- You should see the login page
- Go to `/Account/Register` to create your first agent account
- Log in — you should see the dashboard

### 2. Admin Panel
- Open: `https://ipro-prod-web.azurewebsites.net/Admin/Login`
- Use the `Admin__Username` and `Admin__Password` you set in Step 7
- You should see the admin dashboard with agent stats

### 3. Test Email (important)
- Create a client with a real email address
- Go to Newsletter → Create a newsletter
- Schedule it 2 minutes from now
- Wait 2 minutes and check your inbox

### 4. Test PayPal (use sandbox first)
- Temporarily set `PayPal__IsSandbox` to `true` in Azure app settings
- Go to Billing → choose a package → click Subscribe
- You will be redirected to PayPal sandbox
- Use a PayPal sandbox test buyer account to complete payment
- Return to the portal and check that your subscription shows Active
- Once confirmed, set `PayPal__IsSandbox` back to `false`

### 5. Test File Upload
- Go to My Website → upload a logo image
- It should save and show your logo

---

## STEP 12 — ADD YOUR FIRST PACKAGE

1. Log in to the Admin panel
2. Go to **Packages → Create**
3. Fill in:
   - Package Name: `Basic`
   - Monthly Price: your price
   - Annual Price: yearly price
   - Max Clients: 500
   - PayPal Monthly Plan ID: the `P-` ID from Step 4
4. Click Create
5. Repeat for each package you want to offer

---

## RUNNING LOCALLY FOR DEVELOPMENT

If you want to test changes on your computer before deploying:

### Option A — Docker (recommended, everything in one command)
```bash
cd IPRO_Modern/infra/docker
cp .env.example .env
# Open .env in Notepad/TextEdit and fill in your keys
docker-compose up -d
# Open browser: http://localhost:8080
```

### Option B — Visual Studio 2022
1. Install Visual Studio 2022 Community (free): https://visualstudio.microsoft.com
2. During install, select **ASP.NET and web development**
3. Open `IPRO.sln`
4. Open `src/IPRO.Web/appsettings.json` and fill in your values
5. Right-click `IPRO.Web` in Solution Explorer → **Set as Startup Project**
6. Press **F5** to run

---

## AZURE PLAN RECOMMENDATION

### Start with Basic B2

| Resource | Plan | CAD/month |
|---|---|---|
| App Service (B2) | 2 CPU, 3.5GB RAM | ~$38 |
| MySQL Flexible (B1ms) | 1 CPU, 2GB RAM | ~$22 |
| Blob Storage | Standard LRS | ~$3 |
| **Total to start** | | **~$63/month** |

### When to upgrade
- 50+ active agents → upgrade App Service to **Standard S2** (~$115/mo)
- 200+ agents → upgrade to **Premium P1v3** + add **Azure CDN**

---

## TROUBLESHOOTING

### App shows error on startup
```bash
az webapp log tail --name ipro-prod-web --resource-group ipro-rg
```
Look for red error messages. Most common causes:
- Wrong database connection string
- Missing app setting

### Database connection fails
- Check the connection string format exactly matches the template in Step 7
- Make sure `SslMode=Required` is at the end
- In Azure Portal → MySQL server → Networking → make sure "Allow Azure services" is ON

### Emails not arriving
- Check SendGrid dashboard → Activity → look for bounces or blocks
- Make sure the from-email is verified in SendGrid Sender Authentication
- Check spam folder

### PayPal subscription not activating
- Check PayPal Developer → Webhooks → Recent Deliveries — look for failed webhook calls
- Make sure webhook URL is exactly: `https://yourdomain.com/billing/webhook`
- Make sure IsSandbox matches (true for test, false for live)

### Can't see Plesk hosting button
- Make sure `Plesk__ApiUrl` and `Plesk__ApiKey` are set in Azure app settings
- The Plesk server must be reachable from the internet on port 8443

---

## QUICK REFERENCE URLS

| URL | Purpose |
|---|---|
| `https://yourdomain.com` | Agent portal |
| `https://yourdomain.com/Admin/Login` | Super admin panel |
| `https://yourdomain.com/hangfire` | Background job monitor |
| `https://yourdomain.com/health` | Health check endpoint |
| `https://portal.azure.com` | Azure management |
| `https://app.sendgrid.com` | Email delivery stats |
| `https://developer.paypal.com` | PayPal subscription management |

