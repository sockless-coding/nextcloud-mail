<template>
	<div v-if="composer.expanded && !composer.minimized && !windowMode" class="composer-backdrop" />
	<!-- A window docked at the bottom, not a modal: the mailbox stays usable while writing.
	     Escape and clicks elsewhere do nothing, so a half-written message cannot vanish by
	     accident. Close does what the settings say (keep it in Drafts, ask, or discard it);
	     Send and Discard end it. -->
	<section class="composer"
		:class="{ 'composer--window': windowMode, 'composer--minimized': composer.minimized && !windowMode, 'composer--expanded': composer.expanded && !composer.minimized && !windowMode, 'composer--dropping': dropping }"
		role="dialog"
		aria-modal="false"
		:aria-label="title"
		@dragenter="onDragEnter"
		@dragover="onDragOver"
		@dragleave="onDragLeave"
		@drop="onDrop"
		@keydown.ctrl.enter.prevent="submit"
		@keydown.meta.enter.prevent="submit">
		<header class="composer__header" @dblclick="!windowMode && (composer.minimized = !composer.minimized)">
			<span class="composer__title">{{ composer.minimized && draft.subject ? draft.subject : title }}</span>
			<button v-if="!windowMode"
				type="button"
				class="composer__window-button"
				:aria-label="composer.minimized ? t('sockless_mail', 'Restore') : t('sockless_mail', 'Minimize')"
				:title="composer.minimized ? t('sockless_mail', 'Restore') : t('sockless_mail', 'Minimize')"
				@click="composer.minimized = !composer.minimized">
				<NcIconSvgWrapper :path="composer.minimized ? mdiWindowRestore : mdiWindowMinimize" :size="18" />
			</button>
			<button v-if="!composer.minimized && !windowMode"
				type="button"
				class="composer__window-button"
				:disabled="uploading || sending"
				:aria-label="t('sockless_mail', 'Open in a new window')"
				:title="uploading ? t('sockless_mail', 'Open in a new window, once the uploads are done') : t('sockless_mail', 'Open in a new window')"
				@click="popOutComposer">
				<NcIconSvgWrapper :path="mdiOpenInNew" :size="18" />
			</button>
			<button v-if="!composer.minimized && !windowMode"
				type="button"
				class="composer__window-button"
				:aria-label="composer.expanded ? t('sockless_mail', 'Dock') : t('sockless_mail', 'Enlarge')"
				:title="composer.expanded ? t('sockless_mail', 'Dock') : t('sockless_mail', 'Enlarge')"
				@click="composer.expanded = !composer.expanded">
				<NcIconSvgWrapper :path="composer.expanded ? mdiArrowCollapse : mdiArrowExpand" :size="18" />
			</button>
			<button type="button"
				class="composer__window-button"
				:aria-label="t('sockless_mail', 'Close')"
				:title="closeTitle"
				@click="closeClicked">
				<NcIconSvgWrapper :path="mdiClose" :size="18" />
			</button>
		</header>

		<div v-show="!composer.minimized" class="composer__content">
			<label class="composer__row composer__from">
				<span class="composer__row-label">{{ t('sockless_mail', 'From') }}</span>
				<select v-model="draft.accountId" :disabled="!!draft.source">
					<option v-for="account in accountsState.accounts" :key="account.id" :value="account.id">
						{{ account.senderName ? `${account.senderName} <${account.emailAddress}>` : account.emailAddress }}
					</option>
				</select>
			</label>
			<div class="composer__to">
				<RecipientInput v-model="draft.to" :label="t('sockless_mail', 'To')" :error="errors.to ?? ''" />
				<button v-if="!showCopies"
					type="button"
					class="composer__copies"
					@click="showCopies = true">
					{{ t('sockless_mail', 'Cc/Bcc') }}
				</button>
			</div>
			<template v-if="showCopies">
				<RecipientInput v-model="draft.cc" :label="t('sockless_mail', 'Cc')" :error="errors.cc ?? ''" />
				<RecipientInput v-model="draft.bcc" :label="t('sockless_mail', 'Bcc')" :error="errors.bcc ?? ''" />
			</template>
			<div class="composer__row" :class="{ 'composer__row--error': errors.subject }">
				<label class="composer__row-label" for="composer-subject">{{ t('sockless_mail', 'Subject') }}</label>
				<input id="composer-subject"
					v-model="draft.subject"
					class="composer__subject"
					type="text">
			</div>
			<p v-if="errors.subject" class="composer__error">
				{{ errors.subject }}
			</p>
			<RichEditor v-if="draft.format === 'html'"
				ref="editor"
				v-model="draft.html"
				class="composer__editor"
				@normalized="onNormalized"
				@attach="(files) => files.forEach(attach)"
				:label="t('sockless_mail', 'Message')"
				:placeholder="t('sockless_mail', 'Write your message')" />
			<template v-else>
				<label class="hidden-visually" for="composer-text">{{ t('sockless_mail', 'Message') }}</label>
				<textarea id="composer-text"
					ref="body"
					v-model="draft.text"
					class="composer__text"
					:placeholder="t('sockless_mail', 'Write your message')" />
			</template>
			<p v-if="errors.text" class="composer__error">
				{{ errors.text }}
			</p>

			<ul v-if="draft.uploads.length || draft.forwarded.length" class="composer__attachments" :aria-label="t('sockless_mail', 'Attachments')">
				<li v-for="item in draft.forwarded" :key="`fwd-${item.partId}`" class="composer__chip" :class="{ 'composer__chip--off': !item.include }">
					<NcIconSvgWrapper :path="mdiPaperclip" :size="16" />
					<span class="composer__chip-name">{{ item.fileName }}</span>
					<button type="button"
						class="composer__chip-button"
						:aria-label="item.include ? t('sockless_mail', 'Do not forward {name}', { name: item.fileName }) : t('sockless_mail', 'Forward {name}', { name: item.fileName })"
						@click="item.include = !item.include">
						<NcIconSvgWrapper :path="item.include ? mdiClose : mdiUndo" :size="16" />
					</button>
				</li>
				<li v-for="upload in draft.uploads" :key="upload.key" class="composer__chip" :class="{ 'composer__chip--error': upload.error }">
					<NcIconSvgWrapper :path="mdiPaperclip" :size="16" />
					<span class="composer__chip-name">{{ upload.fileName }}</span>
					<span v-if="upload.error" class="composer__chip-meta">{{ upload.error }}</span>
					<NcLoadingIcon v-else-if="upload.fromFiles" :size="16" />
					<span v-else-if="!upload.id" class="composer__chip-meta">{{ Math.round(upload.progress * 100) }} %</span>
					<button v-if="upload.tooLarge && upload.path"
						type="button"
						class="composer__chip-link"
						@click="linkInstead(upload)">
						{{ t('sockless_mail', 'Share link instead') }}
					</button>
					<button type="button"
						class="composer__chip-button"
						:aria-label="t('sockless_mail', 'Remove {name}', { name: upload.fileName })"
						@click="detach(upload)">
						<NcIconSvgWrapper :path="mdiClose" :size="16" />
					</button>
				</li>
			</ul>

			<NcNoteCard v-if="errors.attachmentIds" type="error" :text="errors.attachmentIds" />
			<NcNoteCard v-if="generalError" type="error" :text="generalError" />
		</div>

		<footer v-show="!composer.minimized" class="composer__footer">
			<NcButton variant="primary"
				:disabled="uploading || sending"
				:title="t('sockless_mail', 'Send (Ctrl+Enter)')"
				@click="submit">
				<template #icon>
					<NcLoadingIcon v-if="sending" :size="20" />
					<NcIconSvgWrapper v-else :path="mdiSend" />
				</template>
				{{ uploading ? t('sockless_mail', 'Uploading…') : t('sockless_mail', 'Send') }}
			</NcButton>
			<NcActions variant="tertiary"
				:disabled="uploading || sending"
				:menu-name="t('sockless_mail', 'Send later')"
				:aria-label="t('sockless_mail', 'Send later')"
				@open="menuOpenedAt = new Date()">
				<template #icon>
					<NcIconSvgWrapper :path="mdiSendClockOutline" />
				</template>
				<NcActionButton v-for="{ preset, when } in laterOptions"
					:key="preset.id"
					close-after-click
					:description="formatSendTime(when, menuOpenedAt)"
					@click="submit(when)">
					<template #icon>
						<NcIconSvgWrapper :path="mdiClockOutline" />
					</template>
					{{ preset.label }}
				</NcActionButton>
				<NcActionButton close-after-click @click="pickingTime = true">
					<template #icon>
						<NcIconSvgWrapper :path="mdiCalendarClock" />
					</template>
					{{ t('sockless_mail', 'Pick date and time…') }}
				</NcActionButton>
				<NcActionButton close-after-click @click="dialogs.sendLaterPresets = true">
					<template #icon>
						<NcIconSvgWrapper :path="mdiCogOutline" />
					</template>
					{{ t('sockless_mail', 'Manage presets…') }}
				</NcActionButton>
			</NcActions>
			<input ref="fileInput"
				type="file"
				multiple
				hidden
				@change="onFiles">
			<NcActions variant="tertiary"
				:menu-name="t('sockless_mail', 'Attach')"
				:aria-label="t('sockless_mail', 'Attach files')">
				<template #icon>
					<NcIconSvgWrapper :path="mdiPaperclip" />
				</template>
				<NcActionButton close-after-click @click="fileInput.click()">
					<template #icon>
						<NcIconSvgWrapper :path="mdiUpload" />
					</template>
					{{ t('sockless_mail', 'Upload from this device') }}
				</NcActionButton>
				<NcActionButton close-after-click @click="attachFromFiles">
					<template #icon>
						<NcIconSvgWrapper :path="mdiFolderOutline" />
					</template>
					{{ t('sockless_mail', 'Attach from Files') }}
				</NcActionButton>
				<NcActionButton close-after-click @click="shareFromFiles">
					<template #icon>
						<NcIconSvgWrapper :path="mdiLinkVariant" />
					</template>
					{{ t('sockless_mail', 'Share link from Files') }}
				</NcActionButton>
			</NcActions>
			<NcButton variant="tertiary"
				:aria-pressed="String(draft.format === 'text')"
				:title="draft.format === 'html' ? t('sockless_mail', 'Write in plain text, without formatting') : t('sockless_mail', 'Write with formatting')"
				@click="switchFormat">
				<template #icon>
					<NcIconSvgWrapper :path="draft.format === 'html' ? mdiFormatLetterCase : mdiFormatText" />
				</template>
				{{ draft.format === 'html' ? t('sockless_mail', 'Plain text') : t('sockless_mail', 'Rich text') }}
			</NcButton>
			<span class="composer__status" :class="{ 'composer__status--error': composer.saveStatus === 'error' }" :title="composer.saveError">
				{{ saveText }}
			</span>
			<button type="button"
				class="composer__window-button"
				:aria-label="t('sockless_mail', 'Discard')"
				:title="t('sockless_mail', 'Discard')"
				@click="discard">
				<NcIconSvgWrapper :path="mdiDeleteOutline" :size="20" />
			</button>
		</footer>
	</section>

	<SendLaterDialog v-if="pickingTime"
		@choose="timeChosen"
		@close="pickTimeClosed" />

	<NcDialog v-if="confirmPlainText"
		:name="t('sockless_mail', 'Switch to plain text?')"
		:message="t('sockless_mail', 'The images in the message are removed, and so is its formatting.')"
		:buttons="plainTextButtons"
		:open="true"
		@update:open="(open) => !open && plainTextClosed()" />

	<NcDialog v-if="confirmDiscard"
		:name="t('sockless_mail', 'Discard this message?')"
		:message="draft.saved ? t('sockless_mail', 'What you wrote and attached will be lost, and the saved draft is deleted.') : t('sockless_mail', 'What you wrote and attached will be lost.')"
		:buttons="discardButtons"
		:open="true"
		@update:open="(open) => !open && discardClosed()" />

	<NcDialog v-if="confirmClose"
		:name="t('sockless_mail', 'Save this message as a draft?')"
		:message="t('sockless_mail', 'Keep it in Drafts to continue later, or discard what you wrote and attached.')"
		:buttons="closeButtons"
		:open="true"
		@update:open="(open) => !open && closeDialogClosed()" />
