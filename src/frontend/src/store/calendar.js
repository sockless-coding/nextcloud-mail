import { apiGet, apiSend } from '../api.js'

/**
 * Nextcloud Calendar: the user's calendars and the calendar data in messages (invitations,
 * cancellations, replies, .ics files). The server reads and writes the calendars over CalDAV.
 */

const LAST_CALENDAR = 'sockless_mail.lastCalendar'
let calendars = null

/** The user's event calendars ({id, name, color, writable}), loaded once per page. */
export function loadCalendars() {
	calendars ??= apiGet('/calendars').catch((e) => {
		calendars = null
		throw e
	})
	return calendars
}

/** The calendar new events went to last time, if it still exists and is writable. */
export function preferredCalendar(list) {
	const writable = list.filter((c) => c.writable)
	return writable.find((c) => c.id === localStorage.getItem(LAST_CALENDAR)) ?? writable[0] ?? null
}

function remember(calendarId) {
	if (calendarId) {
		localStorage.setItem(LAST_CALENDAR, calendarId)
	}
}

const base = (where, messageId, partId) =>
	`/accounts/${where.accountId}/folders/${where.folderId}/messages/${messageId}/invitations/${encodeURIComponent(partId)}`

/**
 * The part to show: the iMIP one (it has a method) before plain .ics attachments.
 *
 * @param {{partId: string, method: string|null}[]} parts the message's calendar parts
 */
export function primaryCalendarPart(parts) {
	return parts.find((p) => p.method) ?? parts[0] ?? null
}

export function getInvitation(where, messageId, partId) {
	return apiGet(base(where, messageId, partId))
}

/**
 * @param {'accepted'|'tentative'|'declined'} response the answer
 * @param {{calendarId?: string, sendReply: boolean, comment?: string}} options where to store it, and whether to tell the organizer
 */
export function respond(where, messageId, partId, response, options) {
	remember(options.calendarId)
	return apiSend('POST', `${base(where, messageId, partId)}/respond`, { response, ...options })
}

export function importEvents(where, messageId, partId, calendarId) {
	remember(calendarId)
	return apiSend('POST', `${base(where, messageId, partId)}/import`, { calendarId })
}

/** A cancellation removes the event; an attendee's reply updates their answer in the organizer's event. */
export function applyToCalendar(where, messageId, partId) {
	return apiSend('POST', `${base(where, messageId, partId)}/apply`, {})
}
