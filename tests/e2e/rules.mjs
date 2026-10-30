// Mail rules: create a rule in the account's Rules dialog (with validation and "Test"), then
// check that the server sorts new mail into a folder by itself and leaves earlier mail alone;
// switch the rule off, reorder, edit; create a rule from a mailing-list message and run it on
// the messages already there; delete. Sends its own messages and removes its rules.
// Needs: dev/dev.sh up, the app deployed, and an account for alice@example.test (node mail.mjs creates one).
import { execSync } from 'node:child_process'
import { accountAction, check, folderRow, messageRows, openAccount, rowMenuAction, start } from './lib.mjs'

const suffix = Date.now().toString().slice(-6)
const sender = `rules-${suffix}@example.test`
const folder = `Rules ${suffix}`
const [before, after, other, listed] = ['before', 'after', 'other', 'listed'].map((what) => `Rule ${what} ${suffix}`)
const listId = `list-${suffix}.example.test`

function send(subject, from = sender, extra = '') {
	execSync('curl -s --url smtp://127.0.0.1:3025 --mail-from rules@example.test --mail-rcpt alice@example.test --upload-file -',
		{ input: `From: Rule Sender <${from}>\r\nTo: alice@example.test\r\n${extra}Subject: ${subject}\r\n\r\nhello\r\n` })
}

send(before)
send(listed, `news-${suffix}@example.test`, `List-Id: "Rules list" <${listId}>\r\n`)

const { page, step, finish } = await start()
const item = (subject) => messageRows(page).filter({ hasText: subject }).first()

/** Polls an async condition, up to 30 seconds (rules run on the server's next sync). */
async function until(condition) {
	for (let i = 0; i < 120; i++) {
		if (await condition()) {
			return true
		}
		await page.waitForTimeout(250)
	}
	return false
}

async function openFolder(name) {
	await folderRow(page, name).locator('.folder-row__main').click()
	await page.locator('.message-list__header h2', { hasText: new RegExp(`^${name}$`) }).waitFor()
}

async function openRules() {
	await accountAction(page, aliceName, 'Rules')
	const dialog = page.getByRole('dialog', { name: /^Rules for / })
	await dialog.waitFor()
	await dialog.locator('.rules__center').waitFor({ state: 'detached' })
	return dialog
}

await openAccount(page, /^Alice/)
const aliceName = (await page.locator('.folder-pane__account-name').innerText()).trim()
await openFolder('Inbox')
await item(before).waitFor({ timeout: 30000 })

step('a folder for the rule')
await accountAction(page, aliceName, 'New folder')
const nameDialog = page.getByRole('dialog')
await nameDialog.getByLabel('Folder name').fill(folder)
await nameDialog.getByRole('button', { name: 'Create' }).click()
await nameDialog.waitFor({ state: 'detached' })
await folderRow(page, folder).waitFor()

step('new rule')
let dialog = await openRules()
await dialog.getByRole('button', { name: 'New rule' }).click()
await dialog.getByRole('button', { name: 'Save rule' }).click()
check(await dialog.getByText('Give the rule a name.').waitFor().then(() => true, () => false), 'saving without a name shows the error')
await dialog.getByLabel('Name', { exact: true }).fill(`Sort ${suffix}`)
await dialog.getByRole('button', { name: 'Save rule' }).click()
check(await dialog.getByText('Enter a value.').first().waitFor().then(() => true, () => false), 'an empty value is pointed out')
await dialog.getByLabel('Value', { exact: true }).fill(sender)
await dialog.getByLabel('Folder', { exact: true }).selectOption({ label: folder })

await dialog.getByRole('button', { name: 'Test on recent inbox messages' }).click()
const result = dialog.locator('[data-test-result]')
await result.waitFor()
check(/Matches 1 of the newest \d+ messages/.test(await result.innerText()), `test: ${(await result.locator('p').innerText()).trim()}`)
check(await result.getByText(before).isVisible(), 'test lists the earlier message')

await dialog.getByRole('button', { name: 'Save rule' }).click()
const row = dialog.locator('[data-rule]', { hasText: `Sort ${suffix}` })
await row.waitFor()
check((await row.innerText()).includes(`Move to folder ${folder}`), 'the list describes the rule')

step('new mail is sorted by the server')
await dialog.getByRole('button', { name: 'Done' }).click()
await dialog.waitFor({ state: 'detached' })
send(after)
send(other, 'someone-else@example.test')
check(await until(async () => await item(other).count() === 1), 'the other message arrives in the Inbox')
await openFolder(folder)
check(await until(async () => await item(after).count() === 1), 'the matching message is in the rule\'s folder')
await openFolder('Inbox')
check(await until(async () => await item(before).count() === 1), 'mail from before the rule stays in the Inbox')
check(await item(after).count() === 0, 'the matching message is not in the Inbox')

step('switch off, reorder, delete')
dialog = await openRules()
await dialog.getByRole('button', { name: 'New rule' }).click()
await dialog.getByLabel('Name', { exact: true }).fill(`Flag ${suffix}`)
await dialog.getByLabel('Value', { exact: true }).fill(`flag-${suffix}@example.test`)
await dialog.getByLabel('Action', { exact: true }).selectOption({ label: 'Flag' })
await dialog.getByRole('button', { name: 'Save rule' }).click()
const names = () => dialog.locator('[data-rule] .rules__name').allInnerTexts()
await dialog.locator('[data-rule]', { hasText: `Flag ${suffix}` }).waitFor()
const mine = (list) => list.filter((n) => n.endsWith(suffix))
check(JSON.stringify(mine(await names())) === JSON.stringify([`Sort ${suffix}`, `Flag ${suffix}`]), 'a new rule goes last')

