// Conversations in the reader: with the account's Conversations switch on, opening a message
// shows its whole conversation: a branch (two answers to the first message), a late answer to an
// earlier message, someone added on Cc, the "new" line, quotes folded away, collapsing and
// expanding, and the reply guard (newer messages; people a reply all would leave out). In the
// list, the conversation is one row, and its actions change all of its messages.
// Needs: dev/dev.sh up, the app deployed, and an account for alice@example.test (node mail.mjs).
import { execSync } from 'node:child_process'
import { check, folderUnread, isUnread, messageRows, openInbox, rowAction, start } from './lib.mjs'

const suffix = Date.now().toString().slice(-6)
const subject = `Plan ${suffix}`
const id = (name) => `<plan-${suffix}-${name}@example.test>`
const date = (minutesAgo) => `Date: ${new Date(Date.now() - minutesAgo * 60_000).toUTCString().replace('GMT', '+0000')}`
const send = (from, headers, body, html = false) => execSync(`curl -s --url smtp://127.0.0.1:3025 --mail-from ${from} --mail-rcpt alice@example.test --upload-file -`,
	{ input: `${[...headers, `Content-Type: text/${html ? 'html' : 'plain'}; charset=utf-8`].join('\r\n')}\r\n\r\n${body}\r\n` })

// a ← b (Carol, adds Dave on Cc) ← d (Carol, late: c came in between); a ← c (Bob, a second answer to a).
send('bob@example.test', ['From: Bob <bob@example.test>', 'To: alice@example.test', date(10), `Message-ID: ${id('a')}`, `Subject: ${subject}`],
	'Shall we start on Monday?')
send('carol@example.test', ['From: Carol <carol@example.test>', 'To: alice@example.test', 'Cc: Dave <dave@example.test>', date(8), `Message-ID: ${id('b')}`,
	`Subject: Re: ${subject}`, `In-Reply-To: ${id('a')}`, `References: ${id('a')}`], 'Monday works for me.')
send('bob@example.test', ['From: Bob <bob@example.test>', 'To: alice@example.test', date(6), `Message-ID: ${id('c')}`,
	`Subject: Re: ${subject}`, `In-Reply-To: ${id('a')}`, `References: ${id('a')}`], 'Or Tuesday, if that is easier.')
send('carol@example.test', ['From: Carol <carol@example.test>', 'To: alice@example.test', 'Cc: Dave <dave@example.test>', date(4), `Message-ID: ${id('d')}`,
	`Subject: Re: ${subject}`, `In-Reply-To: ${id('b')}`, `References: ${id('a')} ${id('b')}`],
	'<p>Dave agrees too.</p><div class="moz-cite-prefix">On Monday, Carol wrote:</div><blockquote type="cite">Monday works for me.</blockquote>', true)

const { page, step, finish } = await start()
const view = page.locator('.message-view')
const conversation = view.locator('.conversation')
const entries = conversation.locator('.conversation__entry')
// In date order; their text is in frames, which hasText does not see.
const ORDER = ['Shall we start', 'Monday works', 'Or Tuesday', 'Dave agrees too']
const entry = (text) => entries.nth(ORDER.indexOf(text))
const settings = page.getByRole('dialog', { name: 'Settings' })

async function until(condition, seconds = 15) {
	for (let i = 0; i < seconds * 4; i++) {
		if (await condition()) {
			return true
		}
		await page.waitForTimeout(250)
	}
	return false
}

async function setConversations(on) {
	await page.getByRole('button', { name: 'Settings', exact: true }).click()
	await settings.waitFor()
	const row = settings.locator('.settings__account').filter({ hasText: 'alice@example.test' }).first()
	const toggle = row.getByRole('switch', { name: /as conversations/ })
	if ((await toggle.isChecked()) !== on) {
		await row.getByText('Conversations', { exact: true }).click()
		await until(async () => (await toggle.isChecked()) === on)
	}
	await page.keyboard.press('Escape')
	await settings.waitFor({ state: 'detached' })
	await page.waitForTimeout(400)
}

const composer = page.getByRole('dialog').filter({ has: page.getByLabel('Subject') })
async function discard() {
	await composer.getByRole('button', { name: 'Discard' }).click()
	const confirm = page.getByRole('dialog').filter({ hasText: /Discard/ }).getByRole('button', { name: 'Discard' })
	if (await confirm.isVisible().catch(() => false)) {
		await confirm.click()
	}
	await composer.waitFor({ state: 'detached' })
}

step('turn conversations on')
await openInbox(page, /^Alice/)
await setConversations(true)

step('open the conversation')
// One row in the list for all four, showing the newest.
const first = messageRows(page).filter({ hasText: subject }).first()
await first.waitFor({ timeout: 30000 })
await first.click()
await conversation.waitFor({ timeout: 20000 })
check(await until(async () => await entries.count() === 4), `the conversation has all four messages (${await entries.count()})`)
check(await conversation.locator('.conversation__new').count() === 1, 'a line marks where the new messages start')
check(await until(async () => await conversation.locator('.conversation__entry--expanded').count() === 4), 'the open message and the unread ones are expanded')

step('markers')
check(await conversation.evaluate((el) => el.classList.contains('conversation--branched')), 'two answers to the first message draw branch rails')
const late = entry('Dave agrees too')
check(/Answers Carol’s message from/.test(await late.locator('.conversation__reply-to').innerText()), 'Carol\'s late answer says which message it answers')
check(/one of 2 answers/.test(await entry('Or Tuesday').locator('.conversation__reply-to').innerText()), 'Bob\'s second answer is marked as one of two answers')
check(await entry('Monday works').locator('.conversation__chip--added', { hasText: 'Dave' }).count() === 1, 'Dave added on Cc shows as a chip')

