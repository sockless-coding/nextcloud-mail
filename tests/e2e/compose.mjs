// Writing mail: new message with attachment, validation, reply, forward with attachment, Sent.
// Sends to alice@example.test herself, so everything can be checked in the same mailbox.
// Needs: dev/dev.sh up, the app deployed, and an account for alice@example.test (node mail.mjs creates one).
import { writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { accountAction, check, folderNames, folderRow, messageRows, openAccount, start } from './lib.mjs'

const { page, step, finish } = await start()
const suffix = Date.now().toString().slice(-6)
const subject = `Compose ${suffix}`
const attachmentName = `notes-${suffix}.txt`
const attachmentPath = join(tmpdir(), attachmentName)
writeFileSync(attachmentPath, `Attachment of ${subject}\n`)

const items = messageRows(page)
// Exact subject match: "Re: X" must not count as X.
const exactItem = (text) => items.filter({ has: page.getByText(text, { exact: true }) }).first()

async function until(condition, timeoutMs = 15000) {
	for (let waited = 0; waited < timeoutMs; waited += 250) {
		if (await condition()) {
			return true
		}
		await page.waitForTimeout(250)
	}
	return false
}

/** New mail arrives by polling; returning to the tab polls immediately. */
async function pollFor(text) {
	return until(async () => {
		await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))
		return await exactItem(text).count() > 0
	})
}

async function openFolder(name) {
	await folderRow(page, name).locator('.folder-row__main').click()
	await page.locator('.message-list__header h2', { hasText: new RegExp(`^${name}$`) }).waitFor()
}

async function openMessage(text) {
	await exactItem(text).click()
	await page.locator('.message-view h2').getByText(text, { exact: true }).waitFor()
}

const composerDialog = () => page.getByRole('dialog').filter({ has: page.getByLabel('Subject') })

async function sendAndWait(dialog) {
	await dialog.getByRole('button', { name: 'Send' }).click()
	await dialog.waitFor({ state: 'detached', timeout: 30000 })
}

await openAccount(page, /^Alice/)
const aliceName = (await page.locator('.folder-pane__account-name').innerText()).trim()
await openFolder('Inbox')

// Keep a copy in Sent: create the folder if the dev mailbox has none yet.
if (!(await folderNames(page)).includes('Sent')) {
	await accountAction(page, aliceName, 'New folder')
	await page.getByRole('dialog').getByLabel('Folder name').fill('Sent')
	await page.getByRole('dialog').getByRole('button', { name: 'Create' }).click()
	await page.getByRole('dialog').waitFor({ state: 'detached' })
	step('created folder "Sent"')
}

// 1. A new message with an attachment; an invalid recipient first.
await page.getByRole('button', { name: 'New message' }).click()
let dialog = composerDialog()
await dialog.waitFor()
await dialog.getByLabel('To', { exact: true }).fill('not an address')
await dialog.getByLabel('Subject').fill(subject)
await dialog.getByLabel('Message').fill('Hello from the e2e check.\nSecond line.')
await dialog.locator('input[type=file]').setInputFiles(attachmentPath)
check(await until(async () => await dialog.locator('.composer__chip', { hasText: attachmentName }).count() === 1
	&& await dialog.getByText('%').count() === 0), 'attachment uploaded')
await dialog.getByRole('button', { name: 'Send' }).click()
check(await until(async () => await dialog.getByText(/is not a valid/).count() > 0), 'invalid recipient is reported on the To field, the dialog stays open')

// The invalid entry stays as a marked chip; remove it.
await dialog.getByRole('button', { name: 'Remove not an address' }).click()
await dialog.getByLabel('To', { exact: true }).fill('Alice <alice@example.test>')
await sendAndWait(dialog)
check(await page.getByText('Message sent').count() > 0, '"Message sent" toast')
check(await pollFor(subject), 'the message arrives in the Inbox')

await openMessage(subject)
check(await page.locator('a.message-view__attachment', { hasText: attachmentName }).count() === 1, 'it carries the attachment')

// 2. Reply: prefilled, quoted, threaded.
await page.getByRole('toolbar').getByRole('button', { name: 'Reply', exact: true }).click()
dialog = composerDialog()
await dialog.waitFor()
check(await dialog.getByLabel('Subject').inputValue() === `Re: ${subject}`, 'reply subject is "Re: …"')
check((await dialog.getByRole('group', { name: 'To recipients' }).locator('.recipients__chip').first().getAttribute('title')) === 'alice@example.test', 'reply goes to the sender')
check((await dialog.locator('.ProseMirror blockquote').innerText()).includes('Hello from the e2e check.'), 'the original is quoted')
await dialog.getByLabel('Message').press('Control+Home')
await dialog.getByLabel('Message').pressSequentially('Thanks!')
await sendAndWait(dialog)
check(await pollFor(`Re: ${subject}`), 'the reply arrives')

// 3. Forward, including the original attachment.
await openMessage(subject)
await page.getByRole('toolbar').getByRole('button', { name: 'Forward' }).click()
dialog = composerDialog()
await dialog.waitFor()
check(await dialog.locator('.composer__chip', { hasText: attachmentName }).count() === 1, 'forward lists the original attachment')
await dialog.getByLabel('To', { exact: true }).fill('alice@example.test')
await sendAndWait(dialog)
check(await pollFor(`Fwd: ${subject}`), 'the forward arrives')
await openMessage(`Fwd: ${subject}`)
check(await page.locator('a.message-view__attachment', { hasText: attachmentName }).count() === 1, 'with the attachment')

// 4. Copies in Sent.
await openFolder('Sent')
check(await until(async () => await exactItem(subject).count() > 0 && await exactItem(`Re: ${subject}`).count() > 0 && await exactItem(`Fwd: ${subject}`).count() > 0),
	'all three are in Sent')

await finish()
