// Rich text and drafts: formatting reaches the recipient, drafts are saved while writing, kept on
// Close, continued from Drafts and removed when sent; plain text is remembered.
// Sends to alice@example.test herself. Needs: dev/dev.sh up, the app deployed, and an account
// for alice@example.test (node mail.mjs creates one).
import { check, folderRow, messageRows, openInbox, start } from './lib.mjs'

const suffix = Date.now().toString().slice(-6)
const subject = `Rich ${suffix}`

const { page, step, finish } = await start()
const items = messageRows(page)
const exactItem = (text) => items.filter({ has: page.getByText(text, { exact: true }) }).first()
const composer = page.locator('section.composer')
const editor = composer.getByLabel('Message')

async function until(condition, seconds = 15) {
	for (let i = 0; i < seconds * 4; i++) {
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

await openInbox(page, /^Alice/)

step('writing with formatting')
await page.getByRole('button', { name: 'New message' }).click()
await composer.waitFor()
if (await composer.getByRole('button', { name: 'Rich text' }).count()) {
	// An earlier run left plain text as the choice.
	await composer.getByRole('button', { name: 'Rich text' }).click()
}
await composer.getByLabel('To', { exact: true }).fill('alice@example.test')
await composer.getByLabel('Subject').fill(subject)
await editor.click()
await page.keyboard.type('Plain then ')
await page.keyboard.press('Control+b')
await page.keyboard.type('bold')
await page.keyboard.press('Control+b')
await page.keyboard.press('Enter')
await composer.getByRole('button', { name: 'Bulleted list' }).click()
await page.keyboard.type('first point')
check(await editor.locator('strong', { hasText: 'bold' }).count() === 1 && await editor.locator('ul li', { hasText: 'first point' }).count() === 1,
	'the toolbar and Ctrl+B format the text')
check(await until(async () => /Draft saved/.test(await composer.locator('.composer__status').innerText())), 'the draft is saved while writing')

step('keeping and continuing')
await composer.getByRole('button', { name: 'Close' }).click()
await composer.waitFor({ state: 'detached' })
check(await page.getByText('Draft saved in Drafts').count() > 0, 'Close keeps the draft')
await page.waitForTimeout(500)
await openFolder('Drafts')
check(await until(async () => {
	await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))
	return (await exactItem(subject).count()) === 1
}), 'it is in Drafts, once')
await exactItem(subject).click()
await page.locator('.message-view h2', { hasText: subject }).waitFor()
await page.locator('.message-view__actions').getByRole('button', { name: 'Continue editing' }).click()
await composer.waitFor()
check(await editor.locator('strong', { hasText: 'bold' }).count() === 1, 'Continue editing brings back the formatted text')
await editor.click()
await page.keyboard.press('Control+End')
await page.keyboard.press('Enter')
await page.keyboard.press('Enter')
await page.keyboard.type('second point')
await composer.getByRole('button', { name: 'Send', exact: true }).click()
await composer.waitFor({ state: 'detached', timeout: 30000 })
check(await page.getByText('Message sent').count() > 0, 'the continued draft is sent')
check(await until(async () => {
	await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))
	return (await exactItem(subject).count()) === 0
}), 'and leaves Drafts')

step('the recipient sees the formatting')
await openFolder('Inbox')
check(await until(async () => {
	await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))
	return (await exactItem(subject).count()) === 1
}, 30), 'the message arrives')
await exactItem(subject).click()
const body = page.frameLocator('iframe.message-body')
await body.locator('strong', { hasText: 'bold' }).waitFor({ timeout: 20000 })
check(await body.locator('li', { hasText: 'first point' }).count() === 1, 'bold text and the list arrive as HTML')
check(await body.getByText('second point').count() === 1, 'with the text added later')

step('plain text')
await page.getByRole('button', { name: 'New message' }).click()
await composer.waitFor()
await composer.getByRole('button', { name: 'Plain text' }).click()
check(await composer.locator('textarea').count() === 1, 'Plain text switches to a plain field')
await composer.getByRole('button', { name: 'Discard' }).click()
await composer.waitFor({ state: 'detached' })
await page.getByRole('button', { name: 'New message' }).click()
await composer.waitFor()
check(await composer.locator('textarea').count() === 1, 'and is remembered for the next message')
await composer.getByRole('button', { name: 'Rich text' }).click()
check(await composer.locator('.ProseMirror').count() === 1, 'Rich text switches back')
await composer.getByRole('button', { name: 'Discard' }).click()
await composer.waitFor({ state: 'detached' })

await finish()
