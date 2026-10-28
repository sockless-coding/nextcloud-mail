<template>
	<div v-if="composer.expanded && !composer.minimized" class="composer-backdrop" />
	<!-- A window docked at the bottom, not a modal: the mailbox stays usable while writing.
	     Escape and clicks elsewhere do nothing, so a half-written message cannot vanish by
	     accident; Send and Discard are the only ways out. -->
	<section class="composer"
		:class="{ 'composer--minimized': composer.minimized, 'composer--expanded': composer.expanded && !composer.minimized }"
		role="dialog"
		aria-modal="false"
		:aria-label="title"
		@keydown.ctrl.enter.prevent="submit"
		@keydown.meta.enter.prevent="submit">
		<header class="composer__header" @dblclick="composer.minimized = !composer.minimized">
			<span class="composer__title">{{ composer.minimized && draft.subject ? draft.subject : title }}</span>
			<button type="button"
				class="composer__window-button"
				:aria-label="composer.minimized ? t('sockless_mail', 'Restore') : t('sockless_mail', 'Minimize')"
				:title="composer.minimized ? t('sockless_mail', 'Restore') : t('sockless_mail', 'Minimize')"
				@click="composer.minimized = !composer.minimized">
				<NcIconSvgWrapper :path="composer.minimized ? mdiWindowRestore : mdiWindowMinimize" :size="18" />
			</button>
			<button v-if="!composer.minimized"
				type="button"
				class="composer__window-button"
				:aria-label="composer.expanded ? t('sockless_mail', 'Dock') : t('sockless_mail', 'Pop out')"
				:title="composer.expanded ? t('sockless_mail', 'Dock') : t('sockless_mail', 'Pop out')"
				@click="composer.expanded = !composer.expanded">
				<NcIconSvgWrapper :path="composer.expanded ? mdiArrowCollapse : mdiArrowExpand" :size="18" />
			</button>
			<button type="button"
				class="composer__window-button"
				:aria-label="t('sockless_mail', 'Discard')"
				:title="t('sockless_mail', 'Discard')"
				@click="discard">
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
			<label class="hidden-visually" for="composer-text">{{ t('sockless_mail', 'Message') }}</label>
			<textarea id="composer-text"
				ref="body"
				v-model="draft.text"
				class="composer__text"
				:placeholder="t('sockless_mail', 'Write your message')" />
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
					<span v-else-if="!upload.id" class="composer__chip-meta">{{ Math.round(upload.progress * 100) }} %</span>
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
			<NcButton variant="primary" :disabled="uploading || sending" @click="submit">
				<template #icon>
					<NcLoadingIcon v-if="sending" :size="20" />
					<NcIconSvgWrapper v-else :path="mdiSend" />
				</template>
				{{ uploading ? t('sockless_mail', 'Uploading…') : t('sockless_mail', 'Send') }}
			</NcButton>
			<input ref="fileInput"
				type="file"
				multiple
				hidden
				@change="onFiles">
			<NcButton variant="tertiary" @click="fileInput.click()">
				<template #icon>
					<NcIconSvgWrapper :path="mdiPaperclip" />
				</template>
				{{ t('sockless_mail', 'Attach files') }}
			</NcButton>
			<span class="composer__hint">{{ t('sockless_mail', 'Ctrl+Enter to send') }}</span>
		</footer>
	</section>

	<NcDialog v-if="confirmDiscard"
		:name="t('sockless_mail', 'Discard this message?')"
		:message="t('sockless_mail', 'What you wrote and attached will be lost.')"
		:buttons="discardButtons"
		:open="true"
		@update:open="(open) => !open && discardClosed()" />
</template>

<script setup>
import { mdiArrowCollapse, mdiArrowExpand, mdiClose, mdiPaperclip, mdiSend, mdiUndo, mdiWindowMinimize, mdiWindowRestore } from '@mdi/js'
import { t } from '@nextcloud/l10n'
import NcButton from '@nextcloud/vue/components/NcButton'
import NcDialog from '@nextcloud/vue/components/NcDialog'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import NcNoteCard from '@nextcloud/vue/components/NcNoteCard'
import { computed, nextTick, onMounted, ref } from 'vue'
import { accountsState } from '../store/accounts.js'
import { attach, closeComposer, composer, detach, hasContent, send } from '../store/compose.js'
import RecipientInput from './RecipientInput.vue'

const draft = composer.draft
const fileInput = ref(null)
const body = ref(null)
const showCopies = ref(!!(draft.cc || draft.bcc))
const errors = ref({})
const generalError = ref('')
const sending = ref(false)
const confirmDiscard = ref(false)

const title = computed(() => ({
	reply: t('sockless_mail', 'Reply'),
	replyAll: t('sockless_mail', 'Reply to all'),
	forward: t('sockless_mail', 'Forward'),
})[draft.source?.mode] ?? t('sockless_mail', 'New message'))

const uploading = computed(() => draft.uploads.some((u) => !u.id && !u.error))

async function submit() {
	if (uploading.value || sending.value) {
		return
	}
	errors.value = {}
	generalError.value = ''
	sending.value = true
	try {
		await send()
		closeComposer(true)
	} catch (e) {
		errors.value = e.fieldErrors ?? {}
		if (errors.value.cc || errors.value.bcc) {
			showCopies.value = true
		}
		if (!Object.keys(errors.value).length) {
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
		closeComposer(false)
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
		closeComposer(false)
	}
}

const discardButtons = computed(() => [
	{ label: t('sockless_mail', 'Keep writing') },
	{ label: t('sockless_mail', 'Discard'), variant: 'error', callback: () => { discardChosen = true } },
])

function onFiles(event) {
	for (const file of event.target.files) {
		attach(file)
	}
	event.target.value = ''
}

// A reply starts in the text, above the quote; a new message in the To field.
onMounted(() => nextTick(() => {
	if (draft.source) {
		body.value?.focus()
		body.value?.setSelectionRange(0, 0)
	} else {
		document.getElementById(document.querySelector('.composer .recipients__label')?.getAttribute('for'))?.focus()
	}
}))
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

.composer__hint {
	margin-inline-start: auto;
	font-size: 12px;
	color: var(--color-text-maxcontrast);
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

	.composer__hint {
		display: none;
	}
}
</style>
