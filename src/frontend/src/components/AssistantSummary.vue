<template>
	<!-- The Assistant's summary of the open message or its conversation (store/assistant.js). -->
	<AssistantCard v-if="entry"
		class="assistant-summary"
		:entry="entry"
		:title="title"
		:working-text="entry.scope === 'message' ? t('sockless_mail', 'Summarizing this message…') : t('sockless_mail', 'Summarizing the conversation…')"
		:close-label="t('sockless_mail', 'Close summary')"
		:copy-text="entry.output"
		:copy-label="t('sockless_mail', 'Copy summary')"
		@close="dismiss(summaries, key)"
		@retry="summarize(where, messageId, entry.scope)">
		<AssistantText :text="entry.output ?? ''" data-testid="assistant-summary-text" />
		<footer class="assistant-card__footer">
			<span class="assistant-card__note">{{ t('sockless_mail', 'Written by AI. Check important details in the messages.') }}</span>
			<button v-if="entry.scope === 'conversation' && entry.messageCount > 1"
				type="button"
				class="assistant-card__text-button"
				@click="summarize(where, messageId, 'message')">
				{{ t('sockless_mail', 'Only this message') }}
			</button>
			<button v-else-if="entry.scope === 'message'"
				type="button"
				class="assistant-card__text-button"
				@click="summarize(where, messageId, 'conversation')">
				{{ t('sockless_mail', 'Whole conversation') }}
			</button>
		</footer>
	</AssistantCard>
</template>

<script setup>
import { n, t } from '@nextcloud/l10n'
import { computed } from 'vue'
import { dismiss, summaries, summarize, summaryKey } from '../store/assistant.js'
import AssistantCard from './AssistantCard.vue'
import AssistantText from './AssistantText.vue'

const props = defineProps({
	/** { accountId, folderId } of the message */
	where: { type: Object, required: true },
	messageId: { type: String, required: true },
})

const key = computed(() => summaryKey(props.where, props.messageId))
const entry = computed(() => summaries[key.value])

const title = computed(() => entry.value.scope === 'conversation' && entry.value.messageCount > 1
	? n('sockless_mail', 'Summary of {count} message', 'Summary of {count} messages', entry.value.messageCount, { count: entry.value.messageCount })
	: t('sockless_mail', 'Summary'))
</script>
