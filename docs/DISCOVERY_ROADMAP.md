# IPRO Modern Discovery Roadmap

Last updated: 2026-07-06

## Current Priority

Stabilize the rebuilt application by completing the core workflows before broad modernization:

1. Registration, login, first-login password change, and admin agent management.
2. Billing and package subscription behavior.
3. Agent control panel modules: website/domain setup, clients, newsletters, content/tools.
4. Admin oversight for agents, packages, revenue, reports, and system health.
5. Old-system feature mapping from database tables, screenshots, and training videos.

## Module Status

| Area | Current Status | What Works | Main Gaps |
| --- | --- | --- | --- |
| Public registration | Mostly working | Modern registration page, required fields, dynamic verify code, duplicate email check, lowercase email normalization, generated username/domain/password, success page | Email delivery depends on SendGrid/Azure settings; package pricing is not yet tied to payment |
| Agent login | Working baseline | Username or email login, inactive users blocked, first-login password change enforced | Password reset/forgot-password for agents is not implemented |
| Admin login | Working baseline | Super admin login/logout, protected admin pages | Needs stronger admin password management later |
| Admin agent management | Working baseline | List/search/filter agents, details, edit, delete, activate/deactivate, reset temporary password | Hosting/Plesk actions need real production verification; delete should be tested against imported legacy data |
| Agent dashboard | Partial | Basic metrics and quick links | Needs workflow-specific widgets and old-system parity |
| Clients/CRM | Partial | Add/edit/delete/search clients, details, comments, CSV import/export | Categories, richer notes/history, segmentation, and old client fields need mapping |
| Website/domain setup | Partial | Website settings form, template selection, logo upload, publish/unpublish flags | Real domain binding workflow is incomplete; Plesk/domain integration needs UX and verification |
| Newsletters | Partial | Draft/create/edit/schedule structure, articles, subscribers | Actual send workflow and templates need verification; delete/remove article UX incomplete |
| Billing/packages | Placeholder | Package admin pages exist; billing page renders | `Subscribe` returns `Ok()` and PayPal service is stubbed; invoices/subscriptions are not production-ready |
| Scheduler/jobs | Partial | Hangfire configured; recurring jobs registered | Need verify newsletter/drip/reminder jobs against real data |
| Reports | Partial | Admin report pages exist | Metrics likely need real calculations and old-system parity |
| Drip campaigns | Data model only | Entities and scheduler job references exist | Agent UI and admin controls are missing |
| Calendar/reminders | Data model only | Entity and scheduler job references exist | Agent UI and reminder workflow are missing |
| Coupons/testimonials/articles | Data model only | Entities exist | Agent/admin UI and website rendering are missing |

## Recommended Next Implementation Order

### 1. Billing and Package Subscription

This is the most urgent functional gap after registration/login. Registration requires a package, but the app does not yet truly charge or activate subscriptions.

Deliverables:

- Replace `BillingController.Subscribe` placeholder with a real flow.
- Load packages from `BillingRule` records rather than the current stub package list.
- Create or update an agent billing/subscription record when a package is selected.
- Generate invoice records.
- Decide whether the first version is manual/admin-approved or real PayPal checkout.

### 2. Website and Domain Setup

This is central to the agent promise: each agent gets a temporary domain and can later attach their real domain.

Deliverables:

- Show the agent temporary domain clearly in the control panel.
- Add custom domain request/setup form.
- Store requested custom domain separately from the setup domain.
- Add admin visibility for domain setup status.
- Verify Plesk integration only after the workflow is clear.

### 3. Client Management Completion

The current CRM is useful but thin.

Deliverables:

- Map old database client fields to the new `Client` entity.
- Add missing fields that matter.
- Improve import validation and duplicate handling.
- Add category/segment management.
- Add activity history that mirrors the old product.

### 4. Newsletter and Content Tools

The current newsletter UI exists, but the old product likely had template/content workflows that need mapping from videos.

Deliverables:

- Verify newsletter send job end to end.
- Add/remove/edit newsletter articles cleanly.
- Add reusable templates.
- Confirm subscriber targeting.

### 5. Old-System Feature Mapping

Use the old database, screenshots, and converted training videos to build a full feature inventory.

Deliverables:

- Screen-by-screen old feature list.
- Database table-to-feature map.
- Mark each feature as keep, modernize, merge, or retire.
- Convert the list into implementation milestones.

## Immediate Next Code Target

Start with billing/package subscription because it is the largest visible mismatch in the current product flow:

- Registration asks for a package.
- Admin has package management.
- Agent billing page exists.
- But the actual subscription action is still a placeholder.

