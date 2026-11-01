// The Assistant in the composer: write a new message, fix the whole text, change the tone of a
// selection, translate below it; write a reply above the quote and shorten it without touching
// the quote, in rich and in plain text. Discards what it wrote.
// Needs: dev/dev.sh up, dev/dev.sh ai (integration_openai against dev/ai-stub.mjs, the
// taskworker running), the app deployed, and an account for alice@example.test (node mail.mjs).
import { execSync } from 'node:child_process'
import { check, messageRows, openInbox, start } from './lib.mjs'

const suffix = Date.now().toString().slice(-6)
const subject = `Offsite ${suffix}`
execSync('curl -s --url smtp://127.0.0.1:3025 --mail-from carol@example.test --mail-rcpt alice@example.test --upload-file -',
	{ input: `From: Carol <carol@example.test>\r\nTo: alice@example.test\r\nMessage-ID: <offsite-${suffix}@example.test>\r\nSubject: ${subject}\r\n\r\nCan you join the offsite in May?\r\n` })

const { page, step, finish } = await start()
const composer = page.locator('section.composer')
const editor = composer.getByLabel('Message')
const dialog = (name) => page.getByRole('dialog', { name })
const paragraphs = async () => (await editor.locator('> p').allInnerTexts()).map((p) => p.trim()).filter(Boolean)

async function menu(item) {
	await composer.getByRole('button', { name: 'Assistant' }).click()
	await page.getByRole('menuitem', { name: item }).click()
}

/** Waits for the answer in the open dialog and returns it. */
async function answer(name) {
	const result = dialog(name).getByTestId('assistant-result')
	await result.waitFor({ timeout: 30000 })
	return result.innerText()
}

async function choose(name, button) {
	await dialog(name).getByRole('button', { name: button, exact: true }).first().click()
	await dialog(name).waitFor({ state: 'detached' })
}

async function discard() {
	await composer.getByRole('button', { name: 'Discard' }).click()
	const confirm = page.getByRole('dialog', { name: 'Discard this message?' })
	if (await confirm.waitFor({ timeout: 3000 }).then(() => true, () => false)) {
		await confirm.getByRole('button', { name: 'Discard' }).click()
	}
	await composer.waitFor({ state: 'detached' })
}

await openInbox(page, /^Alice/)

step('write a new message')
await page.getByRole('button', { name: 'New message' }).click()
await composer.waitFor()
if (await composer.getByRole('button', { name: 'Rich text' }).count()) {
	await composer.getByRole('button', { name: 'Rich text' }).click()
}
await composer.getByLabel('To', { exact: true }).fill('team@example.test')
await composer.getByLabel('Subject').fill(`Lunch ${suffix}`)
await menu('Write the message…')
const write = 'Write with the Assistant'
await dialog(write).waitFor()
check(await dialog(write).getByRole('button', { name: 'Generate' }).isDisabled(), 'Generate waits for a request')
await dialog(write).getByLabel('What should the message say?').fill('Invite the team to lunch')
await dialog(write).getByLabel('Tone', { exact: true }).selectOption('friendly')
await dialog(write).getByRole('button', { name: 'Generate' }).click()
check(await answer(write) === 'Stub message: Invite the team to lunch\n\nBest regards', 'the message is written')
check(await dialog(write).getByText('Written by AI').count() === 1, 'labelled as written by AI')
check(await dialog(write).getByRole('button', { name: 'Replace my text' }).count() === 0, 'nothing to replace yet')
await choose(write, 'Insert')
check(JSON.stringify(await paragraphs()) === JSON.stringify(['Stub message: Invite the team to lunch', 'Best regards']), `inserted as paragraphs: ${await paragraphs()}`)

step('fix the whole text')
await menu('Fix spelling and grammar')
const rewrite = 'Rewrite with the Assistant'
check(await answer(rewrite) === '[improved] Stub message: Invite the team to lunch\n\nBest regards', 'starts at once with the whole text')
await choose(rewrite, 'Replace')
check(JSON.stringify(await paragraphs()) === JSON.stringify(['[improved] Stub message: Invite the team to lunch', 'Best regards']), `replaced: ${await paragraphs()}`)

