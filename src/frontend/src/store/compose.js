import { showSuccess, showWarning } from '@nextcloud/dialogs/toast'
import { t } from '@nextcloud/l10n'
import { reactive } from 'vue'
import { apiBase, apiGet, apiGetBlob, apiSend, apiUpload } from '../api.js'
import { accountsState } from './accounts.js'
import { layout } from './layout.js'
import { ActionError, deleteMessages, loadFolders, mailboxState, messagesById, openLocation, partUrl, refreshMessages } from './mailbox.js'
import { searchState } from './search.js'
import { formatSendTime } from './sendLater.js'
import { unifiedState } from './unified.js'
import { composeInWindow } from './windows.js'

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
	 *   forwarded: [{partId, fileName, size, include}], uploads: [...], images: [{id}], imageJobs,
 *   saved: {folderId, messageId} | null }
 * images are the uploads of images in the HTML (see uploadImage), apart from attachments;
 * imageJobs counts images still being prepared or uploaded (see imageJob).
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

/** Whether a message is being written (it is shown, with a warning, when it is). */
export const composerInUse = busy

function show(draft) {
	// "Separate window" ends up here inside such a window, or when the browser blocked it.
	Object.assign(composer, { draft, minimized: false, expanded: layout.composerSize !== 'small', saveStatus: 'idle', savedAt: null, saveError: '' })
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

/** Addresses of mail the app has loaded (lists, search, unified views), for suggestions; newest first, once each. */
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
		images: [],
		imageJobs: 0,
		saved: null,
	}
}

/**
 * New message from the given account, or else the selected one (in a unified view: the open
 * message's, or the first one).
 *
 * @param {string} [from] the account to send from
 * @param {object} [options] inPage: here, even when the settings say "Separate window"
 */
