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
						<NcIconSvgWrapper :path="mdiArrowLeft" :size="20" />
					</button>
					<h2>{{ message.subject || t('sockless_mail', '(no subject)') }}</h2>
					<TagChips :tags="tagsOf(message)" />
					<span v-if="location" class="message-view__location">
						<span class="message-view__location-dot" :style="{ background: location.color }" aria-hidden="true" />
						{{ location.label }}
					</span>
				</div>

				<!-- Who, to whom, when, and the actions on this message. -->
				<div class="message-view__meta">
					<NcAvatar :display-name="senderName"
						:url="bimiUrl(message)"
						is-no-user
						disable-menu
						:size="40" />
					<div class="message-view__people">
						<div class="message-view__sender">
							<strong>{{ senderName }}</strong>
							<span v-if="sender?.name" class="message-view__address">{{ " " }}&lt;{{ senderAddress }}&gt;</span>
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
					</div>
					<div class="message-view__aside">
						<div class="message-view__actions" role="toolbar" :aria-label="t('sockless_mail', 'Message actions')">
							<button v-if="isDraft"
								class="message-view__icon-button"
								:aria-label="t('sockless_mail', 'Continue editing')"
								:title="t('sockless_mail', 'Continue editing')"
								@click="continueDraft">
								<NcIconSvgWrapper :path="mdiPencilOutline" :size="20" />
							</button>
							<button v-for="mode in REPLIES"
								:key="mode.id"
								class="message-view__icon-button"
								:aria-label="mode.label"
								:title="mode.label"
								@click="reply(mode.id)">
								<NcIconSvgWrapper :path="mode.icon" :size="20" />
							</button>
							<button class="message-view__icon-button"
								:aria-label="t('sockless_mail', 'Tags')"
								:title="t('sockless_mail', 'Tags')"
								@click="askTags([{ ...where, message }])">
								<NcIconSvgWrapper :path="mdiTagOutline" :size="20" />
							</button>
							<button v-if="!windowMode"
								class="message-view__icon-button"
								:aria-label="t('sockless_mail', 'Open in new window')"
								:title="t('sockless_mail', 'Open in new window')"
								@click="openMessageWindow(where, message.id)">
								<NcIconSvgWrapper :path="mdiOpenInNew" :size="20" />
							</button>
							<!-- In a dark theme the mail is recoloured; its original white page is one click away. -->
							<button v-if="darkTheme"
								class="message-view__icon-button"
								:aria-pressed="String(original)"
								:aria-label="original ? t('sockless_mail', 'Show in dark colours') : t('sockless_mail', 'Show in original colours')"
								:title="original ? t('sockless_mail', 'Show in dark colours') : t('sockless_mail', 'Show in original colours')"
								@click="original = !original">
								<NcIconSvgWrapper :path="original ? mdiWeatherNight : mdiWhiteBalanceSunny" :size="20" />
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
								<NcActionButton v-if="tasksState.available"
									close-after-click
									@click="dialogs.createTask = { where, message }">
									<template #icon>
										<NcIconSvgWrapper :path="mdiClipboardCheckOutline" />
									</template>
									{{ t('sockless_mail', 'Create task…') }}
								</NcActionButton>
								<NcActionButton close-after-click @click="createRuleFrom(where, message)">
									<template #icon>
										<NcIconSvgWrapper :path="mdiFilterPlusOutline" />
									</template>
									{{ t('sockless_mail', 'Create rule…') }}
								</NcActionButton>
								<NcActionButton v-if="message.from[0]"
									close-after-click
									@click="dialogs.addContact = { name: message.from[0].name, address: message.from[0].address }">
									<template #icon>
										<NcIconSvgWrapper :path="mdiAccountPlusOutline" />
									</template>
									{{ t('sockless_mail', 'Add sender to contacts') }}
								</NcActionButton>
								<NcActionSeparator />
								<NcActionButton close-after-click @click="rawView = 'headers'">
									<template #icon>
										<NcIconSvgWrapper :path="mdiCodeTags" />
									</template>
									{{ t('sockless_mail', 'View headers') }}
								</NcActionButton>
								<NcActionButton close-after-click @click="rawView = 'source'">
									<template #icon>
										<NcIconSvgWrapper :path="mdiFileCodeOutline" />
									</template>
									{{ t('sockless_mail', 'View source') }}
								</NcActionButton>
								<NcActionLink :href="emlUrl(where, message.id)" download close-after-click>
									<template #icon>
										<NcIconSvgWrapper :path="mdiDownload" />
									</template>
									{{ t('sockless_mail', 'Download message (.eml)') }}
								</NcActionLink>
							</NcActions>
						</div>
						<div class="message-view__when">
							<NcIconSvgWrapper v-if="message.flagged"
								:path="mdiFlag"
								:size="16"
								inline
								class="message-view__flag"
								:name="t('sockless_mail', 'Flagged')" />
							<NcDateTime :timestamp="Date.parse(message.date)" :format="{ dateStyle: 'medium', timeStyle: 'short' }" :relative-time="false" />
						</div>
					</div>
				</div>

				<dl v-if="details" class="message-view__details">
					<template v-for="row in detailRows" :key="row.label">
						<dt>{{ row.label }}</dt>
						<dd>{{ row.value }}</dd>
					</template>
				</dl>
			</header>

			<section v-if="warning"
				class="message-view__warning"
				:class="`message-view__warning--${warning.level}`"
				role="alert">
				<NcIconSvgWrapper :path="mdiShieldAlertOutline" :size="22" class="message-view__warning-icon" />
				<div class="message-view__warning-text">
					<strong>{{ warningTitle(warning) }}</strong>
					<ul>
						<li v-for="(reason, index) in warning.reasons" :key="index">
							{{ reasonText(reason) }}
						</li>
					</ul>
					<p v-if="warning.asciiDomain" class="message-view__warning-ascii">
						{{ t('sockless_mail', 'The domain is spelled {ascii} in plain letters.', { ascii: warning.asciiDomain }) }}
					</p>
				</div>
				<NcButton variant="secondary" :disabled="trusting" @click="trust(warning.registered)">
					{{ t('sockless_mail', 'Trust {domain}', { domain: warning.registered }) }}
				</NcButton>
			</section>

			<div v-if="message.hasRemoteContent && !message.remoteContentAllowed" class="message-view__notice" role="status">
				<NcIconSvgWrapper :path="mdiImageOffOutline" :size="18" inline />
				<span>{{ t('sockless_mail', 'Remote images are blocked to protect your privacy.') }}</span>
				<button class="message-view__text-button" @click="openMessage(where, message.id, true)">
					{{ t('sockless_mail', 'Show images') }}
				</button>
			</div>

			<InvitationCard v-if="calendarPart"
				:key="`${message.id}/${calendarPart.partId}`"
				:where="where"
				:message-id="message.id"
				:part="calendarPart"
				:account-address="accountAddress" />

			<section v-if="message.attachments.length" class="message-view__attachments">
				<h3>
					{{ n('sockless_mail', '{count} attachment', '{count} attachments', message.attachments.length, { count: message.attachments.length }) }}
					<template v-if="message.attachments.length > 1 && attachmentsSize">
						· {{ formatFileSize(attachmentsSize) }}
					</template>
					<button v-if="message.attachments.length > 1"
						type="button"
						class="message-view__save-all"
						@click="saveToFiles(message.attachments)">
						{{ t('sockless_mail', 'Save all to Files') }}
					</button>
				</h3>
				<ul>
					<li v-for="attachment in message.attachments" :key="attachment.partId" class="message-view__attachment-card">
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
						<button type="button"
							class="message-view__save"
							:aria-label="t('sockless_mail', 'Save {name} to Files', { name: attachment.fileName })"
							:title="t('sockless_mail', 'Save to Files')"
							@click="saveToFiles([attachment])">
							<NcIconSvgWrapper :path="mdiFolderDownloadOutline" :size="18" />
						</button>
					</li>
				</ul>
			</section>

			<div class="message-view__body" :class="{ 'message-view__body--paper': darkTheme && original }">
				<MessageBody :key="`${message.id}/${message.remoteContentAllowed}/${original}`"
					:html="message.html"
					:original="original"
					@deceptive-link="askDeceptiveLink" />
			</div>

			<MessageSourceDialog v-if="rawView"
				:where="where"
				:message-id="message.id"
				:subject="message.subject"
				:kind="rawView"
				@close="rawView = null" />
			<DeceptiveLinkDialog v-if="deceptiveLink"
				:key="deceptiveLink.id"
				:link="deceptiveLink"
				@close="(id) => deceptiveLink?.id === id && (deceptiveLink = null)" />
		</article>
	</div>
