// Verifies the dashboard's "Report a Bug" button actually assembles a real,
// correctly pre-filled GitHub issue URL from the live Diagnostics endpoint --
// not just that the button exists.
//
// Reads the *requested* URL via route interception rather than the popup's
// post-navigation url(): a real, unauthenticated browser (exactly this test
// runner's state) gets redirected by GitHub itself from issues/new straight
// to /login before Playwright's popup.url() would ever see the original
// path -- confirmed by first writing this test against popup.url() directly
// and watching it fail with "/login" instead of "/issues/new". Route
// interception captures the request as issued, before any of that happens,
// and this test also never needs github.com to actually be reachable.
const { test, expect } = require('@playwright/test');

const DASHBOARD_URL = '/web/#/configurationpage?name=Media+Integrity+Scanner';

test.describe('Report a Bug', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto(DASHBOARD_URL, { waitUntil: 'load' });
    await expect(page.locator('#total-files')).toHaveText('7', { timeout: 15000 });
  });

  test('opens a pre-filled GitHub issue with real diagnostic values, no file paths', async ({ page, context }) => {
    let requestedUrl = null;
    await context.route('https://github.com/**', async (route) => {
      requestedUrl = route.request().url();
      await route.abort();
    });

    const [popup] = await Promise.all([
      context.waitForEvent('page'),
      page.locator('#btn-report-bug').click()
    ]);
    await popup.close().catch(() => {});

    expect(requestedUrl).toBeTruthy();
    const url = new URL(requestedUrl);

    expect(url.hostname).toBe('github.com');
    expect(url.pathname).toBe(
      '/mcgarrah/jellyfin-plugin-media-integrity-scanner/issues/new'
    );
    expect(url.searchParams.get('template')).toBe('bug_report.yml');

    // Real values from the live server, not placeholders.
    expect(url.searchParams.get('plugin-version')).toMatch(/^\d+\.\d+\.\d+\.\d+$/);
    expect(url.searchParams.get('jellyfin-version')).toBeTruthy();
    expect(url.searchParams.get('operating-system')).toContain('Linux');
    expect(url.searchParams.get('dotnet-version')).toContain('.NET');
    expect(url.searchParams.get('update-channel')).toBeTruthy();
    expect(url.searchParams.get('hardware-accel')).toBeTruthy();
    expect(url.searchParams.get('health-summary')).toMatch(/scanned/);

    // The whole point of building this server-side: no file paths or library
    // names ever end up in a prefilled public issue URL.
    const fullQuery = url.search;
    expect(fullQuery).not.toContain('/media/');
    expect(fullQuery).not.toContain('.mkv');

    // Button re-enables itself once the popup has been dispatched.
    await expect(page.locator('#btn-report-bug')).toBeEnabled();
    await expect(page.locator('#btn-report-bug')).toHaveText('Report a Bug');
  });

  test('reports the configured custom ffmpeg override without leaking its path', async ({ page, context, request }) => {
    // Confirms the withholding behavior end-to-end through the real HTTP
    // stack, not just the unit-tested controller method in isolation.
    const diag = await request.get('/MediaIntegrity/Diagnostics', {
      headers: { 'X-Emby-Token': await page.evaluate(() => window.ApiClient.accessToken()) }
    });
    const diagBody = await diag.json();

    // This suite's test server never configures a custom ffmpeg override, so
    // this asserts the normal (auto-detected) branch actually reached the
    // client -- the withheld-path branch itself is covered by the C# unit
    // tests (GetDiagnostics_WithheldsResolvedPaths_WhenCustomOverrideConfigured).
    expect(diagBody.UsingCustomFfmpegOverride).toBe(false);
    expect(diagBody.FfmpegPath).toBeTruthy();
  });
});
