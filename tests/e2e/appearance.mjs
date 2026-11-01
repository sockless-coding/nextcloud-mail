// Accounts as the user sets them up: colour and badge (icon, text, initials), order by dragging
// in the rail and in Settings, and accounts left out of All accounts.
// Needs: dev/dev.sh up, the app deployed, and at least three accounts (node mail.mjs and
// node accounts.mjs create some).
import { accountAction, accountNames, check, start } from './lib.mjs'

const { page, step, finish } = await start()
const rail = page.locator('.account-rail')
const railButton = (name) => rail.getByRole('button', { name, exact: true })
const dialog = page.getByRole('dialog', { name: /^Customize / })

async function until(condition, seconds = 15) {
	for (let i = 0; i < seconds * 4; i++) {
		if (await condition()) {
			return true
		}
		await page.waitForTimeout(250)
	}
	return false
}

async function save() {
	await dialog.getByRole('button', { name: 'Save' }).click()
	await dialog.waitFor({ state: 'detached' })
	await page.waitForTimeout(400)
}

await page.locator('.account-rail__account').first().waitFor()
const names = await accountNames(page)
check(names.length >= 3, `there are at least three accounts (${names.length})`)
const [first, second] = names

step('colour and icon')
await accountAction(page, first, 'Customize…')
await dialog.waitFor()
await dialog.getByRole('button', { name: '#c13a3a' }).click()
await dialog.getByText('Icon', { exact: true }).click()
await dialog.getByRole('radio', { name: 'briefcase' }).click()
check(await dialog.locator('.appearance__preview svg').count() === 1, 'the preview shows the icon')
await save()
const badge = railButton(first).locator('.account-badge')
check(await badge.evaluate((el) => getComputedStyle(el).backgroundColor) === 'rgb(193, 58, 58)', 'the rail shows the chosen colour')
check(await badge.locator('svg').count() === 1, 'and the icon instead of the initials')

step('text badge, from the rail')
await railButton(first).click({ button: 'right' })
await dialog.waitFor()
await dialog.getByText('Text or emoji', { exact: true }).click()
await dialog.getByLabel('Up to three characters').fill('🦊ok')
await save()
check((await badge.innerText()).trim() === '🦊ok', 'right-click customizes; the badge shows the text')

await railButton(first).click({ button: 'right' })
await dialog.waitFor()
await dialog.getByText('Text or emoji', { exact: true }).click()
await dialog.getByLabel('Up to three characters').fill('ABCD')
await dialog.getByRole('button', { name: 'Save' }).click()
check(await until(async () => (await dialog.innerText()).includes('Use at most three characters.')), 'more than three characters are refused')
await dialog.getByText('Initials', { exact: true }).click()
await dialog.getByRole('button', { name: 'Automatic' }).click()
await save()
check((await badge.innerText()).trim().length <= 2 && await badge.locator('svg').count() === 0, 'back to the initials')

step('order')
await railButton(first).dragTo(railButton(second), { targetPosition: { x: 20, y: 36 } })
check(await until(async () => (await accountNames(page)).slice(0, 2).join() === [second, first].join()), 'dragging in the rail moves the account')
await page.reload()
await page.locator('.account-rail__account').first().waitFor()
check((await accountNames(page)).slice(0, 2).join() === [second, first].join(), 'the order is kept')

await page.getByRole('button', { name: 'Settings', exact: true }).click()
const settings = page.getByRole('dialog', { name: 'Settings' })
await settings.waitFor()
const rows = settings.locator('.settings__account')
await rows.nth(1).locator('.settings__handle').dragTo(rows.nth(0), { targetPosition: { x: 40, y: 4 } })
check(await until(async () => (await accountNames(page)).slice(0, 2).join() === [first, second].join()), 'dragging in Settings moves it back')

step('All accounts')
const third = names[2]
const thirdRow = rows.filter({ hasText: third }).first()
// An earlier run may have stopped with it left out.
if (!(await thirdRow.getByRole('switch', { name: /in All accounts/ }).isChecked())) {
	await thirdRow.getByText('In All accounts').click()
	await until(async () => await thirdRow.getByRole('switch', { name: /in All accounts/ }).isChecked())
}
await thirdRow.getByText('In All accounts').click()
check(await until(async () => !(await thirdRow.getByRole('switch', { name: /in All accounts/ }).isChecked())), `${third} is left out`)
await page.keyboard.press('Escape')
await settings.waitFor({ state: 'detached' })
await page.waitForTimeout(400)

await rail.getByRole('button', { name: 'All accounts' }).click()
await page.locator('.message-list__header h2', { hasText: /^Inbox$/ }).waitFor()
const accountCount = () => page.locator('.folder-pane__account-address').first().innerText().then((text) => text.trim())
check(await until(async () => (await accountCount()) === `${names.length - 1} accounts`),
	`All accounts counts one account less (${await accountCount()})`)
await until(async () => await page.locator('.message-row').count() > 0)
check(await page.locator(`.message-row [title="${third}"]`).count() === 0, `the unified inbox has none of ${third}'s mail`)

await page.getByRole('button', { name: 'Settings', exact: true }).click()
await settings.waitFor()
await rows.filter({ hasText: third }).first().getByText('In All accounts').click()
check(await until(async () => await rows.filter({ hasText: third }).first().getByRole('switch', { name: /in All accounts/ }).isChecked()), 'and taken back in')
await page.keyboard.press('Escape')
await settings.waitFor({ state: 'detached' })
check(await until(async () => (await accountCount()) === `${names.length} accounts`), `All accounts counts it again (${await accountCount()})`)

step('Conversations')
await page.getByRole('button', { name: 'Settings', exact: true }).click()
await settings.waitFor()
const conversations = () => rows.filter({ hasText: third }).first().getByRole('switch', { name: /as conversations/ })
const wasOn = await conversations().isChecked()
await rows.filter({ hasText: third }).first().getByText('Conversations', { exact: true }).click()
check(await until(async () => (await conversations().isChecked()) !== wasOn), 'the Conversations switch turns over')
await page.keyboard.press('Escape')
await settings.waitFor({ state: 'detached' })
await page.waitForTimeout(400)
await page.reload()
await page.getByRole('button', { name: 'Settings', exact: true }).click()
await settings.waitFor()
check(await until(async () => (await conversations().isChecked()) !== wasOn), 'and stays so after a reload (kept on the server)')
await rows.filter({ hasText: third }).first().getByText('Conversations', { exact: true }).click()
check(await until(async () => (await conversations().isChecked()) === wasOn), 'and turns back')
await page.keyboard.press('Escape')

await finish()
