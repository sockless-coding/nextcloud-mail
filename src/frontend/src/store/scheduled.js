import { showSuccess, showWarning } from '@nextcloud/dialogs/toast'
import { t } from '@nextcloud/l10n'
import { reactive } from 'vue'
import { apiGet, apiSend } from '../api.js'
import { closeDrawer } from './layout.js'
import { ActionError, closeMessage, loadFolders, mailboxState, refreshMessages } from './mailbox.js'
import { clearSearch } from './search.js'
import { endSelectionMode } from './selection.js'
import { formatSendTime } from './sendLater.js'

/**
 * Messages waiting to be sent later (the server sends them; see Scheduling/ in the API). The
 * list covers all accounts and is kept loaded for the counts in the folder pane: loaded at the
 * start, again on the scheduledChanged push and when polled. The Scheduled view is shown while
 * mailboxState.selected.scheduled is set: across accounts (accountId null) or for one account.
 */
export const scheduledState = reactive({
	/** [{ id, accountId, sendAt, dueAt, status, subject, recipients, preview, hasAttachments, size, attempts, errorCode, error }] */
	list: [],
	loaded: false,
	loading: false,
	error: null,
	/** The message open in the reading pane: { id, detail, loading, error }. */
	open: { id: null, detail: null, loading: false, error: null },
	/** Ids with an action running (send now, reschedule, cancel). */
	busy: {},
})

export async function loadScheduled() {
	scheduledState.loading = true
	try {
		scheduledState.list = await apiGet('/scheduled')
		scheduledState.loaded = true
		scheduledState.error = null
		const open = scheduledState.open
		if (open.id && !scheduledState.list.some((m) => m.id === open.id)) {
			closeScheduled()
		} else if (open.detail) {
			// The summary may have changed (failed, retried); the content cannot.
			open.detail = { ...open.detail, message: scheduledState.list.find((m) => m.id === open.id) }
		}
	} catch (e) {
		scheduledState.error = new ActionError(e).message
	} finally {
		scheduledState.loading = false
	}
}

/** The scheduled messages of an account, or of all accounts, soonest first. */
export function scheduledOf(accountId = null) {
	return accountId ? scheduledState.list.filter((m) => m.accountId === accountId) : scheduledState.list
}

/**
 * Where a message stands for the user: waiting, retrying after a failed attempt, being sent,
 * or failed (waiting for the user).
 *
 * @return {'scheduled'|'retrying'|'sending'|'failed'}
 */
export function scheduleStatus(message) {
	if (message.status === 'scheduled' && message.attempts > 0) {
		return 'retrying'
	}
	return message.status
}

/** "To Ann, bob@example.com and 2 more". */
export function recipientsText(message) {
	const names = message.recipients.map((r) => r.name || r.address)
	if (names.length === 0) {
		return t('sockless_mail', '(no recipients)')
	}
	return names.length > 2
		? t('sockless_mail', 'To {names} and {count} more', { names: names.slice(0, 2).join(', '), count: names.length - 2 })
		: t('sockless_mail', 'To {names}', { names: names.join(', ') })
}

export function failedCount(accountId = null) {
	return scheduledOf(accountId).filter((m) => m.status === 'failed').length
}

export function scheduledShown() {
	return !!mailboxState.selected.scheduled
}

/** Shows the Scheduled view: across accounts, or of one account (beside its folders). */
export function showScheduled(accountId = null) {
	clearSearch()
	endSelectionMode()
	closeDrawer()
	closeMessage()
	if (mailboxState.selected.scheduled && mailboxState.selected.accountId === accountId) {
		loadScheduled()
		return
	}
	closeScheduled()
	mailboxState.selected = { accountId, folderId: null, scheduled: true }
	loadScheduled()
}

export async function openScheduled(id) {
	const open = scheduledState.open
	Object.assign(open, { id, detail: null, loading: true, error: null })
	try {
		const detail = await apiGet(`/scheduled/${id}`)
		if (open.id === id) {
			open.detail = detail
		}
	} catch (e) {
		if (open.id === id) {
			open.error = new ActionError(e).message
		}
	} finally {
		if (open.id === id) {
			open.loading = false
		}
	}
}

export function closeScheduled() {
	Object.assign(scheduledState.open, { id: null, detail: null, loading: false, error: null })
}

async function act(id, run) {
	scheduledState.busy = { ...scheduledState.busy, [id]: true }
	try {
		return await run()
	} catch (e) {
		throw new ActionError(e)
	} finally {
		const { [id]: _, ...rest } = scheduledState.busy
		scheduledState.busy = rest
		await loadScheduled()
	}
}

/** Sends it right away; a failure is shown on the message (and thrown). */
export async function sendScheduledNow(message) {
	const result = await act(message.id, () => apiSend('POST', `/scheduled/${message.id}/send-now`))
	if (!result.sent) {
		throw new Error(result.message?.error ?? t('sockless_mail', 'The message could not be sent.'))
	}
	showSuccess(t('sockless_mail', 'Message sent'))
	if (result.warning) {
		showWarning(result.warning)
	}
	refreshFoldersOf(message.accountId)
}

/**
 * @param {object} message the scheduled message
 * @param {Date} sendAt the new time
 */
export async function reschedule(message, sendAt) {
	await act(message.id, () => apiSend('PUT', `/scheduled/${message.id}`, { sendAt: sendAt.toISOString() }))
	showSuccess(t('sockless_mail', 'Scheduled for {time}', { time: formatSendTime(sendAt) }))
}

/** Not sent after all: back in Drafts. Returns where the draft is (null when the server could not say). */
export async function cancelScheduled(message) {
	const result = await act(message.id, () => apiSend('DELETE', `/scheduled/${message.id}`))
	refreshFoldersOf(message.accountId, result.draft?.folderId)
	return result.draft
}

/** Sent mail lands in Sent, cancelled mail in Drafts: their lists and counts change. */
function refreshFoldersOf(accountId, folderId = null) {
	if (mailboxState.folders[accountId]) {
		loadFolders(accountId, true)
	}
	if (folderId && mailboxState.selected.accountId === accountId && mailboxState.selected.folderId === folderId) {
		refreshMessages()
	}
}
