// Tags: create one in the tag manager, put it on a message from the row menu, see the chip in the
// list and the reader, create a second tag from the picker (T), take a tag off, open tag folders
// and the tag filter, let a rule tag new mail, and delete both tags (with "remove from all
// messages"). Sends its own messages and removes its tags and rule.
// Needs: dev/dev.sh up, the app deployed, and an account for alice@example.test (node mail.mjs creates one).
import { execSync } from 'node:child_process'
import { accountAction, check, messageRows, openInbox, rowMenuAction, start } from './lib.mjs'

const suffix = Date.now().toString().slice(-6)
const subject = `Tag me ${suffix}`
const [urgent, later] = [`Urgent ${suffix}`, `Later ${suffix}`]

execSync('curl -s --url smtp://127.0.0.1:3025 --mail-from tags@example.test --mail-rcpt alice@example.test --upload-file -',
	{ input: `From: Tag Sender <tags-${suffix}@example.test>\r\nTo: alice@example.test\r\nSubject: ${subject}\r\n\r\nhello\r\n` })

const { page, step, finish } = await start()
const item = () => messageRows(page).filter({ hasText: subject }).first()
const chips = (scope) => scope.locator('.tag-chips__chip')

async function openManager() {
	await page.getByRole('button', { name: 'View settings' }).click()
	await page.getByRole('menuitem', { name: 'Tags', exact: true }).click()
	const dialog = page.getByRole('dialog', { name: 'Tags' })
	await dialog.waitFor()
	return dialog
}

await openInbox(page, /^Alice/)
await item().waitFor({ timeout: 30000 })

step('create a tag')
let dialog = await openManager()
await dialog.getByRole('textbox', { name: 'New tag' }).fill(urgent)
await dialog.getByRole('button', { name: 'Add', exact: true }).click()
await dialog.locator('.tags__row', { hasText: urgent }).waitFor()
await dialog.getByRole('textbox', { name: 'New tag' }).fill(urgent.toLowerCase())
await dialog.getByRole('button', { name: 'Add', exact: true }).click()
check(await dialog.getByText('A tag with this name exists already.').waitFor().then(() => true, () => false), 'a second tag of the same name is refused')
await dialog.getByRole('textbox', { name: 'New tag' }).fill('')
await dialog.getByRole('button', { name: 'Done' }).click()
await dialog.waitFor({ state: 'detached' })

step('tag a message from the row menu')
await rowMenuAction(item(), 'Tags…', { rightClick: true })
let picker = page.getByRole('dialog', { name: 'Tags' })
await picker.waitFor()
const saved = page.waitForResponse((r) => r.url().endsWith('/messages/tags') && r.request().method() === 'PUT')
await picker.getByText(urgent, { exact: true }).click()
check((await saved).ok(), 'the tag is saved on the server')
await picker.getByRole('button', { name: 'Done' }).click()
await picker.waitFor({ state: 'detached' })
check(await chips(item()).filter({ hasText: urgent }).count() === 1, 'the row shows the chip')

step('the reader shows it, T tags with a new tag')
await item().click()
const header = page.locator('.message-view__header')
await chips(header).filter({ hasText: urgent }).waitFor()
check(true, 'the reader shows the chip')
await page.locator('.message-view').click({ position: { x: 5, y: 5 } })
await page.keyboard.press('t')
picker = page.getByRole('dialog', { name: 'Tags' })
await picker.waitFor()
await picker.getByRole('textbox', { name: 'Find or create a tag' }).fill(later)
const created = page.waitForResponse((r) => r.url().endsWith('/messages/tags') && r.request().method() === 'PUT')
await picker.getByRole('textbox', { name: 'Find or create a tag' }).press('Enter')
check((await created).ok(), 'Enter creates the tag and puts it on the message')
await picker.getByRole('button', { name: 'Done' }).click()
await picker.waitFor({ state: 'detached' })
await chips(header).filter({ hasText: later }).waitFor()
check(await chips(item()).count() === 2, 'the row shows both chips')

step('take a tag off')
await rowMenuAction(item(), 'Tags…', { rightClick: true })
picker = page.getByRole('dialog', { name: 'Tags' })
const removed = page.waitForResponse((r) => r.url().endsWith('/messages/tags') && r.request().method() === 'PUT')
await picker.getByText(urgent, { exact: true }).click()
check((await removed).ok(), 'the removal is saved')
await picker.getByRole('button', { name: 'Done' }).click()
check(await chips(item()).filter({ hasText: urgent }).count() === 0, 'the chip is gone from the row')

step('the tags survive a reload')
await page.reload()
await openInbox(page, /^Alice/)
await chips(item()).filter({ hasText: later }).waitFor({ timeout: 30000 })
check(true, 'the row still shows the remaining tag')

