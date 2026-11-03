// Translations: the app shows the user's Nextcloud language (here Swedish), including labels
// that modules define up front (folder names, rule fields) and a validation message from the
// server, and stays English for English.
// Needs: dev/dev.sh up, the app deployed with l10n/sv.json, and at least one account.
// Switches the user's language to sv for the check and back afterwards.
import { base, check, mailHost, start } from './lib.mjs'

const { page, step, finish } = await start()

// The signed-in user's language, through the provisioning API (OCS).
const userUrl = `${base}/ocs/v2.php/cloud/users/${encodeURIComponent(process.env.NC_USER ?? 'admin')}`
const headers = { 'OCS-APIRequest': 'true', Accept: 'application/json' }

async function setLanguage(language) {
	const response = await page.request.put(userUrl, { headers, form: { key: 'language', value: language } })
	check(response.ok(), `language set to ${language} (HTTP ${response.status()})`)
}

const before = (await (await page.request.get(userUrl, { headers })).json()).ocs.data.language || 'en'
step(`language before: ${before}`)
try {
	await setLanguage('sv')
	await page.reload()
	await page.locator('.account-rail__account').first().waitFor({ timeout: 30000 })

	step('Swedish strings in the command bar, rail and folder pane')
	check(await page.getByRole('button', { name: 'Nytt meddelande' }).count() > 0, '"New message" is "Nytt meddelande"')
	check(await page.getByRole('navigation', { name: 'Konton' }).count() > 0, 'the account rail is labelled "Konton"')
	await page.locator('.account-rail__account').first().click()
	await page.locator('.folder-row__name', { hasText: /^Inkorg$/ }).first().waitFor({ timeout: 30000 })
	check(true, 'the inbox is called "Inkorg" (a label defined when the module loads)')

	step('Rule fields, defined when the module loads')
	await page.locator('.folder-pane__account').getByRole('button', { name: 'Kontoåtgärder' }).click()
	await page.getByRole('menuitem', { name: 'Regler' }).click()
	await page.getByRole('button', { name: 'Ny regel' }).click()
	const dialog = page.getByRole('dialog')
	const fieldOptions = await dialog.locator('select').first().locator('option').allInnerTexts()
	check(fieldOptions.includes('Ämne') && fieldOptions.includes('Från'), `rule fields are Swedish (${fieldOptions.slice(0, 4).join(', ')} …)`)
	await page.keyboard.press('Escape')
	await dialog.waitFor({ state: 'detached' })

	step('A validation message from the server')
	await page.getByRole('button', { name: 'Lägg till konto' }).first().click()
	const account = page.getByRole('dialog')
	await account.getByLabel('E-postadress').fill('carol@example.test')
	await account.getByLabel('Kontonamn').fill('l10n check')
	await account.getByLabel('Lösenord', { exact: true }).fill('x')
	const imap = account.locator('fieldset', { hasText: 'Inkommande e-post' })
	await imap.getByLabel('Server').fill(mailHost)
	await imap.getByLabel('Port').fill('70000')
	await account.locator('fieldset', { hasText: 'Utgående e-post' }).getByLabel('Server').fill(mailHost)
	await account.getByRole('button', { name: 'Lägg till konto' }).click()
	await account.getByText('Ange en port mellan 1 och 65535.').waitFor({ timeout: 15000 })
	check(true, 'the server answers "Ange en port mellan 1 och 65535."')
	await account.getByRole('button', { name: 'Avbryt' }).click()
	await account.waitFor({ state: 'detached' })
} finally {
	await setLanguage(before)
}

await page.reload()
await page.getByRole('button', { name: 'New message' }).first().waitFor({ timeout: 30000 })
check(true, 'back in English: "New message"')

await finish()