</template>

<script setup>
import { mdiAccountPlusOutline, mdiArrowLeft, mdiChevronDown, mdiChevronUp, mdiClipboardCheckOutline, mdiCodeTags, mdiDeleteOutline, mdiDownload, mdiEmailOpenOutline, mdiEmailOutline, mdiFileCodeOutline, mdiFilterPlusOutline, mdiFlag, mdiFlagOutline, mdiFlagRemoveOutline, mdiFolderDownloadOutline, mdiFolderMoveOutline, mdiImageOffOutline, mdiOpenInNew, mdiPencilOutline, mdiReply, mdiReplyAll, mdiShare, mdiShieldAlertOutline, mdiTagOutline, mdiWeatherNight, mdiWhiteBalanceSunny } from '@mdi/js'
import { showError } from '@nextcloud/dialogs/toast'
import { n, t } from '@nextcloud/l10n'
import NcActionButton from '@nextcloud/vue/components/NcActionButton'
import NcActionLink from '@nextcloud/vue/components/NcActionLink'
import NcActions from '@nextcloud/vue/components/NcActions'
import NcActionSeparator from '@nextcloud/vue/components/NcActionSeparator'
import NcAvatar from '@nextcloud/vue/components/NcAvatar'
import NcButton from '@nextcloud/vue/components/NcButton'
import NcDateTime from '@nextcloud/vue/components/NcDateTime'
import NcEmptyContent from '@nextcloud/vue/components/NcEmptyContent'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import { computed, ref, watch } from 'vue'
import { bimiUrl } from '../bimi.js'
import { accountColor, accountsState } from '../store/accounts.js'
import { askMove, askTags, createRuleFrom, dialogs, remove, toggleFlagged, toggleSeen } from '../store/actions.js'
import { composeEdit, composeReply } from '../store/compose.js'
import { primaryCalendarPart } from '../store/calendar.js'
import { saveAttachmentsToFiles } from '../store/files.js'
import { readingPane } from '../store/layout.js'
import { tagsOf } from '../store/tags.js'
import { tasksState } from '../store/tasks.js'
import { formatFileSize } from '../fileSize.js'
import { messageWarning, reasonText, trustDomain, warningTitle } from '../store/senders.js'
import { pageColors } from '../mailColors.js'
import { closeMessage, emlUrl, mailboxState, openMessage, partUrl } from '../store/mailbox.js'
import { openMessageWindow, windowMode } from '../store/windows.js'
import DeceptiveLinkDialog from './DeceptiveLinkDialog.vue'
import InvitationCard from './InvitationCard.vue'
import MessageBody from './MessageBody.vue'
import MessageSourceDialog from './MessageSourceDialog.vue'
import TagChips from './TagChips.vue'

