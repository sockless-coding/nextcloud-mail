import { showSuccess, showWarning } from '@nextcloud/dialogs/toast'
import { t } from '@nextcloud/l10n'
import { reactive } from 'vue'
import { apiGet, apiSend, apiUpload } from '../api.js'
import { accountsState } from './accounts.js'
import { ActionError, loadFolders, mailboxState, refreshMessages } from './mailbox.js'

/**
 * The message being written. One composer at a time; it is rendered by App.vue whenever
 * composer.draft is set.
 */
export const composer = reactive({
	/** { accountId, to, cc, bcc, subject, text, source, forwarded: [{partId, fileName, size, include}], uploads: [...] } */
	draft: null,
})

function emptyDraft(accountId) {
	return { accountId, to: '', cc: '', bcc: '', subject: '', text: '', source: null, forwarded: [], uploads: [] }
}

/** New message from the selected account (or the first one). */
export function composeNew() {
	const accountId = mailboxState.selected.accountId ?? accountsState.accounts[0]?.id
	if (accountId) {
		composer.draft = emptyDraft(accountId)
	}
}

/**
 * Reply, reply all or forward the open message; the server builds recipients, subject and quote.
 *
 * @param {'reply'|'replyAll'|'forward'} mode what to do
 */
export async function composeReply(mode) {
	const { accountId, folderId } = mailboxState.selected
	const messageId = mailboxState.open.id
	let draft
	try {
		draft = await apiGet(`/accounts/${accountId}/folders/${folderId}/messages/${messageId}/draft?mode=${mode}`)
	} catch (e) {
		throw new ActionError(e)
	}
	composer.draft = {
		...emptyDraft(accountId),
		to: draft.to,
		cc: draft.cc,
		subject: draft.subject,
		text: draft.text,
		source: draft.source,
		forwarded: draft.forwardedAttachments.map((a) => ({ ...a, include: true })),
	}
}

/**
 * Uploads a file for the current draft; progress and errors are tracked on its entry.
 *
 * @param {File} file chosen file
 */
export async function attach(file) {
	const draft = composer.draft
	const entry = reactive({ key: `${file.name}-${Date.now()}-${Math.random()}`, fileName: file.name, size: file.size, progress: 0, id: null, error: null })
	draft.uploads.push(entry)
	try {
		const upload = await apiUpload('/uploads', file, (p) => { entry.progress = p })
		entry.id = upload.id
		entry.progress = 1
	} catch (e) {
		entry.error = new ActionError(e).message
	}
}

export function detach(entry) {
	const draft = composer.draft
	draft.uploads = draft.uploads.filter((u) => u !== entry)
	if (entry.id) {
		apiSend('DELETE', `/uploads/${entry.id}`).catch(() => {})
	}
}

/**
 * Called once the composer dialog has closed. A discarded draft's uploads are removed from the
 * server right away (sent ones were removed by sending).
 *
 * @param {boolean} sent whether the draft was sent
 */
export function closeComposer(sent) {
	if (!sent) {
		for (const upload of composer.draft?.uploads ?? []) {
			if (upload.id) {
				apiSend('DELETE', `/uploads/${upload.id}`).catch(() => {})
			}
		}
	}
	composer.draft = null
}

/**
 * Sends the draft. Throws an ActionError (with fieldErrors) when the server refuses; the draft
 * stays as it is so nothing typed is lost.
 */
export async function send() {
	const draft = composer.draft
	if (draft.uploads.some((u) => !u.id && !u.error)) {
		throw new Error(t('sockless_mail', 'Wait until all attachments are uploaded.'))
	}
	const payload = {
		to: draft.to,
		cc: draft.cc,
		bcc: draft.bcc,
		subject: draft.subject,
		text: draft.text,
		attachmentIds: draft.uploads.filter((u) => u.id).map((u) => u.id),
		source: draft.source && {
			...draft.source,
			forwardPartIds: draft.forwarded.filter((a) => a.include).map((a) => a.partId),
		},
	}
	let result
	try {
		result = await apiSend('POST', `/accounts/${draft.accountId}/send`, payload)
	} catch (e) {
		if (e?.response?.status === 429) {
			throw new Error(t('sockless_mail', 'You have sent many messages in a short time. Wait a few minutes and try again.'))
		}
		throw new ActionError(e)
	}

	// The composer closes itself (through its dialog) and then calls closeComposer().
	showSuccess(t('sockless_mail', 'Message sent'))
	if (result.warning) {
		showWarning(result.warning)
	}
	markOriginal(draft)
	if (mailboxState.selected.accountId === draft.accountId) {
		loadFolders(draft.accountId, true)
		if (result.sentFolderId && mailboxState.selected.folderId === result.sentFolderId) {
			refreshMessages()
		}
	}
	return result
}

/** The server flagged the original \Answered; show it without waiting for the next refresh. */
function markOriginal(draft) {
	if (!draft.source || draft.source.mode === 'forward') {
		return
	}
	const original = mailboxState.messages.list.find((m) => m.id === draft.source.messageId)
	if (original) {
		original.answered = true
	}
}
