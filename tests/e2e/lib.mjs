// Shared helpers for the browser checks. Settings via environment:
//   NC_URL (default http://localhost:8180), NC_USER/NC_PASSWORD (admin/admin),
//   BROWSER_CHANNEL (msedge, or chrome), MAIL_HOST (greenmail: the server name the app uses).
import { chromium } from 'playwright-core'

export const base = process.env.NC_URL ?? 'http://localhost:8180'
export const mailHost = process.env.MAIL_HOST ?? 'greenmail'
// The companion app's address; APP_PAGE=embedded uses AppAPI's own page instead.
export const appPage = process.env.APP_PAGE === 'embedded'
	? `${base}/index.php/apps/app_api/embedded/sockless_mail/sockless_mail`
	: `${base}/index.php/apps/socklessmail/`

export async function start() {
	const browser = await chromium.launch({ channel: process.env.BROWSER_CHANNEL ?? 'msedge', headless: true })
	const page = await browser.newPage({ viewport: { width: 1400, height: 900 } })
	const errors = []
	let current = 'start'
	page.on('pageerror', (e) => errors.push(`[${current}] ${String(e).split('\n')[0]}`))
	page.on('console', (m) => {
		// Expected answers the checks provoke: 400 (invalid form), 409 (no_trash before asking
		// to delete permanently). Browsers log them as errors; everything else is reported.
		if (m.type() === 'error' && !/status of (400|409)/.test(m.text())) {
			errors.push(`[${current}] ${m.text().split('\n')[0]}`)
		}
	})
	const step = (message) => {
		current = message
		console.log(`- ${message}`)
	}

	await page.goto(`${base}/login`)
	await page.fill('#user', process.env.NC_USER ?? 'admin')
	await page.fill('#password', process.env.NC_PASSWORD ?? 'admin')
	await page.click('button[type=submit]')
	await page.waitForURL((u) => !u.pathname.includes('/login'))
	await page.goto(appPage)
	await page.getByRole('button', { name: 'Add account' }).first().waitFor({ timeout: 30000 })

	return {
		browser,
		page,
		step,
		async finish() {
			await browser.close()
			if (errors.length) {
				console.log(`console errors:\n  ${errors.join('\n  ')}`)
				process.exitCode = 1
			} else {
				console.log('console errors: none')
			}
		},
	}
}

/** Prints a check result; a failed check makes the run exit non-zero. */
export function check(ok, description) {
	console.log(`${ok ? '  ok  ' : '  FAIL'} ${description}`)
	if (!ok) {
		process.exitCode = 1
	}
}

const exact = (name) => new RegExp(`^${name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`)

/** Account names in the account rail, top to bottom. */
export async function accountNames(page) {
	return page.locator('.account-rail__account').evaluateAll((buttons) => buttons.map((b) => b.getAttribute('aria-label')))
}

/** Shows an account (its folders and inbox) by clicking it in the rail; name is a string or RegExp. */
export async function openAccount(page, name) {
	await page.locator('.account-rail__account').first().waitFor()
	const pattern = typeof name === 'string' ? exact(name) : name
	await page.getByRole('navigation', { name: 'Accounts' }).getByRole('button', { name: pattern }).first().click()
}

/**
 * Shows an account's inbox. Opening an account shows the folder last open there, and with several
 * accounts the start view may be all inboxes (also headed "Inbox"), so the inbox is clicked and
 * waited for in the account's own folder pane.
 */
export async function openInbox(page, name) {
	await openAccount(page, name)
	const inbox = page.locator('.folder-pane .folder-row', { has: page.locator('.folder-row__name', { hasText: /^Inbox$/ }) }).first()
	await inbox.locator('.folder-row__main').click()
	await page.locator('.folder-pane .folder-row--active', { has: page.locator('.folder-row__name', { hasText: /^Inbox$/ }) }).waitFor({ timeout: 30000 })
	await page.locator('.message-list__header h2', { hasText: /^Inbox$/ }).waitFor({ timeout: 30000 })
}

/** Opens an account's action menu (in the folder pane, once the account is shown) and clicks an entry. */
export async function accountAction(page, name, action) {
	await openAccount(page, name)
	await page.locator('.folder-pane__account-name', { hasText: exact(name) }).waitFor()
	await page.locator('.folder-pane__account').getByRole('button', { name: 'Account actions' }).click()
	await page.getByRole('menuitem', { name: action }).click()
}

/** The message rows of the list. */
export function messageRows(page) {
	return page.locator('.message-list li.message-row')
}

/** Whether a message row is shown as unread. */
export async function isUnread(row) {
	return (await row.getAttribute('class')).includes('message-row--unread')
}

/** Clicks one of a message row's hover actions ("Delete", "Mark as read", "Flag", "More actions"). */
export async function rowAction(row, action) {
	await row.hover()
	await row.locator('.message-row__actions').getByRole('button', { name: action, exact: true }).click()
}

/** Opens a message row's menu (with the ⋯ hover button, or right-click) and clicks an entry ("Move…", ...). */
export async function rowMenuAction(row, action, { rightClick = false } = {}) {
	if (rightClick) {
		await row.click({ button: 'right' })
	} else {
		await rowAction(row, 'More actions')
	}
	await row.page().getByRole('menuitem', { name: action, exact: true }).click()
}

/** A folder in the folder pane, by its exact name. */
export function folderRow(page, name) {
	return page.locator('.folder-row', { has: page.locator('.folder-row__name', { hasText: exact(name) }) }).first()
}

export async function folderNames(page) {
	return (await page.locator('.folder-row__name').allInnerTexts()).map((s) => s.trim())
}

/** The unread count a folder shows, 0 without one. */
export async function folderUnread(page, name) {
	const counter = folderRow(page, name).locator('.folder-row__count')
	return (await counter.count()) ? Number((await counter.innerText()).trim()) : 0
}

export async function folderAction(page, name, action) {
	const row = folderRow(page, name)
	await row.hover()
	await row.getByRole('button', { name: 'Folder actions' }).click()
	await page.getByRole('menuitem', { name: action }).click()
}
