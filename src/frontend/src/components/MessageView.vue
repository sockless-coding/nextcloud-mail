<template>
	<div class="message-view">
		<div v-if="open.loading" class="message-view__center">
			<NcLoadingIcon :size="44" />
		</div>
		<NcEmptyContent v-else-if="open.error"
			:name="t('sockless_mail', 'Could not open the message')"
			:description="open.error.message" />
		<article v-else-if="message">
			<header class="message-view__header">
				<div class="message-view__title">
					<button v-if="readingPane === 'off'"
						class="message-view__icon-button"
						:aria-label="t('sockless_mail', 'Back to the list')"
						:title="t('sockless_mail', 'Back to the list')"
						@click="closeMessage">
						<NcIconSvgWrapper :path="mdiArrowLeft" />
					</button>
					<h2>{{ message.subject || t('sockless_mail', '(no subject)') }}</h2>
				</div>

				<!-- Sender card: who, to whom, when, and the actions on this message. -->
				<div class="message-view__card">
					<NcAvatar :display-name="senderName" is-no-user disable-menu :size="44" />
					<div class="message-view__people">
						<div class="message-view__sender">
							<strong>{{ senderName }}</strong>
							<span v-if="sender?.name" class="message-view__address">{{ " " }}&lt;{{ sender.address }}&gt;</span>
						</div>
						<button class="message-view__recipients"
							:aria-expanded="String(details)"
							:title="details ? t('sockless_mail', 'Hide details') : t('sockless_mail', 'Show details')"
							@click="details = !details">
							<span class="message-view__recipients-text">
								{{ t('sockless_mail', 'To: {recipients}', { recipients: formatList(message.to) }) }}
								<template v-if="message.cc.length">
									· {{ t('sockless_mail', 'Cc: {recipients}', { recipients: formatList(message.cc) }) }}
								</template>
							</span>
							<NcIconSvgWrapper :path="details ? mdiChevronUp : mdiChevronDown" :size="16" />
						</button>
						<NcDateTime class="message-view__date" :timestamp="Date.parse(message.date)" :format="{ dateStyle: 'full', timeStyle: 'short' }" :relative-time="false" />
					</div>
					<div class="message-view__actions">
						<NcIconSvgWrapper v-if="message.flagged" :path="mdiFlag" class="message-view__flag" :name="t('sockless_mail', 'Flagged')" />
						<button v-if="isDraft"
							class="message-view__icon-button"
							:aria-label="t('sockless_mail', 'Continue editing')"
							:title="t('sockless_mail', 'Continue editing')"
							@click="continueDraft">
							<NcIconSvgWrapper :path="mdiPencilOutline" />
						</button>
						<button v-for="mode in REPLIES"
							:key="mode.id"
							class="message-view__icon-button"
							:aria-label="mode.label"
							:title="mode.label"
							@click="reply(mode.id)">
							<NcIconSvgWrapper :path="mode.icon" />
						</button>
						<NcActions :aria-label="t('sockless_mail', 'More actions')">
							<NcActionButton close-after-click @click="toggleSeen(where, message)">
								<template #icon>
									<NcIconSvgWrapper :path="message.seen ? mdiEmailOutline : mdiEmailOpenOutline" />
								</template>
								{{ message.seen ? t('sockless_mail', 'Mark as unread') : t('sockless_mail', 'Mark as read') }}
							</NcActionButton>
							<NcActionButton close-after-click @click="toggleFlagged(where, message)">
								<template #icon>
									<NcIconSvgWrapper :path="message.flagged ? mdiFlagRemoveOutline : mdiFlagOutline" />
								</template>
								{{ message.flagged ? t('sockless_mail', 'Remove flag') : t('sockless_mail', 'Flag') }}
							</NcActionButton>
							<NcActionButton close-after-click @click="askMove(where, [message.id])">
								<template #icon>
									<NcIconSvgWrapper :path="mdiFolderMoveOutline" />
								</template>
								{{ t('sockless_mail', 'Move…') }}
							</NcActionButton>
							<NcActionButton close-after-click @click="remove(where, [message.id])">
								<template #icon>
									<NcIconSvgWrapper :path="mdiDeleteOutline" />
								</template>
								{{ t('sockless_mail', 'Delete') }}
							</NcActionButton>
						</NcActions>
					</div>
				</div>

				<dl v-if="details" class="message-view__details">
					<template v-for="row in detailRows" :key="row.label">
						<dt>{{ row.label }}</dt>
						<dd>{{ row.value }}</dd>
					</template>
				</dl>
			</header>

			<NcNoteCard v-if="message.hasRemoteContent && !message.remoteContentAllowed" type="info">
				<div class="message-view__remote">
					<span>{{ t('sockless_mail', 'Remote images are blocked to protect your privacy.') }}</span>
					<NcButton variant="secondary" @click="openMessage(where, message.id, true)">
						{{ t('sockless_mail', 'Show images') }}
					</NcButton>
				</div>
			</NcNoteCard>

			<section v-if="message.attachments.length" class="message-view__attachments">
				<h3>{{ n('sockless_mail', '{count} attachment', '{count} attachments', message.attachments.length, { count: message.attachments.length }) }}</h3>
				<ul>
					<li v-for="attachment in message.attachments" :key="attachment.partId">
						<a :href="partUrl(where.accountId, where.folderId, message.id, attachment.partId, true)"
							class="message-view__attachment"
							:title="t('sockless_mail', 'Download {name}', { name: attachment.fileName })"
							download>
							<span class="message-view__file-type" :style="{ background: fileType(attachment).color }" aria-hidden="true">
								{{ fileType(attachment).label }}
							</span>
							<span class="message-view__file">
								<span class="message-view__attachment-name">{{ attachment.fileName }}</span>
								<span v-if="attachment.size" class="message-view__attachment-size">{{ formatFileSize(attachment.size) }}</span>
							</span>
							<NcIconSvgWrapper :path="mdiDownload" :size="18" class="message-view__download" />
						</a>
					</li>
				</ul>
			</section>

			<!-- In a dark theme the mail is recoloured; its original white page is one click away. -->
			<div class="message-view__paper" :class="{ 'message-view__paper--original': original || !darkTheme }">
				<button v-if="darkTheme"
					class="message-view__icon-button message-view__colors"
					:aria-pressed="String(original)"
					:aria-label="original ? t('sockless_mail', 'Show in dark colours') : t('sockless_mail', 'Show in original colours')"
					:title="original ? t('sockless_mail', 'Show in dark colours') : t('sockless_mail', 'Show in original colours')"
					@click="original = !original">
					<NcIconSvgWrapper :path="original ? mdiWeatherNight : mdiWhiteBalanceSunny" :size="18" />
				</button>
				<MessageBody :key="`${message.id}/${message.remoteContentAllowed}/${original}`" :html="message.html" :original="original" />
			</div>

			<div class="message-view__quick">
				<NcButton v-if="isDraft" variant="primary" @click="continueDraft">
					<template #icon>
						<NcIconSvgWrapper :path="mdiPencilOutline" />
					</template>
					{{ t('sockless_mail', 'Continue editing') }}
				</NcButton>
				<NcButton v-for="mode in isDraft ? [] : REPLIES" :key="mode.id" @click="reply(mode.id)">
					<template #icon>
						<NcIconSvgWrapper :path="mode.icon" />
					</template>
					{{ mode.label }}
				</NcButton>
			</div>
		</article>
	</div>
