import { nextTick, reactive, watch } from 'vue'
import { apiGet, apiSend } from '../api.js'

/**
 * What the folder pane remembers between sessions: per account the folders shown expanded and the
 * folder last open, and the view last open (an account's folder or a unified view) for the start.
 *
 * Kept on the server by default (/api/view-state), so every device opens where the user left off,
 * or only in this browser when the user prefers that; the choice itself is always saved on the
 * server, so it is one per user. The browser keeps a copy either way (per Nextcloud user, as in
 * layout.js): it is the state in browser storage, and the fallback when the server cannot be read.
 * Server saves are debounced; each replaces the whole state there.
 */
const KEY = `sockless_mail.view.${document.head?.dataset.user ?? ''}`
const SAVE_DELAY_MS = 1000

export const viewState = reactive({
	/** server | browser */
	storage: 'server',
	/** { accountId, folderId } or { unified } */
	lastView: null,
	/** accountId -> { expanded: [folderId], lastFolderId } */
	accounts: {},
})

let loaded = false
let saveTimer = null
/** The storage the server last confirmed; a switch to browser storage is sent once. */
let serverStorage = null

function readLocal() {
	try {
		const stored = JSON.parse(localStorage.getItem(KEY) ?? '{}')
		return { lastView: stored.lastView ?? null, accounts: stored.accounts ?? {}, storage: stored.storage === 'browser' ? 'browser' : 'server' }
	} catch {
		return null
	}
}

function writeLocal() {
	try {
		localStorage.setItem(KEY, JSON.stringify(viewState))
	} catch {
		// Not remembered in this browser; the session keeps working.
	}
}

function snapshot() {
	return { lastView: viewState.lastView, accounts: viewState.accounts }
}

/** Reads the remembered state; called once at start-up, before the first view is opened. */
export async function loadViewState() {
	const local = readLocal()
	if (local) {
		Object.assign(viewState, local)
	}
	try {
		const remote = await apiGet('/view-state')
		serverStorage = remote.storage
		viewState.storage = remote.storage
		if (remote.storage === 'server') {
			if (remote.state) {
				viewState.lastView = remote.state.lastView ?? null
				viewState.accounts = Object.fromEntries(Object.entries(remote.state.accounts ?? {})
					.map(([id, account]) => [id, { expanded: account.expanded ?? [], lastFolderId: account.lastFolderId ?? null }]))
			} else if (local) {
				// Nothing on the server yet (first use, or just switched back): start from this browser's copy.
				scheduleSave()
			}
		}
	} catch {
		// The browser's copy will do; saving is tried again on the next change.
	}
	loaded = true
	writeLocal()
}

async function save() {
	window.clearTimeout(saveTimer)
	saveTimer = null
	const storage = viewState.storage
	if (storage === 'browser' && serverStorage === 'browser') {
		return
	}
	try {
		await apiSend('PUT', '/view-state', { storage, state: storage === 'server' ? snapshot() : null })
		serverStorage = storage
	} catch {
		// Kept in the browser; the next change tries again.
	}
}

function scheduleSave() {
	window.clearTimeout(saveTimer)
	saveTimer = window.setTimeout(save, SAVE_DELAY_MS)
}

watch(viewState, () => {
	if (!loaded) {
		return
	}
	writeLocal()
	if (viewState.storage === 'server' || serverStorage !== 'browser') {
		scheduleSave()
	}
}, { deep: true })

// A tab being closed or hidden saves what is still waiting.
function flush() {
	if (saveTimer) {
		save()
	}
}
document.addEventListener('visibilitychange', () => {
	if (document.hidden) {
		flush()
	}
})
window.addEventListener('pagehide', flush)

/** @param {'server'|'browser'} storage where to remember the folder pane from now on */
export function setViewStorage(storage) {
	viewState.storage = storage
	// A deliberate choice is saved right away, not after the debounce (the watcher has queued it by then).
	nextTick(flush)
}

function accountView(accountId) {
	if (!viewState.accounts[accountId]) {
		viewState.accounts[accountId] = { expanded: [], lastFolderId: null }
	}
	return viewState.accounts[accountId]
}

export function isExpanded(accountId, folderId) {
	return viewState.accounts[accountId]?.expanded.includes(folderId) ?? false
}

export function setExpanded(accountId, folderId, open) {
	const account = accountView(accountId)
	const expanded = account.expanded.filter((id) => id !== folderId)
	account.expanded = open ? [...expanded, folderId] : expanded
}

/** The folder last open in an account, or null. */
export function lastFolderOf(accountId) {
	return viewState.accounts[accountId]?.lastFolderId ?? null
}

/**
 * Remembers the view shown.
 *
 * @param {{accountId: ?string, folderId: ?string, unified?: string}} selected mailboxState.selected
 */
export function rememberView({ accountId, folderId, unified }) {
	if (unified) {
		viewState.lastView = { unified }
	} else if (accountId && folderId) {
		viewState.lastView = { accountId, folderId }
		accountView(accountId).lastFolderId = folderId
	}
}

/** Drops remembered folders that are gone (deleted, or renamed: the id is the name). */
export function pruneFolders(accountId, list) {
	const account = viewState.accounts[accountId]
	if (!account || !list.length) {
		return
	}
	const ids = new Set(list.map((f) => f.id))
	if (account.expanded.some((id) => !ids.has(id))) {
		account.expanded = account.expanded.filter((id) => ids.has(id))
	}
	if (account.lastFolderId && !ids.has(account.lastFolderId)) {
		account.lastFolderId = null
	}
}

/** A deleted account's state; the server drops its own copy with the account. */
export function forgetAccountView(accountId) {
	delete viewState.accounts[accountId]
	if (viewState.lastView?.accountId === accountId) {
		viewState.lastView = null
	}
}
