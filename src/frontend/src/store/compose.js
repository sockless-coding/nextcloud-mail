import { showSuccess, showWarning } from '@nextcloud/dialogs/toast'
import { t } from '@nextcloud/l10n'
import { reactive } from 'vue'
import { apiGet, apiSend, apiUpload } from '../api.js'
import { accountsState } from './accounts.js'
import { ActionError, loadFolders, mailboxState, messagesById, openLocation, refreshMessages } from './mailbox.js'
import { searchState } from './search.js'
import { unifiedState } from './unified.js'

/**
 * The message being written. One composer at a time; it is rendered by App.vue whenever
 * composer.draft is set, as a window docked at the bottom that can be minimised (to read mail
 * meanwhile) or popped out to a large one.
 */
export const composer = reactive({
	/** { accountId, to, cc, bcc, subject, text, source, forwarded: [{partId, fileName, size, include}], uploads: [...] } */
	draft: null,
	minimized: false,
	expanded: false,
})

/** Another message is being written: show it instead of replacing it. */
function busy() {
	if (!composer.draft) {
		return false
	}
	composer.minimized = false
	showWarning(t('sockless_mail', 'Send or discard the message you are writing first.'))
	return true
}

function show(draft) {
	Object.assign(composer, { draft, minimized: false })
}

/** Whether discarding the draft would lose anything the user typed or attached. */
export function hasContent(draft = composer.draft) {
	if (!draft) {
		return false
	}
	// A reply starts with recipients, subject and quote filled in; only the user's own text counts there.
	const typed = draft.source ? draft.text !== draft.initialText : !!(draft.to || draft.cc || draft.bcc || draft.subject || draft.text)
	return typed || draft.uploads.length > 0
}

/** Addresses of mail the app has loaded (lists, search, unified inbox), for suggestions; newest first, once each. */
export function knownAddresses() {
	const seen = new Map()
	const messages = [
		...mailboxState.messages.list,
		...unifiedState.list.map((h) => h.message),
		...searchState.hits.map((h) => h.message),
	]
	for (const message of messages) {
		for (const a of [...message.from, ...message.to]) {
			const key = a.address.toLowerCase()
			if (!seen.has(key)) {
				seen.set(key, { name: a.name, address: a.address })
			}
		}
	}
	return [...seen.values()]
}

function emptyDraft(accountId) {
	return { accountId, to: '', cc: '', bcc: '', subject: '', text: '', source: null, forwarded: [], uploads: [] }
}

/** New message from the selected account (in the unified inbox: the open message's, or the first one). */
export function composeNew() {
	const accountId = mailboxState.selected.accountId ?? mailboxState.open.accountId ?? accountsState.accounts[0]?.id
	if (accountId && !busy()) {
		show(emptyDraft(accountId))
	}
}

/**
 * Reply, reply all or forward the open message; the server builds recipients, subject and quote.
 *
 * @param {'reply'|'replyAll'|'forward'} mode what to do
 */
export async function composeReply(mode) {
	if (busy()) {
		return
	}
	const { accountId, folderId } = openLocation()
	const messageId = mailboxState.open.id
	let draft
	try {
		draft = await apiGet(`/accounts/${accountId}/folders/${folderId}/messages/${messageId}/draft?mode=${mode}`)
	} catch (e) {
		throw new ActionError(e)
	}
	show({
		...emptyDraft(accountId),
		to: draft.to,
		cc: draft.cc,
		subject: draft.subject,
		text: draft.text,
		initialText: draft.text,
		source: draft.source,
		forwarded: draft.forwardedAttachments.map((a) => ({ ...a, include: true })),
	})
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
 * Called once the composer has closed. A discarded draft's uploads are removed from the
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
	Object.assign(composer, { draft: null, minimized: false, expanded: false })
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

	// The composer closes itself and then calls closeComposer().
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
	for (const original of messagesById({ accountId: draft.accountId, folderId: draft.source.folderId }, [draft.source.messageId])) {
		original.answered = true
	}
}