</template>

<script setup>
import { mdiArrowLeft, mdiChevronDown, mdiChevronUp, mdiDeleteOutline, mdiDownload, mdiEmailOpenOutline, mdiEmailOutline, mdiFlag, mdiFlagOutline, mdiFlagRemoveOutline, mdiFolderMoveOutline, mdiPencilOutline, mdiReply, mdiReplyAll, mdiShare, mdiWeatherNight, mdiWhiteBalanceSunny } from '@mdi/js'
import { showError } from '@nextcloud/dialogs/toast'
import { getCanonicalLocale, n, t } from '@nextcloud/l10n'
import NcActionButton from '@nextcloud/vue/components/NcActionButton'
import NcActions from '@nextcloud/vue/components/NcActions'
import NcAvatar from '@nextcloud/vue/components/NcAvatar'
import NcButton from '@nextcloud/vue/components/NcButton'
import NcDateTime from '@nextcloud/vue/components/NcDateTime'
import NcEmptyContent from '@nextcloud/vue/components/NcEmptyContent'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import NcNoteCard from '@nextcloud/vue/components/NcNoteCard'
import { computed, ref, watch } from 'vue'
import { askMove, remove, toggleFlagged, toggleSeen } from '../store/actions.js'
import { composeEdit, composeReply } from '../store/compose.js'
import { readingPane } from '../store/layout.js'
import { pageColors } from '../mailColors.js'
import { closeMessage, mailboxState, openMessage, partUrl } from '../store/mailbox.js'
import MessageBody from './MessageBody.vue'

const REPLIES = [
	{ id: 'reply', icon: mdiReply, label: t('sockless_mail', 'Reply') },
	{ id: 'replyAll', icon: mdiReplyAll, label: t('sockless_mail', 'Reply all') },
	{ id: 'forward', icon: mdiShare, label: t('sockless_mail', 'Forward') },
]

