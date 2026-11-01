import { mdiEmailMarkAsUnread, mdiFlagOutline, mdiTagOutline } from '@mdi/js'
import { t } from '@nextcloud/l10n'
import { reactive } from 'vue'
import { apiGet } from '../api.js'
import { USE_ICONS, USE_NAMES } from '../folderUses.js'
import { unifiedAccounts } from './accounts.js'
import { closeMessage, hasPendingWrites, mailboxState, writeCounters } from './mailbox.js'
import { mergeUnifiedChanges } from './search.js'
import { tagsState, tagUnread } from './tags.js'
import { rememberView } from './viewState.js'

const PAGE_SIZE = 50

/** The folder id of every account's inbox: base64url("INBOX"), as the server names it. */
export const INBOX_ID = 'SU5CT1g'

/**
 * The views across all accounts (the server's UnifiedView): a unified folder holds the folder of
 * one special use of every account; a smart folder is a quick filter over some of them.
 */
export const UNIFIED_VIEWS = [
	...['inbox', 'drafts', 'sent', 'archive', 'junk', 'trash'].map((use) => ({ id: use, roles: [use], name: USE_NAMES[use], icon: USE_ICONS[use] })),
	{ id: 'unread', roles: ['inbox'], filter: 'unread', name: t('sockless_mail', 'Unread'), icon: mdiEmailMarkAsUnread },
	{ id: 'flagged', roles: ['inbox', 'archive', 'sent'], filter: 'flagged', name: t('sockless_mail', 'Flagged'), icon: mdiFlagOutline },
]

/** The most a tag folder reloads at once when polled (the server's page limit). */
const TAG_RELOAD_MAX = 500

/**
 * A tag folder as a view: "tag:{tagId}" across all accounts, "tag:{tagId}@{accountId}" in one
 * account (shown with that account's folders). Read from the server's tag index.
 */
export function tagViewId(tagId, accountId = null) {
	return accountId ? `tag:${tagId}@${accountId}` : `tag:${tagId}`
}

function tagView(id) {
	const [tagId, accountId = null] = id.slice(4).split('@')
	const tag = tagsState.list.find((t) => t.id === tagId)
	return tag ? { id, roles: [], filter: 'tag', tagId, accountId, tag, name: tag.name, icon: mdiTagOutline } : null
}

export function viewOf(id) {
	if (typeof id === 'string' && id.startsWith('tag:')) {
		return tagView(id)
	}
	return UNIFIED_VIEWS.find((v) => v.id === id) ?? null
}

/** The account's folder of a special use, as the server picks it: the first selectable one in the server's order. */
export function folderOfUse(accountId, use) {
	const folder = mailboxState.folders[accountId]?.list.find((f) => f.specialUse === use && f.selectable)
	return folder?.id ?? (use === 'inbox' ? INBOX_ID : null)
}

/** Whether a folder is part of the unified view shown (false when none is). */
export function inUnifiedView(accountId, folderId) {
	const view = viewOf(mailboxState.selected.unified)
	if (view?.tagId) {
		// A tag folder may hold any folder's mail.
		return !view.accountId || view.accountId === accountId
	}
	return !!view && unifiedAccounts.value.some((a) => a.id === accountId) && view.roles.some((use) => folderOfUse(accountId, use) === folderId)
}

/** Whether the folder pane shows all accounts (a unified view, a tag folder or Scheduled across accounts). */
export function unifiedPaneShown() {
	return (!!mailboxState.selected.unified || !!mailboxState.selected.scheduled) && !mailboxState.selected.accountId
}

/** Unified folders some account has, then the smart folders. */
export function availableViews() {
	return UNIFIED_VIEWS.filter((v) => v.filter || unifiedAccounts.value.some((a) => folderOfUse(a.id, v.id)))
}

/**
 * Unread messages of a view, from the folder lists already loaded: the sum of the accounts' folders.
 * The Unread smart folder counts the inboxes; Flagged has no count.
 */
export function viewUnread(id) {
	const view = viewOf(id)
	if (view?.tagId) {
		return tagUnread(view.tagId, view.accountId)
	}
	if (!view || view.filter === 'flagged') {
		return 0
	}
	return unifiedAccounts.value.reduce((sum, account) => {
		const folderId = folderOfUse(account.id, view.roles[0])
		return sum + (mailboxState.folders[account.id]?.list.find((f) => f.id === folderId)?.unread ?? 0)
	}, 0)
}

/**
 * The unified view shown while mailboxState.selected.unified holds its id. Entries look like
 * search hits, { accountId, folderId, folderFullName, message }, and actions on them go to their
 * own account. errors lists accounts that could not be read ({ accountId, error, message }); their
 * messages are shown as last loaded. A smart folder's total counts its matches (null when not
 * all were counted) and it has no unread count.
 */
