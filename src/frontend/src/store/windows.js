import { showWarning } from '@nextcloud/dialogs/toast'
import { t } from '@nextcloud/l10n'
import { reactive, watch } from 'vue'
import { accountsState, loadAccounts } from './accounts.js'
import { adoptDraft, composeEdit, composeNew, composer, composeReply, handOverDraft } from './compose.js'
import { layout } from './layout.js'
import { loadFolders, mailboxState, openMessage } from './mailbox.js'
import { loadPresets } from './sendLater.js'
import { loadTags } from './tags.js'
import { loadTasks } from './tasks.js'

/**
 * Messages read or written in a separate browser window. Such a window loads the same page
 * with a fragment saying what to show (#window=message&…, #window=compose&…), and the app shows
 * only that: the message, or the composer, without the mailbox around it.
 *
 * A message being written moves to a window as it is (uploads, forwarded attachments, the
 * message replied to): the window that had it puts a copy in a slot on itself (window.opener
 * is the same origin), and the new window takes it from there.
 */

function fromHash() {
	const params = new URLSearchParams(window.location.hash.slice(1))
	const kind = params.get('window')
	return kind === 'message' || kind === 'compose' ? { kind, params } : null
}

/** What this window shows: null for the whole app, or { kind: 'message' | 'compose', params }. */
export const windowMode = fromHash()

export const windowState = reactive({
	/** Starting up, or waiting for a handed-over draft. */
	loading: !!windowMode,
	error: '',
})

/** Drafts handed to windows opened from this one, by token; read by those windows. */
const HANDOVERS = 'socklessMailHandovers'
window[HANDOVERS] = new Map()

function pageUrl(params) {
	return `${window.location.origin}${window.location.pathname}#${new URLSearchParams(params)}`
}

/** A popup the size of a mail program's message window; null when the browser blocked it. */
function openPopup(params, name) {
	const width = Math.min(1000, window.screen.availWidth - 40)
	const height = Math.min(860, window.screen.availHeight - 40)
	const win = window.open(pageUrl(params), name, `popup,width=${width},height=${height}`)
	win?.focus()
	return win
}

function blocked() {
	showWarning(t('sockless_mail', 'The browser blocked the new window. Allow pop-ups for this site to open messages in their own window.'))
}

/**
 * Opens a message in its own window (the same message again focuses that window).
 *
 * @param {{accountId: string, folderId: string}} where the message's folder
 * @param {string} messageId the message
 */
export function openMessageWindow(where, messageId) {
	const name = `sockless-mail-${where.accountId}-${where.folderId}-${messageId}`.replace(/[^\w-]/g, '_')
	if (!openPopup({ window: 'message', account: where.accountId, folder: where.folderId, message: messageId }, name)) {
		blocked()
	}
}

/**
 * Starts writing in a window of its own, when the settings say so and this is the main window.
 * Must be called straight from the click (or key press), or the browser blocks the window.
 * Returns whether the window took over; when it was blocked, the composer opens large here.
 *
 * @param {object} request what to write: { mode: 'new', account } or
 *   { mode: 'reply' | 'replyAll' | 'forward' | 'edit', account, folder, message }
 */
export function composeInWindow(request) {
	if (windowMode || layout.composerSize !== 'window') {
		return false
	}
	if (openPopup({ window: 'compose', ...request }, `sockless-mail-compose-${Date.now()}`)) {
		return true
	}
	// Written here instead, large (see show() in compose.js).
	blocked()
	return false
}

/**
 * Moves the message being written to a window of its own. The draft is saved first, so nothing
 * is lost should the window not come up; the composer here closes without deleting anything.
 */
export async function popOutComposer() {
	const token = `${Date.now()}-${Math.random().toString(36).slice(2)}`
	const slot = { draft: null, blobs: null }
	window[HANDOVERS].set(token, slot)
	if (!openPopup({ window: 'compose', mode: 'handover', token }, `sockless-mail-compose-${token}`)) {
		window[HANDOVERS].delete(token)
		blocked()
		return
	}
	Object.assign(slot, await handOverDraft())
	// Not taken within a minute: the window did not load. The draft is in Drafts.
	window.setTimeout(() => window[HANDOVERS].delete(token), 60_000)
}

/** The handed-over draft, once the window that had it has put it in the slot. */
async function takeHandover(token) {
	for (let i = 0; i < 300; i++) {
		const slot = window.opener?.[HANDOVERS]?.get(token)
		if (slot?.draft) {
			window.opener[HANDOVERS].delete(token)
			return slot
		}
		await new Promise((resolve) => window.setTimeout(resolve, 100))
	}
	return null
}

/** Nextcloud's header and margins are not wanted around a single message or composer. */
function stripPage() {
	document.body.classList.add('sm-window-mode')
}

async function startMessage(params) {
	const where = { accountId: params.get('account'), folderId: params.get('folder') }
	const messageId = params.get('message')
	// For the location label, Move and "Continue editing" of drafts.
	loadFolders(where.accountId, true)
	await openMessage(where, messageId)
	watch(() => mailboxState.open.message?.subject, (subject) => {
		document.title = subject || t('sockless_mail', '(no subject)')
	}, { immediate: true })
	// Deleted or moved away: nothing left to show.
	watch(() => mailboxState.open.id, (id) => {
		if (!id && !composer.draft) {
			window.close()
		}
	})
}

async function startCompose(params) {
	const mode = params.get('mode')
	const where = { accountId: params.get('account'), folderId: params.get('folder') }
	if (mode === 'handover') {
		const slot = await takeHandover(params.get('token'))
		if (!slot) {
			windowState.error = t('sockless_mail', 'The message could not be moved to this window. It is saved in Drafts.')
			return
		}
		adoptDraft(slot)
	} else if (mode === 'new') {
		composeNew(where.accountId)
	} else if (mode === 'edit') {
		await composeEdit(where, params.get('message'))
	} else {
		await composeReply(mode, where, params.get('message'))
	}
	watch(() => composer.draft?.subject, (subject) => {
		document.title = subject || t('sockless_mail', 'New message')
	}, { immediate: true })
	// Sent, kept or discarded: the window has done its job.
	watch(() => composer.draft, (draft) => {
		if (!draft) {
			window.close()
		}
	})
}

/** Starts a window opened by openMessageWindow or composeInWindow (App.vue, instead of the mailbox). */
export async function startWindow() {
	stripPage()
	// What the composer and the reader offer, as in the main window; nothing waits for them.
	loadTasks()
	loadTags()
	loadPresets()
	await loadAccounts()
	try {
		if (!accountsState.accounts.length) {
			throw new Error(accountsState.loadError || t('sockless_mail', 'No mail accounts yet'))
		}
		await (windowMode.kind === 'message' ? startMessage(windowMode.params) : startCompose(windowMode.params))
	} catch (e) {
		windowState.error = e.message
	} finally {
		windowState.loading = false
	}
}
