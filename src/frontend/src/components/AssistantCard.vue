<template>
	<!-- What the Assistant does for the open message (summary, translation, scam check): its
	     title and buttons, then the wait, the answer (default slot) or why there is none. -->
	<NcAssistantContent class="assistant-card" content-classes="assistant-card__content">
		<header class="assistant-card__header">
			<AssistantIcon :size="20" />
			<h3>{{ title }}</h3>
			<slot name="header" />
			<button v-if="copyText && entry.status === 'successful'"
				type="button"
				class="assistant-card__icon-button"
				:aria-label="copyLabel"
				:title="copyLabel"
				@click="copy">
				<NcIconSvgWrapper :path="mdiContentCopy" :size="18" />
			</button>
			<button type="button"
				class="assistant-card__icon-button"
				:aria-label="closeLabel"
				:title="closeLabel"
				@click="emit('close')">
				<NcIconSvgWrapper :path="mdiClose" :size="18" />
			</button>
		</header>

		<div v-if="working" class="assistant-card__working" role="status">
			<NcLoadingIcon :size="20" />
			<span>
				{{ workingText }}
				<template v-if="slow">
					<br>
					<span class="assistant-card__hint">{{ t('sockless_mail', 'The Assistant is busy or runs its tasks in the background on this server. This can take a few minutes; you can keep reading meanwhile.') }}</span>
				</template>
			</span>
			<NcButton variant="tertiary" size="small" @click="cancelRequest(entry)">
				{{ t('sockless_mail', 'Cancel') }}
			</NcButton>
		</div>

		<slot v-else-if="entry.status === 'successful'" />

		<div v-else class="assistant-card__stopped" role="alert">
			<span>{{ entry.status === 'cancelled' ? t('sockless_mail', 'Cancelled.') : entry.error || t('sockless_mail', 'The Assistant could not do this.') }}</span>
			<NcButton variant="secondary" size="small" @click="emit('retry')">
				{{ t('sockless_mail', 'Try again') }}
			</NcButton>
		</div>
	</NcAssistantContent>
</template>

<script setup>
import { mdiClose, mdiContentCopy } from '@mdi/js'
import { showError, showSuccess } from '@nextcloud/dialogs/toast'
import { t } from '@nextcloud/l10n'
import NcAssistantContent from '@nextcloud/vue/components/NcAssistantContent'
import NcButton from '@nextcloud/vue/components/NcButton'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import { computed, onBeforeUnmount, ref } from 'vue'
import { cancelRequest, isWorking } from '../store/assistant.js'
import AssistantIcon from './AssistantIcon.vue'

const props = defineProps({
	/** The entry of store/assistant.js. */
	entry: { type: Object, required: true },
	title: { type: String, required: true },
	workingText: { type: String, required: true },
	closeLabel: { type: String, required: true },
	/** What Copy copies once the answer is there; no Copy button without it. */
	copyText: { type: String, default: null },
	copyLabel: { type: String, default: '' },
})
const emit = defineEmits(['close', 'retry'])

/** After this long a scheduled task is probably waiting for Nextcloud's background jobs. */
const SLOW_AFTER_MS = 15_000

const working = computed(() => isWorking(props.entry))
const now = ref(Date.now())
const clock = window.setInterval(() => { now.value = Date.now() }, 5000)
onBeforeUnmount(() => window.clearInterval(clock))
const slow = computed(() => working.value && now.value - props.entry.startedAt > SLOW_AFTER_MS)

async function copy() {
	try {
		await navigator.clipboard.writeText(props.copyText)
		showSuccess(t('sockless_mail', 'Copied'))
	} catch {
		showError(t('sockless_mail', 'Could not copy'))
	}
}
</script>

<style scoped>
.assistant-card {
	margin-top: 16px;
}

:deep(.assistant-card__content) {
	padding: 10px 12px 12px;
}

.assistant-card__header {
	display: flex;
	align-items: center;
	gap: 8px;
}

.assistant-card__header h3 {
	flex: 1;
	margin: 0;
	font-size: 15px;
	font-weight: 600;
}

.assistant-card__icon-button {
	--sm-button-background: transparent;
	display: inline-flex;
	align-items: center;
	justify-content: center;
	width: 32px;
	height: 32px;
	min-height: 0;
	margin: 0;
	padding: 0;
	border: 0;
	border-radius: var(--border-radius-element, 8px);
	background: none;
	color: var(--color-main-text);
	cursor: pointer;
}

.assistant-card__icon-button:hover {
	--sm-button-background: var(--color-background-hover);
	background: var(--color-background-hover);
}

.assistant-card__icon-button:focus-visible {
	outline: 2px solid var(--color-primary-element);
	outline-offset: 1px;
}

.assistant-card__working,
.assistant-card__stopped {
	display: flex;
	align-items: center;
	flex-wrap: wrap;
	gap: 8px 12px;
	margin-top: 8px;
}

.assistant-card__working > span,
.assistant-card__stopped > span {
	flex: 1;
	min-width: 12em;
}

.assistant-card__hint {
	color: var(--color-text-maxcontrast);
	font-size: 13px;
}
</style>

<style>
/* For the answers in the default slot (scoped styles do not reach slot content). */
.assistant-card__footer {
	display: flex;
	align-items: center;
	flex-wrap: wrap;
	gap: 4px 12px;
	margin-top: 10px;
}

.assistant-card__note {
	flex: 1;
	color: var(--color-text-maxcontrast);
	font-size: 13px;
}

.assistant-card__text-button {
	--sm-button-background: transparent;
	--sm-button-color: var(--color-primary-element-light-text, var(--color-primary-element));
	display: inline-flex;
	align-items: center;
	height: 28px;
	min-height: 0;
	margin: 0;
	padding: 0 10px;
	border: 0;
	border-radius: var(--border-radius-element, 8px);
	background: none;
	color: var(--color-primary-element-light-text, var(--color-primary-element));
	font: inherit;
	font-weight: 600;
	cursor: pointer;
}

.assistant-card__text-button:hover {
	--sm-button-background: var(--color-background-hover);
	background: var(--color-background-hover);
}

.assistant-card__text-button:focus-visible {
	outline: 2px solid var(--color-primary-element);
	outline-offset: 1px;
}
</style>
