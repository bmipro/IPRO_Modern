import { test, expect } from '@playwright/test';

// Read-only checks against the public/unauthenticated surface.
// Safe to run anytime, against production, with no credentials.

test('agent login page loads', async ({ page }) => {
  await page.goto('/');
  await expect(page).toHaveTitle(/IPRO - Agent Login/);
  await expect(page.locator('input[name="username"]')).toBeVisible();
  await expect(page.locator('input[name="password"]')).toBeVisible();
});

test('registration page loads', async ({ page }) => {
  await page.goto('/Account/Register');
  await expect(page.locator('form')).toBeVisible();
});

test('unsubscribe page handles an invalid token gracefully', async ({ page }) => {
  await page.goto('/Newsletter/Unsubscribe?token=not-a-real-token');
  await expect(page.getByText(/not valid/i)).toBeVisible();
});
