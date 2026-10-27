import { showError, showSuccess } from '@nextcloud/dialogs/toast'
import { t } from '@nextcloud/l10n'
import { reactive } from 'vue'
import { createFolder, deleteMessages, mailboxState, moveMessages, renameFolder, setFlags } from './mailbox.js'

/**
 * User-facing message and folder actions: run the store change, report failures as toasts and
 * drive the shared dialogs (rendered once in App.vue).
 */
export const dialogs = reactive({
	/** Message ids waiting for a target folder. */
	move: null,
	/** Message ids waiting for confirmation to delete permanently (account without Trash). */
	confirmPermanent: null,
	/** Folder name prompt: { title, label, value, submit(name) -> Promise } */
	folderName: null,
})

function fail(what, e) {
	showError(t('sockless_mail', '{action} failed: {error}', { action: what, error: e.message }))
}

export async function toggleSeen(message) {
	try {
		await setFlags([message.id], { seen: !message.seen })
	} catch (e) {
		fail(message.seen ? t('sockless_mail', 'Marking as unread') : t('sockless_mail', 'Marking as read'), e)
	}
}

export async function toggleFlagged(message) {
	try {
		await setFlags([message.id], { flagged: !message.flagged })
	} catch (e) {
		fail(t('sockless_mail', 'Flagging'), e)
	}
}

export function askMove(messageIds) {
	dialogs.move = messageIds
}

export async function moveTo(messageIds, targetFolderId) {
	try {
		await moveMessages(messageIds, targetFolderId)
	} catch (e) {
		fail(t('sockless_mail', 'Moving'), e)
	}
}

export async function remove(messageIds) {
	try {
		await deleteMessages(messageIds)
	} catch (e) {
		if (e.code === 'no_trash') {
			dialogs.confirmPermanent = messageIds
		} else {
			fail(t('sockless_mail', 'Deleting'), e)
		}
	}
}

export async function removePermanently(messageIds) {
	try {
		await deleteMessages(messageIds, true)
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

/** Folders the selected messages can be moved to: selectable, not the current one, with their path. */
export function moveTargets() {
	const { accountId, folderId } = mailboxState.selected
	const folders = mailboxState.folders[accountId]?.list ?? []
	const byId = new Map(folders.map((f) => [f.id, f]))
	const path = (f) => (f.parentId && byId.has(f.parentId) ? `${path(byId.get(f.parentId))} / ${f.name}` : f.name)
	return folders
		.filter((f) => f.selectable && f.id !== folderId)
		.map((f) => ({ ...f, path: path(f) }))
}
