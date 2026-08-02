// Logs in through the real Jellyfin web login form (not the REST API) and
// saves the resulting session (Jellyfin's ApiClient keeps its credentials in
// localStorage, not just cookies) as Playwright storageState, so every spec
// starts already authenticated instead of repeating the login flow itself.
//
// Assumes the Jellyfin instance is already up and the startup wizard/test
// library are already in place (tests/setup-jellyfin.sh run beforehand) --
// this only handles the browser-side login, mirroring how run-integration-
// tests.sh assumes the container is already running rather than starting it.
const { chromium } = require('@playwright/test');

const JELLYFIN_URL = process.env.JELLYFIN_URL || 'http://localhost:8096';
const USERNAME = process.env.JELLYFIN_TEST_USER || 'testadmin';
const PASSWORD = process.env.JELLYFIN_TEST_PASSWORD || 'testpassword123';
const STORAGE_STATE_PATH = `${__dirname}/.auth/state.json`;

module.exports = async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage();
  page.setDefaultTimeout(20000);

  await page.goto(`${JELLYFIN_URL}/web/`, { waitUntil: 'load' });
  // The SPA shell needs a moment to bootstrap and render the login form
  // before its inputs are attached -- a bare waitUntil:'load' fires before
  // that finishes.
  await page.waitForSelector('#txtManualName', { timeout: 20000 });
  await page.locator('#txtManualName').fill(USERNAME);
  await page.locator('#txtManualPassword').fill(PASSWORD);
  await page.getByRole('button', { name: 'Sign In' }).click();
  await page.waitForURL(/#\/home/, { timeout: 20000 });

  await page.context().storageState({ path: STORAGE_STATE_PATH });
  await browser.close();
};