</template>

<script setup>
import { mdiArrowCollapse, mdiArrowExpand, mdiCalendarClock, mdiClockOutline, mdiClose, mdiCogOutline, mdiDeleteOutline, mdiFolderOutline, mdiFormatLetterCase, mdiFormatText, mdiLinkVariant, mdiOpenInNew, mdiPaperclip, mdiSend, mdiSendClockOutline, mdiUndo, mdiUpload, mdiWindowMinimize, mdiWindowRestore } from '@mdi/js'
import { showError } from '@nextcloud/dialogs/toast'
import { getCanonicalLocale, t } from '@nextcloud/l10n'
import NcActionButton from '@nextcloud/vue/components/NcActionButton'
import NcActions from '@nextcloud/vue/components/NcActions'
import NcButton from '@nextcloud/vue/components/NcButton'
import NcDialog from '@nextcloud/vue/components/NcDialog'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import NcNoteCard from '@nextcloud/vue/components/NcNoteCard'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { accountsState } from '../store/accounts.js'
import { dialogs } from '../store/actions.js'
import { attach, closeComposer, composer, detach, hasContent, saveSoon, scheduleSend, send, textToHtml, uploadsPending } from '../store/compose.js'
import { attachFromFiles as pickAndAttach, createShareLinks } from '../store/files.js'
import { layout } from '../store/layout.js'
import { formatSendTime, offeredPresets } from '../store/sendLater.js'
import { popOutComposer, windowMode } from '../store/windows.js'
import RecipientInput from './RecipientInput.vue'
import RichEditor from './RichEditor.vue'
import SendLaterDialog from './SendLaterDialog.vue'

