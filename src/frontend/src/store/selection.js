import { reactive } from 'vue'

/**
 * Messages selected in the list (checkboxes, Ctrl- and Shift-click) for actions on several at
 * once. Rows are the list's rows, { key, accountId, folderId, message }; the selection belongs
 * to the list shown and is cleared when another list replaces it.
 */
export const selection = reactive({
	rows: [],
	/** Key of the row a Shift-click range starts from. */
	anchor: null,
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

export function clearSelection() {
	selection.rows = []
	selection.anchor = null
}

/** Rows that left the list (moved, deleted, polled away) leave the selection; kept rows take the fresh row. */
export function pruneSelection(rows) {
	if (!selection.rows.length) {
		return
	}
	const byKey = new Map(rows.map((r) => [r.key, r]))
	selection.rows = selection.rows.filter((r) => byKey.has(r.key)).map((r) => byKey.get(r.key))
}

