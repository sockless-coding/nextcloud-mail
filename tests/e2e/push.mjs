// Push (HaRP only): the tab holds a WebSocket to /api/hub, stops polling the inbox, and shows
// new mail within seconds of its arrival. Needs: dev/dev.sh up and the app deployed through HaRP
// (dev/dev.sh register-docker). Adds an account for alice@example.test if the user has none.
import { execSync } from 'node:child_process'
import { base, check, mailHost, messageRows, openInbox, start } from './lib.mjs'

const subject = `Pushed ${Date.now().toString().slice(-6)}`
const send = (to) => execSync(`curl -s --url smtp://127.0.0.1:3025 --mail-from push@example.test --mail-rcpt ${to} --upload-file -`,
	{ input: `From: Push <push@example.test>\r\nTo: ${to}\r\nSubject: ${subject}\r\n\r\nhello\r\n` })

const { page, step, finish } = await start()
const sockets = []
page.on('websocket', (ws) => sockets.push(ws))
const changeRequests = []
page.on('request', (r) => {
	if (r.url().includes('/changes?')) {
		changeRequests.push(Date.now())
	}
})

const apiBase = '/exapps/sockless_mail/api'
const headers = { 'X-Sockless-Request': '1', requesttoken: await page.evaluate(() => OC.requestToken) }
const existing = await (await page.request.get(`${base}${apiBase}/accounts`, { headers })).json()
if (!existing.some((a) => a.emailAddress === 'alice@example.test')) {
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
	await page.reload()
}
const accounts = await (await page.request.get(`${base}${apiBase}/accounts`, { headers })).json()

step('the tab connects to the hub over a WebSocket')
await openInbox(page, accounts.find((a) => a.emailAddress === 'alice@example.test').displayName)
await messageRows(page).first().waitFor({ timeout: 30000 }).catch(() => {})
await page.waitForTimeout(2000)
const hub = sockets.find((ws) => ws.url().includes('/exapps/sockless_mail/api/hub'))
check(!!hub && !hub.isClosed(), `WebSocket open (${hub?.url().split('?')[0] ?? 'none'})`)

step('no inbox polling while connected')
const before = changeRequests.length
await page.waitForTimeout(20_000)
check(changeRequests.length === before, `changes requests in 20 s: ${changeRequests.length - before} (15 s polling would make one)`)

step('new mail shows up after a push')
const sentAt = Date.now()
send('alice@example.test')
const row = messageRows(page).filter({ hasText: subject }).first()
const shown = await row.waitFor({ timeout: 12_000 }).then(() => true, () => false)
check(shown, `message listed ${shown ? `after ${((Date.now() - sentAt) / 1000).toFixed(1)} s` : 'never'}`)
check(changeRequests.some((t) => t >= sentAt), 'the push made the tab ask the changes endpoint')

await finish()
