// Writing mail: new message with attachment, validation, reply, forward with attachment, Sent.
// Sends to alice@example.test herself, so everything can be checked in the same mailbox.
// Needs: dev/dev.sh up, the app deployed, and an account for alice@example.test (node mail.mjs creates one).
import { writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { check, start } from './lib.mjs'

const { page, step, finish } = await start()
const suffix = Date.now().toString().slice(-6)
const subject = `Compose ${suffix}`
const attachmentName = `notes-${suffix}.txt`
const attachmentPath = join(tmpdir(), attachmentName)
writeFileSync(attachmentPath, `Attachment of ${subject}\n`)

const items = page.locator('.message-list li.list-item__wrapper')
// Exact subject match: "Re: X" must not count as X.
const exactItem = (text) => items.filter({ has: page.getByText(text, { exact: true }) }).first()
const navRow = (name) => page.locator('.app-navigation-entry', { has: page.locator(':scope > a .app-navigation-entry__name, :scope > div .app-navigation-entry__name', { hasText: new RegExp(`^${name}$`) }) }).first()

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
	await navRow(name).locator('a').first().click()
	await page.locator('.message-list__header h2', { hasText: name }).waitFor()
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

const aliceRow = page.locator('li.account-entry > .app-navigation-entry').filter({ hasText: /^Alice/ }).first()
await aliceRow.locator('a').first().click()
await openFolder('Inbox')

// Keep a copy in Sent: create the folder if the dev mailbox has none yet.
if (!(await page.locator('.app-navigation-entry__name').allInnerTexts()).map((s) => s.trim()).includes('Sent')) {
	await aliceRow.hover()
	await aliceRow.getByRole('button', { name: 'Actions' }).first().click()
	await page.getByRole('menuitem', { name: 'New folder' }).click()
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
check((await dialog.getByLabel('To', { exact: true }).inputValue()).includes('alice@example.test'), 'reply goes to the sender')
check((await dialog.getByLabel('Message').inputValue()).includes('> Hello from the e2e check.'), 'the original is quoted')
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
