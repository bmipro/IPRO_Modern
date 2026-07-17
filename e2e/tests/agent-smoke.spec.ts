import { test, expect } from '@playwright/test';

// Authenticated smoke checks. These only run if IPRO_TEST_USERNAME and
// IPRO_TEST_PASSWORD are set (e.g. in a local, git-ignored `.env` file
// loaded via `dotenv`, or exported in your shell) — never commit real
// credentials, and never ask an AI assistant to enter them for you.
const username = process.env.IPRO_TEST_USERNAME;
const password = process.env.IPRO_TEST_PASSWORD;

test.skip(!username || !password, 'Set IPRO_TEST_USERNAME/IPRO_TEST_PASSWORD to run authenticated smoke tests');

test.describe('authenticated agent portal smoke checks', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/Account/Login');
    await page.fill('input[name="username"]', username!);
    await page.fill('input[name="password"]', password!);
    await page.click('button[type="submit"]');
    await expect(page).not.toHaveURL(/Login/);
  });

  const pages = [
    { path: '/Dashboard', label: 'Dashboard' },
    { path: '/Website', label: 'My Website' },
    { path: '/WebsiteAnalytics', label: 'Analytics' },
    { path: '/Newsletter', label: 'Newsletter' },
  ];

  for (const { path, label } of pages) {
    test(`${label} page loads without a server error`, async ({ page }) => {
      const response = await page.goto(path);
      expect(response?.status(), `${label} (${path}) returned an error status`).toBeLessThan(400);
      await expect(page.getByText(/isn't working right now|HTTP ERROR 500/i)).toHaveCount(0);
    });
  }
});