const open = mailboxState.open
// Where the open message lives; it can be another folder than the selected one (search, unified inbox).
const where = computed(() => ({ accountId: open.accountId, folderId: open.folderId }))
const message = computed(() => open.message)
const sender = computed(() => message.value?.from[0] ?? null)
const senderName = computed(() => sender.value?.name || sender.value?.address || t('sockless_mail', 'Unknown sender'))
const details = ref(false)
/** A saved draft: it is continued rather than replied to. */
const isDraft = computed(() => mailboxState.folders[open.accountId]?.list.find((f) => f.id === open.folderId)?.specialUse === 'drafts')

const darkTheme = pageColors().dark
/** This message in the colours it was sent in, rather than recoloured for the dark theme. */
const original = ref(false)

watch(() => open.id, () => {
	details.value = false
	original.value = false
})

/** Every address in full, for the details under the sender card. */
const detailRows = computed(() => {
	const full = (list) => list.map((a) => (a.name ? `${a.name} <${a.address}>` : a.address)).join(', ')
	const m = message.value
	return [
		{ label: t('sockless_mail', 'From'), value: full(m.from) },
		{ label: t('sockless_mail', 'To'), value: full(m.to) },
		m.cc.length && { label: t('sockless_mail', 'Cc'), value: full(m.cc) },
		m.replyTo.length && { label: t('sockless_mail', 'Reply to'), value: full(m.replyTo) },
	].filter(Boolean)
})

async function reply(mode) {
	try {
		await composeReply(mode)
	} catch (e) {
		showError(t('sockless_mail', 'Could not prepare the message: {error}', { error: e.message }))
	}
}

async function continueDraft() {
	try {
		await composeEdit()
	} catch (e) {
		showError(t('sockless_mail', 'Could not open the draft: {error}', { error: e.message }))
	}
}

function formatList(addresses) {
	return addresses.map((a) => a.name || a.address).join(', ')
}

