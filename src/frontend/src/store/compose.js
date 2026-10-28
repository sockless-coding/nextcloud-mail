import { showSuccess, showWarning } from '@nextcloud/dialogs/toast'
import { t } from '@nextcloud/l10n'
import { reactive } from 'vue'
import { apiGet, apiSend, apiUpload } from '../api.js'
import { accountsState } from './accounts.js'
import { layout } from './layout.js'
import { ActionError, deleteMessages, loadFolders, mailboxState, messagesById, openLocation, refreshMessages } from './mailbox.js'
import { searchState } from './search.js'
import { unifiedState } from './unified.js'

/**
 * The message being written. One composer at a time; it is rendered by App.vue whenever
 * composer.draft is set, as a window docked at the bottom that can be minimised (to read mail
 * meanwhile) or popped out to a large one.
 *
 * Drafts are saved to the account's Drafts folder a few seconds after each change (saveSoon);
 * draft.saved is the stored copy, replaced by every save and removed once the message is sent
 * or discarded.
 */
export const composer = reactive({
	/**
	 * { accountId, to, cc, bcc, subject, format ('html' | 'text'), html, text, source,
	 *   forwarded: [{partId, fileName, size, include}], uploads: [...], saved: {folderId, messageId} | null }
	 */
	draft: null,
	minimized: false,
	expanded: false,
	/** idle | saving | saved | error */
	saveStatus: 'idle',
	savedAt: null,
	saveError: '',
})

/** Quiet time after the last change before the draft is saved. */
const SAVE_DELAY_MS = 4000
let saveTimer = null
let saving = null
let saveAgain = false

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
	Object.assign(composer, { draft, minimized: false, saveStatus: 'idle', savedAt: null, saveError: '' })
}

/** Whether discarding the draft would lose anything the user typed or attached. */
export function hasContent(draft = composer.draft) {
	if (!draft) {
		return false
	}
	// A reply or a continued draft starts filled in; only what the user changed counts there.
	const body = draft.format === 'html' ? draft.html !== draft.initialHtml : draft.text !== draft.initialText
	const typed = draft.source ? body : !!(draft.to || draft.cc || draft.bcc || draft.subject || draft.text || (draft.format === 'html' && body))
	return typed || draft.uploads.length > 0 || !!draft.saved
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
	return {
		accountId,
		to: '',
		cc: '',
		bcc: '',
		subject: '',
		format: layout.composeFormat,
		html: '',
		initialHtml: '',
		text: '',
		initialText: '',
		source: null,
		forwarded: [],
		uploads: [],
		saved: null,
	}
}

/** New message from the selected account (in the unified inbox: the open message's, or the first one). */
export function composeNew() {
	const accountId = mailboxState.selected.accountId ?? mailboxState.open.accountId ?? accountsState.accounts[0]?.id
	if (accountId && !busy()) {
		show(emptyDraft(accountId))
	}
}

