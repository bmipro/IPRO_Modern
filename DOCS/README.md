# IPRO Manuals

This directory is the operating manual for the current IPRO application. The instructions describe the screens and behavior implemented in the Agent Portal, Super Admin Portal, and public websites.

## Agent Portal

1. [Account, Profile, and Dashboard](01_AGENT_ACCOUNT_AND_DASHBOARD.md)
2. [Clients, Account Types, Notes, and Follow-ups](02_CLIENTS_AND_FOLLOWUPS.md)
3. [Newsletters and Campaigns](03_NEWSLETTERS_AND_CAMPAIGNS.md)
4. [Website Builder, Pages, Menus, Images, and Templates](04_WEBSITE_BUILDER.md)
5. [Domains, SSL, Website Leads, and Lead Forms](05_DOMAINS_AND_LEADS.md)
6. [Packages, Billing, PayPal, and Invoices](06_BILLING_AND_INVOICES.md)

## Administration

7. [Super Admin Manual](07_SUPER_ADMIN.md)
8. [Public Registration and Account Provisioning](08_PUBLIC_REGISTRATION.md)
9. [Troubleshooting and Deployment Checks](09_TROUBLESHOOTING.md)

## Project References

- [Documentation Standard](DOCUMENTATION_STANDARD.md)
- [Discovery Roadmap](DISCOVERY_ROADMAP.md)
- [Legacy Template Migration](LEGACY_TEMPLATE_MIGRATION.md)
- [Template System V2 Plan](TEMPLATE_SYSTEM_V2_PLAN.md)
- [Template System Consultant Brief](TEMPLATE_SYSTEM_CONSULTANT_BRIEF.md)
- [Legacy Training Workflow Map](LEGACY_TRAINING_WORKFLOW_MAP.md)
- [Production Domain Cutover](PRODUCTION_DOMAIN_CUTOVER.md)
- [Security & Code Quality Audit — 2026-07-24](SECURITY_AUDIT_2026-07-24.md)

## Portal Addresses

- Agent Portal: `https://ipro-prod-web.azurewebsites.net/` (custom domain: `https://app.iproadvisers.com/`) — login at `/Account/Login`
- Super Admin Portal: `https://ipro-prod-admin-fhaydtemgeetbycm.canadaeast-01.azurewebsites.net/` (custom domain: `https://admin.iproadvisers.com/`) — login at `/Admin/Login`, not `/Account/Login`
- Registration: `https://ipro-prod-web.azurewebsites.net/Account/Register`

Note: `app.iproadvisers.com` and `admin.iproadvisers.com` are separate Azure Web Apps (`ipro-prod-web` / `ipro-prod-admin`) with independent App Settings and login routes — easy to conflate by name alone.

## Documentation Rule

Every new user-facing function must include an update to the relevant manual in the same commit. If the function introduces a new workflow, create a new manual and add it to this index.

