// Drives the real Database Backup section (backup/restore, PR #44/#45)
// through an actual browser, proving a genuine round trip rather than a
// no-op: Deep Scan is known (see dashboard.spec.js's comment on the same
// 7-file matrix, and confirmed live against this exact fixture) to move
// PassedFiles/FailedFiles from 4/3 to 2/5 -- bad-truncated and bad-middecode
// pass Header (ffprobe) but fail FullDecode, so their more-authoritative
// deep-scan result flips their most-authoritative status. That's used here
// as a real, deterministic state change to back up before, mutate past, and
// restore back through.
const { test, expect } = require('@playwright/test');

const DASHBOARD_URL = '/web/#/configurationpage?name=Media+Integrity+Scanner';

test.describe('Media Integrity Scanner database backup/restore', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto(DASHBOARD_URL, { waitUntil: 'load' });
    await expect(page.locator('#total-files')).toHaveText('7', { timeout: 15000 });
  });

  test('Backup Now creates a new backup that appears in the list', async ({ page }) => {
    // loadBackups() replaces the whole tbody with a single "No backups yet."
    // placeholder <tr> when there are none -- that's one real DOM row but
    // zero actual backups, so it can't be counted the same way as real rows.
    await expect(page.locator('#backups-body')).not.toContainText('Loading...', { timeout: 10000 });
    const bodyText = await page.locator('#backups-body').textContent();
    const countBefore = bodyText.includes('No backups yet')
      ? 0
      : await page.locator('#backups-body tr').count();

    await page.locator('#btn-backup-now').click();
    await expect(page.locator('#btn-backup-now')).toBeEnabled({ timeout: 15000 });

    await expect(page.locator('#backups-body tr')).toHaveCount(countBefore + 1, { timeout: 10000 });
    // Newest-first ordering: the just-created backup is the first row.
    await expect(page.locator('#backups-body tr').first()).toContainText('media-integrity-backup-');
  });

  test('Restore rolls back a real Deep Scan state change', async ({ page }) => {
    // Ensure a clean, known baseline: run Header scan first (the
    // ScanOnItemAdded automatic scan already covers this, but re-running is
    // idempotent and makes the baseline explicit rather than assumed).
    // Waiting for "Scanning..." before "Idle" matters here, not just
    // decoration: loadStatus() polls the server, and the very first poll can
    // land in the brief window before the fire-and-forget scan task has
    // actually started -- catching a transient pre-scan "Idle" and racing
    // straight past the wait entirely (hit for real developing this test;
    // Idle resolved in ~1s instead of the ~35s+ seven files at
    // DelayBetweenFilesMs=5000 genuinely takes).
    await page.locator('#btn-scan-headers').click();
    await expect(page.locator('#scan-status')).toHaveText('Scanning (Header)...');
    await expect(page.locator('#scan-status')).toHaveText('Idle', { timeout: 60000 });
    await expect(page.locator('#passed-files')).toHaveText('4');
    await expect(page.locator('#failed-files')).toHaveText('3');

    await page.locator('#btn-backup-now').click();
    await expect(page.locator('#btn-backup-now')).toBeEnabled({ timeout: 15000 });
    const backupFileName = await page.locator('#backups-body tr').first().locator('td').first().textContent();

    // Mutate: Deep Scan is the known, real 4/3 -> 2/5 state change.
    await page.locator('#btn-scan-deep').click();
    await expect(page.locator('#scan-status')).toHaveText('Scanning (Deep)...');
    await expect(page.locator('#scan-status')).toHaveText('Idle', { timeout: 60000 });
    await expect(page.locator('#passed-files')).toHaveText('2');
    await expect(page.locator('#failed-files')).toHaveText('5');

    // Restore the pre-deep-scan backup and confirm the state genuinely reverts.
    page.once('dialog', (dialog) => dialog.accept());
    await page
      .locator('#backups-body tr', { hasText: backupFileName })
      .locator('button', { hasText: 'Restore' })
      .click();

    await expect(page.locator('#passed-files')).toHaveText('4', { timeout: 15000 });
    await expect(page.locator('#failed-files')).toHaveText('3');
  });

  test('Restore is blocked with an error while a scan is in progress', async ({ page }) => {
    await expect(page.locator('#backups-body tr').first()).toBeVisible({ timeout: 10000 });
    const backupFileName = await page.locator('#backups-body tr').first().locator('td').first().textContent();

    await page.locator('#btn-scan-deep').click();
    await expect(page.locator('#scan-status')).toHaveText('Scanning (Deep)...');

    page.once('dialog', (dialog) => dialog.accept());
    await page
      .locator('#backups-body tr', { hasText: backupFileName })
      .locator('button', { hasText: 'Restore' })
      .click();

    await expect(page.locator('#error-msg')).toBeVisible({ timeout: 10000 });
    await expect(page.locator('#error-msg')).toContainText('Cannot restore while a scan is in progress');

    // Let the scan finish so it doesn't bleed into whichever spec runs next.
    await expect(page.locator('#scan-status')).toHaveText('Idle', { timeout: 60000 });
  });
});
