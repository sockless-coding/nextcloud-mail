// The Nextcloud Assistant in the reader: summarize a conversation (two messages), then only the
// open message, close the summary, cancel a summary that waits; translate a message and switch
// the language; check a look-alike sender for scams (from its warning) and a normal message
// (from the menu); and the admin switch.
// Needs: dev/dev.sh up, dev/dev.sh ai (integration_openai against dev/ai-stub.mjs, the
// taskworker running), the app deployed, and an account for alice@example.test (node mail.mjs).
import { execSync } from 'node:child_process'
import { check, messageRows, openInbox, start } from './lib.mjs'

const suffix = Date.now().toString().slice(-6)
const subject = `Budget ${suffix}`
const phishing = `Account locked ${suffix}`
// Dated apart: the conversation is ordered by date.
const date = (minutesAgo) => `Date: ${new Date(Date.now() - minutesAgo * 60_000).toUTCString().replace('GMT', '+0000')}`
const send = (from, headers, text) => execSync(`curl -s --url smtp://127.0.0.1:3025 --mail-from ${from} --mail-rcpt alice@example.test --upload-file -`,
	{ input: `${headers.join('\r\n')}\r\n\r\n${text}\r\n` })
send('bob@example.test', ['From: Bob <bob@example.test>', 'To: alice@example.test', date(2), `Message-ID: <budget-${suffix}@example.test>`, `Subject: ${subject}`],
	'Can we cut the budget by 10%?')
send('bob@example.test', ['From: Bob <bob@example.test>', 'To: alice@example.test', date(1), `Message-ID: <budget-${suffix}-2@example.test>`, `Subject: Re: ${subject}`,
	`In-Reply-To: <budget-${suffix}@example.test>`, `References: <budget-${suffix}@example.test>`],
	'Or 5% then?\r\n\r\nOn Monday, Bob wrote:\r\n> Can we cut the budget by 10%?')
send('service@paypa1.com', ['From: PayPal <service@paypa1.com>', 'To: alice@example.test', `Message-ID: <locked-${suffix}@paypa1.com>`, `Subject: ${phishing}`],
	'Your account is locked. Verify within 24 hours: https://paypa1.com/verify')

const { page, step, finish } = await start()
const view = page.locator('.message-view')
const menuButton = view.getByRole('button', { name: 'Assistant' })
const summary = view.locator('.assistant-summary')
const translation = view.locator('.assistant-translation')
const scam = view.locator('.assistant-scam')
const taskReads = []
page.on('request', (r) => /\/assistant\/tasks\/\d+$/.test(r.url()) && taskReads.push(r.url()))

async function assistant(item) {
	await menuButton.click()
	await page.getByRole('menuitem', { name: item }).click()
}

async function open(rowSubject) {
	const rows = messageRows(page).filter({ hasText: rowSubject })
	const row = (rowSubject.startsWith('Re:') ? rows : rows.filter({ hasNotText: 'Re:' })).first()
	await row.waitFor({ timeout: 30000 })
	await row.click()
	await view.locator('h2', { hasText: rowSubject }).waitFor()
}

await openInbox(page, /^Alice/)
await open(`Re: ${subject}`)

step('summarize the conversation')
await menuButton.waitFor({ timeout: 10000 })
const started = Date.now()
await assistant('Summarize')
const text = summary.getByTestId('assistant-summary-text')
await text.waitFor({ timeout: 30000 })
check(/Stub summary of 2 message\(s\): Budget/.test(await text.innerText()), `the summary reads both messages: ${(await text.innerText()).replace(/\n/g, ' | ')}`)
check(await summary.locator('h3').innerText() === 'Summary of 2 messages', await summary.locator('h3').innerText())
check(await summary.locator('li').count() === 1, 'the model\'s "- …" line is a list item')
check(await summary.getByText('Written by AI').count() === 1, 'labelled as written by AI')
check(Date.now() - started < 15000, `done in ${Date.now() - started} ms (pushed, not waiting for the 20 s poll)`)
check(taskReads.length <= 2, `${taskReads.length} task read(s)`)

