// The Scheduled view: the entry in the folder pane, reading a scheduled message, Reschedule,
// Send now, Cancel sending (back to Drafts) and Edit. Sends to alice@example.test herself.
// Needs: dev/dev.sh up, the app deployed, and an account for alice@example.test (node mail.mjs creates one).
import { check, folderRow, messageRows, openInbox, start } from './lib.mjs'

const { page, step, finish } = await start()
const suffix = Date.now().toString().slice(-6)
const alice = 'alice@example.test'

async function until(condition, timeoutMs = 15000) {
	for (let waited = 0; waited < timeoutMs; waited += 250) {
		if (await condition()) {
			return true
		}
		await page.waitForTimeout(250)
	}
	return false
}

const composer = () => page.getByRole('dialog').filter({ has: page.getByLabel('Subject') })
const entry = page.locator('.folder-pane [data-view="scheduled"]')
const rows = page.locator('.scheduled-row')
const row = (subject) => rows.filter({ hasText: subject })
const reader = page.locator('.scheduled-view')

/** Writes a message and schedules it "In 1 hour" from the composer's menu. */
async function scheduleInAnHour(subject, cc = '') {
	await page.getByRole('button', { name: 'New message' }).click()
	const dialog = composer()
	await dialog.waitFor()
	await dialog.getByLabel('To', { exact: true }).fill(alice)
	if (cc) {
		await dialog.getByRole('button', { name: /Cc/ }).first().click()
		await dialog.getByLabel('Cc', { exact: true }).fill(cc)
	}
	await dialog.getByLabel('Subject').fill(subject)
	await dialog.getByLabel('Message').fill(`Body of ${subject}`)
	await dialog.getByRole('button', { name: 'Send later' }).click()
	await page.getByRole('menuitem', { name: /In 1 hour/ }).click()
	await dialog.waitFor({ state: 'detached', timeout: 30000 })
}

async function openScheduledView() {
	await entry.click()
	await page.locator('.scheduled-list__header h2', { hasText: 'Scheduled' }).waitFor()
}

async function openRow(subject) {
	await row(subject).click()
	await reader.locator('h2', { hasText: subject }).waitFor()
}

await openInbox(page, /^Alice/)

step('schedule a message')
const first = `Scheduled ${suffix} A`
await scheduleInAnHour(first)
check(await until(async () => await entry.count() === 1), 'the folder pane shows a Scheduled entry')
await openScheduledView()
check(await until(async () => await row(first).count() === 1), 'the message is in the Scheduled list')
check(await row(first).getByText(`To ${alice}`).count() === 1, 'the row names the recipient')

step('read it')
await openRow(first)
check(await reader.getByText(/^Sends /).count() === 1, 'the reader says when it goes out')
check(await reader.locator('dt:text-is("To") + dd', { hasText: alice }).count() === 1, 'the reader shows the recipient')
check(await until(async () => await reader.locator('iframe').count() === 1), 'the body is shown')

step('reschedule it')
const before = await row(first).locator('.scheduled-row__when').innerText()
await reader.getByRole('button', { name: 'Reschedule' }).click()
await page.getByRole('menuitem', { name: /Tomorrow morning/ }).click()
check(await until(async () => (await row(first).locator('.scheduled-row__when').innerText()) !== before), 'the new time shows in the list')

step('send it now')
await reader.getByRole('button', { name: 'Send now' }).click()
check(await until(async () => await row(first).count() === 0, 30000), 'it leaves the list once sent')

step('cancel one')
const second = `Scheduled ${suffix} B`
await openInbox(page, /^Alice/)
check(await until(async () => {
	await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))
	return await messageRows(page).filter({ has: page.getByText(first, { exact: true }) }).count() > 0
}, 30000), 'the message sent now arrived')
await scheduleInAnHour(second)
await openScheduledView()
await openRow(second)
await reader.getByRole('button', { name: 'Cancel sending' }).click()
const confirm = page.getByRole('dialog', { name: 'Cancel sending?' })
await confirm.waitFor()
await confirm.getByRole('button', { name: 'Cancel sending' }).click()
check(await until(async () => await row(second).count() === 0), 'it leaves the list')
await folderRow(page, 'Drafts').locator('.folder-row__main').click()
check(await until(async () => {
	await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))
	return await messageRows(page).filter({ has: page.getByText(second, { exact: true }) }).count() === 1
}), 'it is back in Drafts')

step('edit one')
const third = `Scheduled ${suffix} C`
await scheduleInAnHour(third)
await openScheduledView()
await openRow(third)
await reader.getByRole('button', { name: 'Edit' }).click()
const dialog = composer()
check(await until(async () => await dialog.count() === 1), 'the composer opens')
check(await dialog.getByLabel('Subject').inputValue() === third, 'with the scheduled message in it')
check(await until(async () => await row(third).count() === 0), 'and it is no longer scheduled')
// Leave nothing behind: discard the draft.
await dialog.getByRole('button', { name: 'Discard' }).click()
const discard = page.getByRole('dialog', { name: 'Discard this message?' })
await discard.waitFor()
await discard.getByRole('button', { name: 'Discard' }).click()
await dialog.waitFor({ state: 'detached' })

step('nothing left')
await openInbox(page, /^Alice/)
check(await until(async () => await entry.count() === 0), 'the Scheduled entry goes once nothing is scheduled for this account')
await finish()