const flagRow = dialog.locator('[data-rule]', { hasText: `Flag ${suffix}` })
await flagRow.getByRole('button', { name: `Actions for Flag ${suffix}` }).click()
await page.getByRole('menuitem', { name: 'Move up' }).click()
await until(async () => mine(await names())[0] === `Flag ${suffix}`)
await flagRow.locator('.checkbox-radio-switch').click()
await page.waitForTimeout(500)
await dialog.getByRole('button', { name: 'Done' }).click()
await dialog.waitFor({ state: 'detached' })

dialog = await openRules()
check(JSON.stringify(mine(await names())) === JSON.stringify([`Flag ${suffix}`, `Sort ${suffix}`]), 'the new order was saved')
check(!(await dialog.locator('[data-rule]', { hasText: `Flag ${suffix}` }).getByRole('switch').or(
	dialog.locator('[data-rule]', { hasText: `Flag ${suffix}` }).locator('input[type=checkbox]')).first().isChecked()), 'the switched-off rule stays off')

step('edit')
// Both rules, so the first in the list (index 0) is covered too.
for (const name of [`Flag ${suffix}`, `Sort ${suffix}`]) {
	await dialog.locator('[data-rule]', { hasText: name }).locator('.rules__text').click()
	const nameField = dialog.getByLabel('Name', { exact: true })
	await nameField.waitFor()
	check(await nameField.inputValue() === name, `clicking ${name} opens it in the editor`)
	await dialog.getByLabel('Value', { exact: true }).fill(`edited-${suffix}@example.test`)
	await dialog.getByRole('button', { name: 'Save rule' }).click()
	const edited = dialog.locator('[data-rule]', { hasText: name })
	await edited.waitFor()
	check((await edited.innerText()).includes(`edited-${suffix}@example.test`), `${name}: the change is saved`)
}
check((await dialog.locator('[data-rule]', { hasText: `Sort ${suffix}` }).innerText()).includes('Matched 1 message'), 'the rule counts the message it sorted')
await dialog.getByRole('button', { name: 'Done' }).click()
await dialog.waitFor({ state: 'detached' })

step('create a rule from a message')
await openFolder('Inbox')
await rowMenuAction(item(listed), 'Create rule…', { rightClick: true })
dialog = page.getByRole('dialog', { name: /^Rules for / })
await dialog.getByLabel('Name', { exact: true }).waitFor()
check(await dialog.getByLabel('Name', { exact: true }).inputValue() === 'Rules list', 'named after the mailing list')
check(await dialog.getByLabel('Header name').inputValue() === 'List-Id', 'goes by the List-Id header')
check(await dialog.getByLabel('Value', { exact: true }).inputValue() === listId, `with the list's id (${listId})`)
await dialog.getByLabel('Name', { exact: true }).fill(`List ${suffix}`)
await dialog.getByLabel('Folder', { exact: true }).selectOption({ label: folder })
await dialog.getByRole('button', { name: 'Save rule' }).click()
const listRow = dialog.locator('[data-rule]', { hasText: `List ${suffix}` })
await listRow.waitFor()

step('run on existing messages')
await listRow.getByRole('button', { name: `Actions for List ${suffix}` }).click()
await page.getByRole('menuitem', { name: 'Run on existing messages…' }).click()
await dialog.getByRole('button', { name: 'Find matching messages' }).click()
const preview = dialog.locator('[data-run-preview]')
await preview.waitFor()
const previewText = (await preview.locator('p').first().innerText()).trim()
check(/^1 of the newest \d+ messages match/.test(previewText), previewText)
check(await preview.getByText(listed).isVisible(), 'the preview lists the message')
await dialog.getByRole('button', { name: 'Apply to 1 message' }).click()
check(await page.locator('.toast-success', { hasText: `"List ${suffix}" was applied to 1 message` }).first().waitFor().then(() => true, () => false), 'a toast confirms it')
await listRow.waitFor()
check((await listRow.innerText()).includes('Matched 1 message'), 'the run counts as a match')
await dialog.getByRole('button', { name: 'Done' }).click()
await dialog.waitFor({ state: 'detached' })
check(await until(async () => await item(listed).count() === 0), 'the message left the Inbox')
await openFolder(folder)
check(await until(async () => await item(listed).count() === 1), "and is in the rule's folder")

dialog = await openRules()
for (const name of [`Flag ${suffix}`, `Sort ${suffix}`, `List ${suffix}`]) {
	await dialog.locator('[data-rule]', { hasText: name }).getByRole('button', { name: `Actions for ${name}` }).click()
	// The list drops the rule at once; wait for the save, or closing the browser could cut it off.
	const saved = page.waitForResponse((r) => r.url().endsWith('/rules') && r.request().method() === 'PUT')
	await page.getByRole('menuitem', { name: 'Delete' }).click()
	check((await saved).ok(), `${name} deleted on the server`)
}
check(mine(await names()).length === 0, 'rules deleted')
await dialog.getByRole('button', { name: 'Done' }).click()

await finish()
