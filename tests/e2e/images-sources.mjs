// Images from elsewhere: a reply quotes the original with its images, pasted web content brings
// its images (fetched by the server, decoded from data: URLs, or copied from a message shown
// here), and images are inserted from Files. Sends to alice@example.test herself.
// Needs: dev/dev.sh up, the app deployed, and an account for alice@example.test (node mail.mjs
// creates one). The remote image is Nextcloud's logo as the app's container reaches it
// (REMOTE_IMAGE, default http://proxy/core/img/logo/logo.png).
import { request } from 'playwright-core'
import { base, check, folderRow, messageRows, openInbox, start } from './lib.mjs'

const suffix = Date.now().toString().slice(-6)
const subject = `Sources ${suffix}`
const remoteImage = process.env.REMOTE_IMAGE ?? 'http://proxy/core/img/logo/logo.png'
const pngBase64 = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=='
const user = process.env.NC_USER ?? 'admin'
const auth = { Authorization: `Basic ${Buffer.from(`${user}:${process.env.NC_PASSWORD ?? 'admin'}`).toString('base64')}` }
const dav = `${base}/remote.php/dav/files/${user}`
const folder = `sm-e2e-images-${suffix}`
const files = await request.newContext({ extraHTTPHeaders: auth })

const { page, step, finish } = await start()
const items = messageRows(page)
const exactItem = (text) => items.filter({ has: page.getByText(text, { exact: true }) }).first()
const composer = page.locator('section.composer')
const editor = composer.getByLabel('Message')
const images = editor.locator('img:not(.ProseMirror-separator)')
const body = page.frameLocator('iframe.message-body')

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

async function arrived(text) {
	await openFolder('Inbox')
	return until(async () => {
		await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))
		return (await exactItem(text).count()) === 1
	}, 30)
}

const allShown = (locator) => locator.evaluateAll((all) => all.length > 0 && all.every((img) => img.complete && img.naturalWidth > 0))
const settled = () => until(async () => await editor.locator('img[data-pending]').count() === 0
	&& await composer.getByRole('button', { name: 'Uploading…' }).count() === 0)

/** Pastes HTML (and nothing else) where the cursor is. */
async function pasteHtml(html) {
	await editor.evaluate((element, html) => {
		const transfer = new DataTransfer()
		transfer.setData('text/html', html)
		transfer.setData('text/plain', 'x')
		element.dispatchEvent(new ClipboardEvent('paste', { clipboardData: transfer, bubbles: true, cancelable: true }))
	}, html)
}

async function newMessage(title) {
	await page.getByRole('button', { name: 'New message' }).click()
	await composer.waitFor()
	if (await composer.getByRole('button', { name: 'Rich text' }).count()) {
		await composer.getByRole('button', { name: 'Rich text' }).click()
	}
	await composer.getByLabel('To', { exact: true }).fill('alice@example.test')
	await composer.getByLabel('Subject').fill(title)
	await editor.click()
}

async function send() {
	await composer.getByRole('button', { name: 'Send', exact: true }).click()
	await composer.waitFor({ state: 'detached', timeout: 30000 })
}

await openInbox(page, /^Alice/)

step('pasted web content brings its images')
await newMessage(subject)
await page.keyboard.type('Pasted: ')
await pasteHtml(`<p>From the web <img src="${remoteImage}" alt="Logo"> and inline <img src="data:image/png;base64,${pngBase64}">`
	+ ' and gone <img src="file:///C:/Users/x/clip_image001.png"></p>')
check(await until(async () => await images.count() === 2), 'the images that can be fetched go in, others are left out')
check(await settled(), 'they are fetched and uploaded')
check(await images.evaluateAll((all) => all.every((img) => img.dataset.cid && !img.dataset.import && img.src.startsWith('blob:'))),
	'each is now a part of the message, shown from here')
check(await allShown(images), 'and shows')
check(await page.getByText('could not be added').count() === 0, 'no warning about images that could not be added')
await send()
check(await arrived(subject), 'the message arrives')
await exactItem(subject).click()
await body.getByText('From the web').waitFor({ timeout: 20000 })
check(await until(async () => await body.locator('img').count() === 2 && await allShown(body.locator('img'))), 'with both images inline')

step('copying from a message shown here')
const shownSrc = await body.locator('img').first().evaluate((img) => img.src)
await newMessage(`Copied ${suffix}`)
await pasteHtml(`<p>Copied text <img src="${shownSrc}"></p>`)
check(await until(async () => await images.count() === 1) && await settled(), 'an image copied from a shown message goes in')
check(await allShown(images) && await images.first().getAttribute('data-cid') !== null, 'as a part of the new message')
await composer.getByRole('button', { name: 'Discard' }).click()
await page.getByRole('dialog', { name: 'Discard this message?' }).getByRole('button', { name: 'Discard' }).click()
await composer.waitFor({ state: 'detached' })

step('a reply quotes the images')
await exactItem(subject).click()
await body.getByText('From the web').waitFor({ timeout: 20000 })
await page.locator('.command-bar').getByRole('button', { name: 'Reply', exact: true }).click()
await composer.waitFor()
const quoted = editor.locator('blockquote img')
check(await until(async () => await quoted.count() === 2 && await allShown(quoted)), 'the quote shows the original\'s images')
await editor.press('Control+Home')
await page.keyboard.type('Nice logo!')
await send()
check(await arrived(`Re: ${subject}`), 'the reply arrives')
await exactItem(`Re: ${subject}`).click()
await body.getByText('Nice logo!').waitFor({ timeout: 20000 })
check(await until(async () => await body.locator('blockquote img').count() === 2 && await allShown(body.locator('blockquote img'))),
	'with the quoted images inline')

step('inserting from Files')
check((await files.fetch(`${dav}/${folder}`, { method: 'MKCOL' })).status() === 201, 'a folder in Files')
check((await files.fetch(`${dav}/${folder}/photo-${suffix}.png`, { method: 'PUT', data: Buffer.from(pngBase64, 'base64'), headers: { 'Content-Type': 'image/png' } })).ok(),
	'with an image')
await newMessage(`Files image ${suffix}`)
await page.keyboard.type('From Files: ')
await composer.getByRole('button', { name: 'Insert image' }).click()
await page.getByRole('menuitem', { name: 'From Files' }).click()
const picker = page.locator('.file-picker')
await picker.waitFor()
await page.waitForTimeout(400)
const inside = picker.locator('.breadcrumb', { hasText: folder })
const row = picker.locator('tr', { hasText: folder })
await inside.or(row).first().waitFor()
if (await inside.count() === 0) {
	await row.getByText(folder).click()
	await inside.waitFor()
}
await picker.locator('tr', { hasText: `photo-${suffix}` }).getByText(`photo-${suffix}`).click()
await picker.getByRole('button', { name: 'Choose' }).click()
await picker.waitFor({ state: 'detached' })
check(await until(async () => await images.count() === 1) && await settled(), 'the image from Files goes into the text')
check(await allShown(images) && await images.first().getAttribute('data-cid') !== null, 'and shows, as a part of the message')
check(await composer.locator('.composer__chip').count() === 0, 'it is not an attachment')
await composer.getByRole('button', { name: 'Discard' }).click()
await page.getByRole('dialog', { name: 'Discard this message?' }).getByRole('button', { name: 'Discard' }).click()
await composer.waitFor({ state: 'detached' })

step('clean up')
check((await files.fetch(`${dav}/${folder}`, { method: 'DELETE' })).ok(), 'folder removed from Files')

await finish()
