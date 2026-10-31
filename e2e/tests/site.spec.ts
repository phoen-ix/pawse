import { expect, test, type Page } from '@playwright/test'

/* pawse.at (web/site): two static pages, the update feed the app trusts, and
 * the files search engines read. No scripts run on the site, so a page error
 * or a failed same-origin request is a bug. */

const RELEASES = 'https://github.com/phoen-ix/pawse/releases/latest'
const REPO = 'https://github.com/phoen-ix/pawse'

/** Collects what would break the privacy page's promise ("no cookies, no JavaScript, nothing
 *  loaded from other sites") or the page itself: a page error, a same-origin request that does
 *  not come back 2xx/3xx, or any request to another site - which is also refused, so a test
 *  never reaches the internet. Each test ends with `expect(problems).toEqual([])`. */
async function guard(page: Page): Promise<string[]> {
  const problems: string[] = []
  page.on('pageerror', (e) => problems.push(`pageerror: ${e.message}`))
  page.on('response', (r) => {
    if (r.status() >= 400) problems.push(`${r.status()} ${r.url()}`)
  })
  await page.route(/^https?:\/\/(?!127\.0\.0\.1[:/])/, (route) => {
    problems.push(`off-site request: ${route.request().url()}`)
    return route.abort()
  })
  return problems
}

const PAGES = [
  { path: '/', heading: 'pawse.', title: 'Pawse - a cat-proof keyboard lock for Windows' },
  { path: '/privacy.html', heading: 'Privacy', title: /Privacy/ },
]
const VIEWPORTS = [
  { name: 'desktop', width: 1280, height: 800 },
  { name: 'phone', width: 390, height: 844 },
]

test.describe('sweep', () => {
  for (const p of PAGES) {
    for (const v of VIEWPORTS) {
      test(`${p.path} on a ${v.name}`, async ({ page }) => {
        const problems = await guard(page)
        await page.setViewportSize({ width: v.width, height: v.height })
        const res = await page.goto(p.path)
        expect(res?.status()).toBe(200)
        await expect(page).toHaveTitle(p.title)
        await expect(page.getByRole('heading', { level: 1 })).toHaveText(p.heading)
        await expect(page.getByRole('heading', { level: 1 })).toBeVisible()
        // Nothing wider than the screen: no sideways scrolling on a phone.
        expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(v.width)
        expect(problems).toEqual([])
      })
    }
  }
})

test.describe('home', () => {
  test('the hero offers the download and the source', async ({ page }) => {
    const problems = await guard(page)
    await page.goto('/')
    await expect(page.getByRole('link', { name: 'Download for Windows' })).toHaveAttribute('href', RELEASES)
    const source = page.getByRole('link', { name: 'Source on GitHub' })
    await expect(source).toHaveCount(2)
    for (const link of await source.all()) await expect(link).toHaveAttribute('href', REPO)
    expect(problems).toEqual([])
  })

  test('every section is there under its own anchor', async ({ page }) => {
    await page.goto('/')
    for (const [id, name] of [
      ['what', 'What Pawse does'],
      ['unlocking', 'Unlocking'],
      ['download', 'Download'],
      ['limits', "What it can't block"],
      ['questions', 'Questions'],
    ]) {
      await expect(page.getByRole('heading', { level: 2, name })).toHaveAttribute('id', id)
      await expect(page.getByRole('region', { name })).toBeVisible()
    }
  })

  test('the four unlock methods state the app defaults', async ({ page }) => {
    await page.goto('/')
    const rows = page.getByRole('region', { name: 'Unlocking' }).locator('.row')
    // Config.cs defaults: chord on (Ctrl+L), lockphrase off, hold on (1200 ms), timer off.
    for (const [name, chip] of [
      ['Keyboard chord', 'on · Ctrl+L'],
      ['Lockphrase', 'off'],
      ['Hold to unlock', 'on · ~1.2 s'],
      ['Auto timer', 'off'],
    ]) {
      await expect(rows.filter({ hasText: name }).locator('.chip')).toHaveText(chip)
    }
    await expect(rows).toHaveCount(4)
  })

  test('every download row points at the latest release', async ({ page }) => {
    await page.goto('/')
    const rows = page.getByRole('region', { name: 'Download' }).getByRole('link')
    await expect(rows).toHaveCount(5)
    for (const row of await rows.all()) await expect(row).toHaveAttribute('href', RELEASES)
    for (const file of [
      'Pawse-Setup-x.y.z-full.exe',
      'Pawse-Setup-x.y.z-min.exe',
      'Pawse-Setup-x.y.z.exe',
      'Pawse-x.y.z.zip',
      'Pawse-x.y.z-min.zip',
    ]) {
      await expect(rows.filter({ hasText: file })).toHaveCount(1)
    }
    await expect(rows.first()).toContainText('Recommended')
  })

  test('the footer leads to the privacy page and back', async ({ page }) => {
    const problems = await guard(page)
    await page.goto('/')
    await page.getByRole('contentinfo').getByRole('link', { name: 'Privacy' }).click()
    await expect(page).toHaveURL(/\/privacy\.html$/)
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Privacy')
    await page.getByRole('link', { name: 'pawse.at' }).first().click()
    await expect(page).toHaveURL(/\/$/)
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('pawse.')
    expect(problems).toEqual([])
  })

  test('link previews and the tab icon have their files', async ({ page, request }) => {
    await page.goto('/')
    await expect(page.locator('link[rel="canonical"]')).toHaveAttribute('href', 'https://www.pawse.at/')
    const og = await page.locator('meta[property="og:image"]').getAttribute('content')
    expect(og).toMatch(/^https:\/\/www\.pawse\.at\/og\.png/)
    const icon = await page.locator('link[rel="icon"]').getAttribute('href')
    for (const path of [new URL(og!).pathname, icon!]) {
      const res = await request.get(path)
      expect(res.status(), path).toBe(200)
    }
    // The structured data is data, never a script - and it must parse.
    const ld = await page.locator('script[type="application/ld+json"]').textContent()
    const graph = JSON.parse(ld!)['@graph'] as { '@type': string; downloadUrl?: string }[]
    expect(graph.map((n) => n['@type'])).toEqual(['WebSite', 'SoftwareApplication'])
    expect(graph[1].downloadUrl).toBe(RELEASES)
  })
})

