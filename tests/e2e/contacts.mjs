// Nextcloud Contacts: recipient suggestions from address books, groups, and adding a sender.
// Needs: dev/dev.sh up, the app deployed, and an account "Alice" with mail in its Inbox (node mail.mjs).
import { request } from 'playwright-core'
import { base, check, messageRows, openAccount, start } from './lib.mjs'

const { page, step, finish } = await start()
const suffix = Date.now().toString().slice(-6)
const group = `Team${suffix}`
const user = process.env.NC_USER ?? 'admin'
// Without the browser's session cookie: with it, Nextcloud wants a CSRF token on WebDAV writes.
const dav = await request.newContext({
	extraHTTPHeaders: { Authorization: `Basic ${Buffer.from(`${user}:${process.env.NC_PASSWORD ?? 'admin'}`).toString('base64')}` },
})
const book = `${base}/remote.php/dav/addressbooks/users/${user}/contacts`

async function putCard(uid, lines) {
	const response = await dav.fetch(`${book}/${uid}.vcf`, {
		method: 'PUT',
		headers: { 'Content-Type': 'text/vcard' },
		data: ['BEGIN:VCARD', 'VERSION:3.0', `UID:${uid}`, ...lines, 'END:VCARD', ''].join('\r\n'),
	})
	return response.ok()
}

async function until(condition, timeoutMs = 15000) {
	for (let waited = 0; waited < timeoutMs; waited += 250) {
		if (await condition()) {
			return true
		}
		await page.waitForTimeout(250)
	}
	return false
}

step('contacts in Nextcloud')
check(await putCard(`zelda-${suffix}`, [`FN:Zelda Zed${suffix}`, `EMAIL:zelda${suffix}@example.org`, `CATEGORIES:${group}`]), 'contact created')
check(await putCard(`yann-${suffix}`, [`FN:Yann Yu${suffix}`, `EMAIL:yann${suffix}@example.org`, `CATEGORIES:${group}`]), 'second contact created')

step('suggestions from the address book')
await openAccount(page, 'Alice')
await page.getByRole('button', { name: 'New message' }).click()
const dialog = page.getByRole('dialog').filter({ has: page.getByLabel('Subject') })
await dialog.waitFor()
const to = dialog.getByLabel('To', { exact: true })
await to.pressSequentially(`zed${suffix}`)
const suggestion = dialog.getByRole('option', { name: new RegExp(`Zelda Zed${suffix}`) })
check(await until(async () => await suggestion.count() === 1), 'the contact is suggested by name')
await to.press('Enter')
check(await dialog.locator('.recipients__chip', { hasText: `Zelda Zed${suffix}` }).count() === 1, 'Enter adds it as a chip with the name')

step('a group adds its members')
await to.pressSequentially(group.toLowerCase())
const groupOption = dialog.getByRole('option', { name: new RegExp(group) })
check(await until(async () => await groupOption.count() === 1), 'the group is suggested')
check((await groupOption.textContent()).includes('2 members'), 'with its number of members')
await groupOption.click()
const chips = await dialog.locator('.recipients__chip').allTextContents()
check(chips.length === 2 && chips.some((c) => c.includes(`Yann Yu${suffix}`)), 'the other member is added, Zelda only once')
await dialog.getByRole('button', { name: 'Discard' }).click()
await page.getByRole('button', { name: 'Discard' }).last().click()

step('add a sender to contacts')
const row = messageRows(page).first()
await row.click()
await page.locator('.message-view').getByRole('button', { name: 'More actions' }).click()
await page.getByRole('menuitem', { name: 'Add sender to contacts' }).click()
let addDialog = page.getByRole('dialog', { name: 'Add to contacts' })
await addDialog.waitFor()
const name = addDialog.getByLabel('Name')
await name.waitFor()
await name.fill(`Sender ${suffix}`)
await addDialog.getByRole('button', { name: 'Add', exact: true }).click()
check(await until(async () => await page.getByText(new RegExp(`Sender ${suffix} was added to Contacts`)).count() > 0), 'confirmed with a toast')
await addDialog.waitFor({ state: 'detached' })

await page.locator('.message-view').getByRole('button', { name: 'More actions' }).click()
await page.getByRole('menuitem', { name: 'Add sender to contacts' }).click()
addDialog = page.getByRole('dialog', { name: 'Add to contacts' })
check(await until(async () => await addDialog.getByText(new RegExp(`already in your contacts as Sender ${suffix}`)).count() > 0),
	'opening it again says the sender is already a contact')
await addDialog.getByRole('button', { name: 'Cancel' }).click()

step('clean up')
const listing = await dav.fetch(book, {
	method: 'REPORT',
	headers: { Depth: '1', 'Content-Type': 'text/xml' },
	data: '<card:addressbook-query xmlns:d="DAV:" xmlns:card="urn:ietf:params:xml:ns:carddav"><d:prop><d:getetag/></d:prop>'
		+ `<card:filter><card:prop-filter name="FN"><card:text-match>${suffix}</card:text-match></card:prop-filter></card:filter></card:addressbook-query>`,
})
for (const href of (await listing.text()).match(/<d:href>[^<]+\.vcf<\/d:href>/g) ?? []) {
	await dav.fetch(base + href.replace(/<\/?d:href>/g, ''), { method: 'DELETE' })
}
await finish()