const draft = composer.draft
const fileInput = ref(null)
const body = ref(null)
const editor = ref(null)
const showCopies = ref(!!(draft.cc || draft.bcc))
const errors = ref({})
const generalError = ref('')
const sending = ref(false)
const confirmDiscard = ref(false)

const title = computed(() => ({
	reply: t('sockless_mail', 'Reply'),
	replyAll: t('sockless_mail', 'Reply to all'),
	forward: t('sockless_mail', 'Forward'),
	edit: t('sockless_mail', 'Draft'),
})[draft.source?.mode] ?? t('sockless_mail', 'New message'))

const timeFormat = new Intl.DateTimeFormat(getCanonicalLocale(), { hour: 'numeric', minute: '2-digit' })

const saveText = computed(() => ({
	saving: t('sockless_mail', 'Saving…'),
	saved: composer.savedAt && t('sockless_mail', 'Draft saved {time}', { time: timeFormat.format(composer.savedAt) }),
	error: t('sockless_mail', 'Could not save the draft'),
})[composer.saveStatus] ?? '')

// What the user changes, not what saving writes back (saved copy, source, attachments).
watch(() => [draft.accountId, draft.to, draft.cc, draft.bcc, draft.subject, draft.format, draft.html, draft.text, draft.forwarded.map((a) => a.include).join()],
	() => saveSoon())

