// Reading mail: paging, safe HTML view, remote images via the proxy, attachments, polling.
// Needs: dev/dev.sh up, the app deployed, dev/dev.sh seed-mail. Adds an account for
// alice@example.test if the user has none for that address.
import { execFileSync, execSync } from 'node:child_process'
import { appPage, base, check, mailHost, messageRows, openInbox, start } from './lib.mjs'

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
await openInbox(page, aliceName)

const items = messageRows(page)
await items.first().waitFor({ timeout: 30000 })
step(`list: "${(await page.locator('.message-list__header').innerText()).replace(/\s+/g, ' ')}", ${await items.count()} shown`)
if (await page.getByRole('button', { name: 'Load older messages' }).count()) {
	const before = await items.count()
	await page.getByRole('button', { name: 'Load older messages' }).click()
	await page.waitForFunction((n) => document.querySelectorAll('.message-list li.message-row').length > n, before)
	step(`after "Load older messages": ${await items.count()} shown`)
}

// Other checks fill the inbox; search for the newsletter when it is not among the loaded pages.
if (await items.filter({ hasText: 'Weekly newsletter' }).count() === 0) {
	await page.getByRole('searchbox').fill('"Weekly newsletter"')
	await page.getByRole('searchbox').press('Enter')
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

// Headers, source and .eml download from the "More actions" menu.
const moreActions = page.locator('.message-view__actions').getByRole('button', { name: 'More actions' })
for (const [action, kind, expected] of [['View headers', 'headers', /^Subject: Weekly newsletter/m], ['View source', 'source', /Content-Type: multipart\//]]) {
	await moreActions.click()
	await page.getByRole('menuitem', { name: action }).click()
	const raw = page.locator(`[data-sockless-raw="${kind}"]`)
	await raw.waitFor({ timeout: 20000 })
	const text = await raw.innerText()
	check(expected.test(text), `${action}: ${text.split('\n').length} lines, matches ${expected}`)
	if (kind === 'headers') {
		check(!text.includes('<h1'), 'View headers: no body')
	}
	await page.getByRole('dialog').getByRole('button', { name: 'Close' }).click()
	await raw.waitFor({ state: 'detached' })
}
await moreActions.click()
const emlHref = await page.getByRole('menuitem', { name: 'Download message (.eml)' }).getAttribute('href')
await page.keyboard.press('Escape')
const eml = await page.request.get(new URL(emlHref, base).toString())
const emlText = await eml.text()
check(eml.status() === 200 && /\.eml/.test(eml.headers()['content-disposition']) && /^Subject: Weekly newsletter/m.test(emlText),
	`eml: ${eml.status()} | ${eml.headers()['content-type']} | ${eml.headers()['content-disposition']} | ${emlText.length} chars`)

await page.getByRole('button', { name: 'Show images' }).click()
await page.waitForFunction(() => document.querySelector('iframe.message-body')?.contentDocument?.querySelector('img[alt="remote image"]')?.getAttribute('src'), null, { timeout: 20000 })
await page.waitForTimeout(3000)
step(`remote image after "Show images": ${await page.locator('iframe.message-body').evaluate((f) => {
	const img = f.contentDocument.querySelector('img[alt="remote image"]')
	return `${img.getAttribute('src').replace(/token=.*/, 'token=…')} loaded=${img.naturalWidth > 0}`
})}`)

// Back to the inbox list itself if the newsletter had to be searched for.
if (await page.getByRole('button', { name: 'Clear search' }).count()) {
	await page.getByRole('button', { name: 'Clear search' }).click()
	await page.locator('.message-list__header h2', { hasText: /^Inbox$/ }).waitFor()
}

const subject = `Polling check ${Date.now()}`
execSync('curl -s --url smtp://127.0.0.1:3025 --mail-from poll@example.test --mail-rcpt alice@example.test --upload-file -',
	{ input: `From: Poll <poll@example.test>\r\nTo: alice@example.test\r\nSubject: ${subject}\r\n\r\nhello\r\n` })
await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))
await page.waitForFunction((s) => document.querySelector('.message-list li.message-row')?.innerText.includes(s), subject, { timeout: 15000 })
step('polling: new mail appeared at the top')

// Deleted by another mail program: the next poll drops it from the list.
const imap = (command) => execFileSync('curl', ['-s', 'imap://127.0.0.1:3143/INBOX', '-u', 'alice@example.test:alice', '-X', command]).toString()
const uid = imap(`UID SEARCH SUBJECT "${subject}"`).match(/SEARCH (\d+)/)[1]
imap(`UID STORE ${uid} +FLAGS (\\Deleted)`)
imap('EXPUNGE')
await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))
await page.waitForFunction((s) => ![...document.querySelectorAll('.message-list li.message-row')].some((li) => li.innerText.includes(s)), subject, { timeout: 20000 })
step('polling: mail deleted elsewhere disappeared')

console.log(`alert dialogs: ${dialogs.length ? dialogs.join(' | ') : 'none'}`)
await finish()