/** A badge per kind of file, coloured like the office suites colour them. */
const FILE_TYPES = [
	{ test: /pdf/, color: '#c4314b' },
	{ test: /word|opendocument\.text|rtf|msword/, color: '#2b579a' },
	{ test: /sheet|excel|csv|opendocument\.spreadsheet/, color: '#1d7044' },
	{ test: /presentation|powerpoint|opendocument\.presentation/, color: '#c43e1c' },
	{ test: /^image\//, color: '#7346c2' },
	{ test: /zip|compressed|tar|gzip|7z|rar/, color: '#6b5b2d' },
	{ test: /^audio\/|^video\//, color: '#0e7584' },
	{ test: /message\/rfc822/, color: '#0f62d6' },
]

function fileType(attachment) {
	const extension = attachment.fileName.includes('.') ? attachment.fileName.split('.').pop() : ''
	const label = (extension && extension.length <= 4 ? extension : attachment.contentType.split('/').pop().slice(0, 4)).toUpperCase()
	const color = FILE_TYPES.find((f) => f.test.test(attachment.contentType))?.color ?? '#4f5d75'
	return { label, color }
}

const UNITS = ['byte', 'kilobyte', 'megabyte', 'gigabyte']

function formatFileSize(bytes) {
	const exponent = Math.min(Math.floor(Math.log(Math.max(bytes, 1)) / Math.log(1024)), UNITS.length - 1)
	return new Intl.NumberFormat(getCanonicalLocale(), { style: 'unit', unit: UNITS[exponent], unitDisplay: 'short', maximumFractionDigits: 1 })
		.format(bytes / 1024 ** exponent)
}
</script>

<style scoped>
.message-view {
	padding: 16px 28px 28px;
	max-width: 1000px;
}

.message-view__center {
	display: flex;
	justify-content: center;
	padding: 48px;
}

.message-view__title {
	display: flex;
	align-items: flex-start;
	gap: 8px;
}

.message-view__title h2 {
	margin: 4px 0 14px;
	font-size: 21px;
	font-weight: 600;
	line-height: 1.3;
	overflow-wrap: anywhere;
}

.message-view__icon-button {
	--sm-button-background: transparent;
	display: grid;
	place-items: center;
	width: 34px;
	height: 34px;
	min-height: 0;
	margin: 0;
	padding: 0;
	flex: none;
	border: 0;
	border-radius: var(--border-radius-element, 8px);
	background: none;
	color: var(--color-main-text);
	cursor: pointer;
}

.message-view__icon-button:hover {
	--sm-button-background: var(--color-background-hover);
	background: var(--color-background-hover);
}

.message-view__icon-button:focus-visible,
.message-view__recipients:focus-visible {
	outline: 2px solid var(--color-primary-element);
}

.message-view__card {
	display: flex;
	align-items: flex-start;
	gap: 12px;
	padding: 14px 12px 14px 16px;
	border: 1px solid var(--color-border);
	border-radius: var(--border-radius-container, 12px);
}

.message-view__people {
	flex: 1;
	min-width: 0;
}

.message-view__sender {
	overflow-wrap: anywhere;
}

.message-view__address,
.message-view__date {
	color: var(--color-text-maxcontrast);
	font-size: 13px;
}

.message-view__recipients {
	--sm-button-background: transparent;
	display: flex;
	align-items: center;
	gap: 2px;
	max-width: 100%;
	min-height: 0;
	margin: 0;
	padding: 0;
	border: 0;
	border-radius: 4px;
	background: none;
	color: var(--color-text-maxcontrast);
	font: inherit;
	font-size: 13px;
	text-align: start;
	cursor: pointer;
}

.message-view__recipients:hover {
	color: var(--color-main-text);
}

.message-view__recipients-text {
	min-width: 0;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.message-view__actions {
	display: flex;
	align-items: center;
	gap: 2px;
	flex: none;
}

.message-view__flag {
	margin-inline-end: 4px;
	color: var(--color-error-text);
}

.message-view__details {
	display: grid;
	grid-template-columns: auto minmax(0, 1fr);
	gap: 4px 12px;
	margin: 8px 0 0;
	padding: 10px 16px;
	border-radius: var(--border-radius-element, 8px);
	background: var(--color-background-hover);
	font-size: 13px;
}

.message-view__details dt {
	color: var(--color-text-maxcontrast);
}

.message-view__details dd {
	margin: 0;
	overflow-wrap: anywhere;
}

.message-view__remote {
	display: flex;
	align-items: center;
	gap: 12px;
	flex-wrap: wrap;
}

.message-view__attachments {
	margin-top: 16px;
}

.message-view__attachments h3 {
	margin: 0 0 8px;
	font-size: 13px;
	font-weight: 600;
	color: var(--color-text-maxcontrast);
}

.message-view__attachments ul {
	display: grid;
	grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
	gap: 8px;
	margin: 0;
	padding: 0;
	list-style: none;
}

.message-view__attachment {
	display: flex;
	align-items: center;
	gap: 10px;
	padding: 8px 10px 8px 8px;
	border: 1px solid var(--color-border);
	border-radius: var(--border-radius-element, 8px);
	color: var(--color-main-text);
}

.message-view__attachment:hover,
.message-view__attachment:focus-visible {
	background: var(--color-background-hover);
}

.message-view__file-type {
	display: grid;
	place-items: center;
	width: 34px;
	height: 40px;
	flex: none;
	border-radius: 4px;
	font-size: 10px;
	font-weight: 700;
	letter-spacing: 0.02em;
	color: #fff;
}

.message-view__file {
	display: flex;
	flex-direction: column;
	flex: 1;
	min-width: 0;
}

.message-view__attachment-name {
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.message-view__attachment-size {
	font-size: 12px;
	color: var(--color-text-maxcontrast);
}

.message-view__download {
	color: var(--color-text-maxcontrast);
	opacity: 0;
}

.message-view__attachment:hover .message-view__download,
.message-view__attachment:focus-visible .message-view__download {
	opacity: 1;
}

.message-view__paper {
	position: relative;
	margin-top: 16px;
	padding: 8px 12px;
	border: 1px solid var(--color-border);
	border-radius: var(--border-radius-container, 12px);
	background: var(--color-main-background);
}

.message-view__paper--original {
	background: #fff;
}

/* Over the mail's top corner, out of the way until hovered. */
.message-view__colors {
	position: absolute;
	top: 6px;
	inset-inline-end: 6px;
	z-index: 1;
	opacity: 0.6;
	--sm-button-background: var(--color-main-background);
	background: var(--color-main-background);
	box-shadow: 0 0 0 1px var(--color-border);
}

.message-view__colors:hover,
.message-view__colors:focus-visible {
	opacity: 1;
}

.message-view__paper--original .message-view__colors {
	--sm-button-background: #fff;
	--sm-button-color: #222;
	background: #fff;
	color: #222;
}

.message-view__quick {
	display: flex;
	flex-wrap: wrap;
	gap: 8px;
	margin-top: 20px;
}

@media (max-width: 800px) {
	.message-view {
		padding: 12px 16px 24px;
	}

	.message-view__card {
		flex-wrap: wrap;
	}

	.message-view__actions {
		width: 100%;
		justify-content: flex-end;
	}
}
</style>
