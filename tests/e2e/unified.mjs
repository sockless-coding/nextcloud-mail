// Unified folders: all accounts' inboxes in one list, opening, actions and polling there; the
// folder pane's unified and smart folders.
// Needs: dev/dev.sh up and the app deployed. Adds accounts for alice@example.test and
// bob@example.test (GreenMail users) if the user has none for those addresses.
import { execSync } from 'node:child_process'
import { appPage, base, check, isUnread, mailHost, messageRows, openInbox, rowAction, start } from './lib.mjs'

const suffix = Date.now().toString().slice(-6)
const [aliceSubject, bobSubject, laterSubject] = ['alice', 'bob', 'later'].map((who) => `Unified ${who} ${suffix}`)
const send = (to, subject) => execSync(`curl -s --url smtp://127.0.0.1:3025 --mail-from unified@example.test --mail-rcpt ${to} --upload-file -`,
	{ input: `From: Unified <unified@example.test>\r\nTo: ${to}\r\nSubject: ${subject}\r\n\r\nhello\r\n` })

const { page, step, finish } = await start()
const items = messageRows(page)
const item = (subject) => items.filter({ hasText: subject }).first()
const header = page.locator('.message-list__header h2')
const unifiedEntry = page.getByRole('navigation', { name: 'Accounts' }).getByRole('button', { name: 'All accounts' })
const view = (id) => page.locator(`.folder-pane__unified[data-view="${id}"]`)
const unifiedInboxShown = () => view('inbox').and(page.locator('[aria-current="page"]')).waitFor({ timeout: 30000 })
// The folder pane's count: the rail's badge stops at 99+.
const unifiedUnread = async () => {
	const counter = view('inbox').locator('.folder-pane__count')
	return (await counter.count()) ? Number((await counter.innerText()).trim()) : 0
}

/** Polls an async condition, up to the given number of seconds. */
async function until(condition, seconds = 10) {
	for (let i = 0; i < seconds * 4; i++) {
		if (await condition()) {
			return true
		}
		await page.waitForTimeout(250)
	}
	return false
}

const apiBase = await page.evaluate(async () => {
	const state = JSON.parse(atob(document.getElementById('initial-state-app_api-sockless_mail_config')?.value ?? 'e30='))
	const config = typeof state === 'string' ? JSON.parse(state) : state
	return config.apiMode === 'harp' ? '/exapps/sockless_mail/api' : '/index.php/apps/app_api/proxy/sockless_mail/api'
})
const headers = { 'X-Sockless-Request': '1', requesttoken: await page.evaluate(() => OC.requestToken) }
const existing = await (await page.request.get(`${base}${apiBase}/accounts`, { headers })).json()
for (const [name, address, password] of [['Alice', 'alice@example.test', 'alice'], ['Bob', 'bob@example.test', 'bob']]) {
	if (!existing.some((a) => a.emailAddress === address)) {
		await page.request.post(`${base}${apiBase}/accounts`, {
			headers,
			data: {
				displayName: name,
				emailAddress: address,
				username: address,
				password,
				imap: { host: mailHost, port: 3143, security: 'none' },
				smtp: { host: mailHost, port: 3025, security: 'none' },
			},
		})
	}
}
const accounts = await (await page.request.get(`${base}${apiBase}/accounts`, { headers })).json()
const bobName = accounts.find((a) => a.emailAddress === 'bob@example.test').displayName

send('alice@example.test', aliceSubject)
// A second apart, so the dates order them.
await page.waitForTimeout(1100)
send('bob@example.test', bobSubject)

// With several accounts, the app opens on all inboxes.
await page.goto(appPage)
await unifiedInboxShown()
step('unified inbox shown')
check(await page.locator('.folder-pane__heading').count() === 0, 'the folder pane lists no accounts')
check(await view('unread').count() === 1 && await view('flagged').count() === 1, 'the smart folders are listed')
check(await until(async () => (await item(aliceSubject).count()) > 0 && (await item(bobSubject).count()) > 0, 20),
	'messages of both accounts are listed')
const subjects = await items.allInnerTexts()
check(subjects.findIndex((s) => s.includes(bobSubject)) < subjects.findIndex((s) => s.includes(aliceSubject)), 'newest first across accounts')
check((await item(bobSubject).innerText()).includes(bobName), 'each message names its account')

step('open and read')
const unreadBefore = await unifiedUnread()
await item(aliceSubject).click()
await page.locator('.message-view h2', { hasText: aliceSubject }).waitFor({ timeout: 20000 })
check(await until(async () => (await unifiedUnread()) === unreadBefore - 1), 'opening a message lowers the unread count of all inboxes')
check(await until(async () => !(await isUnread(item(aliceSubject)))), 'the opened message is shown as read')
check(await view('inbox').getAttribute('aria-current') === 'page', 'opening a message stays in the unified inbox')

step('delete from the list')
await rowAction(item(bobSubject), 'Delete')
// GreenMail has no Trash folder: confirm deleting permanently.
const confirm = page.getByRole('dialog', { name: 'Delete permanently?' })
if (await until(async () => (await confirm.count()) > 0, 3)) {
	await confirm.getByRole('button', { name: 'Delete permanently' }).click()
}
check(await until(async () => (await item(bobSubject).count()) === 0), 'the deleted message leaves the list')
await page.waitForTimeout(1000)
check((await item(bobSubject).count()) === 0, 'and stays away')

step('polling')
send('bob@example.test', laterSubject)
check(await until(async () => (await item(laterSubject).count()) > 0, 40), 'new mail of any account appears without reloading')
check((await items.first().innerText()).includes(laterSubject), 'new mail goes on top')

step('switching views')
await openInbox(page, bobName)
check(await until(async () => (await item(laterSubject).count()) > 0), 'the account inbox shows its own mail')
await unifiedEntry.click()
await unifiedInboxShown()
check(await until(async () => (await item(aliceSubject).count()) > 0 && (await item(laterSubject).count()) > 0), 'back in all inboxes')

step('the Unread smart folder')
await view('unread').click()
await header.filter({ hasText: 'Unread' }).waitFor()
check(await until(async () => (await item(laterSubject).count()) > 0 && (await item(aliceSubject).count()) === 0, 20), 'Unread lists unread mail only')
await item(laterSubject).click()
await page.locator('.message-view h2', { hasText: laterSubject }).waitFor({ timeout: 20000 })
await page.waitForTimeout(1000)
check((await item(laterSubject).count()) === 1, 'a message read in Unread stays until the folder is shown again')
await unifiedEntry.click()
check(await view('unread').getAttribute('aria-current') === 'page', 'the rail keeps the unified folder shown')

await finish()
