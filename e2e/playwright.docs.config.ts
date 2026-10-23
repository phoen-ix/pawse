import { defineConfig, devices } from '@playwright/test'

/* The docs tour of the website (web/site, served as pawse.at): screenshots for
 * docs/screenshots/site-*.png, and a render check of each page it visits.
 * Run it through scripts/docs-screenshots.sh, which serves web/site and sets
 * E2E_BASE_URL and DOCS_SCREENSHOT_DIR. The app's own windows are shot by
 * tests/Pawse.Tests/DocsScreenshotTests.cs (Windows only). */
export default defineConfig({
  testDir: './docs',
  timeout: 60_000,
  workers: 1,
  retries: 0,
  reporter: 'list',
  use: {
    ...devices['Desktop Chrome'],
    baseURL: process.env.E2E_BASE_URL ?? 'http://127.0.0.1:25952',
    viewport: { width: 1280, height: 800 },
    deviceScaleFactor: 2,
    locale: 'en-GB',
    launchOptions: {
      args: ['--lang=en-GB'],
      env: { ...process.env, LANG: 'en_GB.UTF-8', LANGUAGE: 'en_GB:en', LC_ALL: 'en_GB.UTF-8' },
    },
    timezoneId: 'Europe/Vienna',
    reducedMotion: 'reduce',
    screenshot: 'only-on-failure',
    trace: 'off',
  },
})
