<template>
	<div class="message-list">
		<!-- Leaves room for the navigation toggle Nextcloud places at the pane's top left. -->
		<header class="message-list__header">
			<h2>{{ folderName }}</h2>
			<span v-if="!state.loading && !state.error">
				{{ n('sockless_mail', '{count} message', '{count} messages', state.total, { count: state.total }) }}
			</span>
		</header>
		<div v-if="state.loading" class="message-list__center">
			<NcLoadingIcon :size="44" />
		</div>
		<NcEmptyContent v-else-if="state.error"
			:name="t('sockless_mail', 'Could not load the messages')"
			:description="state.error.message" />
		<NcEmptyContent v-else-if="state.list.length === 0"
			:name="t('sockless_mail', 'No messages in this folder')">
			<template #icon>
				<NcIconSvgWrapper :path="mdiEmailOpenOutline" />
			</template>
		</NcEmptyContent>
		<template v-else>
			<ul :aria-label="t('sockless_mail', 'Messages')">
				<NcListItem v-for="message in state.list"
					:key="message.id"
					:name="sender(message)"
					:bold="!message.seen"
					:active="message.id === openId"
					:details="formatDate(message.date)"
					@click="openMessage(message.id)">
					<template #icon>
						<NcAvatar :display-name="sender(message)" is-no-user disable-menu :size="40" />
					</template>
					<template #actions>
						<NcActionButton close-after-click @click="toggleSeen(message)">
							<template #icon>
								<NcIconSvgWrapper :path="message.seen ? mdiEmailOutline : mdiEmailOpenOutline" />
							</template>
							{{ message.seen ? t('sockless_mail', 'Mark as unread') : t('sockless_mail', 'Mark as read') }}
						</NcActionButton>
						<NcActionButton close-after-click @click="toggleFlagged(message)">
							<template #icon>
								<NcIconSvgWrapper :path="message.flagged ? mdiStarOff : mdiStarOutline" />
							</template>
							{{ message.flagged ? t('sockless_mail', 'Remove flag') : t('sockless_mail', 'Flag') }}
						</NcActionButton>
						<NcActionButton close-after-click @click="askMove([message.id])">
							<template #icon>
								<NcIconSvgWrapper :path="mdiFolderMoveOutline" />
							</template>
							{{ t('sockless_mail', 'Move…') }}
						</NcActionButton>
						<NcActionButton close-after-click @click="remove([message.id])">
							<template #icon>
								<NcIconSvgWrapper :path="mdiDeleteOutline" />
							</template>
							{{ t('sockless_mail', 'Delete') }}
						</NcActionButton>
					</template>
					<template #subname>
						<span class="message-list__subject">
							<NcIconSvgWrapper v-if="message.flagged" :path="mdiStar" :size="16" class="message-list__flag" :name="t('sockless_mail', 'Flagged')" />
							<NcIconSvgWrapper v-if="message.hasAttachments" :path="mdiPaperclip" :size="16" :name="t('sockless_mail', 'Has attachments')" />
							{{ message.subject || t('sockless_mail', '(no subject)') }}
						</span>
					</template>
				</NcListItem>
			</ul>
			<div v-if="state.nextBefore" class="message-list__more">
				<NcButton wide :disabled="state.loadingMore" @click="loadMore">
					<template #icon>
						<NcLoadingIcon v-if="state.loadingMore" :size="20" />
					</template>
					{{ t('sockless_mail', 'Load older messages') }}
				</NcButton>
			</div>
		</template>
	</div>
</template>

<script setup>
import { mdiDeleteOutline, mdiEmailOpenOutline, mdiEmailOutline, mdiFolderMoveOutline, mdiPaperclip, mdiStar, mdiStarOff, mdiStarOutline } from '@mdi/js'
import { getCanonicalLocale, n, t } from '@nextcloud/l10n'
import NcActionButton from '@nextcloud/vue/components/NcActionButton'
import NcAvatar from '@nextcloud/vue/components/NcAvatar'
import NcButton from '@nextcloud/vue/components/NcButton'
import NcEmptyContent from '@nextcloud/vue/components/NcEmptyContent'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import NcListItem from '@nextcloud/vue/components/NcListItem'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import { computed } from 'vue'
import { askMove, remove, toggleFlagged, toggleSeen } from '../store/actions.js'
import { loadMore, mailboxState, openMessage } from '../store/mailbox.js'

const state = mailboxState.messages
const openId = computed(() => mailboxState.open.id)
const folderName = computed(() => {
	const { accountId, folderId } = mailboxState.selected
	return mailboxState.folders[accountId]?.list.find((f) => f.id === folderId)?.name ?? ''
})

const timeFormat = new Intl.DateTimeFormat(getCanonicalLocale(), { hour: 'numeric', minute: '2-digit' })
const dateFormat = new Intl.DateTimeFormat(getCanonicalLocale(), { day: 'numeric', month: 'short' })
const yearFormat = new Intl.DateTimeFormat(getCanonicalLocale(), { day: 'numeric', month: 'short', year: 'numeric' })

function formatDate(value) {
	const date = new Date(value)
	const now = new Date()
	if (date.toDateString() === now.toDateString()) {
		return timeFormat.format(date)
	}
	return (date.getFullYear() === now.getFullYear() ? dateFormat : yearFormat).format(date)
}

function sender(message) {
	const from = message.from[0]
	return from ? (from.name || from.address) : t('sockless_mail', 'Unknown sender')
}
</script>

<style scoped>
.message-list ul {
	padding: 4px;
}

.message-list__header {
	display: flex;
	align-items: baseline;
	gap: 8px;
	min-height: var(--default-clickable-area, 34px);
	padding: 10px 8px 4px calc(var(--default-clickable-area, 34px) + 16px);
	color: var(--color-text-maxcontrast);
}

.message-list__header h2 {
	margin: 0;
	font-size: 18px;
	color: var(--color-main-text);
}

.message-list__center {
	display: flex;
	justify-content: center;
	padding: 32px;
}

.message-list__subject {
	display: inline-flex;
	align-items: center;
	gap: 4px;
}

.message-list__flag {
	color: var(--color-warning);
}

.message-list__more {
	padding: 8px;
}
</style>
