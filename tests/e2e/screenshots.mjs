// App Store screenshots into docs/screenshots/, from made-up demo mail only.
// Needs: dev/dev.sh up, the app deployed, and a Nextcloud user kept for screenshots:
//   MSYS_NO_PATHCONV=1 docker compose -f dev/docker-compose.yml exec -e OC_PASS=demo-screens-1 \
//       -u www-data nextcloud php occ user:add --password-from-env --display-name "Alice Martin" demo
// Each run, start from empty mailboxes (GreenMail keeps mail in memory) and seed them:
//   docker compose -f dev/docker-compose.yml restart greenmail && dev/dev.sh seed-demo
//   NC_USER=demo NC_PASSWORD=demo-screens-1 node screenshots.mjs
// The user's accounts Work (alice@) and Personal (bob@) are created again on every run. It
// refuses to run for a user with other accounts, so no real mail ends up in a screenshot.
import { mkdirSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { accountNames, mailHost, messageRows, openInbox, start } from './lib.mjs'

if (!process.env.NC_USER || process.env.NC_USER === 'admin') {
	console.error('Set NC_USER and NC_PASSWORD to a user kept for screenshots (not admin).')
	process.exit(1)
}

const out = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'docs', 'screenshots')
mkdirSync(out, { recursive: true })

const accounts = [
	{ name: 'Work', email: 'alice@example.test', password: 'alice', color: '#2f6fb0', badgeIcon: 'briefcase', conversations: true },
	{ name: 'Personal', email: 'bob@example.test', password: 'bob', color: '#3f8f4f', badgeIcon: 'home', conversations: false },
]

const { page, step, finish } = await start()
await page.setViewportSize({ width: 1600, height: 1000 })

const existing = await accountNames(page)
const unknown = existing.filter((n) => !accounts.some((a) => a.name === n))
if (unknown.length) {
	console.error(`This user has other accounts (${unknown.join(', ')}); use a user kept for screenshots.`)
	await finish()
	process.exit(1)
}

/** Calls the app's API in the page, with the user's session. */
function api(method, path, body) {
	return page.evaluate(async ({ method, path, body }) => {
		const root = OC.webroot ?? ''
		const harp = await fetch(`${root}/exapps/sockless_mail/api/me`).then((r) => r.ok).catch(() => false)
		const base = harp ? `${root}/exapps/sockless_mail/api` : `${root}/index.php/apps/app_api/proxy/sockless_mail/api`
		const response = await fetch(base + path, {
			method,
			headers: {
				'Content-Type': 'application/json',
				'X-Sockless-Request': '1',
				requesttoken: document.head.dataset.requesttoken ?? OC.requestToken,
			},
			body: body === undefined ? undefined : JSON.stringify(body),
		})
		if (!response.ok) {
			throw new Error(`${method} ${path}: HTTP ${response.status}`)
		}
		return response.status === 204 ? null : response.json().catch(() => null)
	}, { method, path, body })
}

// A clean start: the previous run's accounts (and their cache) go.
for (const a of (await api('GET', '/accounts')).filter((a) => accounts.some((w) => w.name === a.displayName))) {
	step(`remove old account ${a.displayName}`)
	await api('DELETE', `/accounts/${a.id}`)
}
await page.reload()
await page.getByRole('button', { name: 'Add account' }).first().waitFor({ timeout: 30000 })

async function addAccount(a) {
	const dialog = page.getByRole('dialog')
	// A click right after the previous dialog closed can be swallowed by its leave transition.
	for (let attempt = 0; ; attempt++) {
		await page.getByRole('button', { name: 'Add account' }).first().click()
		try {
			await dialog.getByLabel('Email address').waitFor({ timeout: 5000 })
			break
		} catch (e) {
			if (attempt === 1) {
				throw e
			}
		}
	}
	await dialog.getByLabel('Email address').fill(a.email)
	await dialog.getByLabel('Account name').fill(a.name)
	await dialog.getByLabel('Password', { exact: true }).fill(a.password)
	const imap = dialog.locator('fieldset', { hasText: 'Incoming mail' })
	const smtp = dialog.locator('fieldset', { hasText: 'Outgoing mail' })
	await imap.locator('select').selectOption('none')
	await smtp.locator('select').selectOption('none')
	await imap.getByLabel('Server').fill(mailHost)
	await imap.getByLabel('Port').fill('3143')
	await smtp.getByLabel('Server').fill(mailHost)
	await smtp.getByLabel('Port').fill('3025')
	await dialog.getByRole('button', { name: 'Add account' }).click()
	await dialog.waitFor({ state: 'detached', timeout: 30000 })
}