const REPLIES = [
	{ id: 'reply', icon: mdiReply, label: t('sockless_mail', 'Reply') },
	{ id: 'replyAll', icon: mdiReplyAll, label: t('sockless_mail', 'Reply all') },
	{ id: 'forward', icon: mdiShare, label: t('sockless_mail', 'Forward') },
]

const open = mailboxState.open
// Where the open message lives; it can be another folder than the selected one (search, unified views).
const where = computed(() => ({ accountId: open.accountId, folderId: open.folderId }))
const message = computed(() => open.message)
const warning = computed(() => messageWarning(message.value))
const trusting = ref(false)
/** A clicked link that shows another domain than it leads to: { href, shows, leads } */
const deceptiveLink = ref(null)
let deceptiveLinks = 0
/** A fresh dialog per click: a dialog still closing (NcDialog reports it 300 ms later) is not reused. */
function askDeceptiveLink(link) {
	deceptiveLink.value = { ...link, id: ++deceptiveLinks }
}
watch(() => message.value?.id, () => { deceptiveLink.value = null })

async function trust(domain) {
	trusting.value = true
	try {
		await trustDomain(domain)
	} catch (e) {
		showError(t('sockless_mail', 'Could not trust {domain}: {error}', { domain, error: e.message }))
	} finally {
		trusting.value = false
	}
}
const sender = computed(() => message.value?.from[0] ?? null)
/** A look-alike international domain reads as what it imitates; show it as spelled then. */
const senderAddress = computed(() => {
	const address = sender.value?.address ?? ''
	const ascii = warning.value?.asciiDomain
	return ascii ? `${address.slice(0, address.lastIndexOf('@') + 1)}${ascii}` : address
})
const senderName = computed(() => sender.value?.name || sender.value?.address || t('sockless_mail', 'Unknown sender'))
const details = ref(false)
/** A saved draft: it is continued rather than replied to. */
const isDraft = computed(() => mailboxState.folders[open.accountId]?.list.find((f) => f.id === open.folderId)?.specialUse === 'drafts')

