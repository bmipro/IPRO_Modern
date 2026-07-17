# IPRO Modern — Playwright E2E Smoke Tests

End-to-end browser tests, separate from the .NET solution. Runs against production by default (`https://ipro-prod-web.azurewebsites.net`) — pass `IPRO_BASE_URL` to target a different environment.

## Setup (one time)

```powershell
cd e2e
npm install
npx playwright install chromium
```

## Run the read-only smoke tests

These need no credentials and are safe to run anytime:

```powershell
npm test
```

## Run the authenticated smoke tests

These log in as a real agent and confirm Dashboard, My Website, Analytics, and Newsletter load without a server error. They only run if you provide test-account credentials as environment variables — **use a real or disposable test agent account, never a production account you care about, and never share these credentials with anyone else, including this assistant.**

```powershell
$env:IPRO_TEST_USERNAME = "your-test-username"
$env:IPRO_TEST_PASSWORD = "your-test-password"
npm test
```

Without those two variables set, the authenticated tests are skipped automatically (the read-only ones still run).

## View the last report

```powershell
npm run report
```

## Notes

- `node_modules/`, `.env`, `playwright-report/`, and `test-results/` are all git-ignored.
- This is intentionally kept separate from the C# solution/CI — see `DOCS/09_TROUBLESHOOTING.md` for why the analytics 500 bug (2026-07-17) motivated adding this.
