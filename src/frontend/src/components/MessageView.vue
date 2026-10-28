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
						class="message-view__back"
						:aria-label="t('sockless_mail', 'Back to the list')"
						:title="t('sockless_mail', 'Back to the list')"
						@click="closeMessage">
						<NcIconSvgWrapper :path="mdiArrowLeft" />
					</button>
					<h2>{{ message.subject || t('sockless_mail', '(no subject)') }}</h2>
				</div>
				<div class="message-view__meta">
					<NcAvatar :display-name="senderName" is-no-user disable-menu :size="44" />
					<div class="message-view__people">
						<div class="message-view__sender">
							<strong>{{ senderName }}</strong>
							<span v-if="sender?.name" class="message-view__address">{{ " " }}&lt;{{ sender.address }}&gt;</span>
						</div>
						<div class="message-view__recipients">
							{{ t('sockless_mail', 'To: {recipients}', { recipients: formatList(message.to) }) }}
							<template v-if="message.cc.length">
								· {{ t('sockless_mail', 'Cc: {recipients}', { recipients: formatList(message.cc) }) }}
							</template>
						</div>
						<NcDateTime class="message-view__date" :timestamp="Date.parse(message.date)" :format="{ dateStyle: 'full', timeStyle: 'short' }" :relative-time="false" />
					</div>
					<NcIconSvgWrapper v-if="message.flagged" :path="mdiFlag" class="message-view__flag" :name="t('sockless_mail', 'Flagged')" />
				</div>
			</header>

			<NcNoteCard v-if="message.hasRemoteContent && !message.remoteContentAllowed" type="info">
				<div class="message-view__remote">
					<span>{{ t('sockless_mail', 'Remote images are blocked to protect your privacy.') }}</span>
					<NcButton variant="secondary" @click="openMessage(where, message.id, true)">
						{{ t('sockless_mail', 'Show images') }}
					</NcButton>
				</div>
			</NcNoteCard>

			<MessageBody :key="`${message.id}/${message.remoteContentAllowed}`" :html="message.html" />

			<section v-if="message.attachments.length" class="message-view__attachments">
				<h3>{{ n('sockless_mail', '{count} attachment', '{count} attachments', message.attachments.length, { count: message.attachments.length }) }}</h3>
				<ul>
					<li v-for="attachment in message.attachments" :key="attachment.partId">
						<a :href="partUrl(where.accountId, where.folderId, message.id, attachment.partId, true)"
							class="message-view__attachment"
							download>
							<NcIconSvgWrapper :path="mdiPaperclip" :size="20" />
							<span class="message-view__attachment-name">{{ attachment.fileName }}</span>
							<span v-if="attachment.size" class="message-view__attachment-size">{{ formatFileSize(attachment.size) }}</span>
						</a>
					</li>
				</ul>
			</section>
		</article>
	</div>
</template>

<script setup>
import { mdiArrowLeft, mdiFlag, mdiPaperclip } from '@mdi/js'
import { getCanonicalLocale, n, t } from '@nextcloud/l10n'
import NcAvatar from '@nextcloud/vue/components/NcAvatar'
import NcButton from '@nextcloud/vue/components/NcButton'
import NcDateTime from '@nextcloud/vue/components/NcDateTime'
import NcEmptyContent from '@nextcloud/vue/components/NcEmptyContent'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import NcNoteCard from '@nextcloud/vue/components/NcNoteCard'
import { computed } from 'vue'
import { readingPane } from '../store/layout.js'
import { closeMessage, mailboxState, openMessage, partUrl } from '../store/mailbox.js'
import MessageBody from './MessageBody.vue'

const open = mailboxState.open
// Where the open message lives; it can be another folder than the selected one (search, unified inbox).
const where = computed(() => ({ accountId: open.accountId, folderId: open.folderId }))
const message = computed(() => open.message)
const sender = computed(() => message.value?.from[0] ?? null)
const senderName = computed(() => sender.value?.name || sender.value?.address || t('sockless_mail', 'Unknown sender'))

function formatList(addresses) {
	return addresses.map((a) => a.name || a.address).join(', ')
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

.message-view__back {
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

.message-view__back:hover {
	--sm-button-background: var(--color-background-hover);
	background: var(--color-background-hover);
}

/* Sender card. */
.message-view__meta {
	display: flex;
	align-items: flex-start;
	gap: 12px;
	margin-bottom: 16px;
	padding: 14px 16px;
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
.message-view__recipients,
.message-view__date {
	color: var(--color-text-maxcontrast);
	font-size: 13px;
}

.message-view__recipients {
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.message-view__flag {
	color: var(--color-error-text);
}

.message-view__remote {
	display: flex;
	align-items: center;
	gap: 12px;
	flex-wrap: wrap;
}

.message-view__attachments ul {
	display: flex;
	flex-wrap: wrap;
	gap: 8px;
}

.message-view__attachment {
	display: inline-flex;
	align-items: center;
	gap: 6px;
	padding: 6px 12px;
	border: 1px solid var(--color-border);
	border-radius: var(--border-radius-element, 8px);
}

.message-view__attachment:hover {
	background: var(--color-background-hover);
}

.message-view__attachment-size {
	color: var(--color-text-maxcontrast);
}

@media (max-width: 800px) {
	.message-view {
		padding: 12px 16px 24px;
	}
}
</style>
