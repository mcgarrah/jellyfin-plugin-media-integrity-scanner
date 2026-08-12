// Drives the real Jellyfin admin web UI against the settings page
// (configurationpage?name=Media+Integrity+Scanner+Settings). Uses Jellyfin's
// standard ApiClient.getPluginConfiguration/updatePluginConfiguration flow,
// not a custom route.
const { test, expect } = require('@playwright/test');
const os = require('os');

const SETTINGS_URL = '/web/#/configurationpage?name=Media+Integrity+Scanner+Settings';

// PluginConfiguration.MaxConcurrentScans now defaults to half this host's
// vCPU count (floored at 1), not a flat 1 -- computed here rather than
// hardcoded so this assertion doesn't depend on the runner's core count.
const EXPECTED_DEFAULT_MAX_CONCURRENT_SCANS = String(Math.max(1, Math.floor(os.cpus().length / 2)));

test.describe('Media Integrity Scanner settings', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto(SETTINGS_URL, { waitUntil: 'load' });
    // loadConfig() populates fields async; wait for the field with the most
    // distinctive default (non-empty, non-zero) rather than an arbitrary timeout.
    await expect(page.locator('#QuietHoursStart')).toHaveValue('02:00', { timeout: 15000 });
  });

  test('pre-populates all fields from the real plugin configuration', async ({ page }) => {
    await expect(page.locator('#MaxConcurrentScans')).toHaveValue(EXPECTED_DEFAULT_MAX_CONCURRENT_SCANS);
    await expect(page.locator('#DelayBetweenFilesMs')).toHaveValue('5000');
    await expect(page.locator('#PauseDuringPlayback')).toBeChecked();
    await expect(page.locator('#EnableDeepScan')).not.toBeChecked();
    await expect(page.locator('#ScanOnItemAdded')).toBeChecked();
    await expect(page.locator('#EnableAutoUpdate')).not.toBeChecked();
    await expect(page.locator('#AutoRestartAfterUpdate')).not.toBeChecked();
    // Both default to true/none respectively -- see PluginConfiguration.cs.
    await expect(page.locator('#EnableAutoDatabaseMaintenance')).toBeChecked();
    await expect(page.locator('#HardwareAccelerationType')).toHaveValue('none');
    await expect(page.locator('#QuietHoursStart')).toHaveValue('02:00');
    await expect(page.locator('#QuietHoursEnd')).toHaveValue('06:00');
    await expect(page.locator('#error-msg')).toBeHidden();
  });

  test('saving a changed value persists across a reload, then restores it', async ({ page }) => {
    const original = await page.locator('#MaxConcurrentScans').inputValue();
    const changed = String(Number(original) + 1);

    await page.locator('#MaxConcurrentScans').fill(changed);
    await page.locator('#btn-save').click();

    // btn-save disables during the save request and re-enables on completion;
    // that round-trip (rather than #success-msg, which real Jellyfin sessions
    // often surface via Dashboard's own toast instead of this page's local
    // div) is the reliable signal that the save actually completed.
    await expect(page.locator('#btn-save')).toBeEnabled({ timeout: 10000 });
    await expect(page.locator('#error-msg')).toBeHidden();

    await page.reload({ waitUntil: 'load' });
    await expect(page.locator('#MaxConcurrentScans')).toHaveValue(changed, { timeout: 15000 });

    // Restore the original value so this test is safe to re-run.
    await page.locator('#MaxConcurrentScans').fill(original);
    await page.locator('#btn-save').click();
    await expect(page.locator('#btn-save')).toBeEnabled({ timeout: 10000 });
    await page.reload({ waitUntil: 'load' });
    await expect(page.locator('#MaxConcurrentScans')).toHaveValue(original, { timeout: 15000 });
  });

  test('saving a changed <select> value persists across a reload, then restores it', async ({ page }) => {
    // The numeric-input round trip above doesn't exercise a <select>, which
    // has its own real failure mode (e.g. loadConfig() using the wrong
    // property name and silently leaving the browser default selected).
    await page.locator('#HardwareAccelerationType').selectOption('nvenc');
    await page.locator('#btn-save').click();
    await expect(page.locator('#btn-save')).toBeEnabled({ timeout: 10000 });
    await expect(page.locator('#error-msg')).toBeHidden();

    await page.reload({ waitUntil: 'load' });
    await expect(page.locator('#HardwareAccelerationType')).toHaveValue('nvenc', { timeout: 15000 });

    await page.locator('#HardwareAccelerationType').selectOption('none');
    await page.locator('#btn-save').click();
    await expect(page.locator('#btn-save')).toBeEnabled({ timeout: 10000 });
    await page.reload({ waitUntil: 'load' });
    await expect(page.locator('#HardwareAccelerationType')).toHaveValue('none', { timeout: 15000 });
  });

  test('"<< Back to Dashboard" navigates via the SPA router without a full reload', async ({ page }) => {
    await page.locator('a.nav-link', { hasText: 'Back to Dashboard' }).click();
    await expect(page).toHaveURL(/configurationpage\?name=Media\+Integrity\+Scanner$/);
    await expect(page.locator('#total-files')).toHaveText('7', { timeout: 15000 });
  });
});
