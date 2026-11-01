<template>
	<div class="message-view">
		<div v-if="open.loading && !inConversation" class="message-view__center">
			<NcLoadingIcon :size="44" />
		</div>
		<NcEmptyContent v-else-if="open.error && !inConversation"
			:name="t('sockless_mail', 'Could not open the message')"
			:description="open.error.message" />
		<article v-else-if="message || inConversation">
			<div class="message-view__title">
				<button v-if="readingPane === 'off'"
					class="message-view__icon-button"
					:aria-label="t('sockless_mail', 'Back to the list')"
					:title="t('sockless_mail', 'Back to the list')"
					@click="closeMessage">
					<NcIconSvgWrapper :path="mdiArrowLeft" :size="20" />
				</button>
				<h2>{{ subject || t('sockless_mail', '(no subject)') }}</h2>
				<TagChips v-if="message && !inConversation" :tags="tagsOf(message)" />
				<span v-if="location" class="message-view__location">
					<span class="message-view__location-dot" :style="{ background: location.color }" aria-hidden="true" />
					{{ location.label }}
				</span>
			</div>

			<ConversationView v-if="inConversation" />
			<MessageCard v-else
				:where="where"
				:message="message"
				@show-images="openMessage(where, message.id, true)" />
		</article>
	</div>
</template>

<script setup>
import { mdiArrowLeft } from '@mdi/js'
import { t } from '@nextcloud/l10n'
import NcEmptyContent from '@nextcloud/vue/components/NcEmptyContent'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import { computed, watch } from 'vue'
import { accountColor, accountsState } from '../store/accounts.js'
import { conversationsOn, conversationState, loadConversation } from '../store/conversation.js'
import { readingPane } from '../store/layout.js'
import { closeMessage, mailboxState, openMessage } from '../store/mailbox.js'
import { tagsOf } from '../store/tags.js'
import ConversationView from './ConversationView.vue'
import MessageCard from './MessageCard.vue'
import TagChips from './TagChips.vue'

/**
 * The reader: the open message, or for an account that shows conversations, the open message's
 * conversation (ConversationView) once it has more than one message.
 */
const open = mailboxState.open
// Where the open message lives; it can be another folder than the selected one (search, unified views).
const where = computed(() => ({ accountId: open.accountId, folderId: open.folderId }))
const message = computed(() => open.message)
const inConversation = computed(() => conversationState.entries.length > 0 && conversationState.accountId === open.accountId)
const subject = computed(() => message.value?.subject ?? conversationState.entries.find((e) => e.message.id === open.id)?.message.subject ?? '')

watch(() => [open.accountId, open.folderId, open.id, conversationsOn(open.accountId)], loadConversation, { immediate: true })

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

@media (max-width: 800px) {
	.message-view {
		padding: 12px 16px 24px;
	}
}
</style>
