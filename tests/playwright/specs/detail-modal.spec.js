// Drives the real click-to-expand result detail modal (PR #47) through an
// actual browser. Relies on the same guarantee dashboard.spec.js's own first
// test does: ScanOnItemAdded defaults to true, so by the time
// setup-jellyfin.sh's settle-poll finishes, every item already has a real
// Header-phase scan_results row -- no scan needs to be triggered here first.
const { test, expect } = require('@playwright/test');

const DASHBOARD_URL = '/web/#/configurationpage?name=Media+Integrity+Scanner';

test.describe('Media Integrity Scanner result detail modal', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto(DASHBOARD_URL, { waitUntil: 'load' });
    await expect(page.locator('#results-body tr').first()).toBeVisible({ timeout: 15000 });
    // Guard against the empty-state row ("No results yet...") rendering
    // before the real async loadResults() call resolves.
    await expect(page.locator('#results-body')).not.toContainText('No results yet', { timeout: 15000 });
  });

  test('clicking a row opens the modal with matching file path, status, and phase', async ({ page }) => {
    const firstRow = page.locator('#results-body tr').first();
    const fullPath = await firstRow.locator('td').first().getAttribute('title');
    const statusText = await firstRow.locator('td').nth(1).textContent();
    const phaseText = await firstRow.locator('td').nth(2).textContent();

    await firstRow.click();

    await expect(page.locator('#detail-modal-overlay')).toHaveClass(/open/);
    await expect(page.locator('#detail-filepath')).toHaveText(fullPath);
    await expect(page.locator('#detail-status')).toHaveText(statusText.trim());
    await expect(page.locator('#detail-phase')).toHaveText(phaseText.trim());
  });

  test('a Header-phase row shows "N/A (header scan)" as its Decode Mode', async ({ page }) => {
    // Every item is still Header-phase-only at this point in the suite (no
    // Deep Scan has run yet in this spec file), so DecodeMode is always
    // NotApplicable -- formatDecodeMode()'s N/A branch.
    await page.locator('#results-body tr').first().click();

    await expect(page.locator('#detail-decode-mode')).toHaveText('N/A (header scan)');
  });

  test('the Close button closes the modal', async ({ page }) => {
    await page.locator('#results-body tr').first().click();
    await expect(page.locator('#detail-modal-overlay')).toHaveClass(/open/);

    await page.locator('.modal-close').click();

    await expect(page.locator('#detail-modal-overlay')).not.toHaveClass(/open/);
  });

  test('clicking the overlay background closes the modal, but clicking inside it does not', async ({ page }) => {
    await page.locator('#results-body tr').first().click();
    await expect(page.locator('#detail-modal-overlay')).toHaveClass(/open/);

    // Click the modal box itself (not the Close button) -- must NOT close,
    // matching the overlay's `if (event.target === this)` guard.
    await page.locator('.modal-box h2').click();
    await expect(page.locator('#detail-modal-overlay')).toHaveClass(/open/);

    // Click the overlay's own background, well outside the modal box. The
    // top-left corner is Jellyfin's own left nav drawer, not this plugin's
    // content -- use the bottom-right instead, clear of both the drawer and
    // the centered, max-width:700px modal box.
    const viewport = page.viewportSize();
    await page.locator('#detail-modal-overlay').click({
      position: { x: viewport.width - 10, y: viewport.height - 10 },
    });
    await expect(page.locator('#detail-modal-overlay')).not.toHaveClass(/open/);
  });
});
