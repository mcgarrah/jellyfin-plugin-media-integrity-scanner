// @ts-check
const { defineConfig, devices } = require('@playwright/test');

module.exports = defineConfig({
  testDir: './specs',
  fullyParallel: false, // both specs share one Jellyfin instance/library; keep them sequential
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  workers: 1,
  // A library-wide scan of the 7-file test matrix can take 30-40s+ on its
  // own (7 files x the default 5s DelayBetweenFilesMs, plus real ffprobe/
  // ffmpeg work) -- comfortably past Playwright's 30s default per-test timeout.
  timeout: 90000,
  reporter: process.env.CI ? [['html', { open: 'never' }], ['list']] : 'list',
  globalSetup: require.resolve('./global-setup'),
  use: {
    baseURL: process.env.JELLYFIN_URL || 'http://localhost:8096',
    storageState: `${__dirname}/.auth/state.json`,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
});