/** A composer draft from the server's prefilled message (reply, forward, continued draft). */
function fromServer(accountId, draft, extra = {}) {
	return {
		...emptyDraft(accountId),
		to: draft.to,
		cc: draft.cc,
		bcc: draft.bcc,
		subject: draft.subject,
		html: draft.html,
		initialHtml: draft.html,
		text: draft.text,
		initialText: draft.text,
		source: draft.source,
		forwarded: draft.forwardedAttachments.map((a) => ({ ...a, include: true })),
		...extra,
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
	show(fromServer(accountId, draft))
}

/**
 * Continues a saved draft (the open message, in a Drafts folder). The first save replaces it.
 */
export async function composeEdit() {
	if (busy()) {
		return
	}
	const { accountId, folderId } = openLocation()
	const messageId = mailboxState.open.id
	let draft
	try {
		draft = await apiGet(`/accounts/${accountId}/folders/${folderId}/messages/${messageId}/draft?mode=edit`)
	} catch (e) {
		throw new ActionError(e)
	}
	show(fromServer(accountId, draft, { saved: { folderId, messageId } }))
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
		saveSoon()
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
	saveSoon()
}

/** What send and save have in common. */
function payload(draft) {
	const html = draft.format === 'html'
	return {
		to: draft.to,
		cc: draft.cc,
		bcc: draft.bcc,
		subject: draft.subject,
		text: html ? '' : draft.text,
		html: html ? draft.html : null,
		attachmentIds: draft.uploads.filter((u) => u.id).map((u) => u.id),
		source: draft.source && {
			...draft.source,
			forwardPartIds: draft.forwarded.filter((a) => a.include).map((a) => a.partId),
		},
	}
}

/** Saves the draft a few seconds after the last change (called on every change). */
export function saveSoon() {
	window.clearTimeout(saveTimer)
	if (composer.draft && hasContent()) {
		saveTimer = window.setTimeout(saveDraft, SAVE_DELAY_MS)
	}
}

/** Saves now; a save asked for while one runs follows it. */
export async function saveDraft() {
	window.clearTimeout(saveTimer)
	if (saving) {
		saveAgain = true
		return saving
	}
	const draft = composer.draft
	if (!draft) {
		return
	}
	composer.saveStatus = 'saving'
	saving = (async () => {
		const body = { ...payload(draft), replaces: draft.saved }
		try {
			const result = await apiSend('PUT', `/accounts/${draft.accountId}/drafts`, body)
			draft.saved = result.messageId ? { folderId: result.folderId, messageId: result.messageId } : null
			// A continued draft: the saved copy now holds the attachments, uploads included.
			if (result.source) {
				draft.source = result.source
				draft.forwarded = result.attachments.map((a) => ({ ...a, include: true }))
				draft.uploads = draft.uploads.filter((u) => !body.attachmentIds.includes(u.id))
			}
			Object.assign(composer, { saveStatus: 'saved', savedAt: new Date(), saveError: '' })
			showSavedDraft(draft.accountId, result.folderId)
		} catch (e) {
			Object.assign(composer, { saveStatus: 'error', saveError: new ActionError(e).message })
		}
	})()
	try {
		await saving
	} finally {
		saving = null
	}
	if (saveAgain && composer.draft === draft) {
		saveAgain = false
		await saveDraft()
	}
}

/** The first save may have created the Drafts folder; an open Drafts list shows the new copy. */
function showSavedDraft(accountId, folderId) {
	const folders = mailboxState.folders[accountId]
	if (folders && !folders.list.some((f) => f.id === folderId)) {
		loadFolders(accountId, true)
	}
	if (mailboxState.selected.accountId === accountId && mailboxState.selected.folderId === folderId) {
		refreshMessages()
	}
}

/** Lets a running save finish, so its result (the saved copy) is known. */
async function settleSaves() {
	window.clearTimeout(saveTimer)
	saveAgain = false
	if (saving) {
		await saving
	}
}

/**
 * Closes the composer. "sent": the server already removed uploads and the saved draft.
 * "kept": the draft is saved first (and stays in Drafts); its uploads are inside it now.
 * "discarded": uploads and the saved copy are removed.
 * Returns false when keeping failed (the draft could not be saved); the composer stays open.
 *
 * @param {'sent'|'kept'|'discarded'} outcome what happened to the message
 */
export async function closeComposer(outcome) {
	const draft = composer.draft
	if (!draft) {
		return true
	}
	if (outcome === 'kept' && hasContent()) {
		await settleSaves()
		await saveDraft()
		if (composer.saveStatus !== 'saved' || !draft.saved) {
			return false
		}
		showSuccess(t('sockless_mail', 'Draft saved in Drafts'))
	}
	await settleSaves()
	Object.assign(composer, { draft: null, minimized: false, expanded: false, saveStatus: 'idle', savedAt: null, saveError: '' })
	if (outcome === 'sent') {
		return true
	}
	for (const upload of draft.uploads) {
		if (upload.id) {
			apiSend('DELETE', `/uploads/${upload.id}`).catch(() => {})
		}
	}
	if (outcome === 'discarded' && draft.saved) {
		deleteMessages({ accountId: draft.accountId, folderId: draft.saved.folderId }, [draft.saved.messageId], true).catch(() => {})
	}
	return true
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
	await settleSaves()
	let result
	try {
		result = await apiSend('POST', `/accounts/${draft.accountId}/send`, { ...payload(draft), draft: draft.saved })
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
		const shown = mailboxState.selected.folderId
		if (shown && (shown === result.sentFolderId || shown === draft.saved?.folderId)) {
			refreshMessages()
		}
	}
	return result
}

/** The server flagged the original \Answered; show it without waiting for the next refresh. */
function markOriginal(draft) {
	if (!draft.source || draft.source.mode === 'forward' || draft.source.mode === 'edit') {
		return
	}
	for (const original of messagesById({ accountId: draft.accountId, folderId: draft.source.folderId }, [draft.source.messageId])) {
		original.answered = true
	}
}

/** Plain text as editor HTML: paragraphs, line breaks kept. */
export function textToHtml(text) {
	const escape = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
	return text.split(/\n{2,}/).map((p) => `<p>${p.split('\n').map(escape).join('<br>')}</p>`).join('')
}
