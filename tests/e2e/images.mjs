// Images in the text: pasted, dropped and inserted from the toolbar, large ones scaled down,
// sized and described from the image menu, kept in a continued draft and received inline.
// Sends to alice@example.test herself. Needs: dev/dev.sh up, the app deployed, and an account
// for alice@example.test (node mail.mjs creates one).
import { writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { check, folderRow, messageRows, openInbox, start } from './lib.mjs'

const suffix = Date.now().toString().slice(-6)
const subject = `Images ${suffix}`
const tinyPath = join(tmpdir(), `tiny-${suffix}.png`)
writeFileSync(tinyPath, Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64'))

const { page, step, finish } = await start()
const items = messageRows(page)
const exactItem = (text) => items.filter({ has: page.getByText(text, { exact: true }) }).first()
const composer = page.locator('section.composer')
const editor = composer.getByLabel('Message')
// Not ProseMirror's own (a separator after an inline image at the end of a line).
const images = editor.locator('img:not(.ProseMirror-separator)')

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

/**
 * Pastes or drops files made in the page (PNG drawn on a canvas, or text) onto the editor.
 *
 * @param {'paste'|'drop'} kind how
 * @param {{name: string, width?: number, height?: number, text?: string}[]} files what
 */
async function deliver(kind, files) {
	await editor.evaluate(async (element, { kind, files }) => {
		const transfer = new DataTransfer()
		for (const f of files) {
			if (f.text !== undefined) {
				transfer.items.add(new File([f.text], f.name, { type: 'text/plain' }))
				continue
			}
			const canvas = document.createElement('canvas')
			canvas.width = f.width
			canvas.height = f.height
			const context = canvas.getContext('2d')
			context.fillStyle = '#1a62d6'
			context.fillRect(0, 0, f.width, f.height)
			const blob = await new Promise((resolve) => canvas.toBlob(resolve, 'image/png'))
			transfer.items.add(new File([blob], f.name, { type: 'image/png' }))
		}
		const box = element.getBoundingClientRect()
		const event = kind === 'paste'
			? new ClipboardEvent('paste', { clipboardData: transfer, bubbles: true, cancelable: true })
			: new DragEvent('drop', { dataTransfer: transfer, clientX: box.left + 20, clientY: box.top + 15, bubbles: true, cancelable: true })
		element.dispatchEvent(event)
	}, { kind, files })
}

const uploaded = () => until(async () => await editor.locator('img[data-pending]').count() === 0
	&& await composer.getByRole('button', { name: 'Uploading…' }).count() === 0)

await openInbox(page, /^Alice/)

step('putting images into the text')
await page.getByRole('button', { name: 'New message' }).click()
await composer.waitFor()
if (await composer.getByRole('button', { name: 'Rich text' }).count()) {
	await composer.getByRole('button', { name: 'Rich text' }).click()
}
await composer.getByLabel('To', { exact: true }).fill('alice@example.test')
await composer.getByLabel('Subject').fill(subject)
await editor.click()
await page.keyboard.type('Here is the chart: ')

await deliver('paste', [{ name: 'image.png', width: 3000, height: 1500 }])
check(await until(async () => await images.count() === 1), 'a pasted image goes into the text')
await uploaded()
check(await until(async () => await images.first().evaluate((img) => img.naturalWidth) === 2000), 'a large one is scaled down to 2000 pixels')

await page.keyboard.press('End')
await deliver('drop', [{ name: 'dropped.png', width: 300, height: 200 }, { name: `notes-${suffix}.txt`, text: 'not an image' }])
check(await until(async () => await images.count() === 2), 'a dropped image goes into the text')
check(await until(async () => await composer.locator('.composer__chip', { hasText: `notes-${suffix}.txt` }).count() === 1),
	'a dropped file that is not an image is attached')

await editor.click()
await page.keyboard.press('Control+End')
await composer.locator('input[type=file][accept*="image/png"]').setInputFiles(tinyPath)
check(await until(async () => await images.count() === 3), 'Insert image adds one from this device')
check(await uploaded(), 'all images are uploaded')
check(await editor.locator('img:not([data-cid]):not(.ProseMirror-separator)').count() === 0, 'each is marked with its Content-ID')

step('the image menu')
// The drop went to the start of the text: dropped, pasted (scaled down), inserted.
const pasted = images.nth(1)
await pasted.click()
const menu = page.getByRole('toolbar', { name: 'Image' })
await menu.waitFor()
await menu.getByRole('button', { name: 'Full quality' }).click()
check(await until(async () => await pasted.evaluate((img) => img.naturalWidth) === 3000) && await uploaded(),
	'a scaled-down image can be sent in full quality instead')
await images.first().click()
check(await menu.getByRole('button', { name: 'Full quality' }).count() === 0, 'one that was not scaled down has no such choice')
await menu.getByRole('button', { name: 'Medium' }).click()
check(await images.first().getAttribute('width') === '400', 'Medium makes it 400 pixels wide')
await images.first().click()
await menu.getByRole('button', { name: 'Alternative text' }).click()
await menu.getByLabel('Description for people who cannot see the image').fill('Blue chart')
await menu.getByRole('button', { name: 'Apply' }).click()
check(await images.first().getAttribute('alt') === 'Blue chart', 'it gets alternative text')

step('plain text asks first')
await composer.getByRole('button', { name: 'Plain text' }).click()
const ask = page.getByRole('dialog', { name: 'Switch to plain text?' })
check(await until(async () => await ask.count() === 1), 'switching to plain text asks, the images would be lost')
await ask.getByRole('button', { name: 'Keep rich text' }).click()
await ask.waitFor({ state: 'detached' })
check(await images.count() === 3, 'keeping rich text keeps them')

step('kept and continued')
check(await until(async () => /Draft saved/.test(await composer.locator('.composer__status').innerText())), 'the draft is saved')
await composer.getByRole('button', { name: 'Close' }).click()
await composer.waitFor({ state: 'detached' })
await page.waitForTimeout(500)
await openFolder('Drafts')
check(await until(async () => {
	await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))
	return (await exactItem(subject).count()) === 1
}), 'it is in Drafts')
await exactItem(subject).click()
await page.locator('.message-view h2', { hasText: subject }).waitFor()
await page.locator('.message-view__actions').getByRole('button', { name: 'Continue editing' }).click()
await composer.waitFor()
check(await until(async () => await images.count() === 3
	&& (await images.evaluateAll((all) => all.every((img) => img.complete && img.naturalWidth > 0)))), 'the continued draft shows its three images')
check(await images.first().getAttribute('width') === '400' && await images.first().getAttribute('alt') === 'Blue chart', 'with size and description')
await composer.getByRole('button', { name: 'Send', exact: true }).click()
await composer.waitFor({ state: 'detached', timeout: 30000 })
check(await page.getByText('Message sent').count() > 0, 'it is sent')

step('the recipient sees them in the text')
await openFolder('Inbox')
check(await until(async () => {
	await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))
	return (await exactItem(subject).count()) === 1
}, 30), 'the message arrives')
await exactItem(subject).click()
const body = page.frameLocator('iframe.message-body')
await body.getByText('Here is the chart').waitFor({ timeout: 20000 })
const shown = body.locator('img')
check(await until(async () => await shown.count() === 3
	&& await shown.evaluateAll((all) => all.every((img) => img.complete && img.naturalWidth > 0))), 'three inline images, all shown')
check(await shown.first().getAttribute('alt') === 'Blue chart', 'with the description')
check(await page.locator('a.message-view__attachment', { hasText: `notes-${suffix}.txt` }).count() === 1
	&& await page.locator('a.message-view__attachment').count() === 1, 'only the text file is an attachment')

await finish()
