import { reactive } from 'vue'
import { apiGet } from '../api.js'
import { closeMessage, hasPendingWrites, mailboxState, writeCounters } from './mailbox.js'

const PAGE_SIZE = 50

/** The folder id of every account's inbox: base64url("INBOX"), as the server names it. */
export const INBOX_ID = 'SU5CT1g'

/**
 * "All inboxes": the inboxes of every account as one list, newest first, shown while
 * mailboxState.selected.unified is set. Entries look like search hits,
 * { accountId, folderId, folderFullName, message }, and actions on them go to their own account.
 * errors lists accounts whose inbox could not be synced ({ accountId, error, message }); their
 * messages are shown as last loaded.
 */
export const unifiedState = reactive({
	list: [],
	nextBefore: null,
	total: 0,
	unread: null,
	token: null,
	errors: [],
	loading: false,
	loadingMore: false,
	error: null,
})

/** Bumped whenever the unified inbox is shown afresh; answers for an earlier showing are dropped. */
let generation = 0

function toError(e) {
	const data = e?.response?.data
	return { code: data?.error ?? (e?.response ? `http_${e.response.status}` : 'network'), message: data?.message ?? String(e?.message ?? e) }
}

function isShown(run) {
	return run === generation && mailboxState.selected.unified === true
}

/** The server's order: date, then account id, then UID; all descending. */
function compare(a, b) {
	const dates = Date.parse(b.message.date) - Date.parse(a.message.date)
	if (dates !== 0) {
		return dates
	}
	if (a.accountId !== b.accountId) {
		return a.accountId < b.accountId ? 1 : -1
	}
	return Number(b.message.id.split('-')[1]) - Number(a.message.id.split('-')[1])
}

function applyPage(page) {
	Object.assign(unifiedState, {
		list: page.messages,
		nextBefore: page.nextBefore,
		total: page.total,
		unread: page.unread,
		token: page.token,
		errors: page.errors,
	})
}

/** Shows the unified inbox; when it is already shown, only refreshes it. */
export async function showUnified() {
	if (mailboxState.selected.unified && !unifiedState.error) {
		await refreshUnified()
		return
	}
	const run = ++generation
	mailboxState.selected = { accountId: null, folderId: null, unified: true }
	closeMessage()
	Object.assign(unifiedState, { list: [], nextBefore: null, total: 0, unread: null, token: null, errors: [], error: null, loading: true })
	const completedAtStart = writeCounters().completed
	try {
		const page = await apiGet(`/unified/messages?limit=${PAGE_SIZE}`)
		if (isShown(run)) {
			applyPage(page)
		}
	} catch (e) {
		if (isShown(run)) {
			unifiedState.error = toError(e)
		}
	} finally {
		if (isShown(run)) {
			unifiedState.loading = false
		}
	}
	// A change that finished while this loaded may be missing from the snapshot (see selectFolder).
	if (isShown(run) && writeCounters().completed !== completedAtStart) {
		await refreshUnified()
	}
}

export async function loadMoreUnified() {
	const run = generation
	if (!unifiedState.nextBefore || unifiedState.loadingMore) {
		return
	}
	unifiedState.loadingMore = true
	try {
		const page = await apiGet(`/unified/messages?limit=${PAGE_SIZE}&before=${encodeURIComponent(unifiedState.nextBefore)}`)
		// The token stays the first page's: changes up to this page's state are not merged yet.
		if (isShown(run)) {
			unifiedState.list.push(...page.messages)
			unifiedState.nextBefore = page.nextBefore
		}
	} catch (e) {
		if (isShown(run)) {
			unifiedState.error = toError(e)
		}
	} finally {
		unifiedState.loadingMore = false
	}
}

/**
 * Polling: merges what changed in any inbox since the list's token, like refreshMessages does
 * for one folder. A token the server cannot answer (an account was added or removed, a folder
 * was renumbered) reloads the first page in place.
 */
export async function refreshUnified() {
	const run = generation
	const state = unifiedState
	if (!isShown(run) || state.loading || state.token === null || hasPendingWrites()) {
		return
	}
	const writes = writeCounters().generation
	try {
		const changes = await apiGet(`/unified/changes?since=${encodeURIComponent(state.token)}`)
		// A change made while this was loading is not in the snapshot; the next refresh catches up.
		if (!isShown(run) || writes !== writeCounters().generation) {
			return
		}
		if (changes.reset) {
			const page = await apiGet(`/unified/messages?limit=${PAGE_SIZE}`)
			if (isShown(run)) {
				applyPage(page)
			}
			return
		}
		const key = (accountId, messageId) => `${accountId}/${messageId}`
		if (changes.removed.length) {
			const removed = new Set(changes.removed.map((r) => key(r.accountId, r.messageId)))
			state.list = state.list.filter((h) => !removed.has(key(h.accountId, h.message.id)))
			const open = mailboxState.open
			if (open.folderId === INBOX_ID && removed.has(key(open.accountId, open.id))) {
				closeMessage()
			}
		}
		const known = new Map(state.list.map((h, i) => [key(h.accountId, h.message.id), i]))
		const fresh = []
		for (const hit of changes.messages) {
			const index = known.get(key(hit.accountId, hit.message.id))
			if (index === undefined) {
				fresh.push(hit)
			} else {
				state.list[index] = hit
			}
		}
		// New mail sorts in by date; anything older than the last loaded entry belongs to pages not loaded yet.
		const last = state.list.at(-1)
		const inRange = fresh.filter((h) => !state.nextBefore || !last || compare(h, last) < 0)
		if (inRange.length) {
			state.list = [...state.list, ...inRange].sort(compare)
		}
		Object.assign(state, { total: changes.total, unread: changes.unread, token: changes.token, errors: changes.errors })
	} catch {
		// Polling failures stay silent; the next explicit action shows errors.
	}
}
