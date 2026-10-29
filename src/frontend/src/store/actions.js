import { showError, showSuccess } from '@nextcloud/dialogs/toast'
import { t } from '@nextcloud/l10n'
import { reactive } from 'vue'
import { createFolder, deleteMessages, loadFolders, mailboxState, moveMessages, renameFolder, setFlags } from './mailbox.js'

/**
 * User-facing message and folder actions: run the store change, report failures as toasts and
 * drive the shared dialogs (rendered once in App.vue). Message actions take the messages'
 * folder, { accountId, folderId } (see mailbox.js).
 */
export const dialogs = reactive({
	/** Messages waiting for a target folder: { where, messageIds }. */
	move: null,
	/** Messages waiting for confirmation to delete permanently (account without Trash): { where, messageIds }. */
	confirmPermanent: null,
	/** Folder name prompt: { title, label, value, submit(name) -> Promise } */
	folderName: null,
	/** Account form: { account } (account null for a new one). */
	accountForm: null,
	/** Account waiting for confirmation to be removed. */
	accountToDelete: null,
	oauthSettings: false,
	/** A sender to add to the address book: { name, address }. */
	addContact: null,
	/** The keyboard shortcut help. */
	shortcuts: false,
})

function fail(what, e) {
	showError(t('sockless_mail', '{action} failed: {error}', { action: what, error: e.message }))
}

export async function toggleSeen(where, message) {
	try {
		await setFlags(where, [message.id], { seen: !message.seen })
	} catch (e) {
		fail(message.seen ? t('sockless_mail', 'Marking as unread') : t('sockless_mail', 'Marking as read'), e)
	}
}

export async function toggleFlagged(where, message) {
	try {
		await setFlags(where, [message.id], { flagged: !message.flagged })
	} catch (e) {
		fail(t('sockless_mail', 'Flagging'), e)
	}
}

/**
 * Actions on several rows at once ({ accountId, folderId, message }, see selection.js); the
 * rows may come from different folders and accounts, so each folder gets its own request.
 */
function byFolder(rows) {
	const groups = new Map()
	for (const row of rows) {
		const key = `${row.accountId}/${row.folderId}`
		if (!groups.has(key)) {
			groups.set(key, { where: { accountId: row.accountId, folderId: row.folderId }, ids: [] })
		}
		groups.get(key).ids.push(row.message.id)
	}
	return [...groups.values()]
}

export async function setSeenRows(rows, seen) {
	try {
		await Promise.all(byFolder(rows).map(({ where, ids }) => setFlags(where, ids, { seen })))
	} catch (e) {
		fail(seen ? t('sockless_mail', 'Marking as read') : t('sockless_mail', 'Marking as unread'), e)
	}
}

export async function setFlaggedRows(rows, flagged) {
	try {
		await Promise.all(byFolder(rows).map(({ where, ids }) => setFlags(where, ids, { flagged })))
	} catch (e) {
		fail(t('sockless_mail', 'Flagging'), e)
	}
}

export async function removeRows(rows) {
	await Promise.all(byFolder(rows).map(({ where, ids }) => remove(where, ids)))
}

/** Moving needs one source folder: the target is chosen among that account's folders. */
export function canMoveRows(rows) {
	return rows.length > 0 && byFolder(rows).length === 1
}

export async function askMoveRows(rows) {
	const [{ where, ids }] = byFolder(rows)
	await askMove(where, ids)
}

export async function askMove(where, messageIds) {
	// In a unified view the account's folders may not have been loaded yet.
	if (!mailboxState.folders[where.accountId]?.list.length) {
		await loadFolders(where.accountId)
	}
	dialogs.move = { where: { accountId: where.accountId, folderId: where.folderId }, messageIds }
}

export async function moveTo({ where, messageIds }, targetFolderId) {
	try {
		await moveMessages(where, messageIds, targetFolderId)
	} catch (e) {
		fail(t('sockless_mail', 'Moving'), e)
	}
}

export async function remove(where, messageIds) {
	try {
		await deleteMessages(where, messageIds)
	} catch (e) {
		if (e.code === 'no_trash') {
			dialogs.confirmPermanent = { where, messageIds }
		} else {
			fail(t('sockless_mail', 'Deleting'), e)
		}
	}
}

export async function removePermanently({ where, messageIds }) {
	try {
		await deleteMessages(where, messageIds, true)
	} catch (e) {
		fail(t('sockless_mail', 'Deleting'), e)
	}
}

/** Errors of the name prompt are shown in the dialog itself, so these rethrow. */
export function askNewFolder(accountId, parent = null) {
	dialogs.folderName = {
		title: parent ? t('sockless_mail', 'New subfolder of {name}', { name: parent.name }) : t('sockless_mail', 'New folder'),
		value: '',
		submitLabel: t('sockless_mail', 'Create'),
		submit: async (name) => {
			await createFolder(accountId, name, parent?.id ?? null)
			showSuccess(t('sockless_mail', 'Folder {name} created', { name }))
		},
	}
}

export function askRenameFolder(accountId, folder) {
	dialogs.folderName = {
		title: t('sockless_mail', 'Rename {name}', { name: folder.name }),
		value: folder.name,
		submitLabel: t('sockless_mail', 'Rename'),
		submit: (name) => renameFolder(accountId, folder.id, name),
	}
}

/** Folders of the messages' account they can be moved to: selectable, not their own, with their path. */
export function moveTargets({ accountId, folderId }) {
	const folders = mailboxState.folders[accountId]?.list ?? []
	const byId = new Map(folders.map((f) => [f.id, f]))
	const path = (f) => (f.parentId && byId.has(f.parentId) ? `${path(byId.get(f.parentId))} / ${f.name}` : f.name)
	return folders
		.filter((f) => f.selectable && f.id !== folderId)
		.map((f) => ({ ...f, path: path(f) }))
}
