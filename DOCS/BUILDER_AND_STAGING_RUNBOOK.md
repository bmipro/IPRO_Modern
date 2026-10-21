# Runbook: the builder for customers and a staging copy of iPro (TODO 585)

**Your decision of 2026-10-17: "Builder + staging together."** One new server holds the builder your
customers use and a staging copy of iPro, where every release is checked before it reaches production.

You do every step in the Azure portal and paste every secret yourself. I check what you set by name and
length only (`ops\Check-AppSettings.ps1`), never by value, and I change nothing in Azure or GitHub
without your word. Nothing here touches production iPro until Part E, which is a release of its own.

## What it is

| Piece | Name | Address | What for |
|---|---|---|---|
| Server (App Service plan) | `ipro-builder-staging-plan` | | Basic B2, Linux, Canada East: the four apps below |
| Builder for customers | `ipro-builder` | builder.iproadvisers.com | what advisers open from My Website |
| Staging builder | `ipro-builder-staging` | ipro-builder-staging.azurewebsites.net | what staging iPro opens |
| Staging portal | `ipro-staging-web` | ipro-staging-web.azurewebsites.net | iPro's portal, as a test copy |
| Staging SuperAdmin | `ipro-staging-admin` | ipro-staging-admin.azurewebsites.net | SuperAdmin, as a test copy |
| Staging database | `ipro-mysql-staging` | | its own MySQL server, never production's |
| Staging storage | `iprostoragestaging` | | its own pictures and files |
| Builder's drafts | database `ipro_builder` | on production's MySQL server | advisers' drafts, with its own login |

**Where the advisers' drafts live:** in a database of their own on production's MySQL server, under a
login that can open that database and nothing else. They get production's backups (7 days, point in
time), stay in Canada, and cost nothing extra. The staging builder uses the staging server instead.

**What it costs (US$ list, Canada East):** the server $25.55 a month; the staging database $16.04
($13.51 + $2.53 storage), or about $2.53 while it is stopped (it can be stopped for up to 7 days at a
time and starts again by itself); storage a few cents. **About $42 a month, about $28 with the staging
database stopped when not in use.** The builder's database on production's server adds nothing.

**How releases work afterwards:** a push to main deploys **staging** (both apps), never production. You
check staging. Production is released from GitHub's Actions tab, "Run workflow", with the commit's SHA:
it refuses a commit staging is not serving, and waits for your approval in GitHub before it deploys.
The builder works the same way in its own repository.

