// Layout: multi-select with Ctrl- and Shift-click and actions on the selection, reading pane
// position and density (kept across reloads), the folder pane toggle.
// Needs: dev/dev.sh up, the app deployed, and an account for alice@example.test (node mail.mjs creates one).
import { execSync } from 'node:child_process'
import { check, isUnread, messageRows, openAccount, start } from './lib.mjs'

const suffix = Date.now().toString().slice(-6)
const subjects = ['one', 'two', 'three'].map((n) => `Layout ${n} ${suffix}`)
for (const subject of subjects) {
	execSync('curl -s --url smtp://127.0.0.1:3025 --mail-from layout@example.test --mail-rcpt alice@example.test --upload-file -',
		{ input: `From: Layout <layout@example.test>\r\nTo: alice@example.test\r\nSubject: ${subject}\r\n\r\nhello\r\n` })
}

const { page, step, finish } = await start()
const items = messageRows(page)
const item = (subject) => items.filter({ hasText: subject }).first()
const commands = page.getByRole('toolbar', { name: 'Message actions' })

async function until(condition) {
	for (let i = 0; i < 40; i++) {
		if (await condition()) {
			return true
		}
		await page.waitForTimeout(250)
	}
	return false
}

async function viewSetting(name) {
	await page.getByRole('button', { name: 'View settings' }).click()
	await page.getByRole('menuitemradio', { name }).click()
	await page.keyboard.press('Escape')
}

await openAccount(page, /^Alice/)
await page.locator('.message-list__header h2', { hasText: /^Inbox$/ }).waitFor()
await item(subjects[0]).waitFor({ timeout: 20000 })
step('inbox shown')

step('multi-select')
// Newest first: three, two, one.
await item(subjects[2]).click({ modifiers: ['Control'] })
await item(subjects[0]).click({ modifiers: ['Shift'] })
check(await until(async () => (await page.locator('.sm-selection h2').innerText().catch(() => '')).includes('3 messages selected')),
	'Ctrl-click then Shift-click selects the range')
check(await commands.getByRole('button', { name: 'Reply', exact: true }).isDisabled(), 'Reply is off for several messages')
await commands.getByRole('button', { name: 'Mark as read' }).click()
check(await until(async () => !(await isUnread(item(subjects[0]))) && !(await isUnread(item(subjects[1]))) && !(await isUnread(item(subjects[2])))),
	'"Mark as read" in the command bar marks all three')
await commands.getByRole('button', { name: 'Flag' }).click()
check(await until(async () => (await items.filter({ hasText: suffix }).locator('.message-row__flag').count()) === 3), '"Flag" flags all three')
await page.getByRole('button', { name: 'Clear selection' }).click()
check(await page.locator('.message-row--selected').count() === 0, '"Clear selection" empties the selection')

step('reading pane and density')
await viewSetting('Bottom')
check(await until(async () => await page.locator('.sm-panes--bottom').count() === 1), 'reading pane at the bottom')
await viewSetting('Compact')
check(await until(async () => await page.locator('.message-list--compact').count() === 1), 'compact list')
await page.reload()
await page.locator('.message-row').first().waitFor({ timeout: 30000 })
check(await page.locator('.sm-panes--bottom').count() === 1 && await page.locator('.message-list--compact').count() === 1, 'both are kept after a reload')
await viewSetting('Off')
await openAccount(page, /^Alice/)
// The reload opened all inboxes, which list the same message: wait for the account's own list.
await page.locator('.message-list__header h2', { hasText: /^Inbox$/ }).waitFor()
await item(subjects[1]).click()
await page.locator('.message-view h2', { hasText: subjects[1] }).waitFor()
check(!(await page.locator('.sm-panes__list').isVisible()), 'reading pane off: the message replaces the list')
await page.getByRole('button', { name: 'Back to the list' }).click()
check(await until(async () => await page.locator('.sm-panes__list').isVisible()), '"Back to the list" shows the list again')
await viewSetting('Right')
await viewSetting('Comfortable')

step('folder pane')
await page.getByRole('button', { name: 'Hide folders' }).click()
check(!(await page.locator('.folder-pane').isVisible()), '"Hide folders" hides the folder pane')
await page.getByRole('button', { name: 'Show folders' }).click()
check(await page.locator('.folder-pane').isVisible(), '"Show folders" brings it back')

await finish()
