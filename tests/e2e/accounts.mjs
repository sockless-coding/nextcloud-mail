// Accounts: add with a wrong then right password, validation, reorder, delete.
// Needs: dev/dev.sh up, the app deployed (register-docker or the manual loop).
import { accountAction, accountNames, mailHost, start } from './lib.mjs'

const { page, step, finish } = await start()
const suffix = Date.now().toString().slice(-5)
const first = `E2E Alice ${suffix}`
const second = `E2E Bob ${suffix}`

/** A click right after the previous dialog closed can be swallowed by its leave transition; retry once. */
async function openAddDialog() {
	const email = page.getByRole('dialog').getByLabel('Email address')
	for (let attempt = 0; attempt < 2; attempt++) {
		await page.getByRole('button', { name: 'Add account' }).first().click()
		try {
			await email.waitFor({ timeout: 5000 })
			return
		} catch (e) {
			if (attempt === 1) {
				throw e
			}
		}
	}
}

async function fillAccount(email, password, name) {
	const dialog = page.getByRole('dialog')
	await dialog.getByLabel('Email address').fill(email)
	await dialog.getByLabel('Account name').fill(name)
	await dialog.getByLabel('Password', { exact: true }).fill(password)
	const imap = dialog.locator('fieldset', { hasText: 'Incoming mail' })
	const smtp = dialog.locator('fieldset', { hasText: 'Outgoing mail' })
	await imap.locator('select').selectOption('none')
	await smtp.locator('select').selectOption('none')
	await imap.getByLabel('Server').fill(mailHost)
	await imap.getByLabel('Port').fill('3143')
	await smtp.getByLabel('Server').fill(mailHost)
	await smtp.getByLabel('Port').fill('3025')
	return dialog
}

async function testResult(dialog) {
	await dialog.getByRole('button', { name: 'Test connection' }).click()
	await dialog.locator('.notecard', { hasText: /IMAP:/ }).waitFor({ timeout: 30000 })
	return (await dialog.locator('.connection-result').innerText()).replace(/\s+/g, ' ').trim()
}

await openAddDialog()
let dialog = await fillAccount('alice@example.test', 'wrong-password', first)
step(`test with wrong password: ${await testResult(dialog)}`)
await dialog.getByLabel('Password', { exact: true }).fill('alice')
step(`test with right password: ${await testResult(dialog)}`)
await dialog.getByRole('button', { name: 'Add account' }).click()
await dialog.waitFor({ state: 'detached' })
await page.locator('.message-list__header h2').waitFor()
step(`after saving, the inbox opens: "${await page.locator('.message-list__header').innerText()}"`)

await openAddDialog()
dialog = await fillAccount('carol@example.test', 'x', 'invalid')
await dialog.locator('fieldset', { hasText: 'Incoming mail' }).getByLabel('Port').fill('70000')
await dialog.getByRole('button', { name: 'Add account' }).click()
await dialog.getByText('Enter a port between 1 and 65535.').waitFor()
step('server-side validation shown on the IMAP port')
await dialog.getByRole('button', { name: 'Cancel' }).click()
await dialog.waitFor({ state: 'detached' })

await openAddDialog()
dialog = await fillAccount('bob@example.test', 'bob', second)
await dialog.getByRole('button', { name: 'Add account' }).click()
await dialog.waitFor({ state: 'detached' })
const ours = (names) => names.filter((n) => n === first || n === second)
step(`order: ${ours(await accountNames(page)).join(', ')}`)
await accountAction(page, second, 'Move up')
await page.waitForTimeout(500)
step(`after "Move up": ${ours(await accountNames(page)).join(', ')}`)
await page.reload()
await page.locator('.account-rail__account').first().waitFor()
step(`after reload: ${ours(await accountNames(page)).join(', ')}`)

for (const name of [second, first]) {
	await accountAction(page, name, 'Delete account')
	await page.getByRole('dialog').getByRole('button', { name: 'Delete' }).click()
	await page.getByRole('dialog').waitFor({ state: 'detached' })
}
step(`after deleting: ${ours(await accountNames(page)).length} test accounts left`)
await finish()
