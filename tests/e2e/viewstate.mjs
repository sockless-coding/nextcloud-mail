// Remembered folder pane: expanded folders and the last open folder survive a reload, kept on the
// server (still there with the browser's copy cleared) or, by choice, only in the browser.
// Needs: dev/dev.sh up, the app deployed, and an account for alice@example.test (node mail.mjs creates one).
import { accountAction, accountNames, check, folderAction, folderRow, openAccount, start } from './lib.mjs'

const suffix = Date.now().toString().slice(-6)
const parent = `View ${suffix}`
const child = `Child ${suffix}`

const { page, step, finish } = await start()

async function until(condition) {
	for (let i = 0; i < 40; i++) {
		if (await condition()) {
			return true
		}
		await page.waitForTimeout(250)
	}
	return false
}

async function submitFolderName(name) {
	const dialog = page.getByRole('dialog')
	await dialog.getByLabel('Folder name').fill(name)
	await dialog.getByRole('button', { name: 'Create' }).click()
	await dialog.waitFor({ state: 'detached' })
	await folderRow(page, name).waitFor()
}

async function storage(name) {
	await page.getByRole('button', { name: 'View settings' }).click()
	await page.getByRole('menuitemradio', { name }).click()
	await page.keyboard.press('Escape')
}

/** Reloads; with clearLocal the browser's copy is gone first. */
async function reload(clearLocal = false) {
	// Debounced server save, and the tab saves when hidden.
	await page.waitForTimeout(1500)
	if (clearLocal) {
		await page.evaluate(() => Object.keys(localStorage).filter((k) => k.startsWith('sockless_mail.view.')).forEach((k) => localStorage.removeItem(k)))
	}
	await page.reload()
	await page.locator('.message-list__header h2').first().waitFor({ timeout: 30000 })
}

const shown = async (name) => await page.locator('.message-list__header h2', { hasText: new RegExp(`^${name}$`) }).count() === 1
const expanded = async (name) => await folderRow(page, name).locator('.folder-row__toggle').getAttribute('aria-expanded') === 'true'

await page.locator('.account-rail__account').first().waitFor()
const aliceName = (await accountNames(page)).find((n) => n.startsWith('Alice'))
check(!!aliceName, 'Alice\'s account is there')

step('folders')
// An earlier run may have left the choice on the browser.
await storage('On all devices')
await accountAction(page, aliceName, 'New folder')
await submitFolderName(parent)
await folderAction(page, parent, 'New subfolder')
await submitFolderName(child)
check(await expanded(parent), 'creating a subfolder expands its parent')
await folderRow(page, child).locator('.folder-row__main').click()
await page.locator('.message-list__header h2', { hasText: new RegExp(`^${child}$`) }).waitFor()

step('kept on the server')
await reload()
check(await until(() => shown(child)), 'the reload opens the last folder')
check(await expanded(parent), 'the parent is still expanded')
await reload(true)
check(await until(() => shown(child)), 'without the browser\'s copy the server still knows the last folder')
check(await expanded(parent), 'and the expanded parent')
if ((await accountNames(page)).length > 1) {
	await page.getByRole('navigation', { name: 'Accounts' }).getByRole('button', { name: 'All accounts' }).click()
	await page.locator('.folder-pane__account-name', { hasText: 'All accounts' }).waitFor()
	await openAccount(page, /^Alice/)
	check(await until(() => shown(child)), 'switching back to the account opens its last folder, not the inbox')
}

step('kept in the browser only')
await storage('In this browser only')
await folderRow(page, parent).locator('.folder-row__toggle').click()
await reload()
check(await until(async () => await folderRow(page, parent).count() === 1 && !(await expanded(parent))), 'the collapsed parent is remembered in the browser')
await reload(true)
check(!(await shown(child)), 'without the browser\'s copy nothing is remembered')
await storage('On all devices')
await openAccount(page, /^Alice/)
await folderRow(page, parent).waitFor()
check(!(await expanded(parent)), 'the server forgot the expanded parent')

await finish()