step('the account\'s tag folder')
const listHeader = () => page.locator('.message-list__header h2')
const tagFolder = (name) => page.locator('.folder-pane .tag-folder', { hasText: name }).locator('button')
await tagFolder(later).click()
await listHeader().filter({ hasText: later }).waitFor()
await item().waitFor({ timeout: 30000 })
check((await item().locator('.message-row__location').innerText()).includes('Inbox'), 'the row says which folder it is in')
check(await page.locator('.folder-pane__account-name').isVisible(), 'the account\'s folders stay in the pane')
check(await page.locator('.message-list__filters').count() === 0, 'a tag folder has no filter chips')

step('the tag folder across all accounts')
const all = page.getByRole('button', { name: 'All accounts' })
if (await all.count()) {
	await all.click()
	await tagFolder(later).click()
	await listHeader().filter({ hasText: later }).waitFor()
	await item().waitFor({ timeout: 30000 })
	check(true, 'the message is in the unified tag folder')
} else {
	console.log('  skip one account only')
}

step('the tag filter chip')
await openInbox(page, /^Alice/)
await page.locator('.message-list__filters').getByRole('button', { name: 'Tag', exact: true }).click()
await page.getByRole('menuitem', { name: later }).click()
await item().waitFor({ timeout: 30000 })
check(await messageRows(page).count() === 1, 'only the tagged message is listed')
check(await page.locator('.message-list__tag-filter--active').count() === 1, 'the chip shows the tag is on')
await page.getByRole('button', { name: 'All', exact: true }).click()

step('a rule tags new mail')
const aliceName = (await page.locator('.folder-pane__account-name').innerText()).trim()
const ruled = `Ruled ${suffix}`
await accountAction(page, aliceName, 'Rules')
let rules = page.getByRole('dialog', { name: /^Rules for / })
await rules.locator('.rules__center').waitFor({ state: 'detached' })
await rules.getByRole('button', { name: 'New rule' }).click()
await rules.getByLabel('Name', { exact: true }).fill(`Tag ${suffix}`)
await rules.getByLabel('Value', { exact: true }).fill(`rule-${suffix}@example.test`)
await rules.getByLabel('Action', { exact: true }).selectOption({ label: 'Add tag' })
await rules.getByLabel('Tag', { exact: true }).selectOption({ label: later })
await rules.getByRole('button', { name: 'Save rule' }).click()
const ruleRow = rules.locator('[data-rule]', { hasText: `Tag ${suffix}` })
await ruleRow.waitFor()
check((await ruleRow.innerText()).includes(`Add tag ${later}`), 'the list describes the rule')
await rules.getByRole('button', { name: 'Done' }).click()
await rules.waitFor({ state: 'detached' })
execSync('curl -s --url smtp://127.0.0.1:3025 --mail-from tags@example.test --mail-rcpt alice@example.test --upload-file -',
	{ input: `From: Rule Sender <rule-${suffix}@example.test>\r\nTo: alice@example.test\r\nSubject: ${ruled}\r\n\r\nhello\r\n` })
const ruledRow = messageRows(page).filter({ hasText: ruled }).first()
await ruledRow.waitFor({ timeout: 60000 })
await chips(ruledRow).filter({ hasText: later }).waitFor({ timeout: 30000 })
check(true, 'the new message arrives with the tag')

await accountAction(page, aliceName, 'Rules')
rules = page.getByRole('dialog', { name: /^Rules for / })
await rules.locator('.rules__center').waitFor({ state: 'detached' })
await rules.locator('[data-rule]', { hasText: `Tag ${suffix}` }).getByRole('button', { name: `Actions for Tag ${suffix}` }).click()
const ruleSaved = page.waitForResponse((r) => r.url().endsWith('/rules') && r.request().method() === 'PUT')
await page.getByRole('menuitem', { name: 'Delete' }).click()
check((await ruleSaved).ok(), 'the rule is deleted')
await rules.getByRole('button', { name: 'Done' }).click()
await rules.waitFor({ state: 'detached' })

step('delete the tags')
dialog = await openManager()
for (const name of [urgent, later]) {
	const row = dialog.locator('.tags__row', { hasText: name })
	await row.getByRole('button', { name: 'Actions' }).click()
	await page.getByRole('menuitem', { name: 'Delete' }).click()
	const confirm = page.getByRole('dialog', { name: `Delete ${name}?` })
	await confirm.waitFor()
	const deleted = page.waitForResponse((r) => r.url().includes('/tags/') && r.request().method() === 'DELETE')
	await confirm.getByRole('button', { name: 'Delete' }).click()
	const response = await deleted
	check(response.ok() && response.url().includes('fromMessages=true'), `${name} deleted, and taken off its messages`)
	await row.waitFor({ state: 'detached' })
}
await dialog.getByRole('button', { name: 'Done' }).click()
check(await chips(item()).count() === 0, 'no chips left')

await finish()