export const unifiedState = reactive({
	view: null,
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

/** Bumped whenever a unified view is shown afresh; answers for an earlier showing are dropped. */
let generation = 0

function toError(e) {
	const data = e?.response?.data
	return { code: data?.error ?? (e?.response ? `http_${e.response.status}` : 'network'), message: data?.message ?? String(e?.message ?? e) }
}

function isShown(run) {
	return run === generation && mailboxState.selected.unified === unifiedState.view
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

/** Whether a message belongs in a smart folder, for merging polled changes. */
function passes(view, message) {
	return { unread: !message.seen, flagged: message.flagged }[view.filter] ?? true
}

function messagesPath(before, limit = PAGE_SIZE) {
	const query = before ? `&before=${encodeURIComponent(before)}` : ''
	const tag = viewOf(unifiedState.view)
	if (tag?.tagId) {
		return `/tags/${tag.tagId}/messages?limit=${limit}${query}${tag.accountId ? `&accountId=${tag.accountId}` : ''}`
	}
	return `/unified/${unifiedState.view}/messages?limit=${limit}${query}`
}

function applyPage(page) {
	Object.assign(unifiedState, {
		list: page.messages,
		nextBefore: page.nextBefore,
		total: page.total,
		unread: page.unread,
		// Tag folders come from the app's own index and are simply read again (see refreshUnified).
		token: page.token ?? 'tags',
		errors: page.errors ?? [],
	})
}

/**
 * Shows a unified view; when it is already shown, only refreshes it.
 *
 * @param {string} view an id of UNIFIED_VIEWS
 */
export async function showUnified(view = 'inbox') {
	if (mailboxState.selected.unified === view && unifiedState.view === view && !unifiedState.error) {
		await refreshUnified()
		return
	}
	const run = ++generation
	// An account's tag folder keeps that account's folders in the pane.
	mailboxState.selected = { accountId: viewOf(view)?.accountId ?? null, folderId: null, unified: view }
	rememberView(mailboxState.selected)
	closeMessage()
	Object.assign(unifiedState, { view, list: [], nextBefore: null, total: 0, unread: null, token: null, errors: [], error: null, loading: true })
	const completedAtStart = writeCounters().completed
	try {
		const page = await apiGet(messagesPath())
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
		const page = await apiGet(messagesPath(unifiedState.nextBefore))
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
 * Polling: merges what changed in the view's folders since the list's token, like refreshMessages
 * does for one folder. A token the server cannot answer (an account was added or removed, a folder
 * was renumbered) reloads the first page in place. A smart folder takes in only changed messages
 * that pass its filter; one that stops passing (read in Unread) stays until the folder is shown
 * again, as with the quick filters.
 */
export async function refreshUnified() {
	const run = generation
	const state = unifiedState
	if (!isShown(run) || state.loading || state.token === null || hasPendingWrites()) {
		return
	}
	const view = viewOf(state.view)
	const writes = writeCounters().generation
	if (view?.tagId) {
		await reloadTagView(run, writes)
		return
	}
	try {
		const changes = await apiGet(`/unified/${state.view}/changes?since=${encodeURIComponent(state.token)}`)
		// A change made while this was loading is not in the snapshot; the next refresh catches up.
		if (!isShown(run) || writes !== writeCounters().generation) {
			return
		}
		if (changes.reset) {
			const page = await apiGet(messagesPath())
			if (isShown(run)) {
				applyPage(page)
			}
			return
		}
		const key = (h) => `${h.accountId}/${h.folderId}/${h.message?.id ?? h.messageId}`
		const before = state.list.length
		if (changes.removed.length) {
			const removed = new Set(changes.removed.map(key))
			state.list = state.list.filter((h) => !removed.has(key(h)))
			const open = mailboxState.open
			if (removed.has(`${open.accountId}/${open.folderId}/${open.id}`)) {
				closeMessage()
			}
		}
		const removedCount = before - state.list.length
		const known = new Map(state.list.map((h, i) => [key(h), i]))
		const fresh = []
		for (const hit of changes.messages) {
			const index = known.get(key(hit))
			if (index !== undefined) {
				state.list[index] = hit
			} else if (passes(view, hit.message)) {
				fresh.push(hit)
			}
		}
		// New mail sorts in by date; anything older than the last loaded entry belongs to pages not loaded yet.
		const last = state.list.at(-1)
		const inRange = fresh.filter((h) => !state.nextBefore || !last || compare(h, last) < 0)
		if (inRange.length) {
			state.list = [...state.list, ...inRange].sort(compare)
		}
		if (view.filter) {
			if (state.total !== null) {
				state.total = Math.max(0, state.total + inRange.length - removedCount)
			}
		} else {
			Object.assign(state, { total: changes.total, unread: changes.unread })
		}
		Object.assign(state, { token: changes.token, errors: changes.errors })
		mergeUnifiedChanges(changes.messages, changes.removed)
	} catch {
		// Polling failures stay silent; the next explicit action shows errors.
	}
}

/**
 * Polling a tag folder: it comes from the app's own index, so it is read again, as many entries
 * as are loaded (up to the server's page limit), and replaces the list.
 */
async function reloadTagView(run, writes) {
	try {
		const page = await apiGet(messagesPath(null, Math.min(TAG_RELOAD_MAX, Math.max(PAGE_SIZE, unifiedState.list.length))))
		if (!isShown(run) || writes !== writeCounters().generation) {
			return
		}
		// A message that left the tag folder stays open, as one read in Unread does.
		applyPage(page)
	} catch {
		// Polling failures stay silent.
	}
}
