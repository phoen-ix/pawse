import { mkdirSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

import { expect, test, type Page } from '@playwright/test'

/* The docs tour of web/site. Static pages, nothing to seed. Every test
 * asserts something page-specific before it shoots; a page that throws fails. */

const OUT = path.resolve(
  process.env.DOCS_SCREENSHOT_DIR ?? fileURLToPath(new URL('../../docs/screenshots', import.meta.url)),
)
mkdirSync(OUT, { recursive: true })

let pageErrors: Error[] = []

test.beforeEach(async ({ page }) => {
  pageErrors = []
  page.on('pageerror', (e) => pageErrors.push(e))
})

test.afterEach(() => {
  expect(pageErrors.map((e) => e.message)).toEqual([])
})

/** The first screen only: the pages' backgrounds are position:fixed, so a
 * full-page shot ends the hero glow in a hard edge one viewport down.
 * `crop` narrows it to a column (x, width in CSS px) and cuts 24 px below the
 * element `below` - the glow is dithered, and a whole 2x desktop screen of it
 * is over the 400 KB budget. */
async function shoot(
  page: Page,
  name: string,
  crop?: { x: number; width: number; below: string },
): Promise<void> {
  // The 5% film grain (body::after) can't be seen at README size, but it makes
  // every PNG incompressible noise - megabytes instead of a few hundred KB.
  await page.addStyleTag({ content: 'body::after { display: none }' })
  await page.evaluate(() => document.fonts.ready)
  await page.evaluate(() => window.scrollTo(0, 0))
  await page.mouse.move(0, 0)
  let clip: { x: number; y: number; width: number; height: number } | undefined
  if (crop) {
    const box = await page.locator(crop.below).first().boundingBox()
    if (!box) throw new Error(`[docs] ${crop.below} is not on the page`)
    clip = { x: crop.x, y: 0, width: crop.width, height: Math.ceil(box.y + box.height) + 24 }
  }
  await page.screenshot({ path: path.join(OUT, `${name}.png`), animations: 'disabled', caret: 'hide', clip })
}

test('home', async ({ page }) => {
  // Taller than the default, so the hero's buttons fit on the first screen.
  await page.setViewportSize({ width: 1280, height: 900 })
  await page.goto('/')
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('pawse.')
  await expect(page.getByRole('link', { name: 'Download for Windows' })).toBeVisible()
  // The hero column: brand, mark, title, lede and the two buttons.
  await shoot(page, 'site-home', { x: 110, width: 660, below: '.hero a.btn' })
})

test('home on a phone', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await page.goto('/')
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('pawse.')
  await shoot(page, 'site-home.mobile', { x: 0, width: 390, below: '.hero a.btn.ghost' })
})

test('privacy', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 })
  await page.goto('/privacy.html')
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Privacy')
  // The 760 px text column (plus its padding), down to "What it stores on your PC".
  await shoot(page, 'site-privacy', { x: 236, width: 808, below: 'ul' })
})
