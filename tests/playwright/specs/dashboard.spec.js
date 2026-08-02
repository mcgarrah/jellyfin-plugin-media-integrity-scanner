// Drives the real Jellyfin admin web UI against the dashboard page
// (configurationpage?name=Media+Integrity+Scanner) through an actual
// Chromium browser -- exercising the page's own JS and the real
// ApiClient-backed session, which the curl+grep checks in
// run-integration-tests.sh never touch.
//
// Assumes tests/setup-jellyfin.sh has already brought up a fresh Jellyfin
// instance with the 7-file test-media matrix loaded. ScanOnItemAdded
// defaults to true, so by the time setup-jellyfin.sh's own settle-poll
// finishes, every item has already gone through one automatic Header scan --
// the manual "Run Header Scan" click below is a re-scan, not the first one,
// which is why counts already match the matrix even in the first test.
const { test, expect } = require('@playwright/test');

const DASHBOARD_URL = '/web/#/configurationpage?name=Media+Integrity+Scanner';

test.describe('Media Integrity Scanner dashboard', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto(DASHBOARD_URL, { waitUntil: 'load' });
    await expect(page.locator('#total-files')).toHaveText('7', { timeout: 15000 });
  });

  test('renders real stats and scan controls', async ({ page }) => {
    await expect(page.locator('h1')).toHaveText('Media Integrity Scanner');
    await expect(page.locator('#total-files')).toHaveText('7');
    await expect(page.locator('#btn-scan-headers')).toBeEnabled();
    await expect(page.locator('#btn-scan-deep')).toBeEnabled();
    await expect(page.locator('#btn-cancel')).toBeDisabled();
  });

  test('running a header scan updates stats to match the known test-media matrix', async ({ page }) => {
    await page.locator('#btn-scan-headers').click();

    // Buttons disable and status flips to "Scanning..." while a scan is active.
    await expect(page.locator('#btn-scan-headers')).toBeDisabled();
    await expect(page.locator('#btn-scan-deep')).toBeDisabled();
    await expect(page.locator('#scan-status')).toHaveText('Scanning...');

    // loadStatus() only polls every 10s; wait out a full scan-and-settle cycle.
    await expect(page.locator('#scan-status')).toHaveText('Idle', { timeout: 60000 });
    await expect(page.locator('#btn-scan-headers')).toBeEnabled();

    // Known matrix (tests/generate-test-media.sh), at Header (ffprobe) phase:
    // 2 good files + bad-truncated + bad-middecode all pass structural
    // probing (4 pass) -- only bad-empty/bad-garbage/bad-header-corrupt fail
    // ffprobe outright (3 fail). The two-phase differentiator (bad-truncated
    // and bad-middecode failing FullDecode) is a Deep scan, not a Header
    // scan, and is already covered by run-integration-tests.sh -- this test
    // is about the dashboard UI reflecting a scan's results, not re-proving
    // the corruption matrix.
    await expect(page.locator('#total-files')).toHaveText('7');
    await expect(page.locator('#passed-files')).toHaveText('4');
    await expect(page.locator('#failed-files')).toHaveText('3');
  });

  test('filtering to Failed Only shows only failing rows', async ({ page }) => {
    // Depends on the previous test's scan having already run in this worker
    // (workers: 1, fullyParallel: false -- tests in a file share ordering).
    await page.locator('#filter-status').selectOption('2');
    await expect(page.locator('#results-body tr')).toHaveCount(3, { timeout: 15000 });
    const statusCells = page.locator('#results-body tr td.status-fail');
    await expect(statusCells).toHaveCount(3);
  });
});
