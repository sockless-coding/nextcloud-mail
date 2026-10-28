// Reading and writing: sender details, attachment cards, the docked composer (minimise, pop out,
// discard, one draft at a time), recipient chips with suggestions, keyboard shortcuts.
// Needs: dev/dev.sh up, the app deployed, and an account for alice@example.test (node mail.mjs creates one).
import { execSync } from 'node:child_process'
import { check, isUnread, messageRows, openAccount, start } from './lib.mjs'

const suffix = Date.now().toString().slice(-6)
const [first, second, withFile] = ['first', 'second', 'file'].map((n) => `Workspace ${n} ${suffix}`)
execSync('curl -s --url smtp://127.0.0.1:3025 --mail-from workspace@example.test --mail-rcpt alice@example.test --upload-file -', {
	input: [
		'From: Wanda Workspace <workspace@example.test>', 'To: alice@example.test', `Subject: ${withFile}`, 'MIME-Version: 1.0',
		'Content-Type: multipart/mixed; boundary="b1"', '', '--b1', 'Content-Type: text/plain', '', 'See the file.',
		'--b1', 'Content-Type: application/pdf; name="report.pdf"', 'Content-Disposition: attachment; filename="report.pdf"',
		'Content-Transfer-Encoding: base64', '', 'JVBERi0xLjQK', '--b1--', '',
	].join('\r\n'),
})
for (const subject of [second, first]) {
	execSync('curl -s --url smtp://127.0.0.1:3025 --mail-from workspace@example.test --mail-rcpt alice@example.test --upload-file -',
		{ input: `From: Wanda Workspace <workspace@example.test>\r\nTo: alice@example.test\r\nCc: Carl <carl@example.test>\r\nSubject: ${subject}\r\n\r\nhello\r\n` })
}

const { page, step, finish } = await start()
const items = messageRows(page)
const item = (subject) => items.filter({ hasText: subject }).first()
const composer = page.locator('section.composer')

async function until(condition, seconds = 10) {
	for (let i = 0; i < seconds * 4; i++) {
		if (await condition()) {
			return true
		}
		await page.waitForTimeout(250)
	}
	return false
}

await openAccount(page, /^Alice/)
await page.locator('.message-list__header h2', { hasText: /^Inbox$/ }).waitFor()
await item(first).waitFor({ timeout: 20000 })
step('reading')

await item(first).click()
await page.locator('.message-view h2', { hasText: first }).waitFor()
await page.locator('.message-view__recipients').click()
check(await until(async () => (await page.locator('.message-view__details').innerText().catch(() => '')).includes('carl@example.test')),
	'the details show every address in full')

await item(withFile).click()
await page.locator('.message-view__attachment').first().waitFor({ timeout: 20000 })
check((await page.locator('.message-view__file-type').first().innerText()).trim() === 'PDF', 'attachments are cards with their file type')

step('composer window')
await page.locator('.message-view h2').first().click()
await page.keyboard.press('n')
await composer.waitFor()
check(await composer.getByLabel('To', { exact: true }).evaluate((e) => e === document.activeElement), 'N opens a new message with the cursor in To')
await composer.getByLabel('To', { exact: true }).fill('wan')
check(await until(async () => (await composer.getByRole('option', { name: /workspace@example\.test/ }).count()) > 0), 'addresses of loaded mail are suggested')
await composer.getByLabel('To', { exact: true }).press('Enter')
check((await composer.locator('.recipients__chip').first().getAttribute('title')) === 'workspace@example.test', 'Enter takes the suggestion as a chip')
await composer.getByLabel('Subject').fill(`Draft ${suffix}`)

await composer.getByRole('button', { name: 'Minimize' }).click()
check(!(await composer.getByLabel('Subject').isVisible()), 'minimised, only the title bar is left')
await item(second).click()
await page.locator('.message-view h2', { hasText: second }).waitFor()
check(true, 'the mailbox stays usable while writing')
await page.getByRole('toolbar', { name: 'Message actions' }).getByRole('button', { name: 'Reply', exact: true }).click()
check(await until(async () => await composer.getByLabel('Subject').isVisible()), 'replying while a draft is open shows the draft instead')
check((await composer.getByLabel('Subject').inputValue()) === `Draft ${suffix}`, 'and keeps it')

await composer.getByRole('button', { name: 'Pop out' }).click()
check(await until(async () => (await composer.getAttribute('class')).includes('composer--expanded')), 'Pop out makes it large')
await composer.getByRole('button', { name: 'Dock' }).click()

await composer.getByRole('button', { name: 'Discard' }).click()
const confirm = page.getByRole('dialog', { name: 'Discard this message?' })
await confirm.waitFor()
await confirm.getByRole('button', { name: 'Keep writing' }).click()
await confirm.waitFor({ state: 'detached' })
check(await composer.isVisible(), '"Keep writing" keeps the draft')
await composer.getByRole('button', { name: 'Discard' }).click()
await confirm.waitFor()
await confirm.getByRole('button', { name: 'Discard' }).click()
check(await until(async () => (await composer.count()) === 0), 'Discard closes it')

step('keyboard shortcuts')
await item(second).click()
await page.locator('.message-view h2', { hasText: second }).waitFor()
await item(second).focus()
await page.keyboard.press('Control+u')
check(await until(async () => await isUnread(item(second))), 'Ctrl+U marks as unread')
await page.keyboard.press('Control+q')
check(await until(async () => !(await isUnread(item(second)))), 'Ctrl+Q marks as read')
await page.keyboard.press('Insert')
check(await until(async () => (await item(second).locator('.message-row__flag').count()) === 1), 'Insert flags')
await page.keyboard.press('r')
await composer.waitFor()
check((await composer.getByLabel('Subject').inputValue()) === `Re: ${second}`, 'R replies')
check(await composer.getByLabel('Message').evaluate((e) => e === document.activeElement), 'with the cursor in the text')
await composer.getByRole('button', { name: 'Discard' }).click()
await composer.waitFor({ state: 'detached' })

await item(first).click()
await page.locator('.message-view h2', { hasText: first }).waitFor()
await item(first).focus()
await page.keyboard.press('Shift+?')
await page.getByRole('dialog', { name: 'Keyboard shortcuts' }).waitFor()
check(true, '? shows the shortcuts')
await page.keyboard.press('Escape')
await page.getByRole('dialog', { name: 'Keyboard shortcuts' }).waitFor({ state: 'detached' })
await item(first).focus()
await page.keyboard.press('Delete')
const permanent = page.getByRole('dialog', { name: 'Delete permanently?' })
if (await until(async () => (await permanent.count()) > 0 || (await item(first).count()) === 0, 5) && await permanent.count()) {
	await permanent.getByRole('button', { name: 'Delete permanently' }).click()
}
check(await until(async () => (await item(first).count()) === 0), 'Delete deletes the message')
await page.keyboard.press('/')
check(await page.locator('.command-bar__search input').evaluate((e) => e === document.activeElement), '/ goes to the search box')

await finish()
