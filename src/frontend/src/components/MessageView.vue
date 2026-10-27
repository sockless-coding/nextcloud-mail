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
				<h2>{{ message.subject || t('sockless_mail', '(no subject)') }}</h2>
				<div class="message-view__meta">
					<NcAvatar :display-name="senderName" is-no-user disable-menu :size="44" />
					<div class="message-view__people">
						<div>
							<strong>{{ senderName }}</strong>
							<span v-if="sender?.name" class="message-view__address">{{ " " }}&lt;{{ sender.address }}&gt;</span>
						</div>
						<div class="message-view__recipients">
							{{ t('sockless_mail', 'To: {recipients}', { recipients: formatList(message.to) }) }}
							<template v-if="message.cc.length">
								· {{ t('sockless_mail', 'Cc: {recipients}', { recipients: formatList(message.cc) }) }}
							</template>
						</div>
					</div>
					<NcDateTime class="message-view__date" :timestamp="Date.parse(message.date)" :format="{ dateStyle: 'medium', timeStyle: 'short' }" :relative-time="false" />
				</div>
				<div class="message-view__toolbar" role="toolbar" :aria-label="t('sockless_mail', 'Message actions')">
					<NcButton variant="secondary" @click="reply('reply')">
						<template #icon>
							<NcIconSvgWrapper :path="mdiReply" />
						</template>
						{{ t('sockless_mail', 'Reply') }}
					</NcButton>
					<NcButton variant="tertiary" @click="reply('replyAll')">
						<template #icon>
							<NcIconSvgWrapper :path="mdiReplyAll" />
						</template>
						{{ t('sockless_mail', 'Reply all') }}
					</NcButton>
					<NcButton variant="tertiary" @click="reply('forward')">
						<template #icon>
							<NcIconSvgWrapper :path="mdiShare" />
						</template>
						{{ t('sockless_mail', 'Forward') }}
					</NcButton>
					<NcButton variant="tertiary" @click="toggleSeen(message)">
						<template #icon>
							<NcIconSvgWrapper :path="message.seen ? mdiEmailOutline : mdiEmailOpenOutline" />
						</template>
						{{ message.seen ? t('sockless_mail', 'Mark as unread') : t('sockless_mail', 'Mark as read') }}
					</NcButton>
					<NcButton variant="tertiary" :pressed="message.flagged" @click="toggleFlagged(message)">
						<template #icon>
							<NcIconSvgWrapper :path="message.flagged ? mdiStar : mdiStarOutline" />
						</template>
						{{ message.flagged ? t('sockless_mail', 'Flagged') : t('sockless_mail', 'Flag') }}
					</NcButton>
					<NcButton variant="tertiary" @click="askMove([message.id])">
						<template #icon>
							<NcIconSvgWrapper :path="mdiFolderMoveOutline" />
						</template>
						{{ t('sockless_mail', 'Move…') }}
					</NcButton>
					<NcButton variant="tertiary" @click="remove([message.id])">
						<template #icon>
							<NcIconSvgWrapper :path="mdiDeleteOutline" />
						</template>
						{{ t('sockless_mail', 'Delete') }}
					</NcButton>
				</div>
			</header>

			<NcNoteCard v-if="message.hasRemoteContent && !message.remoteContentAllowed" type="info">
				<div class="message-view__remote">
					<span>{{ t('sockless_mail', 'Remote images are blocked to protect your privacy.') }}</span>
					<NcButton variant="secondary" @click="openMessage(message.id, true)">
						{{ t('sockless_mail', 'Show images') }}
					</NcButton>
				</div>
			</NcNoteCard>

			<MessageBody :key="`${message.id}/${message.remoteContentAllowed}`" :html="message.html" />

			<section v-if="message.attachments.length" class="message-view__attachments">
				<h3>{{ n('sockless_mail', '{count} attachment', '{count} attachments', message.attachments.length, { count: message.attachments.length }) }}</h3>
				<ul>
					<li v-for="attachment in message.attachments" :key="attachment.partId">
						<a :href="partUrl(selected.accountId, selected.folderId, message.id, attachment.partId, true)"
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
import { mdiDeleteOutline, mdiEmailOpenOutline, mdiEmailOutline, mdiFolderMoveOutline, mdiPaperclip, mdiReply, mdiReplyAll, mdiShare, mdiStar, mdiStarOutline } from '@mdi/js'
import { showError } from '@nextcloud/dialogs/toast'
import { getCanonicalLocale, n, t } from '@nextcloud/l10n'
import NcAvatar from '@nextcloud/vue/components/NcAvatar'
import NcButton from '@nextcloud/vue/components/NcButton'
import NcDateTime from '@nextcloud/vue/components/NcDateTime'
import NcEmptyContent from '@nextcloud/vue/components/NcEmptyContent'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import NcNoteCard from '@nextcloud/vue/components/NcNoteCard'
import { computed } from 'vue'
import { askMove, remove, toggleFlagged, toggleSeen } from '../store/actions.js'
import { composeReply } from '../store/compose.js'
import { mailboxState, openMessage, partUrl } from '../store/mailbox.js'
import MessageBody from './MessageBody.vue'

const open = mailboxState.open
const selected = computed(() => mailboxState.selected)
const message = computed(() => open.message)
const sender = computed(() => message.value?.from[0] ?? null)
const senderName = computed(() => sender.value?.name || sender.value?.address || t('sockless_mail', 'Unknown sender'))

async function reply(mode) {
	try {
		await composeReply(mode)
	} catch (e) {
		showError(t('sockless_mail', 'Could not prepare the message: {error}', { error: e.message }))
	}
}

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
	padding: 16px 24px;
	max-width: 1000px;
}

.message-view__center {
	display: flex;
	justify-content: center;
	padding: 48px;
}

.message-view__header h2 {
	margin: 8px 0 16px;
	overflow-wrap: anywhere;
}

.message-view__meta {
	display: flex;
	align-items: center;
	gap: 12px;
	margin-bottom: 16px;
}

.message-view__people {
	flex: 1;
	min-width: 0;
}

.message-view__address,
.message-view__recipients,
.message-view__date {
	color: var(--color-text-maxcontrast);
}

.message-view__recipients {
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.message-view__toolbar {
	display: flex;
	flex-wrap: wrap;
	gap: 4px;
	margin-bottom: 12px;
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
</style>