test.describe('privacy', () => {
  test('covers the app, the site and downloads, and says where to ask', async ({ page }) => {
    const problems = await guard(page)
    await page.goto('/privacy.html')
    for (const name of ['The app', 'This website', 'Downloads', 'Questions and changes']) {
      await expect(page.getByRole('heading', { level: 2, name })).toBeVisible()
    }
    await expect(page.getByRole('heading', { level: 3, name: 'What it stores on your PC' })).toBeVisible()
    const ask = page.getByRole('paragraph').filter({ hasText: 'Ask on GitHub' })
    await expect(ask.getByRole('link', { name: 'GitHub' })).toHaveAttribute(
      'href',
      'https://github.com/phoen-ix/pawse/issues',
    )
    expect(problems).toEqual([])
  })

  test('the site keeps its own promise: no cookies, no scripts', async ({ page, context }) => {
    for (const p of PAGES) {
      await page.goto(p.path)
      // JSON-LD is data, not a script that runs.
      await expect(page.locator('script:not([type="application/ld+json"])')).toHaveCount(0)
    }
    expect(await context.cookies()).toEqual([])
  })
})

test.describe('update feed', () => {
  test('latest.json is the shape the app checks downloads against', async ({ request }) => {
    const res = await request.get('/latest.json')
    expect(res.status()).toBe(200)
    const feed = await res.json()
    expect(feed.version).toMatch(/^\d+\.\d+\.\d+$/)
    expect(feed.notes).toBe(`https://github.com/phoen-ix/pawse/releases/tag/v${feed.version}`)
    expect(typeof feed.auto).toBe('boolean')
    const base = `https://github.com/phoen-ix/pawse/releases/download/v${feed.version}/`
    const assets = {
      'installers.full': `Pawse-Setup-${feed.version}-full.exe`,
      'installers.min': `Pawse-Setup-${feed.version}-min.exe`,
      'portable.full': `Pawse-${feed.version}.zip`,
      'portable.min': `Pawse-${feed.version}-min.zip`,
    }
    for (const [key, file] of Object.entries(assets)) {
      const [group, build] = key.split('.')
      const asset = feed[group][build]
      expect(asset.url, key).toBe(base + file)
      expect(asset.sha256, key).toMatch(/^[0-9a-f]{64}$/)
    }
  })
})

test.describe('search engines', () => {
  test('robots.txt keeps the feed out and names the sitemap', async ({ request }) => {
    const robots = await (await request.get('/robots.txt')).text()
    expect(robots).toContain('Disallow: /latest.json')
    expect(robots).toContain('Sitemap: https://www.pawse.at/sitemap.xml')
  })

  test('every page in the sitemap exists', async ({ request }) => {
    const xml = await (await request.get('/sitemap.xml')).text()
    const locs = [...xml.matchAll(/<loc>([^<]+)<\/loc>/g)].map((m) => new URL(m[1]).pathname)
    expect(locs).toEqual(['/', '/privacy.html'])
    for (const path of locs) expect((await request.get(path)).status(), path).toBe(200)
  })
})
