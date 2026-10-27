import { reactive } from 'vue'
import { apiBase, apiGet } from '../api.js'

/**
 * Folders, the message list of the selected folder and the open message. Everything is read
 * from the API on demand; polling (App.vue) refreshes the selected folder and the folder counts.
 */
export const mailboxState = reactive({
	/** accountId -> { list, loading, error } */
	folders: {},
	selected: { accountId: null, folderId: null },
	messages: { list: [], nextBefore: null, total: 0, uidValidity: null, loading: false, loadingMore: false, error: null },
	open: { id: null, message: null, loading: false, error: null },
})

const PAGE_SIZE = 50

/** API errors carry { error, message }; keep both for the UI. */
function toError(e) {
	const data = e?.response?.data
	return { code: data?.error ?? (e?.response ? `http_${e.response.status}` : 'network'), message: data?.message ?? String(e?.message ?? e) }
}

/** Absolute URL of an API-relative path (from data-sockless-src or a part link). */
export function apiUrl(path) {
	return `${apiBase}/${path}`
}

export function partUrl(accountId, folderId, messageId, partId, download) {
	return apiUrl(`accounts/${accountId}/folders/${folderId}/messages/${messageId}/parts/${encodeURIComponent(partId)}${download ? '?download=true' : ''}`)
}

/**
 * @param {string} accountId account to load
 * @param {boolean} quiet keep showing the old list while reloading (polling)
 */
export async function loadFolders(accountId, quiet = false) {
	if (!mailboxState.folders[accountId]) {
		mailboxState.folders[accountId] = { list: [], loading: false, error: null }
	}
	// Read back through the reactive state: the object literal itself is not reactive.
	const entry = mailboxState.folders[accountId]
	if (!quiet) {
		entry.loading = true
	}
	try {
		entry.list = await apiGet(`/accounts/${accountId}/folders`)
		entry.error = null
	} catch (e) {
		if (!quiet) {
			entry.error = toError(e)
		}
	} finally {
		entry.loading = false
	}
	return entry.list
}

/**
 * @param {string} accountId account
 * @param {string} folderId folder
 */
export async function selectFolder(accountId, folderId) {
	mailboxState.selected = { accountId, folderId }
	closeMessage()
	Object.assign(mailboxState.messages, { list: [], nextBefore: null, total: 0, uidValidity: null, error: null, loading: true })
	try {
		const page = await apiGet(`/accounts/${accountId}/folders/${folderId}/messages?limit=${PAGE_SIZE}`)
		if (isSelected(accountId, folderId)) {
			Object.assign(mailboxState.messages, {
				list: page.messages,
				nextBefore: page.nextBefore,
				total: page.total,
				uidValidity: page.uidValidity,
			})
		}
	} catch (e) {
		if (isSelected(accountId, folderId)) {
			mailboxState.messages.error = toError(e)
		}
	} finally {
		if (isSelected(accountId, folderId)) {
			mailboxState.messages.loading = false
		}
	}
}

export async function loadMore() {
	const { accountId, folderId } = mailboxState.selected
	const state = mailboxState.messages
	if (!state.nextBefore || state.loadingMore) {
		return
	}
	state.loadingMore = true
	try {
		const page = await apiGet(`/accounts/${accountId}/folders/${folderId}/messages?limit=${PAGE_SIZE}&before=${state.nextBefore}`)
		if (isSelected(accountId, folderId)) {
			state.list.push(...page.messages)
			state.nextBefore = page.nextBefore
		}
	} catch (e) {
		state.error = toError(e)
	} finally {
		state.loadingMore = false
	}
}

/**
 * Polling: merges the newest page into the list (new mail on top, updated flags) without
 * resetting what the user scrolled to. A changed UIDVALIDITY means the ids are stale: reload.
 */
export async function refreshMessages() {
	const { accountId, folderId } = mailboxState.selected
	const state = mailboxState.messages
	if (!accountId || !folderId || state.loading) {
		return
	}
	try {
		const page = await apiGet(`/accounts/${accountId}/folders/${folderId}/messages?limit=${PAGE_SIZE}`)
		if (!isSelected(accountId, folderId)) {
			return
		}
		if (state.uidValidity !== null && page.uidValidity !== state.uidValidity) {
			await selectFolder(accountId, folderId)
			return
		}
		const known = new Map(state.list.map((m, i) => [m.id, i]))
		const fresh = []
		for (const message of page.messages) {
			if (known.has(message.id)) {
				state.list[known.get(message.id)] = message
			} else {
				fresh.push(message)
			}
		}
		// Messages newer than everything we have go on top; the rest of the newest page is already listed.
		const newestKnown = state.list.length ? uidOf(state.list[0].id) : 0
		state.list.unshift(...fresh.filter((m) => uidOf(m.id) > newestKnown))
		state.total = page.total
	} catch {
		// Polling failures stay silent; the next explicit action shows errors.
	}
}

/**
 * @param {string} messageId message to open
 * @param {boolean} allowRemote load remote images through the proxy
 */
export async function openMessage(messageId, allowRemote = false) {
	const { accountId, folderId } = mailboxState.selected
	Object.assign(mailboxState.open, { id: messageId, loading: !allowRemote, error: null })
	if (!allowRemote) {
		mailboxState.open.message = null
	}
	try {
		const message = await apiGet(`/accounts/${accountId}/folders/${folderId}/messages/${messageId}${allowRemote ? '?remote=allow' : ''}`)
		if (mailboxState.open.id === messageId) {
			mailboxState.open.message = message
		}
	} catch (e) {
		if (mailboxState.open.id === messageId) {
			mailboxState.open.error = toError(e)
		}
	} finally {
		if (mailboxState.open.id === messageId) {
			mailboxState.open.loading = false
		}
	}
}

export function closeMessage() {
	Object.assign(mailboxState.open, { id: null, message: null, loading: false, error: null })
}

/** Removes everything cached for a deleted or changed account. */
export function forgetAccount(accountId) {
	delete mailboxState.folders[accountId]
	if (mailboxState.selected.accountId === accountId) {
		mailboxState.selected = { accountId: null, folderId: null }
		closeMessage()
		Object.assign(mailboxState.messages, { list: [], nextBefore: null, total: 0, uidValidity: null, error: null, loading: false })
	}
}

function isSelected(accountId, folderId) {
	return mailboxState.selected.accountId === accountId && mailboxState.selected.folderId === folderId
}

function uidOf(messageId) {
	return Number(messageId.split('-')[1])
}
