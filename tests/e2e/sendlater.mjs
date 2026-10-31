// Send later: the composer's menu offers presets and a date and time picker; a scheduled message
// goes out at its time with the browser closed. Sends to alice@example.test herself.
// Needs: dev/dev.sh up, the app deployed, and an account for alice@example.test (node mail.mjs creates one).
// Takes about four minutes: the earliest time the picker allows is a few minutes ahead.
import { check, messageRows, openAccount, openInbox, start } from './lib.mjs'

const suffix = Date.now().toString().slice(-6)
const subject = `Later ${suffix}`

let { page, step, finish } = await start()

async function until(condition, timeoutMs = 15000) {
	for (let waited = 0; waited < timeoutMs; waited += 500) {
		if (await condition()) {
			return true
		}
		await page.waitForTimeout(500)
	}
	return false
}

await openAccount(page, /^Alice/)
const aliceAddress = 'alice@example.test'

step('write a message')
await page.getByRole('button', { name: 'New message' }).click()
const composer = page.getByRole('dialog').filter({ has: page.getByLabel('Subject') })
await composer.waitFor()
await composer.getByLabel('To', { exact: true }).fill(aliceAddress)
await composer.getByLabel('Subject').fill(subject)
await composer.getByLabel('Message').fill('Sent by the server while nobody was looking.')

step('open the Send later menu')
await composer.getByRole('button', { name: 'Send later' }).click()
const menu = page.getByRole('menu')
await menu.waitFor()
check(await menu.getByRole('menuitem', { name: /Tomorrow morning/ }).count() === 1, 'offers "Tomorrow morning"')
check(await menu.getByRole('menuitem', { name: /In 1 hour/ }).count() === 1, 'offers "In 1 hour"')
const afterLunch = await menu.getByRole('menuitem', { name: /After lunch/ }).count()
check(afterLunch === (new Date().getHours() < 13 ? 1 : 0), `"After lunch" ${afterLunch ? 'offered' : 'hidden'} at this time of day`)
const monday = await menu.getByRole('menuitem', { name: /Monday morning/ }).count()
check(monday === ([5, 6, 0].includes(new Date().getDay()) ? 1 : 0), `"Monday morning" ${monday ? 'offered' : 'hidden'} today`)

step('pick a time a few minutes ahead')
await menu.getByRole('menuitem', { name: /Pick date and time/ }).click()
const picker = page.getByRole('dialog', { name: 'Send later' })
await picker.waitFor()
const sendAt = new Date(Date.now() + 4 * 60 * 1000)
sendAt.setSeconds(0, 0)
const pad = (n) => String(n).padStart(2, '0')
const local = `${sendAt.getFullYear()}-${pad(sendAt.getMonth() + 1)}-${pad(sendAt.getDate())}T${pad(sendAt.getHours())}:${pad(sendAt.getMinutes())}`
await picker.locator('input[type=datetime-local]').fill(local)

// Too soon is refused in the dialog itself.
const soon = new Date(Date.now() + 30 * 1000)
await picker.locator('input[type=datetime-local]').fill(`${soon.getFullYear()}-${pad(soon.getMonth() + 1)}-${pad(soon.getDate())}T${pad(soon.getHours())}:${pad(soon.getMinutes())}`)
check(await until(async () => await picker.getByText(/at least a few minutes/).count() === 1, 5000), 'a time less than a few minutes ahead is refused')
await picker.locator('input[type=datetime-local]').fill(local)
check(await until(async () => await picker.getByText(/at least a few minutes/).count() === 0, 5000), 'a time four minutes ahead is accepted')

await picker.getByRole('button', { name: /^Send / }).click()
check(await until(async () => await composer.count() === 0), 'the composer closes once it is scheduled')
check(await until(async () => await page.getByText(/^Scheduled for/).count() > 0, 5000), 'a toast says when it goes out')

step('close the browser until it is due')
await finish()
const waitMs = sendAt.getTime() - Date.now() + 20 * 1000
console.log(`  waiting ${Math.round(waitMs / 1000)} s with no browser open`)
await new Promise((resolve) => setTimeout(resolve, Math.max(0, waitMs)))

;({ page, step, finish } = await start())
await openInbox(page, /^Alice/)
step('look for it in the inbox')
const item = messageRows(page).filter({ has: page.getByText(subject, { exact: true }) })
check(await until(async () => {
	await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))
	return await item.count() > 0
}, 90000), 'the scheduled message arrived while no browser was open')
await finish()
