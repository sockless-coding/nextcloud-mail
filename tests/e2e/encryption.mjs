// Encrypted and signed mail: importing a passphrase-protected OpenPGP key in Settings → Encryption,
// unlocking it from the locked message, reading the decrypted text and attachment, and an S/MIME
// signature by an untrusted certificate. The fixtures (fixtures/) hold a test key (passphrase
// "e2e passphrase") for alice@example.test and messages made with it.
// Needs: dev/dev.sh up, the app deployed, and an account "Alice" for alice@example.test (node mail.mjs).
import { readFileSync } from 'node:fs'
import { createConnection } from 'node:net'
import { check, messageRows, openInbox, start } from './lib.mjs'

const fixture = (name) => readFileSync(new URL(`./fixtures/${name}`, import.meta.url), 'utf8').replace(/\r\n/g, '\n')
const PASSPHRASE = 'e2e passphrase'
const suffix = Date.now().toString().slice(-6)

/** Sends a raw message to Alice over SMTP. */
function sendMail(from, raw) {
	return new Promise((resolve, reject) => {
		const lines = ['EHLO e2e', `MAIL FROM:<${from}>`, 'RCPT TO:<alice@example.test>', 'DATA', `${raw.replace(/\n/g, '\r\n')}\r\n.`, 'QUIT']
		const socket = createConnection(Number(process.env.SMTP_PORT ?? 3025), '127.0.0.1')
		socket.setEncoding('utf8')
		socket.on('data', (data) => {
			if (/^[45]\d\d /m.test(data)) {
				reject(new Error(`SMTP: ${data}`))
			} else if (lines.length) {
				socket.write(lines.shift() + '\r\n')
			}
		})
		socket.on('end', resolve)
		socket.on('error', reject)
	})
}

/** The fixture with a fresh outer subject, date and Message-ID (all outside the protected part). */
function fresh(raw, subject) {
	return raw
		.replace(/^Subject: .*$/m, `Subject: ${subject}`)
		.replace(/^Date: .*$/m, `Date: ${new Date().toUTCString().replace('GMT', '+0000')}`)
		.replace(/^Message-Id: .*$/mi, `Message-Id: <e2e-${suffix}-${Math.random().toString(36).slice(2)}@example.test>`)
}

await sendMail('bob@example.org', fresh(fixture('encrypted.eml'), `Encrypted e2e ${suffix}`))
await sendMail('reports@example.org', fresh(fixture('smime-signed.eml'), `Signed e2e ${suffix}`))

const { page, step, finish } = await start()
const settings = page.getByRole('dialog', { name: 'Settings' })

step('import the key')
await page.getByRole('button', { name: 'Settings', exact: true }).click()
await settings.waitFor()
await settings.getByRole('link', { name: 'Encryption' }).click().catch(() => {})
const encryption = settings.locator('.encryption')
await encryption.waitFor()
// A key unlocked by an earlier run would open the message without asking.
const lockAll = encryption.getByRole('button', { name: 'Lock all keys now' })
if (await lockAll.count()) {
	await lockAll.click()
}
await encryption.getByLabel('Pasted key').fill(fixture('alice-secret.asc'))
await encryption.getByLabel('Passphrase or file password (for private keys)').fill('wrong')
await encryption.getByRole('button', { name: 'Import' }).click()
await encryption.getByText('The passphrase is wrong.').waitFor()
check(true, 'a wrong passphrase is refused on import')
await encryption.getByLabel('Passphrase or file password (for private keys)').fill(PASSPHRASE)
await encryption.getByRole('button', { name: 'Import' }).click()
const keyRow = encryption.locator('.key-row', { hasText: 'alice@example.test' }).first()
await keyRow.waitFor()
check((await keyRow.innerText()).includes('Locked'), 'the imported key is listed, locked')
await page.keyboard.press('Escape')
await settings.waitFor({ state: 'detached' })
await page.waitForTimeout(400)

step('open the encrypted message')
await openInbox(page, /^Alice/)
const encryptedRow = messageRows(page).filter({ hasText: `Encrypted e2e ${suffix}` }).first()
await encryptedRow.waitFor({ timeout: 30000 })
await encryptedRow.click()
const security = page.locator('.message-security')
await security.getByText('This message is encrypted with OpenPGP').waitFor({ timeout: 20000 })
check(true, 'the locked message asks for the passphrase')
check(await page.locator('.message-view__assistant').count() === 0, 'no Assistant menu for encrypted mail')

const passphrase = security.getByLabel(/^Passphrase for/)
await passphrase.fill('wrong')
await security.getByRole('button', { name: 'Unlock' }).click()
await security.getByText('The passphrase is wrong.').waitFor()
check(true, 'a wrong passphrase is refused')
await passphrase.fill(PASSPHRASE)
await security.getByRole('button', { name: 'Unlock' }).click()
await security.getByText('Encrypted with OpenPGP').waitFor({ timeout: 20000 })
await page.frameLocator('iframe.message-body').getByText('4711').waitFor({ timeout: 20000 })
check(true, 'the decrypted text shows')
check((await page.locator('.message-view__title h2').innerText()).trim() === 'Encrypted e2e: vault code', 'the protected subject replaces the outer one')
const attachment = page.locator('.message-view__attachment', { hasText: 'plan.txt' })
check(await attachment.count() === 1, 'the decrypted attachment is listed')
const href = await attachment.getAttribute('href')
const download = await page.evaluate(async (url) => (await fetch(url, { headers: { 'X-Sockless-Request': '1' } })).text(), href)
check(download.includes('Step one'), 'the decrypted attachment downloads')

