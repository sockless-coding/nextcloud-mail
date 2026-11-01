// Settings: the size new messages open in, and what the composer's close button does (keep,
// ask, discard). Needs: dev/dev.sh up, the app deployed, and an account for alice@example.test
// (node mail.mjs creates one).
import { check, openInbox, start } from './lib.mjs'

const { page, step, finish } = await start()
const composer = page.locator('section.composer')
const settings = page.getByRole('dialog', { name: 'Settings' })

async function choose(label) {
	await page.getByRole('button', { name: 'Settings', exact: true }).click()
	await settings.waitFor()
	await settings.getByText(label, { exact: true }).click()
	await page.keyboard.press('Escape')
	await settings.waitFor({ state: 'detached' })
	// NcDialog reports itself closed 300 ms after it is gone.
	await page.waitForTimeout(400)
}

async function newMessage(subject) {
	await page.getByRole('button', { name: 'New message' }).click()
	await composer.waitFor()
	if (subject) {
		await composer.getByLabel('Subject').fill(subject)
	}
}

const closeButton = () => composer.locator('header').getByRole('button', { name: 'Close' })

await openInbox(page, /^Alice/)

step('opening size')
await choose('Large, over the mailbox')
await newMessage()
check(await composer.evaluate((el) => el.classList.contains('composer--expanded')), 'Large opens new messages enlarged')
await closeButton().click()
await composer.waitFor({ state: 'detached' })
check(await page.getByRole('dialog', { name: /draft/ }).count() === 0, 'an empty message closes without asking')
await choose('Small, docked at the bottom')
await newMessage()
check(!(await composer.evaluate((el) => el.classList.contains('composer--expanded'))), 'Small opens them docked')
await closeButton().click()
await composer.waitFor({ state: 'detached' })

step('close asks')
await choose('Ask')
await newMessage(`Ask ${Date.now()}`)
await closeButton().click()
const ask = page.getByRole('dialog', { name: 'Save this message as a draft?' })
await ask.waitFor()
check(true, 'Close asks when the message has content')
await ask.getByRole('button', { name: 'Keep writing' }).click()
await ask.waitFor({ state: 'detached' })
check(await composer.isVisible(), 'Keep writing leaves the composer open')
await closeButton().click()
await ask.waitFor()
await ask.getByRole('button', { name: 'Discard' }).click()
await composer.waitFor({ state: 'detached' })
check(await page.getByText('Draft saved in Drafts').count() === 0, 'Discard closes without keeping a draft')

step('close discards')
await choose('Discard it')
await newMessage(`Discard ${Date.now()}`)
await closeButton().click()
await composer.waitFor({ state: 'detached' })
check(await ask.count() === 0 && await page.getByText('Draft saved in Drafts').count() === 0, 'Close discards without asking')

step('close keeps (default)')
await choose('Keep it as a draft')
await newMessage(`Keep ${Date.now()}`)
await closeButton().click()
await composer.waitFor({ state: 'detached' })
check(await page.getByText('Draft saved in Drafts').count() > 0, 'Close keeps the draft')

await finish()