/** "Account › Folder" beside the subject, in the account's colour. */
const location = computed(() => {
	const account = accountsState.accounts.find((a) => a.id === open.accountId)
	const folder = mailboxState.folders[open.accountId]?.list.find((f) => f.id === open.folderId)
	if (!account || !folder) {
		return null
	}
	const folderName = folder.specialUse === 'inbox' ? t('sockless_mail', 'Inbox') : folder.name
	return { label: `${account.displayName} › ${folderName}`, color: accountColor(account.id) }
})

const darkTheme = pageColors().dark
/** This message in the colours it was sent in, rather than recoloured for the dark theme. */
const original = ref(false)
/** 'headers' or 'source' while that dialog is open. */
const rawView = ref(null)

watch(() => open.id, () => {
	details.value = false
	original.value = false
	rawView.value = null
})

/** Every address in full, for the details under the sender. */
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

const calendarPart = computed(() => primaryCalendarPart(message.value.calendarParts ?? []))
const accountAddress = computed(() => accountsState.accounts.find((a) => a.id === where.value.accountId)?.emailAddress ?? '')

const attachmentsSize = computed(() => message.value.attachments.reduce((sum, a) => sum + (a.size ?? 0), 0))

async function reply(mode) {
	try {
		await composeReply(mode)
	} catch (e) {
		showError(t('sockless_mail', 'Could not prepare the message: {error}', { error: e.message }))
	}
}