step('quotes are folded')
const lateFrame = late.locator('iframe').contentFrame()
check(await until(async () => await lateFrame.locator('details.sockless-quote').count() === 1), 'the quote at the end is folded')
check(!(await lateFrame.getByText('Monday works for me.').isVisible()), 'and hidden until opened')
check(await lateFrame.getByText('Dave agrees too.').isVisible(), 'the answer itself shows')

step('jump to the message answered')
await late.locator('.conversation__reply-to').click()
check(await until(async () => await entry('Monday works').evaluate((el) => el.classList.contains('conversation__entry--flash'))), 'the answered message lights up')

step('collapse and expand')
await conversation.getByRole('button', { name: 'Collapse all' }).click()
check(await until(async () => await conversation.locator('.conversation__row').count() === 3), 'all but the open message collapse to one line')
await conversation.locator('.conversation__row', { hasText: 'Or Tuesday' }).click()
check(await until(async () => await entry('Or Tuesday').locator('.message-view__card').count() === 1), 'a click expands one again')
await conversation.getByRole('button', { name: 'Expand all' }).click()
check(await until(async () => await conversation.locator('.conversation__row').count() === 0), 'Expand all shows every message')

step('reply guard: newer messages')
await entry('Shall we start').locator('.message-view__actions').getByRole('button', { name: 'Reply', exact: true }).click()
const guard = page.getByRole('dialog', { name: 'There are newer messages' })
await guard.waitFor()
check(/3 messages in this conversation came after/.test(await guard.innerText()), 'replying to the first message says three came after it')
await guard.getByRole('button', { name: 'Reply to the latest' }).click()
await composer.waitFor()
check((await composer.getByRole('group', { name: 'To recipients' }).locator('.recipients__chip').first().getAttribute('title')) === 'carol@example.test',
	'"Reply to the latest" answers Carol\'s last message')
await discard()

step('reply guard: left-out people')
await entry('Monday works').locator('.message-view__actions').getByRole('button', { name: 'Reply all' }).click()
await guard.waitFor()
check(/Bob took part/.test(await guard.innerText()), 'a reply all to Carol would leave Bob out')
await guard.getByRole('button', { name: 'Reply anyway' }).click()
await composer.waitFor()
const cc = await composer.getByRole('group', { name: 'Cc recipients' }).locator('.recipients__chip').evaluateAll((chips) => chips.map((c) => c.getAttribute('title')))
check(cc.includes('bob@example.test') && cc.includes('dave@example.test'), `Bob is back on Cc, with Dave (${cc.join(', ')})`)
await discard()

step('one row per conversation')
const conversationRows = messageRows(page).filter({ hasText: subject })
check(await until(async () => await conversationRows.count() === 1), `the list shows the conversation as one row (${await conversationRows.count()})`)
const row = conversationRows.first()
check(await until(async () => (await row.locator('.message-row__count').innerText()).trim() === '4'), 'counting its four messages')
check((await row.locator('.message-row__senders').innerText()).trim() === 'Bob, Carol', `naming who wrote, oldest first (${await row.locator('.message-row__senders').innerText()})`)
check(await until(async () => !(await isUnread(row))), 'read, as the conversation was read')

step('actions on the whole conversation')
const unreadBefore = await folderUnread(page, 'Inbox')
await rowAction(row, 'Mark as unread')
check(await until(async () => await isUnread(row) && await folderUnread(page, 'Inbox') === unreadBefore + 4),
	`Mark as unread marks all four (${unreadBefore} → ${await folderUnread(page, 'Inbox')})`)
await rowAction(row, 'Mark as read')
check(await until(async () => !(await isUnread(row)) && await folderUnread(page, 'Inbox') === unreadBefore), 'Mark as read reads all four')
await rowAction(row, 'Flag')
check(await until(async () => await row.locator('.message-row__flag').count() === 1), 'Flag flags the conversation')
await rowAction(row, 'Remove flag')
check(await until(async () => await row.locator('.message-row__flag').count() === 0), 'and Remove flag takes it off')

step('opening the row opens the newest message')
await row.click()
await conversation.waitFor()
check(await until(async () => await entries.count() === 4), 'the conversation opens with all four')
check(await until(async () => await entry('Dave agrees too').locator('.message-view__card').count() === 1), 'the newest expanded')

step('all inboxes group it too')
await page.getByRole('navigation', { name: 'Accounts' }).getByRole('button', { name: 'All accounts' }).click()
await page.locator('.folder-pane__unified[data-view="inbox"]').first().click()
// Dev has several accounts on alice's mailbox: each that shows conversations has one row of four.
const listed = messageRows(page).filter({ hasText: subject })
const grouped = listed.filter({ has: page.locator('.message-row__count', { hasText: /^\s*4\s*$/ }) })
check(await until(async () => await grouped.count() >= 1),
	`All inboxes shows the conversation as one row of four (${await grouped.count()} grouped of ${await listed.count()} rows)`)
await openInbox(page, /^Alice/)

step('delete the conversation')
await rowAction(row, 'Delete')
check(await until(async () => await conversationRows.count() === 0), 'all of it leaves the inbox')

step('turn conversations off')
await setConversations(false)
await page.locator('.folder-row__name', { hasText: /^Trash$/ }).first().click()
const trashed = messageRows(page).filter({ hasText: subject })
check(await until(async () => await trashed.count() === 4), `without conversations the four are rows of their own again (in Trash: ${await trashed.count()})`)
await trashed.first().click()
await view.locator('.message-view__card').waitFor()
check(await conversation.count() === 0, 'and a message reads alone')

await finish()
