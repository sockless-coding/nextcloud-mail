// Nextcloud Calendar: answering an invitation, a cancellation, and adding an .ics file.
// Mail goes to alice@example.test through the dev GreenMail SMTP port (127.0.0.1:3025).
// Needs: dev/dev.sh up, the app deployed, and an account "Alice" for alice@example.test (node mail.mjs).
import { createConnection } from 'node:net'
import { request } from 'playwright-core'
import { base, check, folderRow, messageRows, openAccount, start } from './lib.mjs'

const { page, step, finish } = await start()
const suffix = Date.now().toString().slice(-6)
const organizer = `olga${suffix}@example.test`
const user = process.env.NC_USER ?? 'admin'
// Without the browser's session cookie: with it, Nextcloud wants a CSRF token on WebDAV writes.
const dav = await request.newContext({
	extraHTTPHeaders: { Authorization: `Basic ${Buffer.from(`${user}:${process.env.NC_PASSWORD ?? 'admin'}`).toString('base64')}` },
})
const calendarUrl = `${base}/remote.php/dav/calendars/${user}/personal`

/** Sends a raw message to Alice over SMTP. */
function sendMail(from, raw) {
	return new Promise((resolve, reject) => {
		const lines = ['EHLO e2e', `MAIL FROM:<${from}>`, 'RCPT TO:<alice@example.test>', 'DATA', `${raw.replace(/\n/g, '\r\n')}\r\n.`, 'QUIT']
		const socket = createConnection(Number(process.env.SMTP_PORT ?? 3025), '127.0.0.1')
		socket.setEncoding('utf8')
		socket.on('data', (data) => {
			if (/^[45]\d\d /m.test(data)) {
				reject(new Error(`SMTP: ${data}`))
			} else if (lines.length) {
				socket.write(lines.shift() + '\r\n')
			}
		})
		socket.on('end', resolve)
		socket.on('error', reject)
	})
}

function ics(method, uid, extra = '') {
	return [
		'BEGIN:VCALENDAR', `METHOD:${method}`, 'PRODID:-//e2e//EN', 'VERSION:2.0',
		'BEGIN:VEVENT', `UID:${uid}`, 'DTSTAMP:20261029T100000Z', 'SEQUENCE:0',
		'DTSTART;TZID=Europe/Berlin:20261110T090000', 'DTEND;TZID=Europe/Berlin:20261110T103000',
		`SUMMARY:Review ${suffix}`, 'LOCATION:Room 4', 'RRULE:FREQ=WEEKLY;COUNT=4',
		`ORGANIZER;CN=Olga:mailto:${organizer}`,
		'ATTENDEE;PARTSTAT=NEEDS-ACTION;RSVP=TRUE;CN=Alice:mailto:alice@example.test',
		'ATTENDEE;PARTSTAT=ACCEPTED;CN=Bob:mailto:bob@corp.example',
		extra, 'END:VEVENT', 'END:VCALENDAR',
	].filter(Boolean).join('\r\n')
}

function imip(subject, method, calendar) {
	return `From: Olga <${organizer}>\nTo: alice@example.test\nSubject: ${subject}\nMIME-Version: 1.0\n`
		+ 'Content-Type: multipart/alternative; boundary="b1"\n\n--b1\nContent-Type: text/plain\n\nSee the invitation.\n'
		+ `--b1\nContent-Type: text/calendar; method=${method}; charset=utf-8\n\n${calendar.replace(/\r\n/g, '\n')}\n--b1--`
}

async function until(condition, timeoutMs = 15000) {
	for (let waited = 0; waited < timeoutMs; waited += 250) {
		if (await condition()) {
			return true
		}
		await page.waitForTimeout(250)
	}
	return false
}

async function openMessage(subject) {
	const row = messageRows(page).filter({ hasText: subject }).first()
	await until(async () => {
		await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))
		return await row.count() > 0
	}, 30000)
	await row.click()
	await page.locator('.message-view h2, .message-view__subject', { hasText: subject }).first().waitFor()
}

const card = () => page.locator('.invitation')
/** The card's text with whitespace collapsed (the status line is several text nodes). */
const cardText = async () => (await card().textContent()).replace(/\s+/g, ' ')
const stored = (uid) => dav.get(`${calendarUrl}/${uid}.ics`)
const uid = `e2e-${suffix}`

step('an invitation')
await sendMail(organizer, imip(`Invitation: Review ${suffix}`, 'REQUEST', ics('REQUEST', uid)))
await openAccount(page, 'Alice')
await openMessage(`Invitation: Review ${suffix}`)
await card().getByText(`Review ${suffix}`).waitFor()
check(await card().getByText('Invitation', { exact: true }).count() === 1, 'shown as an invitation')
check(await card().getByText('Repeats weekly').count() === 1, 'with its recurrence')
check(await card().getByText('Not in your calendar').count() === 1, 'not in the calendar yet')
check((await card().textContent()).includes('2 attendees'), 'with its attendees')

step('accept')
await card().getByRole('button', { name: 'Accept' }).click()
check(await until(async () => (await cardText()).includes('In your calendar Personal · You accepted')), 'now in the calendar, accepted')
const object = await stored(uid)
const text = (await object.text()).replace(/\r\n /g, '')
check(object.ok() && /ATTENDEE;[^\n]*PARTSTAT=ACCEPTED[^\n]*:mailto:alice@example\.test/.test(text), 'Nextcloud has the event with the answer')
check(!text.includes('SCHEDULE-AGENT') && !text.includes('METHOD:'), 'stored without METHOD, scheduling handed back to Nextcloud')

step('the answer went out from the account')
await folderRow(page, 'Sent').locator('.folder-row__main').click()
check(await until(async () => await messageRows(page).filter({ hasText: `Accepted: Review ${suffix}` }).count() === 1, 30000),
	'"Accepted: …" is in Sent')

step('a cancellation')
await sendMail(organizer, imip(`Cancelled: Review ${suffix}`, 'CANCEL', ics('CANCEL', uid, 'STATUS:CANCELLED')))
await folderRow(page, 'Inbox').locator('.folder-row__main').click()
await openMessage(`Cancelled: Review ${suffix}`)
await card().getByText('Cancelled event').waitFor()
await card().getByRole('button', { name: 'Remove from calendar' }).click()
check(await until(async () => await card().getByText('Not in your calendar').count() === 1), 'removed, says the card')
check((await stored(uid)).status() === 404, 'gone from Nextcloud')

step('an .ics file')
const fileUid = `e2e-file-${suffix}`
const file = ics('', fileUid).replace('METHOD:\r\n', '')
await sendMail(organizer, `From: Olga <${organizer}>\nTo: alice@example.test\nSubject: Event file ${suffix}\nMIME-Version: 1.0\n`
	+ 'Content-Type: multipart/mixed; boundary="b2"\n\n--b2\nContent-Type: text/plain\n\nAttached.\n'
	+ `--b2\nContent-Type: application/octet-stream; name="event.ics"\nContent-Disposition: attachment; filename="event.ics"\n\n${file.replace(/\r\n/g, '\n')}\n--b2--`)
await openMessage(`Event file ${suffix}`)
await card().getByText(`Review ${suffix}`).waitFor()
await card().getByRole('button', { name: 'Add to calendar' }).click()
check(await until(async () => (await cardText()).includes('In your calendar Personal')), 'added, says the card')
check((await stored(fileUid)).ok(), 'Nextcloud has it')

step('clean up')
await dav.fetch(`${calendarUrl}/${fileUid}.ics`, { method: 'DELETE' })
await finish()
