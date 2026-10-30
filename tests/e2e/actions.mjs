// Message and folder actions: read state, flag, delete (with and without Trash), move, folders.
// Works on three messages it sends itself, so it does not depend on other checks or earlier runs.
// Needs: dev/dev.sh up, the app deployed, and an account for alice@example.test (node mail.mjs creates one).
import { execSync } from 'node:child_process'
import { accountAction, check, folderAction, folderNames, folderRow, folderUnread, isUnread, messageRows, openAccount, rowAction, rowMenuAction, start } from './lib.mjs'

const suffix = Date.now().toString().slice(-6)
const [readMe, deleteMe, moveMe] = ['read', 'delete', 'move'].map((what) => `Action ${what} ${suffix}`)
for (const subject of [moveMe, deleteMe, readMe]) {
	execSync('curl -s --url smtp://127.0.0.1:3025 --mail-from actions@example.test --mail-rcpt alice@example.test --upload-file -',
		{ input: `From: Actions <actions@example.test>\r\nTo: alice@example.test\r\nSubject: ${subject}\r\n\r\nhello\r\n` })
}

// Only now load the page, so the initial folder counts already include the new messages.
const { page, step, finish } = await start()
const items = messageRows(page)
const item = (subject) => items.filter({ hasText: subject }).first()
const unread = (folder) => folderUnread(page, folder)
const isBold = (subject) => isUnread(item(subject))

/** Polls an async condition (UI updates follow requests), up to 10 seconds. */
async function until(condition) {
	for (let i = 0; i < 40; i++) {
		if (await condition()) {
			return true
		}
		await page.waitForTimeout(250)
	}
	return false
}

async function openFolder(name) {
	await folderRow(page, name).locator('.folder-row__main').click()
	await page.locator('.message-list__header h2', { hasText: new RegExp(`^${name}$`) }).waitFor()
}

async function submitFolderName(name) {
	const dialog = page.getByRole('dialog')
	await dialog.getByLabel('Folder name').fill(name)
	await dialog.getByRole('button', { name: /^(Create|Rename)$/ }).click()
	await dialog.waitFor({ state: 'detached' })
	await folderRow(page, name).waitFor()
}

await openAccount(page, /^Alice/)
const aliceName = (await page.locator('.folder-pane__account-name').innerText()).trim()
await openFolder('Inbox')
await item(readMe).waitFor()

// Opening an unread message marks it as read; the list and the counter follow.
const unreadBefore = await unread('Inbox')
check(await isBold(readMe), 'new message is shown as unread')
await item(readMe).click()
await page.locator('.message-view h2', { hasText: readMe }).waitFor()
check(await until(async () => await unread('Inbox') === unreadBefore - 1), `opening it lowers the Inbox count (${unreadBefore} -> ${await unread('Inbox')})`)
check(await until(async () => !(await isBold(readMe))), 'opened message is no longer bold')

await rowAction(item(readMe), 'Mark as unread')
check(await until(async () => await unread('Inbox') === unreadBefore && await isBold(readMe)), '"Mark as unread" from the hover actions of the row')
await page.getByRole('toolbar').getByRole('button', { name: 'Flag' }).click()
await item(readMe).locator('.message-row__flag').waitFor()
check(true, '"Flag" from the command bar shows the flag in the list')

// Without a Trash folder, delete asks first; then Trash is created and delete moves into it.
if (!(await folderNames(page)).includes('Trash')) {
	await rowAction(item(deleteMe), 'Delete')
	const confirm = page.getByRole('dialog', { name: 'Delete permanently?' })
	await confirm.waitFor()
	await confirm.getByRole('button', { name: 'Cancel' }).click()
	await confirm.waitFor({ state: 'detached' })
	check(await item(deleteMe).count() === 1, 'no Trash: "Delete permanently?" asked, Cancel keeps the message')
	await accountAction(page, aliceName, 'New folder')
	await submitFolderName('Trash')
	step('created folder "Trash"')
}
await rowAction(item(deleteMe), 'Delete')
check(await until(async () => await item(deleteMe).count() === 0), 'deleted message leaves the list')
await openFolder('Trash')
check(await until(async () => await item(deleteMe).count() === 1), 'and is in Trash')

// Move into a new folder through the dialog, then rename that folder.
const folder = `E2E ${suffix}`
await accountAction(page, aliceName, 'New folder')
await submitFolderName(folder)
await openFolder('Inbox')
await rowMenuAction(item(moveMe), 'Move…')
const moveDialog = page.getByRole('dialog')
await moveDialog.getByLabel('Find folder').fill(suffix)
await moveDialog.getByText(folder, { exact: true }).click()
await moveDialog.getByRole('button', { name: 'Move' }).click()
await moveDialog.waitFor({ state: 'detached' })
check(await until(async () => await item(moveMe).count() === 0), 'moved message leaves the Inbox')

await folderAction(page, folder, 'Rename')
await submitFolderName(`${folder} renamed`)
await openFolder(`${folder} renamed`)
check(await until(async () => await item(moveMe).count() === 1), 'renamed folder contains the moved message')

await finish()