step('change the tone of a selection')
await editor.locator('> p', { hasText: 'Best regards' }).click({ clickCount: 3 })
await menu('Change the tone…')
await dialog(rewrite).getByText('The selected text').waitFor()
await dialog(rewrite).getByLabel('Tone', { exact: true }).selectOption('more formal')
await dialog(rewrite).getByRole('button', { name: 'Generate' }).click()
check(await answer(rewrite) === '[more formal] Best regards', 'only the selection is rewritten, with the provider\'s tone')
await choose(rewrite, 'Replace')
check(JSON.stringify(await paragraphs()) === JSON.stringify(['[improved] Stub message: Invite the team to lunch', '[more formal] Best regards']), `selection replaced: ${await paragraphs()}`)

step('translate below')
await editor.locator('> p').first().click({ clickCount: 3 })
await menu('Translate…')
await dialog(rewrite).getByLabel('Translate into').selectOption('de')
await dialog(rewrite).getByRole('button', { name: 'Generate' }).click()
const translated = await answer(rewrite)
// The stub names the language as the provider's list does.
check(/^\[(German|Deutsch)\] \[improved\] Stub message/.test(translated), `translated: ${translated}`)
await choose(rewrite, 'Insert below')
const afterTranslation = await paragraphs()
check(afterTranslation.length === 3 && afterTranslation[0].startsWith('[improved]') && afterTranslation[1] === translated, `inserted below the first paragraph: ${afterTranslation}`)

step('stop and cancel')
execSync('docker stop sockless-dev-taskworker-1')
try {
	await menu('Make it shorter')
	await dialog(rewrite).getByText('The Assistant is writing…').waitFor({ timeout: 10000 })
	await dialog(rewrite).getByRole('button', { name: 'Stop' }).click()
	await dialog(rewrite).getByText('Cancelled.').waitFor({ timeout: 10000 })
	check(await dialog(rewrite).getByRole('button', { name: 'Try again' }).count() === 1, 'offers to try again')
	await choose(rewrite, 'Close')
	check((await paragraphs()).length === 3, 'the text is unchanged')
} finally {
	execSync('docker start sockless-dev-taskworker-1')
}
await discard()

step('write a reply above the quote')
await messageRows(page).filter({ hasText: subject }).first().click()
await page.locator('.message-view h2', { hasText: subject }).waitFor()
await page.locator('.message-view').getByRole('button', { name: 'Reply', exact: true }).click()
await composer.waitFor()
await editor.locator('blockquote').waitFor()
await menu('Write the reply…')
await dialog(write).getByRole('button', { name: 'Generate' }).click()
check(await answer(write) === 'Stub message: a fitting answer\n\nBest regards', 'an empty request writes a fitting reply')
await choose(write, 'Insert')
check((await paragraphs())[0] === 'Stub message: a fitting answer', 'the reply is at the top')
check(await editor.locator('blockquote', { hasText: 'Can you join the offsite in May?' }).count() === 1, 'the quote is still there')

step('shorten my text, not the quote')
await menu('Make it shorter')
check(await answer(rewrite) === '[improved] Stub message: a fitting answer\n\nBest regards', 'only the text above the quote')
await choose(rewrite, 'Replace')
const reply = await paragraphs()
check(reply[0] === '[improved] Stub message: a fitting answer' && reply[1] === 'Best regards' && /wrote:$/.test(reply[2] ?? ''), `replaced above the quote: ${reply}`)
check(await editor.locator('blockquote', { hasText: 'Can you join the offsite in May?' }).count() === 1, 'the quote is untouched')

step('plain text')
await composer.getByRole('button', { name: 'Plain text' }).click()
const text = composer.locator('textarea#composer-text')
await text.waitFor()
await menu('Make it clearer')
// Switched from rich text: paragraphs are single lines, and the quote has lost its "> ".
check(await answer(rewrite) === '[improved] [improved] Stub message: a fitting answer\nBest regards', 'plain text: the text above the quote')
await choose(rewrite, 'Replace')
const value = await text.inputValue()
check(/^\[improved\] \[improved\] Stub message: a fitting answer\nBest regards\n\nOn .+ wrote:\n\nCan you join the offsite in May\?/.test(value),
	`plain text: replaced, quote kept: ${JSON.stringify(value)}`)
await discard()
// The next new message opens in rich text again.
await page.getByRole('button', { name: 'New message' }).click()
await composer.waitFor()
if (await composer.getByRole('button', { name: 'Rich text' }).count()) {
	await composer.getByRole('button', { name: 'Rich text' }).click()
}
await discard()

await finish()
