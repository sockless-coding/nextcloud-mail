// Separate windows: a message opened in its own window (double-click, reader button, row menu),
// replying there, a message being written moved to a window with its text, image and
// attachment, and "In a separate window" as the composer setting.
// Sends to alice@example.test herself. Needs: dev/dev.sh up, the app deployed, and an account
// for alice@example.test (node mail.mjs creates one).
import { writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { check, messageRows, openInbox, rowMenuAction, start } from './lib.mjs'

const suffix = Date.now().toString().slice(-6)
const subject = `Window ${suffix}`
const notePath = join(tmpdir(), `window-note-${suffix}.txt`)
writeFileSync(notePath, 'attached before moving to a window')
const tinyPath = join(tmpdir(), `window-tiny-${suffix}.png`)
writeFileSync(tinyPath, Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64'))

const { page, step, finish } = await start()
const items = messageRows(page)
const exactItem = (text) => items.filter({ has: page.getByText(text, { exact: true }) }).first()
const composer = page.locator('section.composer')

async function until(condition, seconds = 15) {
	for (let i = 0; i < seconds * 4; i++) {
		if (await condition()) {
			return true
		}
		await page.waitForTimeout(250)
	}
	return false
}

/** The window an action opens. */
async function popupFrom(action) {
	const [popup] = await Promise.all([page.waitForEvent('popup'), action()])
	await popup.waitForLoadState()
	popup.on('pageerror', (e) => console.log(`  popup error: ${String(e).split('\n')[0]}`))
	return popup
}

async function chooseSize(label) {
	await page.getByRole('button', { name: 'Settings', exact: true }).click()
	const settings = page.getByRole('dialog', { name: 'Settings' })
	await settings.waitFor()
	await settings.getByText(label, { exact: true }).click()
	await page.keyboard.press('Escape')
	await settings.waitFor({ state: 'detached' })
	await page.waitForTimeout(400)
}

await openInbox(page, /^Alice/)
await chooseSize('Small, docked at the bottom')

step('reading in a window')
const first = items.first()
const firstSubject = (await first.locator('.message-row__subject-text').innerText()).trim()
let reader = await popupFrom(() => first.dblclick())
await reader.locator('.message-view h2').waitFor({ timeout: 30000 })
check((await reader.locator('.message-view h2').innerText()).trim() === firstSubject, 'double-click opens the message in a window')
check(!(await reader.locator('#header').isVisible()) && await reader.locator('.account-rail').count() === 0,
	'without Nextcloud\'s header and the mailbox')
check(await until(async () => (await reader.title()) === firstSubject), 'the window is titled with the subject')

await reader.locator('.message-view__actions').getByRole('button', { name: 'Reply', exact: true }).click()
const readerComposer = reader.locator('section.composer')
await readerComposer.waitFor()
check(await readerComposer.evaluate((el) => el.classList.contains('composer--window')), 'Reply there fills the window with the composer')
await readerComposer.locator('header').getByRole('button', { name: 'Close' }).click()
await readerComposer.waitFor({ state: 'detached' })
check(await reader.locator('.message-view h2').isVisible(), 'closing the unchanged reply shows the message again')
await reader.close()

await first.click()
await page.locator('.message-view h2').waitFor()
reader = await popupFrom(() => page.locator('.message-view__actions').getByRole('button', { name: 'Open in new window' }).click())
await reader.locator('.message-view h2').waitFor({ timeout: 30000 })
check(true, 'the reader\'s Open in new window opens it')
await reader.close()

reader = await popupFrom(() => rowMenuAction(first, 'Open in new window', { rightClick: true }))
await reader.locator('.message-view h2').waitFor({ timeout: 30000 })
check(true, 'and so does the row menu')
await reader.close()

step('moving a message being written to a window')
await page.getByRole('button', { name: 'New message' }).click()
await composer.waitFor()
if (await composer.getByRole('button', { name: 'Rich text' }).count()) {
	await composer.getByRole('button', { name: 'Rich text' }).click()
}
await composer.getByLabel('To', { exact: true }).fill('alice@example.test')
await composer.getByLabel('Subject').fill(subject)
await composer.getByLabel('Message').click()
await page.keyboard.type('Written in the main window. ')
await composer.locator('input[type=file][accept*="image/png"]').setInputFiles(tinyPath)
await composer.locator('input[type=file]:not([accept])').setInputFiles(notePath)
check(await until(async () => await composer.getByRole('button', { name: 'Uploading…' }).count() === 0
	&& await composer.locator('.composer__chip', { hasText: `window-note-${suffix}.txt` }).count() === 1), 'an image and a file are added')

const writer = await popupFrom(() => composer.locator('header').getByRole('button', { name: 'Open in a new window' }).click())
const moved = writer.locator('section.composer')
await moved.waitFor({ timeout: 30000 })
check(await until(async () => await composer.count() === 0), 'the composer leaves the main window')
check(await moved.getByLabel('Subject').inputValue() === subject && await moved.locator('.composer__to').getByText('alice@example.test').count() > 0,
	'the window has the recipient and subject')
check((await moved.getByLabel('Message').innerText()).includes('Written in the main window.'), 'and the text')
const movedImage = moved.getByLabel('Message').locator('img:not(.ProseMirror-separator)')
check(await until(async () => await movedImage.count() === 1 && await movedImage.evaluate((img) => img.complete && img.naturalWidth > 0)),
	'and the image, still shown')
check(await moved.locator('.composer__chip', { hasText: `window-note-${suffix}.txt` }).count() === 1, 'and the attachment')
await moved.getByLabel('Message').click()
await writer.keyboard.press('Control+End')
await writer.keyboard.type('Finished in a window.')
const closed = writer.waitForEvent('close', { timeout: 30000 })
await moved.getByRole('button', { name: 'Send', exact: true }).click()
check(await closed.then(() => true, () => false), 'sending closes the window')

await until(async () => {
	await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))
	return (await exactItem(subject).count()) === 1
}, 30)
await exactItem(subject).click()
const body = page.frameLocator('iframe.message-body')
await body.getByText('Finished in a window.').waitFor({ timeout: 20000 })
check(await body.locator('img').count() === 1, 'the message arrives with the text from both windows and the image')
check(await page.locator('.message-view__attachment', { hasText: `window-note-${suffix}.txt` }).count() === 1, 'and the attachment')

step('writing in a separate window by default')
await chooseSize('In a separate window')
const fresh = await popupFrom(() => page.getByRole('button', { name: 'New message' }).click())
const freshComposer = fresh.locator('section.composer')
await freshComposer.waitFor({ timeout: 30000 })
check(await composer.count() === 0 && await freshComposer.evaluate((el) => el.classList.contains('composer--window')),
	'New message opens in a window')
const freshClosed = fresh.waitForEvent('close')
await freshComposer.locator('header').getByRole('button', { name: 'Close' }).click()
check(await freshClosed.then(() => true, () => false), 'closing the empty message closes the window')

const replyWindow = await popupFrom(() => page.locator('.message-view__actions').getByRole('button', { name: 'Reply', exact: true }).click())
await replyWindow.locator('section.composer').waitFor({ timeout: 30000 })
check((await replyWindow.locator('section.composer').getByLabel('Subject').inputValue()).startsWith('Re: '), 'Reply opens in a window too')
await replyWindow.close()
await chooseSize('Small, docked at the bottom')

await finish()
