// Nextcloud Files: attach from Files, share link, save an attachment to Files, and the Files
// app's "Send by email". Sends to alice@example.test herself.
// Needs: dev/dev.sh up, the app deployed, and an account "Alice" for alice@example.test (node mail.mjs creates one).
import { request } from 'playwright-core'
import { base, check, messageRows, openAccount, start } from './lib.mjs'

const { page, step, finish } = await start()
const suffix = Date.now().toString().slice(-6)
const folder = `sm-e2e-${suffix}`
const fileName = `note ${suffix}.txt`
const fileContent = `From Files ${suffix}\n`
const subject = `Files ${suffix}`
const user = process.env.NC_USER ?? 'admin'
const auth = { Authorization: `Basic ${Buffer.from(`${user}:${process.env.NC_PASSWORD ?? 'admin'}`).toString('base64')}` }
const dav = `${base}/remote.php/dav/files/${user}`
// Without the browser's session cookie: with it, Nextcloud wants a CSRF token on WebDAV writes.
const files = await request.newContext({ extraHTTPHeaders: auth })

async function until(condition, timeoutMs = 15000) {
	for (let waited = 0; waited < timeoutMs; waited += 250) {
		if (await condition()) {
			return true
		}
		await page.waitForTimeout(250)
	}
	return false
}

const composerDialog = () => page.getByRole('dialog').filter({ has: page.getByLabel('Subject') })
const picker = () => page.locator('.file-picker')

/**
 * Waits for the file picker like a person would: a picker closed within milliseconds of opening
 * makes NcModal activate its focus trap after the modal is gone, and log an error.
 */
async function pickerOpen() {
	await picker().waitFor()
	await page.waitForTimeout(400)
}

async function attachMenu(dialog, entry) {
	await dialog.getByRole('button', { name: /^Attach/ }).click()
	await page.getByRole('menuitem', { name: entry }).click()
	await pickerOpen()
}

/** In the open file picker: into our folder, then select a file (or nothing). */
async function pickInFolder(name) {
	const inside = picker().locator('.breadcrumb', { hasText: folder })
	const row = picker().locator('tr', { hasText: folder })
	// The picker opens where it was last; wait for either.
	await inside.or(row).first().waitFor()
	if (await inside.count() === 0) {
		await row.getByText(folder).click()
		await inside.waitFor()
	}
	if (name) {
		await picker().locator('tr', { hasText: name }).getByText(name.split('.')[0]).click()
	}
}

step('a folder and a file in Files')
check((await files.fetch(`${dav}/${folder}`, { method: 'MKCOL' })).status() === 201, 'folder created')
check((await files.fetch(`${dav}/${folder}/${encodeURIComponent(fileName)}`, { method: 'PUT', data: fileContent })).ok(), 'file created')

step('attach from Files')
await openAccount(page, 'Alice')
await page.getByRole('button', { name: 'New message' }).click()
let dialog = composerDialog()
await dialog.waitFor()
await dialog.getByLabel('To', { exact: true }).fill('alice@example.test')
await dialog.getByLabel('Subject').fill(subject)
await attachMenu(dialog, 'Attach from Files')
await pickInFolder(fileName)
await picker().getByRole('button', { name: 'Choose' }).click()
check(await until(async () => await dialog.locator('.composer__chip', { hasText: fileName }).count() === 1
	&& await dialog.locator('.composer__chip--error').count() === 0
	&& await dialog.locator('.composer__chip .loading-icon').count() === 0), 'the file is attached')

step('share link from Files')
await attachMenu(dialog, 'Share link from Files')
await pickInFolder(fileName)
await picker().getByRole('button', { name: 'Choose' }).click()
const link = dialog.locator('.ProseMirror a', { hasText: fileName })
check(await until(async () => await link.count() === 1), 'a link named after the file is in the message')
const href = await link.getAttribute('href')
check(href?.startsWith(`${base}/`) && href.includes('/s/'), `the link uses the browser's address (${href})`)
check((await (await request.newContext().then((c) => c.get(href))).text()).includes(fileName), 'the link opens the shared file, signed out')

step('send it')
await dialog.getByRole('button', { name: 'Send', exact: true }).click()
await dialog.waitFor({ state: 'detached', timeout: 30000 })
const row = messageRows(page).filter({ hasText: subject }).first()
check(await until(async () => {
	await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))
	return await row.count() > 0
}, 30000), 'the message arrives')

step('save the attachment to Files')
await row.click()
const card = page.locator('.message-view__attachment-card', { hasText: fileName })
await card.waitFor()
await card.hover()
await card.getByRole('button', { name: `Save ${fileName} to Files` }).click()
await pickerOpen()
await pickInFolder(null)
await picker().getByRole('button', { name: `Save to ${folder}` }).click()
const savedAs = `/${folder}/note ${suffix} (2).txt`
check(await until(async () => await page.getByText(`Saved as ${savedAs}`).count() > 0), `saved without replacing the original (${savedAs})`)
const saved = await files.get(`${dav}/${folder}/${encodeURIComponent(`note ${suffix} (2).txt`)}`)
// As downloaded: a text part loses its last line break in the mail (it belongs to the MIME boundary).
const download = await page.request.get(new URL(await card.locator('a[download]').getAttribute('href'), base).href)
const savedText = await saved.text()
check(saved.ok() && savedText === await download.text() && savedText === fileContent.trimEnd(), `the saved file is the attachment as downloaded (${JSON.stringify(savedText)})`)

step('"Send by email" in the Files app')
await page.goto(`${base}/index.php/apps/files/files?dir=/${folder}`)
const fileRow = page.locator('[data-cy-files-list-row-name]', { hasText: `note ${suffix}` }).first()
await fileRow.waitFor({ timeout: 30000 })
await fileRow.click({ button: 'right' })
await page.getByRole('menuitem', { name: 'Send by email' }).click()
await page.waitForURL(/\/embedded\/sockless_mail\//, { timeout: 30000 })
dialog = composerDialog()
await dialog.waitFor({ timeout: 30000 })
check(await until(async () => await dialog.locator('.composer__chip', { hasText: fileName }).count() >= 1
	&& await dialog.locator('.composer__chip .loading-icon').count() === 0), 'a new message opens with the file attached')
check(!page.url().includes('fileIds'), 'the file ids are removed from the address')
await dialog.getByRole('button', { name: 'Discard' }).click()
await page.getByRole('button', { name: 'Discard' }).last().click()

step('clean up')
await files.fetch(`${dav}/${folder}`, { method: 'DELETE' })
await finish()
