// Preview text in the list and the quick filters: Unread, Flagged, Attachments, in a folder and
// in all inboxes, kept current by polling.
// Needs: dev/dev.sh up, the app deployed, accounts for alice@example.test and bob@example.test
// (node unified.mjs adds them).
import { execFileSync, execSync } from 'node:child_process'
import { check, messageRows, openAccount, start } from './lib.mjs'

const suffix = Date.now().toString().slice(-6)
const [unreadSubject, readSubject, flaggedSubject, fileSubject, laterSubject] =
	['unread', 'read', 'flagged', 'file', 'later'].map((what) => `Filter ${what} ${suffix}`)
const send = (subject, body, headers = 'Content-Type: text/plain; charset=utf-8') => execSync(
	'curl -s --url smtp://127.0.0.1:3025 --mail-from filters@example.test --mail-rcpt alice@example.test --upload-file -',
	{ input: `From: Filters <filters@example.test>\r\nTo: alice@example.test\r\nSubject: ${subject}\r\n${headers}\r\n\r\n${body}\r\n` })
const imap = (command) => execFileSync('curl', ['-s', 'imap://127.0.0.1:3143/INBOX', '-u', 'alice@example.test:alice', '-X', command]).toString()
const store = (subject, flag) => imap(`UID STORE ${imap(`UID SEARCH SUBJECT "${subject}"`).match(/SEARCH (\d+)/)[1]} +FLAGS (${flag})`)

send(unreadSubject, `Quarterly numbers ${suffix} are in, see the sheet.`)
send(readSubject, 'Already read')
send(flaggedSubject, 'Important')
send(fileSubject, [
	'--b1', 'Content-Type: text/plain', '', 'See the file.',
	'--b1', 'Content-Type: application/pdf; name="report.pdf"', 'Content-Disposition: attachment; filename="report.pdf"',
	'Content-Transfer-Encoding: base64', '', 'JVBERi0xLjQK', '--b1--',
].join('\r\n'), 'MIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary="b1"')
store(readSubject, '\\Seen')
store(flaggedSubject, '\\Seen \\Flagged')
store(fileSubject, '\\Seen')

const { page, step, finish } = await start()
const items = messageRows(page)
const item = (subject) => items.filter({ hasText: subject }).first()
const shown = async () => (await items.allInnerTexts()).filter((s) => s.includes(suffix))
const chip = (name) => page.getByRole('group', { name: 'Show' }).getByRole('button', { name, exact: true })

async function until(condition, seconds = 10) {
	for (let i = 0; i < seconds * 4; i++) {
		if (await condition()) {
			return true
		}
		await page.waitForTimeout(250)
	}
	return false
}

await openAccount(page, /^Alice/)
await page.locator('.message-list__header h2', { hasText: /^Inbox$/ }).waitFor()
await item(unreadSubject).waitFor({ timeout: 20000 })
step('inbox shown')
check((await item(unreadSubject).innerText()).includes(`Quarterly numbers ${suffix} are in`), 'the row shows the start of the text')

step('filters in a folder')
await chip('Unread').click()
check(await until(async () => {
	const rows = await shown()
	return rows.some((s) => s.includes(unreadSubject)) && !rows.some((s) => s.includes(readSubject) || s.includes(flaggedSubject) || s.includes(fileSubject))
}), 'Unread shows only unread messages')
check(await chip('Unread').getAttribute('aria-pressed') === 'true', 'the chip shows it is on')
await chip('Flagged').click()
check(await until(async () => (await shown()).length === 1 && (await shown())[0].includes(flaggedSubject)), 'Flagged shows only the flagged message')
await chip('Attachments').click()
check(await until(async () => (await shown()).length === 1 && (await shown())[0].includes(fileSubject)), 'Attachments shows only the message with a file')

step('polling in a filter')
await chip('Unread').click()
await item(unreadSubject).waitFor()
send(laterSubject, 'Arrived later')
await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))
check(await until(async () => (await item(laterSubject).count()) > 0, 20), 'new unread mail appears in the Unread filter')
await item(unreadSubject).click()
await page.locator('.message-view h2', { hasText: unreadSubject }).waitFor()
await page.waitForTimeout(1000)
check(await item(unreadSubject).count() === 1, 'a message read in the Unread filter stays until the filter is shown again')
await chip('All').click()
check(await until(async () => (await item(readSubject).count()) > 0), '"All" shows every message again')

step('filters in the unified inbox')
await page.getByRole('navigation', { name: 'Accounts' }).getByRole('button', { name: 'All accounts' }).click()
await page.locator('.folder-pane__unified[data-view="inbox"][aria-current="page"]').waitFor()
await chip('Flagged').click()
check(await until(async () => (await item(flaggedSubject).count()) > 0 && (await item(readSubject).count()) === 0, 20), 'Flagged across all inboxes')
check((await item(flaggedSubject).innerText()).includes('Alice'), 'the row names its account')
await chip('All').click()

step('the Flagged smart folder')
await page.locator('.folder-pane__unified[data-view="flagged"]').click()
await page.locator('.message-list__header h2', { hasText: 'Flagged' }).waitFor()
check(await until(async () => (await item(flaggedSubject).count()) > 0 && (await item(readSubject).count()) === 0, 20), 'Flagged lists flagged mail of every account')
check(/Alice.* › Inbox/.test(await item(flaggedSubject).innerText()), 'the row names its account and folder')
check(await page.locator('.message-list__filters').count() === 0, 'a smart folder has no filter chips')

await finish()
