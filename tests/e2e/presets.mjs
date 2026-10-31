// Send later presets: managed from the composer's Send later menu, kept on the server (they
// survive a reload), limited to some weekdays, and restored to the built-in ones.
// Needs: dev/dev.sh up, the app deployed, and an account for alice@example.test (node mail.mjs creates one).
import { check, openInbox, start } from './lib.mjs'

const { page, step, finish } = await start()
const suffix = Date.now().toString().slice(-6)
const coffee = `Coffee ${suffix}`

async function until(condition, timeoutMs = 10000) {
	for (let waited = 0; waited < timeoutMs; waited += 250) {
		if (await condition()) {
			return true
		}
		await page.waitForTimeout(250)
	}
	return false
}

const composer = () => page.getByRole('dialog').filter({ has: page.getByLabel('Subject') })
const presetsDialog = () => page.getByRole('dialog', { name: 'Send later presets' })

async function openComposerMenu() {
	if (await composer().count() === 0) {
		await page.getByRole('button', { name: 'New message' }).click()
		await composer().waitFor()
	}
	await composer().getByRole('button', { name: 'Send later' }).click()
	await page.getByRole('menu').waitFor()
}

async function menuItems() {
	const names = await page.getByRole('menuitem').allInnerTexts()
	await page.keyboard.press('Escape')
	return names.map((n) => n.split('\n')[0].trim())
}

async function managePresets() {
	await openComposerMenu()
	await page.getByRole('menuitem', { name: /Manage presets/ }).click()
	await presetsDialog().waitFor()
}

async function save() {
	await presetsDialog().getByRole('button', { name: 'Save' }).click()
	await presetsDialog().waitFor({ state: 'detached' })
}

await openInbox(page, /^Alice/)

step('start from the built-in presets')
await managePresets()
let dialog = presetsDialog()
if (await dialog.getByRole('button', { name: 'Restore defaults' }).count()) {
	// Left over from an earlier run.
	await dialog.getByRole('button', { name: 'Restore defaults' }).click()
	await save()
	await managePresets()
	dialog = presetsDialog()
}
check(await dialog.getByText('These are the built-in presets.').count() === 1, 'the built-in presets are shown as such')
check(await dialog.locator('[data-preset]').count() === 4, 'four built-in presets')

step('add, move and delete')
await dialog.getByRole('button', { name: 'Add preset' }).click()
const editor = dialog.locator('[data-preset-editor]')
await editor.getByLabel('Name').fill(coffee)
await editor.locator('#preset-kind').selectOption('hours')
await editor.getByLabel('Hours', { exact: true }).fill('3')
check(await editor.getByText(/Offered now as/).count() === 1, 'the editor shows when it would send')
await editor.getByRole('button', { name: 'Done' }).click()
check(await dialog.locator('[data-preset]').count() === 5, 'the new preset is listed')
check(await dialog.locator('[data-preset]').last().getByText('In 3 hours').count() === 1, 'described in words')
for (let i = 0; i < 4; i++) {
	await dialog.locator('[data-preset]').filter({ hasText: coffee }).getByRole('button', { name: 'Move up' }).click()
}
await dialog.getByRole('button', { name: 'Delete After lunch' }).click()
await save()

let items = await (async () => {
	await openComposerMenu()
	return menuItems()
})()
check(items[0] === coffee, `the new preset comes first in the menu (${items[0]})`)
check(!items.includes('After lunch'), 'the deleted preset is gone from the menu')
check(items.includes('Manage presets…'), 'the menu offers to manage presets')

step('they are kept on the server')
await composer().getByRole('button', { name: 'Discard' }).click().catch(() => {})
await page.reload()
await openInbox(page, /^Alice/)
await openComposerMenu()
items = await menuItems()
check(items[0] === coffee, 'after a reload the own presets are still there')

step('limit one to other weekdays')
await composer().getByRole('button', { name: 'Send later' }).click()
await page.getByRole('menuitem', { name: /Manage presets/ }).click()
dialog = presetsDialog()
await dialog.waitFor()
await dialog.getByRole('button', { name: `Edit ${coffee}` }).click()
// Switch today off (the day buttons are titled with the day's name; the UI is in English here).
check(await dialog.locator('.preset-editor__day').count() === 7, 'seven days to choose from')
const todayName = new Date().toLocaleDateString('en', { weekday: 'long' })
await dialog.locator(`.preset-editor__day[title="${todayName}"]`).click()
check(await dialog.getByText('Not offered right now.').count() === 1, 'without today it is not offered now')
await dialog.locator('[data-preset-editor]').getByRole('button', { name: 'Done' }).click()
check(await dialog.locator('[data-preset]').filter({ hasText: coffee }).getByText(/offered on/).count() === 1, 'the list says on which days')
await save()
await openComposerMenu()
items = await menuItems()
check(!items.includes(coffee), 'not offered in the menu today')

step('restore the built-in presets')
await composer().getByRole('button', { name: 'Send later' }).click()
await page.getByRole('menuitem', { name: /Manage presets/ }).click()
dialog = presetsDialog()
await dialog.waitFor()
await dialog.getByRole('button', { name: 'Restore defaults' }).click()
check(await dialog.getByText('These are the built-in presets.').count() === 1, 'the list shows the built-in presets again')
await save()
await openComposerMenu()
items = await menuItems()
check(items[0] === 'Tomorrow morning' && !items.includes(coffee), 'the menu offers the built-in presets')

await composer().getByRole('button', { name: 'Discard' }).click()
await finish()
