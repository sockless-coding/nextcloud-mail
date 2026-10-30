import { reactive } from 'vue'
import { apiBase, apiGet, apiSend } from '../api.js'
import { dropHits, mergeFolderChanges, searchState } from './search.js'
import { inUnifiedView, refreshUnified, unifiedState } from './unified.js'
import { isExpanded, pruneFolders, rememberView, setExpanded } from './viewState.js'

/**
 * Folders, the message list of the selected folder and the open message. Everything is read
 * from the API on demand; polling (App.vue) asks what changed in the selected folder since the
 * list's token, and refreshes the folder counts.
 *
 * A message is addressed by where it lives ({ accountId, folderId }) and its id: lists can show
 * messages of other folders (search results, the unified inbox), so actions take the location
 * of the message rather than assume the selected folder. selected.unified holds the unified
 * view shown (see unified.js); accountId and folderId are null then.
 */
export const mailboxState = reactive({
	/** accountId -> { list, loading, error } */
	folders: {},
	selected: { accountId: null, folderId: null },
	messages: { list: [], nextBefore: null, total: 0, uidValidity: null, token: null, loading: false, loadingMore: false, error: null },
	open: { id: null, accountId: null, folderId: null, message: null, loading: false, error: null },
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

/** The whole message as an .eml download. */
export function emlUrl(where, messageId) {
	return apiUrl(`accounts/${where.accountId}/folders/${where.folderId}/messages/${messageId}/source?download=true`)
}

/**
 * The message as the server stores it; neither request marks it as read.
 *
 * @param {{accountId: string, folderId: string}} where the message's folder
 * @param {string} messageId the message
 * @param {'headers'|'source'} kind { headers } or { text, size, truncated }
 */
export async function loadRawMessage(where, messageId, kind) {
	try {
		return await apiGet(`/accounts/${where.accountId}/folders/${where.folderId}/messages/${messageId}/${kind}`)
	} catch (e) {
		throw toError(e)
	}
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
	const generation = writeGeneration
	try {
		const list = await apiGet(`/accounts/${accountId}/folders`)
		// Background reloads must not undo counts adjusted for a change made meanwhile.
		if (!quiet || generation === writeGeneration) {
			entry.list = list
		}
		pruneFolders(accountId, list)
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
 * Shows a folder. Selecting the folder that is already shown only refreshes it: a late or
 * repeated selection (a click while the initial load is still running) must not throw away the
 * list position or close the message the user opened in the meantime.
 *
 * @param {string} accountId account
 * @param {string} folderId folder
 * @param {boolean} reset reload from scratch even if the folder is already shown
 */
export async function selectFolder(accountId, folderId, reset = false) {
	if (!reset && isSelected(accountId, folderId) && !mailboxState.messages.error) {
		await refreshMessages()
		return
	}
	mailboxState.selected = { accountId, folderId }
	rememberView(mailboxState.selected)
	closeMessage()
	Object.assign(mailboxState.messages, { list: [], nextBefore: null, total: 0, uidValidity: null, token: null, error: null, loading: true })
	const completedAtStart = writesCompleted
	try {
		const page = await apiGet(`/accounts/${accountId}/folders/${folderId}/messages?limit=${PAGE_SIZE}`)
		if (isSelected(accountId, folderId)) {
			Object.assign(mailboxState.messages, {
				list: page.messages,
				nextBefore: page.nextBefore,
				total: page.total,
				uidValidity: page.uidValidity,
				token: page.token,
			})
			updateFolderCounts(page)
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
	// A change that finished while this loaded (e.g. a delete into the Trash just opened) may be
	// missing from the snapshot, and its own follow-up refresh was skipped during loading.
	if (isSelected(accountId, folderId) && writesCompleted !== completedAtStart) {
		await refreshMessages()
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
		// The token stays the first page's: changes up to this page's token are not merged yet.
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
 * Polling: merges what changed since the list's token (new mail on top, updated flags, messages
 * removed elsewhere) without resetting what the user scrolled to. A changed UIDVALIDITY means
 * the ids are stale, so the folder is shown afresh; a token the server can no longer answer
 * (reset) reloads the list in place.
 */
export async function refreshMessages() {
	const { accountId, folderId } = mailboxState.selected
	const state = mailboxState.messages
	if (!accountId || !folderId || state.loading || state.token === null || hasPendingWrites()) {
		return
	}
	const generation = writeGeneration
	try {
		const changes = await apiGet(`/accounts/${accountId}/folders/${folderId}/changes?since=${state.token}`)
		// A change made while this was loading is not in the snapshot; the next refresh catches up.
		if (!isSelected(accountId, folderId) || generation !== writeGeneration) {
			return
		}
		if (state.uidValidity !== null && changes.uidValidity !== state.uidValidity) {
			await selectFolder(accountId, folderId, true)
			return
		}
		if (changes.reset) {
			await reloadList(accountId, folderId)
			return
		}
		if (changes.removed.length) {
			const removed = new Set(changes.removed)
			state.list = state.list.filter((m) => !removed.has(m.id))
			if (isOpen({ accountId, folderId }, removed)) {
				closeMessage()
			}
		}
		const known = new Map(state.list.map((m, i) => [m.id, i]))
		const fresh = []
		for (const message of changes.messages) {
			if (known.has(message.id)) {
				state.list[known.get(message.id)] = message
			} else {
				fresh.push(message)
			}
		}
		// Messages newer than everything listed go on top; others belong to pages not loaded yet.
		const newestKnown = state.list.length ? uidOf(state.list[0].id) : 0
		state.list.unshift(...fresh.filter((m) => uidOf(m.id) > newestKnown))
		state.total = changes.total
		state.token = changes.token
		updateFolderCounts(changes)
		mergeFolderChanges({ accountId, folderId }, changes.messages, changes.removed)
	} catch {
		// Polling failures stay silent; the next explicit action shows errors.
	}
}

/** Replaces the list with the newest page, keeping the open message. */
async function reloadList(accountId, folderId) {
	const page = await apiGet(`/accounts/${accountId}/folders/${folderId}/messages?limit=${PAGE_SIZE}`)
	if (isSelected(accountId, folderId)) {
		Object.assign(mailboxState.messages, {
			list: page.messages,
			nextBefore: page.nextBefore,
			total: page.total,
			uidValidity: page.uidValidity,
			token: page.token,
		})
		updateFolderCounts(page)
	}
}

/** The selected folder's counts in the navigation, from a page or a changes answer. */
function updateFolderCounts({ total, unread }) {
	const folder = folderEntry(mailboxState.selected)
	if (folder && folder.total !== null && folder.total !== undefined) {
		folder.total = total
	}
	if (folder && unread !== null && unread !== undefined) {
		folder.unread = unread
	}
}

/**
 * @param {{accountId: string, folderId: string}} where the message's folder
 * @param {string} messageId message to open
 * @param {boolean} allowRemote load remote images through the proxy
 */
export async function openMessage(where, messageId, allowRemote = false) {
	const { accountId, folderId } = where
	const ids = new Set([messageId])
	Object.assign(mailboxState.open, { id: messageId, accountId, folderId, loading: !allowRemote, error: null })
	if (!allowRemote) {
		mailboxState.open.message = null
	}
	try {
		const message = await apiGet(`/accounts/${accountId}/folders/${folderId}/messages/${messageId}${allowRemote ? '?remote=allow' : ''}`)
		if (isOpen(where, ids)) {
			mailboxState.open.message = message
			// Opening a message reads it; the server side only fetched with BODY.PEEK.
			if (!message.seen) {
				setFlags(where, [messageId], { seen: true }).catch(() => {})
			}
		}
	} catch (e) {
		if (isOpen(where, ids)) {
			mailboxState.open.error = toError(e)
		}
	} finally {
		if (isOpen(where, ids)) {
			mailboxState.open.loading = false
		}
	}
}

export function closeMessage() {
	Object.assign(mailboxState.open, { id: null, accountId: null, folderId: null, message: null, loading: false, error: null })
}

/** The open message's folder, for actions on it. */
export function openLocation() {
	const { accountId, folderId } = mailboxState.open
	return { accountId, folderId }
}

/** Whether the open message is one of messageIds (a Set) in the given folder. */
function isOpen(where, messageIds) {
	const open = mailboxState.open
	return open.accountId === where.accountId && open.folderId === where.folderId && messageIds.has(open.id)
}

/** Removes everything cached for a deleted or changed account. */
export function forgetAccount(accountId) {
	delete mailboxState.folders[accountId]
	searchState.hits = searchState.hits.filter((h) => h.accountId !== accountId)
	unifiedState.list = unifiedState.list.filter((h) => h.accountId !== accountId)
	if (mailboxState.open.accountId === accountId) {
		closeMessage()
	}
	if (mailboxState.selected.accountId === accountId) {
		mailboxState.selected = { accountId: null, folderId: null }
		closeMessage()
		Object.assign(mailboxState.messages, { list: [], nextBefore: null, total: 0, uidValidity: null, token: null, error: null, loading: false })
	}
}

function isSelected(accountId, folderId) {
	return mailboxState.selected.accountId === accountId && mailboxState.selected.folderId === folderId
}

function uidOf(messageId) {
	return Number(messageId.split('-')[1])
}

/** A failed change, with the server's error code and message (and field errors for 400). */
export class ActionError extends Error {
	constructor(e) {
		const data = e?.response?.data
		const fieldErrors = data?.errors ? Object.fromEntries(Object.entries(data.errors).map(([k, v]) => [k, v[0]])) : {}
		super(data?.message ?? Object.values(fieldErrors)[0] ?? (e?.response ? `HTTP ${e.response.status}` : String(e?.message ?? e)))
		this.code = data?.error ?? (e?.response?.status === 400 ? 'invalid' : 'failed')
		this.fieldErrors = fieldErrors
	}
}

/**
 * Changes go out one at a time, in the order the user made them. The server runs requests on
 * several pooled IMAP connections, so two quick changes to the same message ("read" when it
 * opens, then "unread") could otherwise land in the wrong order.
 */
let writeChain = Promise.resolve()
let pendingWrites = 0
/** Bumped by every change; a snapshot requested before the current value may predate a change. */
let writeGeneration = 0
/** Bumped when a change has been applied on the server. */
let writesCompleted = 0

/** While changes are in flight, server snapshots (polling) may predate them and must not be merged. */
export function hasPendingWrites() {
	return pendingWrites > 0
}

/** The change counters, for other stores that merge server snapshots the way refreshMessages does. */
export function writeCounters() {
	return { generation: writeGeneration, completed: writesCompleted }
}

async function send(method, path, body) {
	pendingWrites++
	writeGeneration++
	const run = writeChain.then(() => apiSend(method, path, body))
	writeChain = run.catch(() => {})
	try {
		return await run
	} catch (e) {
		throw new ActionError(e)
	} finally {
		pendingWrites--
		writesCompleted++
	}
}

/** After a move or delete: fresh counts, and the target folder's list if that is the one shown. */
function afterTransfer(accountId, targetFolderId) {
	if (hasPendingWrites()) {
		return
	}
	loadFolders(accountId, true)
	if (targetFolderId && isSelected(accountId, targetFolderId)) {
		refreshMessages()
	}
	if (targetFolderId && inUnifiedView(accountId, targetFolderId)) {
		refreshUnified()
	}
}

function folderBase({ accountId, folderId }) {
	return `/accounts/${accountId}/folders/${folderId}`
}

function folderEntry({ accountId, folderId }) {
	return mailboxState.folders[accountId]?.list.find((f) => f.id === folderId)
}

/**
 * Every copy of the given messages the UI holds: in the message list (when it shows their
 * folder), in search results, in the unified view and as the open message.
 *
 * @param {{accountId: string, folderId: string}} where the messages' folder
 * @param {string[]} ids message ids
 */
export function messagesById(where, ids) {
	const wanted = new Set(ids)
	const inFolder = (h) => h.accountId === where.accountId && h.folderId === where.folderId && wanted.has(h.message.id)
	return [
		...(isSelected(where.accountId, where.folderId) ? mailboxState.messages.list.filter((m) => wanted.has(m.id)) : []),
		...searchState.hits.filter(inFolder).map((h) => h.message),
		...unifiedState.list.filter(inFolder).map((h) => h.message),
		...(mailboxState.open.message && isOpen(where, wanted) ? [mailboxState.open.message] : []),
	]
}

/**
 * Sets \Seen and/or \Flagged. Optimistic: the lists, the open message and the unread counts
 * change at once and are restored if the server refuses.
 *
 * @param {{accountId: string, folderId: string}} where the messages' folder
 * @param {string[]} messageIds messages of that folder
 * @param {{seen?: boolean, flagged?: boolean}} flags flags to set (true) or clear (false)
 */
export async function setFlags(where, messageIds, { seen, flagged }) {
	const targets = messagesById(where, messageIds)
	const before = targets.map((m) => ({ m, seen: m.seen, flagged: m.flagged }))
	const folder = folderEntry(where)
	const unreadBefore = { folder: folder?.unread, unified: unifiedState.unread }
	if (seen !== undefined) {
		// The same message may be held several times (list entry, open message): count it once.
		const changed = new Set(targets.filter((m) => m.seen !== seen).map((m) => m.id)).size
		const adjust = (count) => Math.max(0, count + (seen ? -changed : changed))
		if (folder && folder.unread !== undefined && folder.unread !== null) {
			folder.unread = adjust(folder.unread)
		}
		if (inUnifiedView(where.accountId, where.folderId) && unifiedState.unread !== null) {
			unifiedState.unread = adjust(unifiedState.unread)
		}
	}
	for (const m of targets) {
		if (seen !== undefined) m.seen = seen
		if (flagged !== undefined) m.flagged = flagged
	}
	try {
		await send('PUT', `${folderBase(where)}/messages/flags`, { messageIds, seen, flagged })
	} catch (e) {
		for (const { m, seen: s, flagged: f } of before) {
			m.seen = s
			m.flagged = f
		}
		if (folder) folder.unread = unreadBefore.folder
		unifiedState.unread = unreadBefore.unified
		throw e
	}
}

/** Takes messages out of every list (and closes the view if one of them is open); returns an undo. */
function removeFromList(where, messageIds) {
	const { accountId, folderId } = where
	const ids = new Set(messageIds)
	const state = mailboxState.messages
	const shown = isSelected(accountId, folderId)
	const previous = { list: state.list, total: state.total, unified: unifiedState.list, unifiedTotal: unifiedState.total, unifiedUnread: unifiedState.unread }
	const folder = folderEntry(where)
	const previousUnread = folder?.unread
	const previousHits = searchState.hits
	// Each message counted once, wherever it is listed.
	const unseen = new Set(messagesById(where, messageIds).filter((m) => !m.seen).map((m) => m.id)).size
	if (shown) {
		const remaining = state.list.filter((m) => !ids.has(m.id))
		state.total = Math.max(0, state.total - (state.list.length - remaining.length))
		state.list = remaining
	}
	const unifiedRemoved = unifiedState.list.filter((h) => h.accountId === accountId && h.folderId === folderId && ids.has(h.message.id))
	if (unifiedRemoved.length) {
		unifiedState.list = unifiedState.list.filter((h) => !unifiedRemoved.includes(h))
		unifiedState.total = Math.max(0, unifiedState.total - unifiedRemoved.length)
		if (unifiedState.unread !== null) {
			unifiedState.unread = Math.max(0, unifiedState.unread - unifiedRemoved.filter((h) => !h.message.seen).length)
		}
	}
	if (folder && folder.unread !== null && folder.unread !== undefined) {
		folder.unread = Math.max(0, folder.unread - unseen)
	}
	if (isOpen(where, ids)) {
		closeMessage()
	}
	dropHits(messageIds, accountId, folderId)
	const hitsAfterDrop = searchState.hits
	const unifiedAfterDrop = unifiedState.list
	return () => {
		if (folder) folder.unread = previousUnread
		// Unless a new search replaced the results meanwhile.
		if (searchState.hits === hitsAfterDrop) {
			searchState.hits = previousHits
		}
		// Unless the unified view was reloaded or merged changes meanwhile.
		if (unifiedState.list === unifiedAfterDrop) {
			Object.assign(unifiedState, { list: previous.unified, total: previous.unifiedTotal, unread: previous.unifiedUnread })
		}
		// The user may have switched folders while the request ran; that list must stay as it is.
		if (shown && isSelected(accountId, folderId)) {
			state.list = previous.list
			state.total = previous.total
		}
	}
}

/**
 * @param {{accountId: string, folderId: string}} where the messages' folder
 * @param {string[]} messageIds messages of that folder
 * @param {string} targetFolderId destination, in the same account
 */
export async function moveMessages(where, messageIds, targetFolderId) {
	const undo = removeFromList(where, messageIds)
	try {
		await send('POST', `${folderBase(where)}/messages/move`, { messageIds, targetFolderId })
	} catch (e) {
		undo()
		throw e
	}
	afterTransfer(where.accountId, targetFolderId)
}

/**
 * Moves to Trash, or deletes permanently in Trash. Throws an ActionError with code "no_trash"
 * when the account has no Trash folder; call again with permanent = true after asking.
 *
 * @param {{accountId: string, folderId: string}} where the messages' folder
 * @param {string[]} messageIds messages of that folder
 * @param {boolean} permanent skip the Trash
 */
export async function deleteMessages(where, messageIds, permanent = false) {
	const undo = removeFromList(where, messageIds)
	let result
	try {
		result = await send('POST', `${folderBase(where)}/messages/delete`, { messageIds, permanent })
	} catch (e) {
		undo()
		throw e
	}
	afterTransfer(where.accountId, result.trashFolderId)
	return result
}

/**
 * @param {string} accountId account
 * @param {string} name folder name
 * @param {string|null} parentId parent folder, or null for the top level
 */
export async function createFolder(accountId, name, parentId = null) {
	const folder = await send('POST', `/accounts/${accountId}/folders`, { name, parentId })
	if (parentId) {
		// Show the new subfolder.
		setExpanded(accountId, parentId, true)
	}
	await loadFolders(accountId, true)
	return folder
}

/**
 * Renaming changes the folder's id (it is derived from the name), so a selected folder is
 * re-selected under its new id.
 *
 * @param {string} accountId account
 * @param {string} folderId folder to rename
 * @param {string} name new name
 */
/**
 * A rename gives the folder and everything below it new ids (the id is the full name): expand the
 * renamed ones that were expanded before, matching subfolders by name.
 */
function keepExpanded(accountId, oldList, oldId, oldExpanded, newId) {
	const newList = mailboxState.folders[accountId]?.list ?? []
	if (oldExpanded.has(oldId)) {
		setExpanded(accountId, newId, true)
	}
	for (const child of oldList.filter((f) => f.parentId === oldId)) {
		const renamed = newList.find((f) => f.parentId === newId && f.name === child.name)
		if (renamed) {
			keepExpanded(accountId, oldList, child.id, oldExpanded, renamed.id)
		}
	}
}

export async function renameFolder(accountId, folderId, name) {
	const folder = await send('PUT', `/accounts/${accountId}/folders/${folderId}`, { name })
	const wasSelected = isSelected(accountId, folderId)
	const oldList = mailboxState.folders[accountId]?.list ?? []
	const oldFullName = oldList.find((f) => f.id === folderId)?.fullName
	const oldExpanded = oldList.filter((f) => isExpanded(accountId, f.id)).map((f) => f.id)
	await loadFolders(accountId, true)
	keepExpanded(accountId, oldList, folderId, new Set(oldExpanded), folder.id)
	if (wasSelected) {
		await selectFolder(accountId, folder.id, true)
	} else if (oldFullName && mailboxState.selected.accountId === accountId) {
		// A subfolder of the renamed one was selected: its id changed as well.
		const current = mailboxState.folders[accountId].list.find((f) => f.id === mailboxState.selected.folderId)
		if (!current) {
			mailboxState.selected = { accountId: null, folderId: null }
		}
	}
	return folder
}