step('only this message')
await summary.getByRole('button', { name: 'Only this message' }).click()
await summary.getByText(/Stub summary of 1 message\(s\): Re: Budget/).waitFor({ timeout: 30000 })
check(await summary.locator('h3').innerText() === 'Summary', 'titled "Summary"')
check(await summary.getByRole('button', { name: 'Whole conversation' }).count() === 1, 'offers the whole conversation again')

step('kept per message')
await open(subject)
check(await summary.count() === 0, 'another message has no summary')
await open(`Re: ${subject}`)
check(await summary.count() === 1, 'the summary is still there when coming back')

step('close')
await summary.getByRole('button', { name: 'Close summary' }).click()
check(await summary.count() === 0, 'closed')

step('cancel while waiting')
execSync('docker stop sockless-dev-taskworker-1')
try {
	await assistant('Summarize')
	await summary.getByText('Summarizing the conversation…').waitFor({ timeout: 10000 })
	await summary.getByRole('button', { name: 'Cancel' }).click()
	await summary.getByText('Cancelled.').waitFor({ timeout: 10000 })
	check(await summary.getByRole('button', { name: 'Try again' }).count() === 1, 'offers to try again')
	await summary.getByRole('button', { name: 'Close summary' }).click()
} finally {
	execSync('docker start sockless-dev-taskworker-1')
}

step('translate')
await assistant('Translate')
const translated = translation.getByTestId('assistant-translation-text')
await translated.waitFor({ timeout: 30000 })
check(/^\[English\] Re: Budget \d+\n+Or 5% then\?/.test(await translated.innerText()), `into the user's language, subject first: ${(await translated.innerText()).replace(/\n/g, ' | ')}`)
check(await translation.getByText('Translated by AI. The original is below.').count() === 1, 'labelled, original kept')
await translation.getByLabel('Translate into').selectOption('de')
const again = await translation.getByText(/^\[Deutsch\] Re: Budget/).waitFor({ timeout: 30000 }).then(() => true, () => false)
check(again, 'switching the language translates again')
await translation.getByRole('button', { name: 'Close translation' }).click()

step('scam check from the sender warning')
await open(phishing)
const warning = view.locator('.message-view__warning')
await warning.waitFor({ timeout: 10000 })
await warning.getByRole('button', { name: 'Ask the Assistant' }).click()
const verdict = scam.getByTestId('assistant-scam-verdict')
await verdict.waitFor({ timeout: 30000 })
check((await verdict.innerText()).startsWith('Likely a scam'), `verdict: ${(await verdict.innerText()).replace(/\n/g, ' | ')}`)
check(/imitates paypal/.test(await scam.locator('.assistant-scam__reasons').innerText()), 'the app\'s own findings went in as reasons')
check(await scam.getByText('Delete it.').count() === 1, 'the advice shows')
check(await warning.getByRole('button', { name: 'Ask the Assistant' }).count() === 0, 'no second check offered')
check(await view.locator('.assistant-scam__verdict--scam').count() === 1, 'shown as a scam')

step('scam check from the menu')
await open(subject)
await assistant('Check for scams')
await verdict.waitFor({ timeout: 30000 })
check((await verdict.innerText()).startsWith('Looks safe'), `verdict: ${(await verdict.innerText()).replace(/\n/g, ' | ')}`)
check(await scam.getByText('An AI assessment').count() === 1, 'labelled as an AI assessment')

step('admin switch')
await page.getByRole('button', { name: 'Administration' }).click()
const settings = page.getByRole('dialog', { name: 'Administration' })
const toggle = settings.getByText('Offer the Nextcloud Assistant in mail')
await toggle.waitFor()
await toggle.click()
await page.waitForResponse((r) => r.url().endsWith('/api/assistant'))
check(await menuButton.count() === 0, 'switched off: no Assistant menu')
await toggle.click()
await page.waitForResponse((r) => r.url().endsWith('/api/assistant'))
await menuButton.waitFor({ timeout: 5000 })
check(await menuButton.count() === 1, 'switched on again: the menu is back')
await settings.getByRole('button', { name: 'Close' }).last().click()

await finish()