/**
 * The editor rewrites loaded HTML in its own form (<b> becomes <strong>, a trailing paragraph is
 * added). That is not a change by the user: it becomes what "unchanged" is compared with.
 */
function onNormalized(html) {
	if (draft.html === draft.initialHtml || draft.html === html) {
		draft.html = html
		draft.initialHtml = html
	}
}

const confirmPlainText = ref(false)
let plainTextChosen = false

/** Rich text and plain text, either way; the choice is remembered for new messages. */
function switchFormat(confirmed = false) {
	if (draft.format === 'html' && confirmed !== true && editor.value?.hasImages()) {
		plainTextChosen = false
		confirmPlainText.value = true
		return
	}
	if (draft.format === 'html') {
		draft.text = editor.value?.getText() ?? draft.text
		draft.format = 'text'
	} else {
		draft.html = draft.text ? textToHtml(draft.text) : ''
		draft.format = 'html'
	}
	layout.composeFormat = draft.format
}

const plainTextButtons = computed(() => [
	{ label: t('sockless_mail', 'Keep rich text') },
	{ label: t('sockless_mail', 'Switch to plain text'), variant: 'primary', callback: () => { plainTextChosen = true } },
])

// As with discarding: acted on once the dialog has closed.
function plainTextClosed() {
	confirmPlainText.value = false
	if (plainTextChosen) {
		switchFormat(true)
	}
}

/** Keeps the message: saved in Drafts, to be continued from there. */
async function closeKeeping() {
	if (!(await closeComposer('kept'))) {
		showError(t('sockless_mail', 'The draft could not be saved: {error}', { error: composer.saveError }))
	}
}

const closeTitle = computed(() => ({
	keep: t('sockless_mail', 'Close and keep the draft'),
	ask: t('sockless_mail', 'Close'),
	discard: t('sockless_mail', 'Close and discard the message'),
})[layout.composerClose] ?? t('sockless_mail', 'Close'))

