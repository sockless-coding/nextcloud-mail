// The message row menu: right-click, the ⋯ hover button and the keyboard open it; it acts on the
// row, or on the selection the row is part of; Reply works on a message that is not open.
// Needs: dev/dev.sh up, the app deployed, and an account for alice@example.test (node mail.mjs creates one).
import { execSync } from 'node:child_process'
import { check, isUnread, messageRows, openInbox, rowAction, rowMenuAction, start } from './lib.mjs'

const suffix = Date.now().toString().slice(-6)
const [first, second, third] = ['one', 'two', 'three'].map((what) => `Menu ${what} ${suffix}`)
for (const subject of [third, second, first]) {
	execSync('curl -s --url smtp://127.0.0.1:3025 --mail-from menu@example.test --mail-rcpt alice@example.test --upload-file -',
		{ input: `From: Menu Sender <menu@example.test>\r\nTo: alice@example.test\r\nSubject: ${subject}\r\n\r\nhello\r\n` })
}

const { page, step, finish } = await start()
const items = messageRows(page)
const item = (subject) => items.filter({ hasText: subject }).first()
const menuItems = async () => (await page.getByRole('menuitem').allInnerTexts()).map((s) => s.trim())

async function until(condition) {
	for (let i = 0; i < 40; i++) {
		if (await condition()) {
			return true
		}
		await page.waitForTimeout(250)
	}
	return false
}

await openInbox(page, /^Alice/)
await item(first).waitFor({ timeout: 30000 })

step('hover bar')
await item(first).hover()
const hover = await item(first).locator('.message-row__actions button').evaluateAll((b) => b.map((e) => e.getAttribute('aria-label')))
check(hover.length === 4 && hover.at(-1) === 'More actions' && !hover.includes('Move…'), `hover bar: ${hover.join(', ')}`)

step('right-click')
await item(first).click({ button: 'right' })
await page.getByRole('menuitem', { name: 'Reply', exact: true }).waitFor()
const single = await menuItems()
check(['Open', 'Reply', 'Reply all', 'Forward', 'Mark as read', 'Flag', 'Move…', 'Delete', 'Add sender to contacts', 'Download message (.eml)'].every((n) => single.includes(n)),
	`one message: ${single.join(', ')}`)
check((await item(first).getAttribute('class')).includes('message-row--menu'), 'the row the menu is for is marked')
check(await page.locator('.message-view').count() === 0 || !(await page.locator('.message-view h2', { hasText: first }).count()), 'right-click does not open the message')
await page.getByRole('menuitem', { name: 'Mark as read', exact: true }).click()
check(await until(async () => !(await isUnread(item(first)))), '"Mark as read" from the menu')
check(await until(async () => await page.evaluate(() => document.activeElement?.classList.contains('message-row'))), 'focus is back on a row')

step('keyboard')
await item(second).focus()
await page.keyboard.press('Shift+F10')
await page.getByRole('menuitem', { name: 'Flag', exact: true }).waitFor()
check(await until(async () => await page.evaluate(() => !!document.activeElement?.closest('[role="menu"]'))), 'Shift+F10 opens the menu, focus in it')
if (process.env.MENU_SCREENSHOT) {
	await page.screenshot({ path: process.env.MENU_SCREENSHOT })
}
await page.getByRole('menuitem', { name: 'Flag', exact: true }).click()
await item(second).locator('.message-row__flag').waitFor()
check(true, '"Flag" from the keyboard menu')

step('selection')
await item(first).click({ modifiers: ['Control'] })
await item(third).click({ modifiers: ['Control'] })
await item(third).click({ button: 'right' })
await page.getByRole('menuitem', { name: 'Delete 2 messages' }).waitFor()
const multi = await menuItems()
check(!multi.includes('Reply') && !multi.includes('Download message (.eml)'), `selection: ${multi.join(', ')}`)
// The first is read, the third is not: the menu offers what changes something.
check(multi.includes('Mark as read') && multi.includes('Flag'), 'selection gets read state and flag for all')
await page.getByRole('menuitem', { name: 'Mark as read' }).click()
check(await until(async () => !(await isUnread(item(first))) && !(await isUnread(item(third)))), '"Mark as read" on both selected')
await page.keyboard.press('Escape')
// A row outside the selection gets its own menu.
await item(first).click({ modifiers: ['Control'] })
await item(second).click({ button: 'right' })
await page.getByRole('menuitem', { name: 'Reply', exact: true }).waitFor()
check(true, 'right-click outside the selection: menu for that row alone')
await page.keyboard.press('Escape')
await page.keyboard.press('Escape')

step('reply from the menu')
await rowMenuAction(item(third), 'Reply')
const composer = page.getByRole('dialog').filter({ has: page.getByLabel('Subject') })
await composer.waitFor({ timeout: 15000 })
check(await until(async () => (await composer.getByLabel('Subject').inputValue()) === `Re: ${third}`), 'Reply prefills the subject of that row')
await composer.getByRole('button', { name: 'Discard' }).click()
await composer.waitFor({ state: 'detached' })

step('⋯ button')
await rowAction(item(second), 'More actions')
await page.getByRole('menuitem', { name: 'Move…', exact: true }).waitFor()
check(true, '⋯ opens the menu')
await page.keyboard.press('Escape')

await finish()