step('answer encrypted')
await page.locator('.command-bar').getByRole('button', { name: 'Reply', exact: true }).click()
const composer = page.getByRole('dialog').filter({ has: page.getByLabel('Subject') })
await composer.waitFor()
check((await composer.locator('.ProseMirror blockquote').innerText()).includes('4711'), 'the reply quotes the decrypted text')
const status = composer.locator('.composer-security')
await status.getByText(/No OpenPGP key was found for bob@example\.org/).waitFor({ timeout: 30000 })
check(true, 'the reply starts encrypted and names the recipient without a key')
await composer.getByRole('button', { name: /^Remove Bob/ }).click()
await composer.getByLabel('To', { exact: true }).fill('alice@example.test')
await composer.getByLabel('Subject').fill(`Re: vault ${suffix}`)
await status.getByText('Encrypted with OpenPGP for 1 recipient and you.').waitFor({ timeout: 30000 })
check(true, 'a recipient with a key is shown as covered')
await composer.getByRole('button', { name: 'Send', exact: true }).click()
await page.getByText('Message sent').first().waitFor({ timeout: 30000 })
check(true, 'the encrypted reply is sent')

let replyRow = null
for (let attempt = 0; attempt < 40 && !replyRow; attempt++) {
	// As in compose.mjs: a visible tab catching up loads new mail.
	await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))
	await page.waitForTimeout(750)
	const rows = messageRows(page).filter({ hasText: `Re: vault ${suffix}` })
	if (await rows.count()) {
		replyRow = rows.first()
	}
}
check(!!replyRow, 'the encrypted reply arrives')
check(!!replyRow && await replyRow.locator('.message-row__security[aria-label="Encrypted"]').count() === 1, 'the list marks it as encrypted')
if (replyRow) {
	await replyRow.click()
	await security.getByText('Encrypted with OpenPGP').waitFor({ timeout: 20000 })
	// In the folded quote: there, not shown.
	await page.frameLocator('iframe.message-body').getByText('4711').waitFor({ state: 'attached', timeout: 20000 })
	check(true, 'the reply opens decrypted, with the quote')
}

step('import an attached key')
const boundary = `b-${suffix}`
await sendMail('carol@example.org', [
	'From: Carol <carol@example.org>', 'To: alice@example.test', `Subject: Carol's key ${suffix}`, 'MIME-Version: 1.0',
	`Content-Type: multipart/mixed; boundary="${boundary}"`, '',
	`--${boundary}`, 'Content-Type: text/plain', '', 'My key is attached.',
	`--${boundary}`, 'Content-Type: application/pgp-keys; name="carol.asc"', 'Content-Disposition: attachment; filename="carol.asc"', '',
	fixture('carol-public.asc').trimEnd(), `--${boundary}--`,
].join('\n'))
let carolRow = null
for (let attempt = 0; attempt < 40 && !carolRow; attempt++) {
	await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))
	await page.waitForTimeout(750)
	const rows = messageRows(page).filter({ hasText: `Carol's key ${suffix}` })
	if (await rows.count()) {
		carolRow = rows.first()
	}
}
await carolRow.click()
await page.getByRole('button', { name: 'Import carol.asc as a key' }).click()
await page.getByText('Imported the key for carol@example.org').waitFor({ timeout: 20000 })
check(true, 'a key attached to a message is imported with one click')

step('save a key to a contact')
await page.getByRole('button', { name: 'Settings', exact: true }).click()
await settings.waitFor()
const carolKey = settings.locator('.key-row', { hasText: 'carol@example.org' }).first()
await carolKey.waitFor()
await carolKey.getByRole('button', { name: 'Key actions' }).click()
await page.getByRole('menuitem', { name: 'Save to contact' }).click()
await page.getByText(/Saved in (a new contact|the contact)/).first().waitFor({ timeout: 20000 })
check(true, 'the key is saved into a contact')
await page.keyboard.press('Escape')
await settings.waitFor({ state: 'detached' })
await page.waitForTimeout(400)

step('S/MIME signature')
const signedRow = messageRows(page).filter({ hasText: `Signed e2e ${suffix}` }).first()
check(await signedRow.locator('.message-row__security[aria-label="Signed"]').count() === 1, 'the list marks signed mail')
await signedRow.click()
await security.getByText(/Signed by Reports <reports@example.org>, but the certificate is not trusted/).waitFor({ timeout: 20000 })
check(true, 'an untrusted S/MIME signature is shown as such')
await page.frameLocator('iframe.message-body').getByText('All figures are final.').waitFor()
check(true, 'the signed text shows')

await finish()