const confirmClose = ref(false)
/** keep | discard | null (keep writing): the choice, acted on once the dialog has closed (see discardClosed). */
let closeChosen = null

/** The close button, as set in the settings. A message with nothing in it just closes. */
async function closeClicked() {
	if (!hasContent()) {
		closeComposer('discarded')
		return
	}
	if (layout.composerClose === 'discard') {
		closeComposer('discarded')
		return
	}
	if (layout.composerClose !== 'ask') {
		closeKeeping()
		return
	}
	if (confirmClose.value) {
		confirmClose.value = false
		await nextTick()
	}
	closeChosen = null
	confirmClose.value = true
}

function closeDialogClosed() {
	confirmClose.value = false
	if (closeChosen === 'keep') {
		closeKeeping()
	} else if (closeChosen === 'discard') {
		closeComposer('discarded')
	}
}

const closeButtons = computed(() => [
	{ label: t('sockless_mail', 'Keep writing') },
	{ label: t('sockless_mail', 'Discard'), variant: 'error', callback: () => { closeChosen = 'discard' } },
	{ label: t('sockless_mail', 'Save draft'), variant: 'primary', callback: () => { closeChosen = 'keep' } },
])

/** Leaving the page with unsaved changes asks first. */
function beforeUnload(event) {
	if (hasContent() && composer.saveStatus !== 'saved') {
		event.preventDefault()
	}
}

const uploading = computed(() => uploadsPending(draft))

const pickingTime = ref(false)
// Acted on once the dialog has closed (see discardClosed).
let pickedTime = null

function timeChosen(when) {
	pickedTime = when
}

function pickTimeClosed() {
	pickingTime.value = false
	const when = pickedTime
	pickedTime = null
	if (when) {
		submit(when)
	}
}
// The presets are worked out when the menu opens: "After lunch" is gone once lunch is over.
const menuOpenedAt = ref(new Date())
const laterOptions = computed(() => offeredPresets(undefined, menuOpenedAt.value))

/**
 * Sends now, or with a time, has the server send it then.
 *
 * @param {Date} [sendAt] when to send; now when left out
 */
async function submit(sendAt) {
	if (uploading.value || sending.value) {
		return
	}
	errors.value = {}
	generalError.value = ''
	sending.value = true
	try {
		await (sendAt instanceof Date ? scheduleSend(sendAt) : send())
		closeComposer('sent')
	} catch (e) {
		const { sendAt: timeError, ...fieldErrors } = e.fieldErrors ?? {}
		errors.value = fieldErrors
		if (errors.value.cc || errors.value.bcc) {
			showCopies.value = true
		}
		if (timeError) {
			generalError.value = timeError
		} else if (!Object.keys(errors.value).length) {
			generalError.value = e.message
		}
		composer.minimized = false
	} finally {
		sending.value = false
	}
}

/** The confirmation's choice, acted on once it has closed (see discardClosed). */
let discardChosen = false

async function discard() {
	if (!hasContent()) {
		closeComposer('discarded')
		return
	}
	// Still closing from the last time (NcDialog reports closed 300 ms late): show it afresh.
	if (confirmDiscard.value) {
		confirmDiscard.value = false
		await nextTick()
	}
	discardChosen = false
	confirmDiscard.value = true
}

// Unmounting the composer while its dialog closes would tear the dialog's focus trap down mid-way.
function discardClosed() {
	confirmDiscard.value = false
	if (discardChosen) {
		closeComposer('discarded')
	}
}

const discardButtons = computed(() => [
	{ label: t('sockless_mail', 'Keep writing') },
	{ label: t('sockless_mail', 'Discard'), variant: 'error', callback: () => { discardChosen = true } },
])

async function attachFromFiles() {
	try {
		await pickAndAttach()
	} catch (e) {
		showError(t('sockless_mail', 'Could not open Files: {error}', { error: e.message }))
	}
}

/**
 * Puts public links to the files in the message where the cursor is: linked names in rich
 * text, "name: link" lines in plain text.
 *
 * @param {string[]} [paths] these files instead of asking
 */
