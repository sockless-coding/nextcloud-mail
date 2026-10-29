import { FilePickerClosed, getFilePickerBuilder, showError, showLoading, showSuccess, showWarning } from '@nextcloud/dialogs'
import { n, t } from '@nextcloud/l10n'
import { reactive } from 'vue'
import { generateUrl } from '@nextcloud/router'
import { apiSend } from '../api.js'
import { composer, saveSoon } from './compose.js'
import { ActionError } from './mailbox.js'

/**
 * Nextcloud Files from the mail app: attach from Files, share links, save attachments. The file
 * picker runs in the browser (the page is a Nextcloud page); copying is done by the server,
 * which reads and writes the user's Files over WebDAV as that user.
 */

/**
 * Paths chosen in Nextcloud's file picker, or null when it was closed.
 *
 * @param {string} title picker title
 * @param {boolean} folders whether folders may be chosen too
 */
async function pickFiles(title, folders = false) {
	try {
		return await getFilePickerBuilder(title)
			.setMultiSelect(true)
			.allowDirectories(folders)
			.addButton({ label: t('sockless_mail', 'Choose'), variant: 'primary', callback: () => {} })
			.build()
			.pick()
	} catch (e) {
		if (e instanceof FilePickerClosed) {
			return null
		}
		throw e
	}
}

/**
 * A folder chosen in the file picker (the selected one, or the one shown), or null when closed.
 *
 * @param {string} title picker title
 */
async function pickFolder(title) {
	let chosen = null
	try {
		await getFilePickerBuilder(title)
			.setMultiSelect(false)
			.allowDirectories(true)
			.setMimeTypeFilter(['httpd/unix-directory'])
			.setButtonFactory((selected, currentPath) => {
				const target = selected[0]?.path ?? currentPath
				const name = target === '/' ? t('sockless_mail', 'Files') : target.split('/').pop()
				return [{
					label: t('sockless_mail', 'Save to {folder}', { folder: name }),
					variant: 'primary',
					callback: () => { chosen = target },
				}]
			})
			.build()
			.pick()
	} catch (e) {
		if (!(e instanceof FilePickerClosed)) {
			throw e
		}
	}
	return chosen
}

/** Lets the user choose files in Files and attaches them to the open draft. */
export async function attachFromFiles() {
	const paths = await pickFiles(t('sockless_mail', 'Attach from Files'))
	if (paths?.length) {
		await addFromFiles({ paths }, paths.length)
	}
}

/**
 * Attaches files by their Nextcloud file ids (the Files app's "Send by email").
 *
 * @param {number[]} fileIds file ids
 */
export function attachFileIds(fileIds) {
	return addFromFiles({ fileIds }, fileIds.length)
}

/**
 * One placeholder chip while the server copies the files, then a chip per file (with its
 * error, if any). Copies that arrive after the draft was closed, or the placeholder removed,
 * are thrown away again.
 *
 * @param {{paths?: string[], fileIds?: number[]}} input what to attach
 * @param {number} count how many files
 */
async function addFromFiles(input, count) {
	const draft = composer.draft
	if (!draft) {
		return
	}
	const placeholder = reactive({
		key: `files-${Date.now()}-${Math.random()}`,
		fileName: n('sockless_mail', 'Adding {count} file from Files', 'Adding {count} files from Files', count, { count }),
		fromFiles: true,
		progress: 0,
		id: null,
		error: null,
	})
	draft.uploads.push(placeholder)

	let files
	try {
		files = (await apiSend('POST', '/files/attachments', input)).files
	} catch (e) {
		placeholder.error = new ActionError(e).message
		return
	}

	const uploaded = files.filter((f) => f.upload)
	if (composer.draft !== draft || !draft.uploads.includes(placeholder)) {
		for (const file of uploaded) {
			apiSend('DELETE', `/uploads/${file.upload.id}`).catch(() => {})
		}
		return
	}

	const index = draft.uploads.indexOf(placeholder)
	draft.uploads.splice(index, 1, ...files.map((file) => reactive({
		key: `files-${file.upload?.id ?? file.name}-${Math.random()}`,
		fileName: file.upload?.fileName ?? file.name,
		size: file.upload?.size ?? 0,
		progress: 1,
		id: file.upload?.id ?? null,
		error: file.upload ? null : file.message,
		tooLarge: file.error === 'too_large',
		path: file.path,
	})))
	if (uploaded.length) {
		saveSoon()
	}
}

/**
 * Lets the user choose files or folders and returns public links to them ([{name, url}]),
 * or [] when nothing was chosen. Failures are shown; the links that worked are returned.
 *
 * @param {string[]} [paths] links for these paths instead of asking
 */
export async function createShareLinks(paths) {
	const chosen = paths ?? await pickFiles(t('sockless_mail', 'Share link from Files'), true)
	if (!chosen?.length) {
		return []
	}
	const results = await Promise.allSettled(chosen.map((path) => apiSend('POST', '/files/share-links', { path })))
	const failed = results.filter((r) => r.status === 'rejected')
	if (failed.length) {
		showError(new ActionError(failed[0].reason).message)
	}
	// The server's url has the host the app reaches Nextcloud by, which may be an internal one.
	return results.filter((r) => r.status === 'fulfilled')
		.map(({ value }) => ({ name: value.name, url: window.location.origin + generateUrl('/s/{token}', { token: value.token }) }))
}

/**
 * Saves attachments of a message into a folder the user chooses.
 *
 * @param {{accountId: string, folderId: string}} where the message's folder
 * @param {string} messageId message id
 * @param {{partId: string, fileName: string}[]} attachments what to save
 */
export async function saveAttachmentsToFiles(where, messageId, attachments) {
	const title = attachments.length === 1
		? t('sockless_mail', 'Save {name} to Files', { name: attachments[0].fileName })
		: n('sockless_mail', 'Save {count} attachment to Files', 'Save {count} attachments to Files', attachments.length, { count: attachments.length })
	const folder = await pickFolder(title)
	if (folder === null) {
		return
	}
	const loading = showLoading(t('sockless_mail', 'Saving to Files…'))
	let saved
	try {
		saved = (await apiSend('POST', `/accounts/${where.accountId}/folders/${where.folderId}/messages/${messageId}/save-to-files`, {
			folder,
			partIds: attachments.map((a) => a.partId),
		})).saved
	} catch (e) {
		showError(new ActionError(e).message)
		return
	} finally {
		loading.hideToast()
	}

	const done = saved.filter((s) => s.path)
	if (done.length === 1) {
		showSuccess(t('sockless_mail', 'Saved as {path}', { path: done[0].path }))
	} else if (done.length > 1) {
		showSuccess(n('sockless_mail', 'Saved {count} file to {folder}', 'Saved {count} files to {folder}', done.length, { count: done.length, folder }))
	}
	if (done.length < saved.length) {
		showWarning(n('sockless_mail', '{count} attachment could not be saved', '{count} attachments could not be saved', saved.length - done.length, { count: saved.length - done.length }))
	}
}

/**
 * The Files app's "Send by email" opens our page with ?fileIds=1,2. Returns the ids once and
 * removes them from the address, so reloading does not attach them again.
 */
export function takeFileIdsFromUrl() {
	const url = new URL(window.location.href)
	const raw = url.searchParams.get('fileIds')
	if (!raw) {
		return []
	}
	url.searchParams.delete('fileIds')
	window.history.replaceState(window.history.state, '', url)
	return raw.split(',').map(Number).filter((id) => Number.isInteger(id) && id > 0).slice(0, 25)
}
