// Search: the open folder on the server (message text included) and all accounts from the cache.
// Needs: dev/dev.sh up, the app deployed, and an account for alice@example.test (node mail.mjs creates one).
import { execSync } from 'node:child_process'
import { check, start } from './lib.mjs'

const suffix = Date.now().toString().slice(-6)
const marker = `kumquat${suffix}`
const bodySubject = `Fruit report ${suffix}`
const nameSubject = `Postcard ${suffix}`
const send = (from, subject, body) => execSync('curl -s --url smtp://127.0.0.1:3025 --mail-from sender@example.test --mail-rcpt alice@example.test --upload-file -',
	{ input: `From: ${from}\r\nTo: alice@example.test\r\nSubject: ${subject}\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n${body}\r\n` })
send('Fruit <fruit@example.test>', bodySubject, `The ${marker} harvest was good.`)
send('=?utf-8?Q?Zo=C3=AB_Postcard?= <zoe@example.test>', nameSubject, 'Greetings')

const { page, step, finish } = await start()
const items = page.locator('.message-list li.list-item__wrapper')
const searchBox = page.getByRole('searchbox')
const header = page.locator('.message-list__header')

async function until(condition) {
	for (let i = 0; i < 60; i++) {
		if (await condition()) {
			return true
		}
		await page.waitForTimeout(250)
	}
	return false
}

const aliceRow = page.locator('li.account-entry > .app-navigation-entry').filter({ hasText: /^Alice/ }).first()
await aliceRow.locator('a').first().click()
await page.locator('.message-list__header h2', { hasText: /^Inbox$/ }).waitFor()
await items.filter({ hasText: nameSubject }).first().waitFor({ timeout: 20000 })
step('inbox shown')

await searchBox.fill(marker)
await searchBox.press('Enter')
check(await until(async () => (await header.innerText()).includes('1 result')), 'a word only in the body finds the message (server search)')
check((await items.allInnerTexts()).join('\n').includes(bodySubject), `the result is "${bodySubject}"`)

await page.getByRole('button', { name: 'Clear search' }).click()
check(await until(async () => (await header.innerText()).includes('Inbox')), 'clearing the search shows the inbox again')

await page.getByText('All accounts and folders').click()
await searchBox.fill('ZOË')
await searchBox.press('Enter')
check(await until(async () => (await items.allInnerTexts()).join('\n').includes(nameSubject)), 'search across accounts matches the sender name, case-insensitively')
const hit = items.filter({ hasText: nameSubject }).first()
check((await hit.innerText()).includes('› Inbox'), 'the result says where the message is')
await hit.click()
check(await until(async () => (await page.locator('.message-view').innerText().catch(() => '')).includes(nameSubject)), 'clicking the result opens the message')

await searchBox.fill(marker)
await searchBox.press('Enter')
check(await until(async () => (await header.innerText()).includes('No messages found') || (await page.getByText('No messages found').count()) > 0),
	'the cache search does not look into message text')

await finish()