**Three deployment identities, not one** (step A7): staging's can deploy the staging apps and nothing
else; production iPro's works only from GitHub's "production" environment, which asks you first; the
production builder's can deploy `ipro-builder` and nothing else. So a push to iPro's main, or a change
to one of its workflow files, cannot put anything on production iPro without your approval in GitHub
(once SuperAdmin's deploy secret is moved into that environment too, step A7d). **The builder is the
exception:** its repository is private, and GitHub's free plan has no approvals for private
repositories, so whoever can push to the builder's main branch could also deploy the production builder
by changing a workflow. Today that is only you; keep it so (step A7b).

**What keeps staging away from real people and real money** (built into iPro, and checked at start-up:
a staging copy set up wrongly does not start, and says why):

- it emails only the addresses in `Email__OnlyTo` (every other email is written to its log, not sent);
- PayPal is the sandbox only;
- it never touches customers' domains;
- no Application Insights (none of its settings, under any of their names);
- its links lead to staging, never production (`App__BaseUrl` and `Signups__AdminBaseUrl` must be
  staging's own), and it refuses production's database server and storage by name;
- search engines are told to stay away, and every screen says STAGING;
- its data starts empty: the schema and the starter content are made at its first start, and no
  production data is ever copied in.

## Part A: create the pieces (Azure portal)

1. **Resource group:** create `ipro-builder-staging`, region Canada East.
2. **App Service plan:** create `ipro-builder-staging-plan` in that group: Linux, Canada East, **Basic B2**.
3. **Four Web Apps** on that plan, each: Publish **Code**, Runtime stack **.NET 8 (LTS)**, Linux,
   Canada East, and on the Monitoring tab **Application Insights: No**. Names: `ipro-builder`,
   `ipro-builder-staging`, `ipro-staging-web`, `ipro-staging-admin`. (If a name is taken, add `-ca` and
   tell me the name you used.) Then in each app, **Configuration > General settings**: Always on **On**,
   HTTPS only **On**, Minimum TLS **1.2**, FTP **Disabled**; **Health check**: path `/health`.
4. **Staging MySQL:** Azure Database for MySQL flexible server `ipro-mysql-staging`, group
   `ipro-builder-staging`, Canada East, **MySQL 8.4** (recommended; 8.0 is also offered), **Burstable
   B1ms**, 20 GB, backups 7 days, no high availability. Networking: public access, and tick **Allow
   public access from any Azure service** (as production has). Choose its admin name and password and
   keep them in your password manager. Then **Databases > Add**: `ipro_staging` and `ipro_builder`
   (both utf8mb4).

   Why 8.4: production's server is on 8.0, whose standard support at Azure ends on 2026-12-31, and it is
   to be upgraded to 8.4 (TODO 590). A staging server on 8.4 from the start runs iPro and the builder on
   8.4 before production moves. Nothing in either needs 8.0: iPro's portal and SuperAdmin ask the server
   its version when they start; the builder writes SQL of 8.0's kind, which 8.4 accepts; and every
   connection to Azure's MySQL uses TLS (`SslMode=Required`, as in B), which 8.4's default way of
   checking passwords (`caching_sha2_password`) needs. A server keeps the version it is created with
   (Azure upgrades go forward only), so choose 8.4 here rather than upgrade later. (The local MySQL that
   the bench and every local gate use is 8.0.44: staging is the first place iPro runs on 8.4.)
5. **Staging storage:** storage account `iprostoragestaging`, Standard, LRS, Canada East, with the same
   **Allow Blob anonymous access** setting as `iprostorageprod` (iPro serves website pictures publicly).
6. **The builder's database on production's server:** on `ipro-mysql-prod`, **Databases > Add**
   `ipro_builder` (utf8mb4). Then its own login: open **Connect** (or your MySQL tool), sign in as the
   server's admin, and run, typing a NEW password of your own where it says so:

   ```
   CREATE USER 'ipro_builder'@'%' IDENTIFIED BY 'your new password';
   GRANT ALL PRIVILEGES ON `ipro_builder`.* TO 'ipro_builder'@'%';
   ```

   Do the same on `ipro-mysql-staging` (another new password). The builder makes its own tables at its
   first start.
7. **Deployment rights: three identities.** Each is an app registration (Microsoft Entra ID > App
   registrations > New registration; no secret is ever made for them: GitHub signs in with a
   federated credential). Rights are given **on each app**, not on the resource group (the app's
   Access control (IAM) > Add role assignment > **Website Contributor**).

   a. **Staging** (`ipro-deploy-staging`, new): Website Contributor on `ipro-staging-web`,
      `ipro-staging-admin` and `ipro-builder-staging`, and nothing else. Federated credentials
      (Certificates & secrets > Federated credentials > Add > GitHub Actions): organization `bmipro`,
      repository `IPRO_Modern`, entity **Branch**, branch `main`; and a second one for repository
      `IPRO.TemplateBuilder`, entity **Branch**, branch `main`.
   b. **Production builder** (`ipro-deploy-builder`, new): Website Contributor on `ipro-builder` only.
      Federated credential: repository `IPRO.TemplateBuilder`, entity **Branch**, branch `main`. (That
      repository is private, where GitHub's free plan has no environments; its production workflow runs
      only when you start it, and only for a commit the staging builder serves. Check in its GitHub
      settings, Collaborators, that nobody but you can write to it.)
   c. **Production iPro** (the existing app registration behind `AZUREAPPSERVICE_CLIENTID_...`): add a
      federated credential for repository `IPRO_Modern`, entity **Environment**, environment
      `production`. In GitHub, `IPRO_Modern` > Settings > Environments > New environment `production`,
      tick **Required reviewers** and add yourself. Its rights stay as they are (production's apps; it
      gets nothing on the new group). **Do this before the first production release** (Part E): the
      release workflows now run in that environment, and without this credential Azure refuses them.
      After that first release has worked, delete this identity's old credential for entity
      **Branch** `main`, so that nothing started by a push can get production's token.
   d. **SuperAdmin's deploy secret.** Production SuperAdmin is deployed with a publish profile (the
      secret `AZUREAPPSERVICE_PUBLISHPROFILE_IPRO_PROD_ADMIN`), not with the identity above. Today it
      is a repository secret, which any workflow can read. Move it into the environment: in
      `IPRO_Modern` > Settings > Environments > `production` > **Add environment secret**, the same
      name, the value from `ipro-prod-admin` > Overview > **Download publish profile**; then delete the
      repository secret of that name (Settings > Secrets and variables > Actions). From then on only
      the approved production job can read it.

   Then the Actions secrets (IDs, not passwords): in `IPRO_Modern`, `AZURE_STAGING_CLIENT_ID` (the
   Application (client) ID of a.); in `IPRO.TemplateBuilder`, `AZURE_STAGING_CLIENT_ID` (a.),
   `AZURE_PRODUCTION_CLIENT_ID` (b.), `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID`.
8. **Sign-in in front of staging:** on `ipro-staging-admin` and on `ipro-staging-web`, **Authentication >
   Add identity provider > Microsoft**, as you did for production's SuperAdmin on 2026-09-12
   (DOCS/14 and TODO 482: require authentication, only you assigned). Then tell me, and on your word I
   exclude the addresses that must stay open: `/health,/health/version` on both, and `/builder-api` on
   `ipro-staging-web` (the staging builder's server talks to it; without the exclusion every opening of
   the staging builder fails with "iPro's answer could not be read"). The two builders need no such
   step: nobody gets into a builder without a session from iPro.

## Part B: settings (each app's Configuration > Application settings)

You paste every value. Names with `__` are written exactly so. Then tell me and I run
`ops\Check-AppSettings.ps1` for each app: it lists names and lengths and nothing else.

**`ipro-staging-web`**

| Name | Value |
|---|---|
| `App__Environment` | `Staging` |
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `ConnectionStrings__DefaultConnection` | staging MySQL, database `ipro_staging` (the same form as production's, ending `SslMode=Required;`) |
| `App__BaseUrl`, `App__PortalBaseUrl` | `https://ipro-staging-web.azurewebsites.net` |
| `Signups__AdminBaseUrl` | `https://ipro-staging-admin.azurewebsites.net` (the links in staging's notices) |
| `Signups__NotificationEmail` | your test address (where staging's "site sent for review" notices go) |
| `Azure__StorageAccountName`, `Azure__StorageConnectionString` | the staging storage account's |
| `Email__OnlyTo` | your test addresses, for example `test@iproadvisers.com` |
| `PayPal__IsSandbox` | `true` |
| `PayPal__ClientId`, `PayPal__ClientSecret` | your PayPal **sandbox** app's |
| `AzureDomainAutomation__Enabled` | `false` |
| `Builder__BaseUrl` | `https://ipro-builder-staging.azurewebsites.net` |
| `Builder__ServiceKey` | a new random key (step B5), the same as the staging builder's |
| `WEBSITE_RUN_FROM_PACKAGE` | `1` |

Leave out on staging: every `Email__Ses__*`, `Email__AzureCommunicationConnectionString` (no email is
sent at all until you want staging emails at your test address; then add it, and `Email__Provider` =
`Azure`), `APPLICATIONINSIGHTS_CONNECTION_STRING` and the other Application Insights settings,
`GoogleCalendar__*`, `Ai__AnthropicApiKey`, and every other `AzureDomainAutomation__*`.

**`ipro-staging-admin`:** `App__Environment` = `Staging`, `ASPNETCORE_ENVIRONMENT` = `Production`, the
same `ConnectionStrings__DefaultConnection`, `Azure__*`, `Email__OnlyTo`, `PayPal__IsSandbox` = `true`
(and the sandbox `PayPal__ClientId`/`ClientSecret`), `AzureDomainAutomation__Enabled` = `false`,
`App__BaseUrl` and `App__PortalBaseUrl` = `https://ipro-staging-web.azurewebsites.net`,
`Signups__AdminBaseUrl` = `https://ipro-staging-admin.azurewebsites.net`, `WEBSITE_RUN_FROM_PACKAGE` = `1`,
and `Admin__Username` / `Admin__Password`: the first SuperAdmin of the empty staging database (choose a
new password; it is made once at the first start).

**`ipro-builder` (the builder customers use):**

| Name | Value |
|---|---|
| `Hosting__Mode` | `Hosted` |
| `Hosting__DataRoot` | `/home/builder-data` |
| `Hosting__Database` | `MySql` |
| `ConnectionStrings__Builder` | `Server=ipro-mysql-prod.mysql.database.azure.com;Database=ipro_builder;User=ipro_builder;Password=...;SslMode=Required` |
| `Hosting__IproBaseUrl` | `https://app.iproadvisers.com` |
| `Hosting__IproOrigins__0` | only if advisers also open My Website at another address (for example `https://www.iproaccountants.com`): that address; `__1`, `__2` for more. Otherwise leave it out |
| `Hosting__ServiceKey` | a new random key (B5), the same as production iPro's `Builder__ServiceKey` later |
| `AllowedHosts` | `builder.iproadvisers.com` |

(The builder reads Azure's forwarded headers itself, so the session cookie is always the secure kind.)

**`ipro-builder-staging`:** the same names, with `ConnectionStrings__Builder` pointing at
`ipro-mysql-staging` (its `ipro_builder` login), `Hosting__IproBaseUrl` =
`https://ipro-staging-web.azurewebsites.net`, its own `Hosting__ServiceKey` (the staging portal's), and
`AllowedHosts` = `ipro-builder-staging.azurewebsites.net`.

Every call between the two servers carries this key: iPro's builder doors do not exist for a caller
without it, and a builder whose key is missing or shorter than 32 characters does not start.

**Later, not needed for the Beta:** the builder's key ring (`/home/builder-data/keys`, which protects
the session tokens iPro gives it) is kept on the app's own disk, readable by anyone who can open the
app's files in Azure. Protecting it with Azure Key Vault is a later step, if you want it.

**B5. A random key** for `Hosting__ServiceKey` / `Builder__ServiceKey`: in PowerShell on your PC,
`$b = New-Object byte[] 24; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); -join ($b | ForEach-Object { $_.ToString('x2') })`,
copy it into the two settings that share it, and nowhere else.

## Part C: the builder's address (your DNS host, where iproadvisers.com's records are)

Checked on 2026-10-17 (read-only): iproadvisers.com's name servers are `ns1/ns2.websiteservername.com`
(the cPanel host), and its CAA records include `digicert.com`, so Azure's free certificate can be issued.
Add two records and touch nothing else (the mail records stay exactly as they are):

1. `CNAME` `builder` -> `ipro-builder.azurewebsites.net`
2. `TXT` `asuid.builder` -> the app's **Custom domain verification ID** (on `ipro-builder`, **Custom
   domains**).

Then on `ipro-builder`: **Custom domains > Add custom domain** `builder.iproadvisers.com`, and **Add
binding** with an **App Service Managed Certificate**.

## Part D: the first deploys (on your word, by me)

1. iPro: this branch merged into main, then pushed. With the new workflows the push deploys **staging
   only**. I check `/health/version` on both staging apps and that staging started its empty database.
2. The builder: its repository's `main` gets the Beta branch; the push deploys the **staging builder**
   (with the secrets of step A7 in place first).
3. You try the whole path on staging (DOCS/BUILDER_BETA_BENCH.md, "Try it", with the staging addresses).

## Part E: production, each on your word

1. The builder: "Release the builder to PRODUCTION", with the SHA the staging builder serves.
2. iPro: "Release ... to production" for the portal, then SuperAdmin, with the SHA staging serves.
   GitHub asks for your approval ("Review deployments") before each deploys. (Step A7c first.)
3. Production iPro's settings: `Builder__BaseUrl` = `https://builder.iproadvisers.com` and
   `Builder__ServiceKey` (the builder's key). Until they are set, the Beta card says the builder is not
   available, even for an adviser it is switched on for.
4. Turn the Beta on for one adviser in SuperAdmin.

**Undo:** clear `Builder__BaseUrl` on production iPro (the card says the builder is not available; every
website stays as it is). The staging pieces can be stopped (apps) or deleted (the resource group) without
touching production; the builder's database on production's server is dropped with
`DROP DATABASE ipro_builder;` if you ever want it gone.
