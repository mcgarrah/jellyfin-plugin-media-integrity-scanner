// Drives the real dashboard pagination controls (page-size selector,
// Prev/Next, jump-to-page) added in PR #46. These never render at all
// against the shared 7-file test-media matrix: Web/integrity_dashboard.html's
// renderPagination() blanks the whole #pagination div whenever totalPages <=
// 1, and even the smallest page-size option (25/page) can't produce a second
// page for 7 rows. Rather than growing the shared media matrix (which would
// ripple into dashboard.spec.js's and run-integration-tests.sh's exact
// pass/fail count assertions), this spec seeds synthetic scan_results rows
// directly via tests/seed-pagination-rows.py -- invisible to every other
// spec's stats, since TotalFiles comes from the real Jellyfin library count,
// not this table.
const { test, expect } = require('@playwright/test');
const { execFileSync } = require('node:child_process');
const path = require('node:path');

const DASHBOARD_URL = '/web/#/configurationpage?name=Media+Integrity+Scanner';
const DB_PATH = path.join(__dirname, '..', '..', 'jellyfin-config', 'plugins', 'configurations', 'MediaIntegrityScanner', 'media-integrity.db');
const SEED_SCRIPT = path.join(__dirname, '..', '..', 'seed-pagination-rows.py');
// seed() wipes scan_results entirely first, so this is exactly 260 rows,
// regardless of what earlier specs left behind -- producing more than one page
// at every page-size option (11/6/3/2 pages at 25/50/100/250 respectively).
const SEED_COUNT = 260;

test.describe('Media Integrity Scanner dashboard pagination', () => {
  test.beforeAll(() => {
    execFileSync('sudo', ['python3', SEED_SCRIPT, DB_PATH, 'seed', String(SEED_COUNT)]);
  });

  test.afterAll(() => {
    execFileSync('sudo', ['python3', SEED_SCRIPT, DB_PATH, 'clear']);
  });

  test.beforeEach(async ({ page }) => {
    await page.goto(DASHBOARD_URL, { waitUntil: 'load' });
    await expect(page.locator('#pagination')).toContainText('Page 1 of', { timeout: 15000 });
  });

  test('renders Page 1 of N with a Next button but no Prev button', async ({ page }) => {
    await expect(page.locator('#pagination')).toContainText('Page 1 of 6 (260 total)');
    await expect(page.locator('#pagination button', { hasText: 'Next' })).toBeVisible();
    await expect(page.locator('#pagination button', { hasText: 'Prev' })).toHaveCount(0);
  });

  test('Next advances a page and reveals Prev; Prev returns to page 1', async ({ page }) => {
    await page.locator('#pagination button', { hasText: 'Next' }).click();
    await expect(page.locator('#pagination')).toContainText('Page 2 of 6');
    await expect(page.locator('#pagination button', { hasText: 'Prev' })).toBeVisible();

    await page.locator('#pagination button', { hasText: 'Prev' }).click();
    await expect(page.locator('#pagination')).toContainText('Page 1 of 6');
    await expect(page.locator('#pagination button', { hasText: 'Prev' })).toHaveCount(0);
  });

  test('the last page shows no Next button', async ({ page }) => {
    await page.locator('#jump-page-input').fill('6');
    await page.locator('#pagination button', { hasText: 'Go' }).click();

    await expect(page.locator('#pagination')).toContainText('Page 6 of 6');
    await expect(page.locator('#pagination button', { hasText: 'Next' })).toHaveCount(0);
    await expect(page.locator('#pagination button', { hasText: 'Prev' })).toBeVisible();
  });

  test('jump-to-page navigates directly to a specific page', async ({ page }) => {
    await page.locator('#jump-page-input').fill('4');
    await page.locator('#pagination button', { hasText: 'Go' }).click();

    await expect(page.locator('#pagination')).toContainText('Page 4 of 6');
  });

  test('jump-to-page rejects an out-of-range page number', async ({ page }) => {
    await page.locator('#jump-page-input').fill('9999');
    await page.locator('#pagination button', { hasText: 'Go' }).click();

    await expect(page.locator('#error-msg')).toBeVisible();
    await expect(page.locator('#error-msg')).toContainText('between 1 and 6');
    // The invalid jump must not have actually navigated anywhere.
    await expect(page.locator('#pagination')).toContainText('Page 1 of 6');
  });

  test('changing page size recomputes total pages and resets to page 1', async ({ page }) => {
    // Land on page 3 first, so resetting to page 1 is an observable change.
    await page.locator('#jump-page-input').fill('3');
    await page.locator('#pagination button', { hasText: 'Go' }).click();
    await expect(page.locator('#pagination')).toContainText('Page 3 of 6');

    await page.locator('#page-size-select').selectOption('250');

    // 260 rows / 250 per page = 2 pages, and totalPages > 2 is required for
    // the jump-to-page input to render at all -- confirms it disappears too.
    await expect(page.locator('#pagination')).toContainText('Page 1 of 2 (260 total)');
    await expect(page.locator('#jump-page-input')).toHaveCount(0);

    // Restore the default for any test that might run after this file.
    await page.locator('#page-size-select').selectOption('50');
  });
});
