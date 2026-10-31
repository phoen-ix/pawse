import { defineConfig, devices } from '@playwright/test'

/* The e2e suite of the website (web/site, served as pawse.at). Run it through
 * scripts/e2e.sh, which serves web/site and sets E2E_BASE_URL - and on Windows
 * also runs the app's own UI suite (tests/Pawse.Tests/E2E, Category=E2E).
 * Coverage map: e2e/COVERAGE.md. */
export default defineConfig({
  testDir: './tests',
  timeout: 30_000,
  workers: 2,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    ...devices['Desktop Chrome'],
    baseURL: process.env.E2E_BASE_URL ?? 'http://127.0.0.1:25952',
    locale: 'en-GB',
    timezoneId: 'Europe/Vienna',
    reducedMotion: 'reduce',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
})