async function shareFromFiles(paths) {
	let links
	try {
		links = await createShareLinks(Array.isArray(paths) ? paths : undefined)
	} catch (e) {
		showError(t('sockless_mail', 'Could not open Files: {error}', { error: e.message }))
		return false
	}
	if (!links.length) {
		return false
	}
	if (draft.format === 'html') {
		editor.value?.insertLinks(links)
	} else {
		const textarea = body.value
		const at = textarea?.selectionStart ?? draft.text.length
		const lines = links.map((l) => `${l.name}: ${l.url}`).join('\n')
		const before = draft.text.slice(0, at)
		const insert = (before && !before.endsWith('\n') ? '\n' : '') + lines + '\n'
		draft.text = before + insert + draft.text.slice(at)
		nextTick(() => textarea?.setSelectionRange(at + insert.length, at + insert.length))
	}
	return true
}

/** A file too large to attach: a link to it in the message instead. */
async function linkInstead(upload) {
	if (await shareFromFiles([upload.path])) {
		detach(upload)
	}
}

// Files dragged over the composer: dropped into the text, images go there (RichEditor); dropped
// anywhere else, everything is attached.
const dropping = ref(false)
let dragDepth = 0

const draggingFiles = (event) => event.dataTransfer?.types.includes('Files')

function onDragEnter(event) {
	if (draggingFiles(event)) {
		dragDepth++
		dropping.value = true
	}
}

function onDragOver(event) {
	if (draggingFiles(event)) {
		event.preventDefault()
		event.dataTransfer.dropEffect = 'copy'
	}
}

function onDragLeave(event) {
	if (draggingFiles(event) && --dragDepth <= 0) {
		dragDepth = 0
		dropping.value = false
	}
}

function onDrop(event) {
	dragDepth = 0
	dropping.value = false
	if (event.defaultPrevented || !event.dataTransfer?.files.length) {
		return
	}
	event.preventDefault()
	for (const file of event.dataTransfer.files) {
		attach(file)
	}
}

function onFiles(event) {
	for (const file of event.target.files) {
		attach(file)
	}
	event.target.value = ''
}

// A reply starts in the text, above the quote; a new message in the To field.
onMounted(() => {
	window.addEventListener('beforeunload', beforeUnload)
	nextTick(() => {
		if (draft.source && draft.format === 'html') {
			editor.value?.focus('start')
		} else if (draft.source) {
			body.value?.focus()
			body.value?.setSelectionRange(0, 0)
		} else {
			document.getElementById(document.querySelector('.composer .recipients__label')?.getAttribute('for'))?.focus()
		}
	})
})

onBeforeUnmount(() => window.removeEventListener('beforeunload', beforeUnload))
</script>

<style scoped>
.composer {
	position: fixed;
	inset-inline-end: 24px;
	bottom: 0;
	z-index: 1500;
	display: flex;
	flex-direction: column;
	width: min(600px, calc(100vw - 32px));
	height: min(640px, calc(100vh - 80px));
	border: 1px solid var(--color-border);
	border-bottom: 0;
	border-radius: var(--border-radius-container, 12px) var(--border-radius-container, 12px) 0 0;
	background: var(--color-main-background);
	color: var(--color-main-text);
	box-shadow: 0 -4px 32px rgb(0 0 0 / 22%);
}

.composer--minimized {
	height: auto;
	width: min(340px, calc(100vw - 32px));
}

.composer--expanded {
	inset: 6vh 8vw;
	width: auto;
	height: auto;
	border-bottom: 1px solid var(--color-border);
	border-radius: var(--border-radius-container, 12px);
}

/* A window of its own: the composer is all there is. */
.composer--window {
	inset: 0;
	width: auto;
	height: auto;
	border: 0;
	border-radius: 0;
	box-shadow: none;
}

.composer__window-button:disabled {
	opacity: 0.5;
	cursor: default;
}

.composer--dropping {
	outline: 2px dashed var(--color-primary-element);
	outline-offset: -4px;
}

.composer-backdrop {
	position: fixed;
	inset: 0;
	z-index: 1499;
	background: rgb(0 0 0 / 30%);
}

.composer__header {
	display: flex;
	align-items: center;
	gap: 2px;
	padding: 6px 6px 6px 16px;
	border-bottom: 1px solid var(--color-border);
	border-radius: inherit;
	border-end-start-radius: 0;
	border-end-end-radius: 0;
	background: color-mix(in srgb, var(--color-main-text) 4%, var(--color-main-background));
	cursor: default;
	user-select: none;
}

.composer--minimized .composer__header {
	border-bottom: 0;
}

