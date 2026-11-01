<template>
	<!-- The open message translated by the Assistant (store/assistant.js); the original stays below. -->
	<AssistantCard v-if="entry"
		class="assistant-translation"
		:entry="entry"
		:title="t('sockless_mail', 'Translation')"
		:working-text="t('sockless_mail', 'Translating into {language}…', { language: languageName })"
		:close-label="t('sockless_mail', 'Close translation')"
		:copy-text="entry.output"
		:copy-label="t('sockless_mail', 'Copy translation')"
		@close="dismiss(translations, key)"
		@retry="translateMessage(where, messageId, entry.language)">
		<template #header>
			<label class="hidden-visually" :for="`translation-language-${key}`">{{ t('sockless_mail', 'Translate into') }}</label>
			<select :id="`translation-language-${key}`"
				class="assistant-translation__language"
				:value="entry.language"
				:disabled="working"
				@change="translateMessage(where, messageId, $event.target.value)">
				<option v-for="option in assistantState.languages" :key="option.value" :value="option.value">
					{{ option.name }}
				</option>
			</select>
		</template>
		<AssistantText :text="entry.output ?? ''" data-testid="assistant-translation-text" />
		<footer class="assistant-card__footer">
			<span class="assistant-card__note">
				{{ entry.truncated
					? t('sockless_mail', 'Translated by AI. The message is long: only its first part was translated.')
					: t('sockless_mail', 'Translated by AI. The original is below.') }}
			</span>
		</footer>
	</AssistantCard>
</template>

<script setup>
import { t } from '@nextcloud/l10n'
import { computed } from 'vue'
import { assistantState, dismiss, isWorking, summaryKey, translateMessage, translations } from '../store/assistant.js'
import AssistantCard from './AssistantCard.vue'
import AssistantText from './AssistantText.vue'

const props = defineProps({
	/** { accountId, folderId } of the message */
	where: { type: Object, required: true },
	messageId: { type: String, required: true },
})

const key = computed(() => summaryKey(props.where, props.messageId))
const entry = computed(() => translations[key.value])
const working = computed(() => isWorking(entry.value))
const languageName = computed(() => assistantState.languages.find((l) => l.value === entry.value.language)?.name ?? entry.value.language)
</script>

<style scoped>
.assistant-translation__language {
	max-width: 12em;
	height: 32px;
	min-height: 0;
	margin: 0;
}
</style>
