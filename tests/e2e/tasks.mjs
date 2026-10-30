// Create task from a message: from the row menu (due tomorrow, all day, with the message text)
// and from the reader (due at a time, without the text); checks what Nextcloud stored over
// CalDAV and that "open in Tasks" shows the task. Removes the tasks it made.
// Needs: dev/dev.sh up with the Tasks app enabled, the app deployed, and an account for
// alice@example.test (node mail.mjs creates one).
import { execSync } from 'node:child_process'
import { base, check, messageRows, openInbox, rowMenuAction, start } from './lib.mjs'

const user = process.env.NC_USER ?? 'admin'
const auth = `Basic ${Buffer.from(`${user}:${process.env.NC_PASSWORD ?? 'admin'}`).toString('base64')}`
const suffix = Date.now().toString().slice(-6)
const [first, second] = ['invoice', 'call'].map((what) => `Task ${what} ${suffix}`)
const messageId = (subject) => `${subject.replace(/ /g, '.')}@example.test`
for (const subject of [second, first]) {
	execSync('curl -s --url smtp://127.0.0.1:3025 --mail-from tasks@example.test --mail-rcpt alice@example.test --upload-file -',
		{ input: `From: Task Sender <tasks@example.test>\r\nTo: alice@example.test\r\nMessage-ID: <${messageId(subject)}>\r\nSubject: ${subject}\r\n\r\nPlease pay by Friday.\r\n` })
}

const { page, step, finish } = await start()
const item = (subject) => messageRows(page).filter({ hasText: subject }).first()
const created = []

async function stored({ calendarId, name }) {
	const response = await fetch(`${base}/remote.php/dav/calendars/${user}/${encodeURIComponent(calendarId)}/${encodeURIComponent(name)}`, { headers: { Authorization: auth } })
	return response.ok ? (await response.text()).replace(/\r\n /g, '') : null
}

const line = (ics, name) => ics.split('\r\n').find((l) => l.startsWith(`${name}:`) || l.startsWith(`${name};`)) ?? null
const pad = (v) => String(v).padStart(2, '0')

/** Fills the open dialog and creates the task; returns where the server stored it. */
async function createIn(dialog, setUp) {
	await setUp(dialog)
	const answer = page.waitForResponse((r) => r.url().endsWith('/task') && r.request().method() === 'POST')
	await dialog.getByRole('button', { name: 'Create task' }).click()
	const response = await answer
	check(response.ok(), `task saved (HTTP ${response.status()})`)
	const task = await response.json()
	created.push(task)
	await dialog.waitFor({ state: 'detached' })
	return task
}

await openInbox(page, /^Alice/)
await item(first).waitFor({ timeout: 30000 })

step('from the row menu')
await rowMenuAction(item(first), 'Create task…', { rightClick: true })
let dialog = page.getByRole('dialog', { name: 'Create task' })
await dialog.waitFor()
check(await dialog.getByLabel('Title').inputValue() === first, 'title is the subject')
const task1 = await createIn(dialog, async (d) => {
	await d.getByRole('button', { name: 'Tomorrow' }).click()
})
const tomorrow = new Date(Date.now() + 24 * 60 * 60 * 1000)
let ics = await stored(task1)
check(!!ics, 'the task is in the task list')
check(line(ics, 'SUMMARY') === `SUMMARY:${first}`, line(ics, 'SUMMARY'))
check(line(ics, 'DUE') === `DUE;VALUE=DATE:${tomorrow.getFullYear()}${pad(tomorrow.getMonth() + 1)}${pad(tomorrow.getDate())}`, `${line(ics, 'DUE')} (tomorrow, all day)`)
check(/From: Task Sender <tasks@example\.test>.*Please pay by Friday/.test(line(ics, 'DESCRIPTION') ?? ''), 'notes have the sender and the text')
check(line(ics, 'X-SOCKLESS-MAIL-MESSAGE-ID') === `X-SOCKLESS-MAIL-MESSAGE-ID:${messageId(first)}`, 'the Message-ID is kept')
const toast = page.locator('.toast-success', { hasText: 'Task created' })
check(await toast.count() > 0, 'a toast says it was created')

step('open in Tasks')
const opened = page.waitForEvent('popup')
await toast.first().click()
const tasksPage = await opened
check(tasksPage.url().includes(`/apps/tasks/#/calendars/${task1.calendarId}/tasks/${task1.name}`), `clicking the toast opens ${tasksPage.url()}`)
const shown = await tasksPage.getByText(first).first().waitFor({ timeout: 20000 }).then(() => true, () => false)
check(shown, 'the Tasks app shows the task')
await tasksPage.close()

step('from the reader')
await item(second).click()
await page.locator('.message-view h2', { hasText: second }).waitFor()
await page.locator('.message-view').getByRole('button', { name: 'More actions' }).click()
await page.getByRole('menuitem', { name: 'Create task…' }).click()
dialog = page.getByRole('dialog', { name: 'Create task' })
await dialog.waitFor()
const task2 = await createIn(dialog, async (d) => {
	await d.getByLabel('Title').fill(`Call back ${suffix}`)
	await d.getByRole('button', { name: 'Today' }).click()
	await d.getByText('At a time').click()
	await d.getByLabel('Time', { exact: true }).fill('14:30')
	await d.getByText('Include the message text in the notes').click()
})
ics = await stored(task2)
const today = new Date()
const at = new Date(today.getFullYear(), today.getMonth(), today.getDate(), 14, 30)
const utc = `${at.getUTCFullYear()}${pad(at.getUTCMonth() + 1)}${pad(at.getUTCDate())}T${pad(at.getUTCHours())}${pad(at.getUTCMinutes())}00Z`
check(line(ics, 'SUMMARY') === `SUMMARY:Call back ${suffix}`, line(ics, 'SUMMARY'))
check(line(ics, 'DUE') === `DUE:${utc}`, `${line(ics, 'DUE')} (today 14:30 local)`)
check(!(line(ics, 'DESCRIPTION') ?? '').includes('Please pay'), 'notes without the text')

step('validation')
await rowMenuAction(item(first), 'Create task…')
dialog = page.getByRole('dialog', { name: 'Create task' })
await dialog.getByLabel('Title').fill('')
check(await dialog.getByRole('button', { name: 'Create task' }).isDisabled(), 'no title: Create task is disabled')
await dialog.getByRole('button', { name: 'Cancel' }).click()
await dialog.waitFor({ state: 'detached' })


for (const task of created) {
	await fetch(`${base}/remote.php/dav/calendars/${user}/${encodeURIComponent(task.calendarId)}/${encodeURIComponent(task.name)}`, { method: 'DELETE', headers: { Authorization: auth } })
}

await finish()
