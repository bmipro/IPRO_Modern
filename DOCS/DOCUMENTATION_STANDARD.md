# Documentation Standard

## Requirement

Every user-facing feature must be documented in the same commit that implements or materially changes it.

## Where to Document

- Agent account/dashboard: `01_AGENT_ACCOUNT_AND_DASHBOARD.md`
- CRM and follow-ups: `02_CLIENTS_AND_FOLLOWUPS.md`
- Newsletters/campaigns: `03_NEWSLETTERS_AND_CAMPAIGNS.md`
- Website builder: `04_WEBSITE_BUILDER.md`
- Domains/leads: `05_DOMAINS_AND_LEADS.md`
- Billing/invoices: `06_BILLING_AND_INVOICES.md`
- Super Admin: `07_SUPER_ADMIN.md`
- Registration: `08_PUBLIC_REGISTRATION.md`
- Failures and operations: `09_TROUBLESHOOTING.md`

Create a new numbered manual when a feature does not fit an existing area, then add it to `DOCS/README.md`.

## Manual Section Format

Use this structure:

```markdown
## Function Name

One sentence explaining the purpose.

1. Open **Menu → Screen**.
2. Enter or select the required information.
3. Click **Command**.
4. Confirm the expected result.

### Important Behavior

- State package limits, security rules, automation, and side effects.

### Troubleshooting

- State the most likely failure and the corrective action.
```

## Writing Rules

- Use the exact labels visible in the application.
- Write numbered steps for actions.
- State required versus optional information.
- Describe the expected successful result.
- Mention package restrictions and upgrade behavior.
- Mention emails, CRM updates, billing effects, domain changes, or destructive actions.
- Never place passwords, API secrets, private keys, or live tokens in documentation.
- Update URLs when production domains change.
- Keep screenshots free of credentials and personal information.

## Feature Completion Checklist

Before committing a feature:

- [ ] The function works in the intended portal.
- [ ] Validation and authorization are covered.
- [ ] Empty, loading, error, and success states are considered.
- [ ] Mobile and desktop layouts are checked when UI changed.
- [ ] The correct manual is updated.
- [ ] Troubleshooting guidance is added for operational integrations.
- [ ] `DOCS/README.md` links to any new manual.
- [ ] Release builds pass.
- [ ] If the feature is a paid/gated agent capability (not a bug fix or free platform improvement), it has a new `PackageFeatureCodes` entry, a matching row in `PackageEntitlementSeeder.BuildFeatureDefinitions()`, and a server-side `IPackageEntitlementService.GetAccessAsync(...)` check in the controller action(s) that expose it — never gate on UI alone. This makes it automatically appear as a toggleable checkbox in Super Admin's `Packages/Edit` screen (`_PackageFeaturesEditor.cshtml`) for every package (Silver/Gold/Platinum/Broker), which is how Super Admin controls who gets the feature. `ClientInvoicing` and `ClientPortal` are the reference examples.

