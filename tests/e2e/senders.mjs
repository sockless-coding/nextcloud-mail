// Suspicious senders: the list icon and the reader banner for a look-alike domain, "Trust", the
// trusted domains dialog, the setting that turns warnings off, and links that show another domain
// than they lead to (banner reason, marker, confirmation on click).
// Needs: dev/dev.sh up, the app deployed, and an account for alice@example.test (node mail.mjs creates one).
import { execSync } from 'node:child_process'
import { check, messageRows, openAccount, start } from './lib.mjs'

const suffix = Date.now().toString().slice(-6)
const phish = `Your account is limited ${suffix}`
const ordinary = `Lunch on Friday ${suffix}`
const linked = `Confirm your details ${suffix}`
const send = (from, subject, body = 'hello', type = 'text/plain') => execSync('curl -s --url smtp://127.0.0.1:3025 --mail-from senders@example.test --mail-rcpt alice@example.test --upload-file -',
	{ input: `From: ${from}\r\nTo: alice@example.test\r\nSubject: ${subject}\r\nMIME-Version: 1.0\r\nContent-Type: ${type}; charset=utf-8\r\n\r\n${body}\r\n` })
// pаypal.com with a Cyrillic а, as it travels in a header.
send('"PayPal" <service@xn--pypal-4ve.com>', phish)
send('Petra <petra@example.org>', ordinary)
send('News <news@example.org>', linked,
	'<p><a href="https://paypal.com.evil.test/signin">https://www.paypal.com/signin</a></p><p><a href="https://example.org/news">example.org</a></p>',
	'text/html')

const { page, step, finish } = await start()
const items = messageRows(page)
const item = (subject) => items.filter({ hasText: subject }).first()
const banner = page.locator('.message-view__warning')

async function until(condition, timeoutMs = 15000) {
	for (let waited = 0; waited < timeoutMs; waited += 250) {
		if (await condition()) {
			return true
		}
		await page.waitForTimeout(250)
	}
	return false
}

/** Opens a message; a click can be lost while the list is replaced (all inboxes, then the account's). */
async function openRow(subject) {
	const shown = async () => (await page.locator('.message-view h2').innerText().catch(() => '')) === subject
	await until(async () => {
		if (!(await shown())) {
			await item(subject).click()
		}
		await page.waitForTimeout(1000)
		return await shown()
	})
}

async function viewMenu() {
	await page.getByRole('button', { name: 'View settings' }).click()
}

await openAccount(page, /^Alice/)
await page.locator('.message-list__header h2', { hasText: /^Inbox$/ }).waitFor()
await item(phish).waitFor({ timeout: 20000 })
await item(ordinary).waitFor()

step('the list')
check(await until(async () => await item(phish).locator('.message-row__warning--danger').count() === 1), 'the look-alike sender has a red shield')
check((await item(phish).locator('.message-row__warning').getAttribute('title')).includes('is not paypal.com'), 'its tooltip says what it imitates')
check(await item(ordinary).locator('.message-row__warning').count() === 0, 'an ordinary sender has none')

step('the reader')
await openRow(phish)
check(await until(async () => await banner.count() === 1), 'the open message shows a warning')
const text = await banner.innerText()
check(text.includes('This message may be a phishing attempt'), 'headed as possible phishing')
check(text.includes('The sender\'s domain is not paypal.com: it imitates it with letters from another alphabet.'), 'explaining the other alphabet')
check(await page.locator('.message-view__address', { hasText: 'service@xn--pypal-4ve.com' }).count() === 1, 'the address shows the domain as spelled')
check(text.includes('xn--pypal-4ve.com'), 'with the domain in plain letters')
await openRow(ordinary)
check(await until(async () => await banner.count() === 0), 'no warning on the ordinary message')

step('deceptive links')
await openRow(linked)
check(await until(async () => await banner.count() === 1), 'a link showing paypal.com that leads elsewhere warns')
check((await banner.innerText()).includes('A link shows paypal.com but leads to paypal.com.evil.test.'), 'naming both domains')
const body = page.frameLocator('.message-body')
const deceptive = body.locator('a', { hasText: 'https://www.paypal.com/signin' })
check(await deceptive.getAttribute('title') === 'Shows paypal.com but leads to paypal.com.evil.test', 'the link has a tooltip with its real destination')
check(await body.locator('a', { hasText: /^example\.org$/ }).getAttribute('data-sockless-link-leads') === null, 'a link to the domain it shows is not marked')
let popups = 0
page.context().on('page', () => popups++)
await deceptive.click()
const confirm = page.getByRole('dialog', { name: 'This link leads somewhere else' })
await confirm.waitFor()
check((await confirm.innerText()).includes('https://paypal.com.evil.test/signin'), 'clicking it asks first, showing the whole address')
await confirm.getByRole('button', { name: 'Cancel' }).click()
check(await until(async () => await confirm.count() === 0), 'Cancel closes the question')
check(popups === 0, 'and nothing was opened')
await deceptive.click()
await confirm.waitFor()
// The made-up host never loads; the request shows where the tab went.
const requested = page.context().waitForEvent('request', (request) => request.url().startsWith('https://paypal.com.evil.test/'))
const opened = page.context().waitForEvent('page')
await confirm.getByRole('button', { name: 'Open paypal.com.evil.test' }).click()
const [request, tab] = await Promise.all([requested, opened])
check(request.url() === 'https://paypal.com.evil.test/signin' && request.isNavigationRequest(), 'Open opens the real address in a new tab')
await tab.close()

step('trusting the domain')
await openRow(phish)
await banner.getByRole('button', { name: 'Trust xn--pypal-4ve.com' }).click()
check(await until(async () => await banner.count() === 0), 'the banner goes away')
check(await item(phish).locator('.message-row__warning').count() === 0, 'and so does the list icon')

step('the trusted domains dialog')
await viewMenu()
await page.getByRole('menuitem', { name: 'Trusted domains' }).click()
const dialog = page.getByRole('dialog', { name: 'Trusted domains' })
await dialog.waitFor()
check(await until(async () => (await dialog.innerText()).includes('xn--pypal-4ve.com')), 'lists the trusted domain')
await dialog.getByRole('button', { name: 'Stop trusting xn--pypal-4ve.com' }).click()
check(await until(async () => !(await dialog.innerText()).includes('xn--pypal-4ve.com')), 'removing it takes it off the list')
await page.keyboard.press('Escape')
await page.reload()
await openAccount(page, /^Alice/)
await page.locator('.message-list__header h2', { hasText: /^Inbox$/ }).waitFor()
await item(phish).waitFor({ timeout: 20000 })
check(await until(async () => await item(phish).locator('.message-row__warning').count() === 1), 'after a reload the warning is back')

step('the setting')
await viewMenu()
await page.getByRole('menuitemcheckbox', { name: 'Warn about suspicious senders' }).click()
await page.keyboard.press('Escape')
check(await until(async () => await item(phish).locator('.message-row__warning').count() === 0), 'turned off, no warnings')
await viewMenu()
await page.getByRole('menuitemcheckbox', { name: 'Warn about suspicious senders' }).click()
await page.keyboard.press('Escape')
check(await until(async () => await item(phish).locator('.message-row__warning').count() === 1), 'turned on again, back')

await finish()