for (const a of accounts) {
	step(`add account ${a.name}`)
	await addAccount(a)
}

// Appearance and conversations, as the dialogs would set them.
step('set account colours, badges and conversations')
const created = await api('GET', '/accounts')
for (const a of accounts) {
	const { id } = created.find((c) => c.displayName === a.name)
	await api('PUT', `/accounts/${id}/appearance`, { color: a.color, badgeText: null, badgeIcon: a.badgeIcon, includeInUnified: true })
	await api('PUT', `/accounts/${id}/conversations`, { enabled: a.conversations })
}

async function shot(name) {
	// No hover toolbar on the list, no toasts.
	await page.mouse.move(190, 600)
	await page.evaluate(() => document.activeElement?.closest('.message-list') && document.activeElement.blur())
	await page.waitForTimeout(1500)
	await page.evaluate(() => document.querySelectorAll('.toastify').forEach((t) => t.remove()))
	await page.screenshot({ path: join(out, name) })
	step(`saved docs/screenshots/${name}`)
}

async function open(subject) {
	await messageRows(page).filter({ hasText: subject }).first().click()
	await page.locator('.message-view, .conversation-view').getByText(subject).first().waitFor({ timeout: 30000 })
}

await page.reload()
await page.locator('.account-rail__account').first().waitFor()

// 1. All accounts, with the invitation open.
step('All accounts with an invitation')
// The app restores the last view on load, which can switch away again after the click.
for (let attempt = 0; attempt < 10; attempt++) {
	await page.getByRole('navigation', { name: 'Accounts' }).getByRole('button', { name: 'All accounts' }).click()
	await page.waitForTimeout(1000)
	if (await page.locator('.folder-pane').getByText('All accounts', { exact: true }).count()) {
		break
	}
}
await messageRows(page).filter({ hasText: 'Invitation: Q4 planning session' }).first().waitFor({ timeout: 60000 })
await open('Invitation: Q4 planning session')
await shot('1-all-accounts.png')

// 2. A conversation in the work inbox.
step('Work inbox with a conversation')
await openInbox(page, 'Work')
await open('Q4 planning: draft agenda')
await shot('2-conversation.png')

// 3. Writing a message.
step('Composer')
await page.getByRole('button', { name: 'New message' }).click()
const composer = page.getByRole('dialog').filter({ has: page.getByLabel('Subject') })
await composer.waitFor()
await composer.getByLabel('To', { exact: true }).fill('Nora Ellis <nora.ellis@northwind.example>')
await composer.getByLabel('To', { exact: true }).press('Enter')
await composer.getByLabel('Subject').fill('Relaunch date')
await composer.getByLabel('Message').fill('Hi Nora,\n\nGreat progress this week! Shall we aim for the 24th for the relaunch?\nI can share the announcement draft tomorrow.\n\nAlice')
await shot('3-composer.png')
await composer.getByRole('button', { name: 'Discard' }).click()
const confirm = page.getByRole('dialog', { name: 'Discard this message?' })
await confirm.waitFor({ timeout: 3000 }).catch(() => {})
if (await confirm.count()) {
	await confirm.getByRole('button', { name: /^Discard/ }).click()
}
await composer.waitFor({ state: 'detached' })

// 4. Dark theme, a newsletter with remote images blocked.
step('Personal inbox, dark theme')
await page.emulateMedia({ colorScheme: 'dark' })
await openInbox(page, 'Personal')
await open('November in the garden')
await shot('4-dark-newsletter.png')

await finish()
