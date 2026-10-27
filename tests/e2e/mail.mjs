// Reading mail: paging, safe HTML view, remote images via the proxy, attachments, polling.
// Needs: dev/dev.sh up, the app deployed, dev/dev.sh seed-mail. Adds an account for
// alice@example.test if the user has none for that address.
import { execSync } from 'node:child_process'
import { appPage, base, mailHost, start } from './lib.mjs'

const { page, step, finish } = await start()
const dialogs = []
page.on('dialog', async (d) => {
	dialogs.push(d.message())
	await d.dismiss()
})

// The API base the frontend uses (HaRP or PHP proxy) is read from the page itself.
const apiBase = await page.evaluate(async () => {
	const state = JSON.parse(atob(document.getElementById('initial-state-app_api-sockless_mail_config')?.value ?? 'e30='))
	const config = typeof state === 'string' ? JSON.parse(state) : state
	return config.apiMode === 'harp' ? '/exapps/sockless_mail/api' : '/index.php/apps/app_api/proxy/sockless_mail/api'
})
const headers = { 'X-Sockless-Request': '1', requesttoken: await page.evaluate(() => OC.requestToken) }
const accounts = await (await page.request.get(`${base}${apiBase}/accounts`, { headers })).json()
if (!accounts.some((a) => a.emailAddress === 'alice@example.test')) {
	await page.request.post(`${base}${apiBase}/accounts`, {
		headers,
		data: {
			displayName: 'Alice',
			emailAddress: 'alice@example.test',
			username: 'alice@example.test',
			password: 'alice',
			imap: { host: mailHost, port: 3143, security: 'none' },
			smtp: { host: mailHost, port: 3025, security: 'none' },
		},
	})
	await page.goto(appPage)
}

const aliceName = (await (await page.request.get(`${base}${apiBase}/accounts`, { headers })).json())
	.find((a) => a.emailAddress === 'alice@example.test').displayName
await page.locator('li.account-entry .app-navigation-entry__name', { hasText: new RegExp(`^${aliceName}$`) }).first().click()

const items = page.locator('.message-list li.list-item__wrapper')
await items.first().waitFor({ timeout: 30000 })
step(`list: "${(await page.locator('.message-list__header').innerText()).replace(/\s+/g, ' ')}", ${await items.count()} shown`)
if (await page.getByRole('button', { name: 'Load older messages' }).count()) {
	const before = await items.count()
	await page.getByRole('button', { name: 'Load older messages' }).click()
	await page.waitForFunction((n) => document.querySelectorAll('.message-list li.list-item__wrapper').length > n, before)
	step(`after "Load older messages": ${await items.count()} shown`)
}

await items.filter({ hasText: 'Weekly newsletter' }).first().click()
await page.frameLocator('iframe.message-body').locator('h1').waitFor({ timeout: 20000 })
await page.waitForTimeout(1000)
const view = await page.locator('iframe.message-body').evaluate((frame) => {
	const doc = frame.contentDocument
	const img = (alt) => doc.querySelector(`img[alt="${alt}"]`)
	return {
		scripts: doc.querySelectorAll('script').length,
		eventHandlers: doc.querySelectorAll('[onclick]').length,
		styleApplied: getComputedStyle(doc.querySelector('h1')).color === 'rgb(0, 130, 201)',
		inlineImageLoaded: img('logo')?.naturalWidth > 0,
		remoteImageBlocked: !img('remote image')?.getAttribute('src'),
		sandbox: frame.getAttribute('sandbox'),
	}
})
step(`newsletter: ${JSON.stringify(view)}`)

const href = await page.locator('a.message-view__attachment').first().getAttribute('href')
const download = await page.request.get(new URL(href, base).toString())
step(`attachment: ${download.status()} | ${download.headers()['content-disposition']} | ${download.headers()['content-security-policy']}`)

await page.getByRole('button', { name: 'Show images' }).click()
await page.waitForFunction(() => document.querySelector('iframe.message-body')?.contentDocument?.querySelector('img[alt="remote image"]')?.getAttribute('src'), null, { timeout: 20000 })
await page.waitForTimeout(3000)
step(`remote image after "Show images": ${await page.locator('iframe.message-body').evaluate((f) => {
	const img = f.contentDocument.querySelector('img[alt="remote image"]')
	return `${img.getAttribute('src').replace(/token=.*/, 'token=…')} loaded=${img.naturalWidth > 0}`
})}`)

const subject = `Polling check ${Date.now()}`
execSync('curl -s --url smtp://127.0.0.1:3025 --mail-from poll@example.test --mail-rcpt alice@example.test --upload-file -',
	{ input: `From: Poll <poll@example.test>\r\nTo: alice@example.test\r\nSubject: ${subject}\r\n\r\nhello\r\n` })
await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))
await page.waitForFunction((s) => document.querySelector('.message-list li.list-item__wrapper')?.innerText.includes(s), subject, { timeout: 15000 })
step('polling: new mail appeared at the top')

console.log(`alert dialogs: ${dialogs.length ? dialogs.join(' | ') : 'none'}`)
await finish()