export function composeNew(from, { inPage = false } = {}) {
	const accountId = (typeof from === 'string' && from) || mailboxState.selected.accountId || mailboxState.open.accountId || accountsState.accounts[0]?.id
	if (!accountId || (!inPage && composeInWindow({ mode: 'new', account: accountId }))) {
		return
	}
	if (!busy()) {
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
 * Reply, reply all or forward a message (by default the open one); the server builds
 * recipients, subject and quote.
 *
 * @param {'reply'|'replyAll'|'forward'} mode what to do
 * @param {{accountId: string, folderId: string}} where the message's folder
 * @param {string} messageId the message
 */
export async function composeReply(mode, where = openLocation(), messageId = mailboxState.open.id) {
	if (composeInWindow({ mode, account: where.accountId, folder: where.folderId, message: messageId }) || busy()) {
		return
	}
	const { accountId, folderId } = where
	let draft
	try {
		draft = await apiGet(`/accounts/${accountId}/folders/${folderId}/messages/${messageId}/draft?mode=${mode}`)
	} catch (e) {
		throw new ActionError(e)
	}
	// The quoted original's own images go along.
	const html = await withImages(draft.html, draft.inlineImages, (partId) => partUrl(accountId, folderId, messageId, partId))
	show(fromServer(accountId, { ...draft, html }))
}

/**
 * Continues a saved draft (by default the open message, in a Drafts folder). The first save
 * replaces it.
 *
 * @param {{accountId: string, folderId: string}} where the draft's folder
 * @param {string} messageId the draft
 */
export async function composeEdit(where = openLocation(), messageId = mailboxState.open.id) {
	if (composeInWindow({ mode: 'edit', account: where.accountId, folder: where.folderId, message: messageId }) || busy()) {
		return
	}
	const { accountId, folderId } = where
	let draft
	try {
		draft = await apiGet(`/accounts/${accountId}/folders/${folderId}/messages/${messageId}/draft?mode=edit`)
	} catch (e) {
		throw new ActionError(e)
	}
	const html = await withImages(draft.html, draft.inlineImages, (partId) => partUrl(accountId, folderId, messageId, partId))
	show(fromServer(accountId, { ...draft, html }, { saved: { folderId, messageId } }))
}

/**
 * Takes the message being written out of this window, for another one (windows.js): saved
 * first, then copied, with the images shown in the text (object URLs of this window) as blobs.
 * The composer here closes without deleting anything.
 *
 * @return {Promise<{draft: object, blobs: Map<string, Blob>}>} what adoptDraft takes
 */
export async function handOverDraft() {
	await settleSaves()
	if (hasContent()) {
		await saveDraft()
	}
	const draft = JSON.parse(JSON.stringify(composer.draft))
	const blobs = new Map()
	for (const url of new Set(`${draft.html}${draft.initialHtml}`.match(/blob:[^"'\s)]+/g) ?? [])) {
		// One this module did not make is shown broken there; the image itself is kept by its Content-ID.
		if (objectUrls.has(url)) {
			blobs.set(url, objectUrls.get(url))
		}
	}
	await closeComposer('handedOver')
	return { draft, blobs }
}

/**
 * Continues a message handed over by another window (handOverDraft there).
 *
 * @param {{draft: object, blobs: Map<string, Blob>}} handover what the other window gave
 */
export async function adoptDraft({ draft, blobs }) {
	// Blobs of the other window, copied into this one: its object URLs end with it.
	for (const [url, blob] of blobs) {
		const local = objectUrl(new Blob([await blob.arrayBuffer()], { type: blob.type }))
		draft.html = draft.html.replaceAll(url, local)
		draft.initialHtml = draft.initialHtml.replaceAll(url, local)
	}
	show({ ...draft, imageJobs: 0 })
	if (draft.saved) {
		Object.assign(composer, { saveStatus: 'saved', savedAt: new Date() })
	}
}

/**
 * Object URLs of the images shown in the editor, with their blobs (the page's CSP does not let
 * fetch() read blob: URLs back); released when the composer closes.
 */
let objectUrls = new Map()

/**
 * A URL showing the image in the editor for as long as the composer is open.
 *
 * @param {Blob} blob the image
 */
export function objectUrl(blob) {
	const url = URL.createObjectURL(blob)
	objectUrls.set(url, blob)
	return url
}

/**
 * The draft's HTML with its images (src="cid:…", parts of the draft) shown from object URLs:
 * fetched once, so they still show once the draft they came from has been replaced.
 * An image that cannot be fetched keeps its Content-ID, and so stays in the message.
 */
async function withImages(html, images, urlOf) {
	if (!images?.length) {
		return html
	}
	const sources = new Map(await Promise.all(images.map(async (image) => {
		try {
			return [image.contentId.toLowerCase(), objectUrl(await apiGetBlob(urlOf(image.partId)))]
		} catch {
			return [image.contentId.toLowerCase(), null]
		}
	})))
	const document = new DOMParser().parseFromString(`<body>${html}</body>`, 'text/html')
	for (const img of document.body.querySelectorAll('img[src^="cid:"]')) {
		const contentId = img.getAttribute('src').slice(4)
		img.setAttribute('data-cid', contentId)
		const src = sources.get(contentId.toLowerCase())
		if (src) {
			img.setAttribute('src', src)
		}
	}
	return document.body.innerHTML
}

/** The Content-ID of an image that is an upload (OutgoingHtml.UploadContentId on the server). */
const uploadContentId = (id) => `${id}@inline.sockless-mail`

/** Where an uploaded image can be fetched from, to show it. */
export const uploadContentUrl = (id) => `${apiBase}/uploads/${id}/content`

/**
 * Remembers an upload as an image of the current draft (deleted with the draft) and returns
 * the Content-ID to mark it with.
 *
 * @param {string} id the upload
 */
export function adoptImage(id) {
	composer.draft?.images.push({ id })
	return uploadContentId(id)
}

/**
 * Forgets an image upload that is not used after all, and deletes it.
 *
 * @param {string} id the upload
 */
export function discardImage(id) {
	if (composer.draft) {
		composer.draft.images = composer.draft.images.filter((i) => i.id !== id)
	}
	apiSend('DELETE', `/uploads/${id}`).catch(() => {})
}

/**
 * Uploads an image put into the text of the current draft.
 *
 * @param {File} file the image, as it should be sent
 * @return {Promise<string>} the Content-ID the editor marks the image with
 */
export async function uploadImage(file) {
	try {
		return adoptImage((await apiUpload('/uploads', file)).id)
	} catch (e) {
		throw new ActionError(e)
	}
}

/**
 * Has the server fetch an image of pasted web content; the upload is not adopted yet.
 *
 * @param {string} url where the image is
 * @return {Promise<{id: string, fileName: string, contentType: string}>} the upload
 */
export async function importImage(url) {
	try {
		return await apiSend('POST', '/uploads/import', { url })
	} catch (e) {
		throw new ActionError(e)
	}
}

/**
 * Runs work on an image of the current draft (preparing, uploading, fetching): sending waits
 * until it is done.
 *
 * @param {() => Promise<any>} work what to do
 */
export async function imageJob(work) {
	const draft = composer.draft
	draft.imageJobs++
	try {
		return await work()
	} finally {
		draft.imageJobs--
	}
}

/** Whether images or attachments are still uploading. */
export function uploadsPending(draft = composer.draft) {
	return draft.uploads.some((u) => !u.id && !u.error) || draft.imageJobs > 0
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
				draft.images = draft.images.filter((i) => !i.id || !body.html?.includes(uploadContentId(i.id)))
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
 * "handedOver": nothing is removed; another window goes on with the draft (handOverDraft).
 * Returns false when keeping failed (the draft could not be saved); the composer stays open.
 *
 * @param {'sent'|'kept'|'discarded'|'handedOver'} outcome what happened to the message
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
	// After the editor is gone (it is unmounted with the draft).
	const urls = [...objectUrls.keys()]
	objectUrls = new Map()
	window.setTimeout(() => urls.forEach((url) => URL.revokeObjectURL(url)))
	// Sent: the server removed them. Handed over: another window goes on with them.
	if (outcome === 'sent' || outcome === 'handedOver') {
		return true
	}
	for (const upload of [...draft.uploads, ...draft.images]) {
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
	const result = await submitDraft(draft, `/accounts/${draft.accountId}/send`, {})

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

/** What sending and scheduling have in common: the server builds the message from the draft either way. */
async function submitDraft(draft, path, extra) {
	if (uploadsPending(draft)) {
		throw new Error(t('sockless_mail', 'Wait until all attachments and images are uploaded.'))
	}
	await settleSaves()
	try {
		return await apiSend('POST', path, { ...payload(draft), draft: draft.saved, ...extra })
	} catch (e) {
		if (e?.response?.status === 429) {
			throw new Error(t('sockless_mail', 'You have sent many messages in a short time. Wait a few minutes and try again.'))
		}
		throw new ActionError(e)
	}
}

/**
 * Has the server send the draft at sendAt, without the browser. Throws like send(); the server
 * removes the saved draft and the uploads (the message keeps its attachments).
 *
 * @param {Date} sendAt when to send
 */
export async function scheduleSend(sendAt) {
	const draft = composer.draft
	const scheduled = await submitDraft(draft, `/accounts/${draft.accountId}/schedule`, { sendAt: sendAt.toISOString() })
	showSuccess(t('sockless_mail', 'Scheduled for {time}', { time: formatSendTime(sendAt) }))
	if (draft.saved && mailboxState.selected.accountId === draft.accountId && mailboxState.selected.folderId === draft.saved.folderId) {
		refreshMessages()
	}
	return scheduled
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