async function saveToFiles(attachments) {
	try {
		await saveAttachmentsToFiles(where.value, message.value.id, attachments)
	} catch (e) {
		showError(t('sockless_mail', 'Could not open Files: {error}', { error: e.message }))
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
</script>

<style scoped>
.message-view {
	container-type: inline-size;
	max-width: 1040px;
	padding: 20px 32px 32px;
}

.message-view__center {
	display: flex;
	justify-content: center;
	padding: 48px;
}

.message-view__header {
	padding-bottom: 16px;
	border-bottom: 1px solid var(--color-border);
}

.message-view__title {
	display: flex;
	align-items: center;
	flex-wrap: wrap;
	gap: 6px 10px;
	margin-bottom: 18px;
}

.message-view__title h2 {
	flex: 0 1 auto;
	min-width: 0;
	margin: 0;
	font-size: 20px;
	font-weight: 600;
	line-height: 1.35;
	overflow-wrap: anywhere;
}

.message-view__location {
	display: inline-flex;
	align-items: center;
	gap: 6px;
	height: 24px;
	padding: 0 10px;
	border-radius: 12px;
	background: var(--color-background-hover);
	color: var(--color-text-maxcontrast);
	font-size: 12px;
	white-space: nowrap;
}

.message-view__location-dot {
	width: 8px;
	height: 8px;
	border-radius: 50%;
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

.message-view__icon-button[aria-pressed="true"] {
	--sm-button-background: var(--color-primary-element-light);
	background: var(--color-primary-element-light);
}

.message-view__icon-button:focus-visible,
.message-view__recipients:focus-visible,
.message-view__text-button:focus-visible {
	outline: 2px solid var(--color-primary-element);
	outline-offset: 1px;
}

.message-view__meta {
	display: grid;
	grid-template-columns: auto minmax(0, 1fr) auto;
	align-items: start;
	gap: 12px;
}

.message-view__people {
	min-width: 0;
	padding-top: 1px;
}

.message-view__sender {
	font-size: 15px;
	line-height: 1.4;
	overflow-wrap: anywhere;
}

.message-view__address {
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
	margin: 2px 0 0;
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

.message-view__aside {
	display: flex;
	flex-direction: column;
	align-items: flex-end;
	gap: 2px;
	margin-top: -4px;
}

.message-view__actions {
	display: flex;
	align-items: center;
	gap: 2px;
}

.message-view__when {
	display: flex;
	align-items: center;
	gap: 6px;
	padding-inline-end: 8px;
	color: var(--color-text-maxcontrast);
	font-size: 12px;
	white-space: nowrap;
}

.message-view__flag {
	color: var(--color-error-text);
}

.message-view__details {
	display: grid;
	grid-template-columns: auto minmax(0, 1fr);
	gap: 4px 12px;
	margin: 12px 0 0 52px;
	padding: 10px 14px;
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

.message-view__warning {
	display: flex;
	align-items: flex-start;
	flex-wrap: wrap;
	gap: 6px 12px;
	margin-top: 16px;
	padding: 10px 8px 10px 12px;
	border-inline-start: 4px solid var(--color-warning);
	border-radius: var(--border-radius-element, 8px);
	background: var(--color-background-hover);
}

.message-view__warning--danger {
	border-inline-start-color: var(--color-error);
}

.message-view__warning-icon {
	color: var(--color-warning-text);
}

.message-view__warning--danger .message-view__warning-icon {
	color: var(--color-error-text);
}

.message-view__warning-text {
	align-self: center;
	flex: 1;
	min-width: 12em;
}

.message-view__warning-text ul {
	margin: 4px 0 0;
	padding-inline-start: 18px;
	list-style: disc;
}

.message-view__warning-ascii {
	margin-top: 4px;
	color: var(--color-text-maxcontrast);
	font-size: 13px;
}

.message-view__notice {
	display: flex;
	align-items: center;
	flex-wrap: wrap;
	gap: 4px 10px;
	margin-top: 16px;
	padding: 6px 8px 6px 12px;
	border-radius: var(--border-radius-element, 8px);
	background: var(--color-background-hover);
	color: var(--color-text-maxcontrast);
	font-size: 13px;
}

.message-view__notice > span {
	flex: 1;
	min-width: 0;
}

.message-view__text-button {
	--sm-button-background: transparent;
	--sm-button-color: var(--color-primary-element-text-dark, var(--color-primary-element));
	height: 28px;
	min-height: 0;
	margin: 0;
	padding: 0 10px;
	border: 0;
	border-radius: var(--border-radius-element, 8px);
	background: none;
	color: var(--color-primary-element-text-dark, var(--color-primary-element));
	font: inherit;
	font-weight: 600;
	cursor: pointer;
}

.message-view__text-button:hover {
	--sm-button-background: var(--color-primary-element-light);
	background: var(--color-primary-element-light);
}

.message-view__attachments {
	margin-top: 16px;
}

.message-view__attachments h3 {
	margin: 0 0 8px;
	font-size: 12px;
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

.message-view__attachment-card {
	display: flex;
	align-items: center;
	border: 1px solid var(--color-border);
	border-radius: var(--border-radius-element, 8px);
}

.message-view__attachment-card:hover,
.message-view__attachment-card:focus-within {
	border-color: var(--color-border-dark);
	background: var(--color-background-hover);
}

.message-view__attachment {
	display: flex;
	flex: 1;
	min-width: 0;
	align-items: center;
	gap: 10px;
	padding: 6px 4px 6px 6px;
	color: var(--color-main-text);
}

.message-view__save {
	display: grid;
	place-items: center;
	width: 32px;
	height: 32px;
	min-height: 0;
	margin: 0 4px 0 0;
	padding: 0;
	border: none;
	border-radius: 50%;
	background: none;
	color: var(--color-text-maxcontrast);
	opacity: 0;
	cursor: pointer;
}

.message-view__save:hover {
	background: var(--color-background-dark);
	color: var(--color-main-text);
}

.message-view__attachment-card:hover .message-view__save,
.message-view__attachment-card:focus-within .message-view__save {
	opacity: 1;
}

.message-view__save-all {
	margin: 0 0 0 8px;
	padding: 0;
	min-height: 0;
	border: none;
	background: none;
	font-size: 12px;
	font-weight: 600;
	color: var(--color-primary-element);
	cursor: pointer;
}

.message-view__save-all:hover {
	text-decoration: underline;
}

@media (hover: none) {
	.message-view__save {
		opacity: 1;
	}
}

.message-view__file-type {
	display: grid;
	place-items: center;
	width: 32px;
	height: 36px;
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
	font-size: 13px;
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

.message-view__attachment-card:hover .message-view__download,
.message-view__attachment:focus-visible .message-view__download {
	opacity: 1;
}

/* The mail reads straight on the page; the frame's own 8px padding lines its text up with the header. */
.message-view__body {
	margin: 12px -8px 0;
}

/* Original colours in a dark theme: the mail's white page, framed. */
.message-view__body--paper {
	margin-inline: 0;
	padding: 8px 12px;
	border-radius: var(--border-radius-container, 12px);
	background: #fff;
}

@container (max-width: 560px) {
	.message-view__meta {
		grid-template-columns: auto minmax(0, 1fr);
	}

	.message-view__aside {
		grid-column: 1 / -1;
		flex-direction: row-reverse;
		justify-content: space-between;
		align-items: center;
		margin-top: 0;
	}

	.message-view__when {
		padding-inline: 0;
	}

	.message-view__details {
		margin-inline-start: 0;
	}
}

@media (max-width: 800px) {
	.message-view {
		padding: 12px 16px 24px;
	}
}
</style>
