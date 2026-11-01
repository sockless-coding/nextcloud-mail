import { reactive } from 'vue'
import { conversationIdsOf } from './conversationRows.js'
import { mailboxState } from './mailbox.js'

/**
 * Messages selected in the list (Ctrl- and Shift-click, Space, Shift+arrows, or clicks in
 * selection mode) for actions on several at once. Rows are the list's rows,
 * { key, accountId, folderId, message }; the selection belongs to the list shown and is cleared
 * when another list replaces it.
 */
export const selection = reactive({
	rows: [],
	/** Key of the row a Shift-click range starts from. */
	anchor: null,
	/** Selection mode: a click toggles a row instead of opening it (for touch and mouse-only use). */
	mode: false,
})

export function isSelected(row) {
	return selection.rows.some((r) => r.key === row.key)
}

export function toggleRow(row) {
	selection.rows = isSelected(row) ? selection.rows.filter((r) => r.key !== row.key) : [...selection.rows, row]
	selection.anchor = row.key
}

/**
 * @param {object[]} rows the list's rows, in order
 * @param {object} row the Shift-clicked row
 */
export function selectRange(rows, row) {
	const from = rows.findIndex((r) => r.key === selection.anchor)
	const to = rows.indexOf(row)
	if (from < 0 || to < 0) {
		toggleRow(row)
		return
	}
	const range = rows.slice(Math.min(from, to), Math.max(from, to) + 1)
	const keys = new Set(selection.rows.map((r) => r.key))
	selection.rows = [...selection.rows, ...range.filter((r) => !keys.has(r.key))]
}

/** Shift+arrow: the selection becomes the rows from the anchor to this one, so it shrinks when going back. */
export function extendSelection(rows, row) {
	const from = rows.findIndex((r) => r.key === selection.anchor)
	const to = rows.indexOf(row)
	if (from < 0 || to < 0) {
		return
	}
	selection.rows = rows.slice(Math.min(from, to), Math.max(from, to) + 1)
}

export function clearSelection() {
	selection.rows = []
	selection.anchor = null
}

/** Leaves selection mode, and with it the selection. */
export function endSelectionMode() {
	clearSelection()
	selection.mode = false
}

/** Rows that left the list (moved, deleted, polled away) leave the selection; kept rows take the fresh row. */
export function pruneSelection(rows) {
	if (!selection.rows.length) {
		return
	}
	const byKey = new Map(rows.map((r) => [r.key, r]))
	selection.rows = selection.rows.filter((r) => byKey.has(r.key)).map((r) => byKey.get(r.key))
}

/** What commands (command bar, keyboard) act on: the selected messages, or else the open one. */
export function commandTargets() {
	if (selection.rows.length) {
		return selection.rows
	}
	const open = mailboxState.open
	if (!open.message) {
		return []
	}
	const where = { accountId: open.accountId, folderId: open.folderId }
	// In a list of conversations, the open message stands for its conversation in that folder.
	const ids = conversationIdsOf(where, open.id)
	return [{ ...where, message: open.message, ...(ids ? { ids } : {}) }]
}