.composer__title {
	flex: 1;
	min-width: 0;
	font-weight: 600;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.composer__window-button,
.composer__chip-button,
.composer__copies {
	--sm-button-background: transparent;
	display: grid;
	place-items: center;
	min-height: 0;
	margin: 0;
	padding: 0;
	border: 0;
	background: none;
	color: var(--color-text-maxcontrast);
	font: inherit;
	cursor: pointer;
}

.composer__window-button {
	width: 32px;
	height: 32px;
	border-radius: var(--border-radius-element, 8px);
}

.composer__window-button:hover,
.composer__chip-button:hover,
.composer__copies:hover {
	--sm-button-background: var(--color-background-hover);
	--sm-button-color: var(--color-main-text);
	background: var(--color-background-hover);
	color: var(--color-main-text);
}

.composer__content {
	display: flex;
	flex-direction: column;
	flex: 1;
	min-height: 0;
	overflow-y: auto;
}

.composer__row {
	display: grid;
	grid-template-columns: 64px minmax(0, 1fr);
	align-items: center;
	min-height: 40px;
	border-bottom: 1px solid var(--color-border);
}

.composer__row-label {
	padding-inline-start: 14px;
	color: var(--color-text-maxcontrast);
}

.composer__from select,
.composer__subject {
	width: 100%;
	height: 38px;
	min-height: 0;
	margin: 0;
	padding: 0 8px 0 4px;
	border: 0;
	outline: none;
	background: none;
	box-shadow: none;
	color: var(--color-main-text);
	font: inherit;
}

.composer__from select:disabled {
	opacity: 1;
	color: var(--color-text-maxcontrast);
}

.composer__to {
	position: relative;
}

.composer__copies {
	position: absolute;
	top: 6px;
	inset-inline-end: 8px;
	padding: 0 8px;
	height: 28px;
	border-radius: var(--border-radius-element, 8px);
	font-size: 13px;
}

.composer__to :deep(.recipients__field) {
	padding-inline-end: 70px;
}

.composer__text {
	flex: 1;
	box-sizing: border-box;
	width: 100%;
	max-width: none;
	min-height: 200px;
	margin: 0;
	padding: 12px 14px;
	border: 0;
	border-radius: 0;
	outline: none;
	resize: none;
	background: none;
	box-shadow: none;
	color: var(--color-main-text);
	font: inherit;
	line-height: 1.5;
}

.composer__error {
	margin: 4px 14px;
	font-size: 13px;
	color: var(--color-error-text);
}

.composer__row--error {
	border-bottom-color: var(--color-error);
}

.composer__chip-link {
	border: none;
	background: none;
	padding: 0;
	color: var(--color-primary-element);
	text-decoration: underline;
	cursor: pointer;
	white-space: nowrap;
}

.composer__attachments {
	display: flex;
	flex-wrap: wrap;
	gap: 6px;
	margin: 0;
	padding: 8px 14px;
	list-style: none;
}

.composer__chip {
	display: inline-flex;
	align-items: center;
	gap: 6px;
	max-width: 100%;
	height: 32px;
	padding-inline: 10px 4px;
	border: 1px solid var(--color-border);
	border-radius: var(--border-radius-element, 8px);
}

.composer__chip-name {
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.composer__chip--off .composer__chip-name {
	text-decoration: line-through;
	color: var(--color-text-maxcontrast);
}

.composer__chip--error {
	border-color: var(--color-error);
}

.composer__chip-meta {
	color: var(--color-text-maxcontrast);
	font-size: 13px;
}

.composer__chip-button {
	width: 24px;
	height: 24px;
	border-radius: 6px;
}

.composer__footer {
	display: flex;
	align-items: center;
	gap: 8px;
	padding: 10px 14px;
	border-top: 1px solid var(--color-border);
}

.composer__editor {
	min-height: 220px;
}

.composer__status {
	margin-inline-start: auto;
	font-size: 12px;
	color: var(--color-text-maxcontrast);
	white-space: nowrap;
}

.composer__status--error {
	color: var(--color-error-text);
}

@media (max-width: 800px) {
	.composer,
	.composer--expanded {
		inset: 0;
		width: auto;
		height: auto;
		border-radius: 0;
	}

	.composer--minimized {
		inset: auto 0 0;
	}

	.composer__footer {
		flex-wrap: wrap;
	}
}
</style>
