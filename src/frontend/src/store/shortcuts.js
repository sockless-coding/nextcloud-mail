import { showError } from '@nextcloud/dialogs/toast'
import { t } from '@nextcloud/l10n'
import { dialogs, removeRows, setFlaggedRows, setSeenRows } from './actions.js'
import { composeNew, composeReply } from './compose.js'
import { readingPane } from './layout.js'
import { closeMessage, mailboxState } from './mailbox.js'
import { commandTargets, endSelectionMode, selection } from './selection.js'

/**
 * Keyboard shortcuts in Outlook's layout, plus single keys (N, R, F, …) for the ones browsers
 * keep for themselves (Ctrl+N opens a window). They are off while typing and while a dialog
 * is open. The list also feeds the help dialog (ShortcutsDialog.vue).
 */
export const SHORTCUTS = [
	{ keys: ['N', 'Ctrl+Shift+M'], label: t('sockless_mail', 'New message'), run: composeNew },
	{ keys: ['R', 'Ctrl+R'], label: t('sockless_mail', 'Reply'), run: () => reply('reply') },
	{ keys: ['Shift+R', 'Ctrl+Shift+R'], label: t('sockless_mail', 'Reply all'), run: () => reply('replyAll') },
	{ keys: ['F', 'Ctrl+Shift+F'], label: t('sockless_mail', 'Forward'), run: () => reply('forward') },
	{ keys: ['Delete'], label: t('sockless_mail', 'Delete'), run: () => withTargets(removeRows) },
	{ keys: ['Ctrl+Q'], label: t('sockless_mail', 'Mark as read'), run: () => withTargets((rows) => setSeenRows(rows, true)) },
	{ keys: ['Ctrl+U'], label: t('sockless_mail', 'Mark as unread'), run: () => withTargets((rows) => setSeenRows(rows, false)) },
	{ keys: ['Insert'], label: t('sockless_mail', 'Flag or remove the flag'), run: () => withTargets((rows) => setFlaggedRows(rows, rows.some((r) => !r.message.flagged))) },
	{ keys: ['/', 'Alt+Q'], label: t('sockless_mail', 'Search'), run: focusSearch },
	{ keys: ['Esc'], label: t('sockless_mail', 'Clear the selection, or close the message'), run: escape },
	{ keys: ['?'], label: t('sockless_mail', 'Show keyboard shortcuts'), run: () => { dialogs.shortcuts = true } },
]

/** Handled where they apply, listed here for the help dialog. */
export const LOCAL_SHORTCUTS = [
	{ keys: ['↑', '↓'], label: t('sockless_mail', 'Previous or next message (in the list)') },
	{ keys: ['Space'], label: t('sockless_mail', 'Select or deselect the message (in the list)') },
	{ keys: ['Shift+↑', 'Shift+↓'], label: t('sockless_mail', 'Extend the selection (in the list)') },
	{ keys: ['Ctrl+A'], label: t('sockless_mail', 'Select all loaded messages (in the list)') },
	{ keys: ['Menu', 'Shift+F10'], label: t('sockless_mail', 'Message actions (in the list)') },
	{ keys: ['Ctrl+Enter'], label: t('sockless_mail', 'Send (in the composer)') },
]

async function reply(mode) {
	if (selection.rows.length || !mailboxState.open.message) {
		return
	}
	try {
		await composeReply(mode)
	} catch (e) {
		showError(t('sockless_mail', 'Could not prepare the message: {error}', { error: e.message }))
	}
}

function withTargets(action) {
	const rows = commandTargets()
	if (rows.length) {
		action(rows)
	}
}

function focusSearch() {
	document.querySelector('.command-bar__search input')?.focus()
}

function escape() {
	if (selection.rows.length || selection.mode) {
		endSelectionMode()
	} else if (readingPane.value === 'off' && mailboxState.open.id) {
		closeMessage()
	}
}

/** "Ctrl+Shift+R" → "ctrl+shift+r"; Cmd counts as Ctrl. */
function signature(event) {
	const key = { Escape: 'esc', ' ': 'space' }[event.key] ?? event.key.toLowerCase()
	// Shift is part of "?" itself.
	const shift = event.shiftKey && key !== '?'
	return [(event.ctrlKey || event.metaKey) && 'ctrl', event.altKey && 'alt', shift && 'shift', key].filter(Boolean).join('+')
}

const BY_SIGNATURE = new Map(SHORTCUTS.flatMap((s) => s.keys.map((k) => [k.toLowerCase(), s])))

function typing(target) {
	return target instanceof HTMLElement
		&& (target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName))
}

function onKeydown(event) {
	if (event.defaultPrevented || event.isComposing || typing(event.target)) {
		return
	}
	// Dialogs (and the composer) handle their own keys.
	if (event.target instanceof Element && event.target.closest('[role="dialog"], .modal-mask')) {
		return
	}
	// Nextcloud dialogs keep their hidden backdrop for a moment after closing; only a visible one counts.
	if ([...document.querySelectorAll('.modal-mask')].some((mask) => mask.checkVisibility())) {
		return
	}
	const shortcut = BY_SIGNATURE.get(signature(event))
	if (shortcut) {
		event.preventDefault()
		shortcut.run()
	}
}

export function installShortcuts() {
	document.addEventListener('keydown', onKeydown)
	return () => document.removeEventListener('keydown', onKeydown)
}
