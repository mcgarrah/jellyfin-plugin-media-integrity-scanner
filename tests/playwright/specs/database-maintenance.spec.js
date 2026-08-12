// Drives the real Database Maintenance section (integrity check + VACUUM,
// item #30) through an actual browser.
const { test, expect } = require('@playwright/test');

const DASHBOARD_URL = '/web/#/configurationpage?name=Media+Integrity+Scanner';

test.describe('Media Integrity Scanner database maintenance', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto(DASHBOARD_URL, { waitUntil: 'load' });
    await expect(page.locator('#total-files')).toHaveText('7', { timeout: 15000 });
  });

  test('shows real on-disk and reclaimable size on load', async ({ page }) => {
    await expect(page.locator('#db-file-size')).not.toHaveText('—', { timeout: 10000 });
    await expect(page.locator('#db-reclaimable-size')).not.toHaveText('—', { timeout: 10000 });
    // formatBytes() always appends a unit suffix.
    await expect(page.locator('#db-file-size')).toHaveText(/[KMB]B?$/);
  });

  test('Run Maintenance Now reports a passing integrity check', async ({ page }) => {
    await page.locator('#btn-maintenance-now').click();

    await expect(page.locator('#maintenance-result')).toContainText('Integrity check passed', { timeout: 15000 });
    await expect(page.locator('#btn-maintenance-now')).toBeEnabled();
  });

  test('is blocked with an error while a scan is in progress', async ({ page }) => {
    await page.locator('#btn-scan-headers').click();
    await expect(page.locator('#scan-status')).toHaveText('Scanning...');

    await page.locator('#btn-maintenance-now').click();

    await expect(page.locator('#error-msg')).toBeVisible({ timeout: 10000 });
    await expect(page.locator('#error-msg')).toContainText('Cannot run database maintenance while a scan is in progress');

    // Let the scan finish so it doesn't bleed into whichever spec runs next.
    await expect(page.locator('#scan-status')).toHaveText('Idle', { timeout: 60000 });
  });
});
