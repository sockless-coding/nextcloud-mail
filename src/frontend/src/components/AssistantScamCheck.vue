<template>
	<!-- The Assistant's judgement whether the open message is phishing or a scam (store/assistant.js). -->
	<AssistantCard v-if="entry"
		class="assistant-scam"
		:entry="entry"
		:title="t('sockless_mail', 'Scam check')"
		:working-text="t('sockless_mail', 'Checking the message for phishing and scams…')"
		:close-label="t('sockless_mail', 'Close scam check')"
		@close="dismiss(scamChecks, key)"
		@retry="checkScam(where, messageId)">
		<div class="assistant-scam__verdict"
			:class="`assistant-scam__verdict--${result.verdict ?? 'unknown'}`"
			data-testid="assistant-scam-verdict">
			<NcIconSvgWrapper :path="verdict.icon" :size="22" class="assistant-scam__icon" />
			<div class="assistant-scam__text">
				<strong>{{ verdict.label }}</strong>
				<p v-if="result.summary">
					{{ result.summary }}
				</p>
			</div>
		</div>
		<ul v-if="result.reasons.length" class="assistant-scam__reasons">
			<li v-for="(reason, index) in result.reasons" :key="index">
				{{ reason }}
			</li>
		</ul>
		<p v-if="result.advice" class="assistant-scam__advice">
			{{ result.advice }}
		</p>
		<footer class="assistant-card__footer">
			<span class="assistant-card__note">{{ t('sockless_mail', 'An AI assessment: it can be wrong either way. When in doubt, contact the sender another way, and never enter passwords or payment details from a link in a mail.') }}</span>
		</footer>
	</AssistantCard>
</template>

<script setup>
import { mdiHelpCircleOutline, mdiShieldAlertOutline, mdiShieldCheckOutline, mdiShieldOffOutline } from '@mdi/js'
import { t } from '@nextcloud/l10n'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import { computed } from 'vue'
import { checkScam, dismiss, scamChecks, scamVerdict, summaryKey } from '../store/assistant.js'
import AssistantCard from './AssistantCard.vue'

const props = defineProps({
	/** { accountId, folderId } of the message */
	where: { type: Object, required: true },
	messageId: { type: String, required: true },
})

const VERDICTS = {
	safe: { icon: mdiShieldCheckOutline, label: t('sockless_mail', 'Looks safe') },
	suspicious: { icon: mdiShieldAlertOutline, label: t('sockless_mail', 'Suspicious') },
	scam: { icon: mdiShieldOffOutline, label: t('sockless_mail', 'Likely a scam') },
	unknown: { icon: mdiHelpCircleOutline, label: t('sockless_mail', 'No clear verdict') },
}

const key = computed(() => summaryKey(props.where, props.messageId))
const entry = computed(() => scamChecks[key.value])
const result = computed(() => scamVerdict(entry.value?.output))
const verdict = computed(() => VERDICTS[result.value.verdict ?? 'unknown'])
</script>

<style scoped>
.assistant-scam__verdict {
	display: flex;
	align-items: flex-start;
	gap: 10px;
	margin-top: 8px;
	padding: 8px 10px;
	border-inline-start: 4px solid var(--color-border-dark);
	border-radius: var(--border-radius-element, 8px);
	background: var(--color-background-hover);
}

.assistant-scam__verdict--safe {
	border-inline-start-color: var(--color-success);
}

.assistant-scam__verdict--safe .assistant-scam__icon {
	color: var(--color-success-text);
}

.assistant-scam__verdict--suspicious {
	border-inline-start-color: var(--color-warning);
}

.assistant-scam__verdict--suspicious .assistant-scam__icon {
	color: var(--color-warning-text);
}

.assistant-scam__verdict--scam {
	border-inline-start-color: var(--color-error);
}

.assistant-scam__verdict--scam .assistant-scam__icon {
	color: var(--color-error-text);
}

.assistant-scam__text {
	flex: 1;
	min-width: 0;
}

.assistant-scam__text p {
	margin: 2px 0 0;
}

.assistant-scam__reasons {
	margin: 8px 0 0;
	padding-inline-start: 20px;
	list-style: disc;
	overflow-wrap: anywhere;
}

.assistant-scam__advice {
	margin: 8px 0 0;
	font-weight: 600;
}
</style>
