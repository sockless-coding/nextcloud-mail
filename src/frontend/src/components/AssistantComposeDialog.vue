<template>
	<!-- The composer's Assistant: write the message from a short request, or change the text.
	     Nothing reaches the message before the user picks Replace or Insert. Closing goes through
	     NcDialog's buttons (see AccountDialog.vue for why). -->
	<NcDialog :name="mode === 'write' ? t('sockless_mail', 'Write with the Assistant') : t('sockless_mail', 'Rewrite with the Assistant')"
		size="normal"
		:buttons="buttons"
		:open="true"
		@update:open="(open) => !open && closed()">
		<div class="assistant-compose">
			<!-- What to ask for -->
			<template v-if="!request">
				<template v-if="mode === 'write'">
					<label class="assistant-compose__label" for="assistant-instruction">
						{{ isReply ? t('sockless_mail', 'What should the reply say?') : t('sockless_mail', 'What should the message say?') }}
					</label>
					<textarea id="assistant-instruction"
						ref="instructionInput"
						v-model="instruction"
						class="assistant-compose__textarea"
						rows="3"
						:maxlength="MAX_INSTRUCTION"
						:placeholder="isReply ? t('sockless_mail', 'For example: accept, and suggest Tuesday instead') : t('sockless_mail', 'For example: invite the team to lunch on Friday at noon')" />
					<p v-if="isReply" class="assistant-compose__hint">
						{{ t('sockless_mail', 'Leave it empty for a fitting reply. The Assistant reads the conversation.') }}
					</p>
					<label class="assistant-compose__label" for="assistant-tone">{{ t('sockless_mail', 'Tone') }}</label>
					<select id="assistant-tone" v-model="tone" class="assistant-compose__select">
						<option v-for="option in TONES" :key="option.value" :value="option.value">
							{{ option.name }}
						</option>
					</select>
					<NcCheckboxRadioSwitch v-if="target.text" v-model="buildOn">
						{{ t('sockless_mail', 'Build on what I have written') }}
					</NcCheckboxRadioSwitch>
				</template>

				<template v-else>
					<p class="assistant-compose__label">
						{{ target.selection ? t('sockless_mail', 'The selected text') : t('sockless_mail', 'Your text, without the quoted message') }}
					</p>
					<blockquote class="assistant-compose__source">
						{{ excerpt }}
					</blockquote>
					<fieldset class="assistant-compose__actions">
						<legend class="hidden-visually">{{ t('sockless_mail', 'What to do') }}</legend>
						<NcCheckboxRadioSwitch v-for="option in actions"
							:key="option.value"
							v-model="action"
							type="radio"
							name="assistant-action"
							:value="option.value">
							{{ option.name }}
						</NcCheckboxRadioSwitch>
					</fieldset>
					<template v-if="action === 'tone'">
						<label class="assistant-compose__label" for="assistant-rewrite-tone">{{ t('sockless_mail', 'Tone') }}</label>
						<select id="assistant-rewrite-tone" v-model="rewriteTone" class="assistant-compose__select">
							<option v-for="option in tones" :key="option.value" :value="option.value">
								{{ option.name }}
							</option>
						</select>
					</template>
					<template v-else-if="action === 'translate'">
						<label class="assistant-compose__label" for="assistant-language">{{ t('sockless_mail', 'Translate into') }}</label>
						<select id="assistant-language" v-model="language" class="assistant-compose__select">
							<option v-for="option in assistantState.languages" :key="option.value" :value="option.value">
								{{ option.name }}
							</option>
						</select>
					</template>
					<template v-else-if="action === 'custom'">
						<label class="assistant-compose__label" for="assistant-custom">{{ t('sockless_mail', 'How should it change?') }}</label>
						<textarea id="assistant-custom"
							v-model="instruction"
							class="assistant-compose__textarea"
							rows="2"
							:maxlength="MAX_INSTRUCTION"
							:placeholder="t('sockless_mail', 'For example: add a short greeting and keep it under 100 words')" />
					</template>
				</template>
			</template>

			<!-- Waiting -->
			<div v-else-if="working" class="assistant-compose__working" role="status">
				<NcLoadingIcon :size="24" />
				<span>
					{{ t('sockless_mail', 'The Assistant is writing…') }}
					<template v-if="slow">
						<br>
						<span class="assistant-compose__hint">{{ t('sockless_mail', 'The Assistant is busy or runs its tasks in the background on this server. This can take a few minutes.') }}</span>
					</template>
				</span>
			</div>

			<!-- The answer -->
			<template v-else-if="request.status === 'successful'">
				<div class="assistant-compose__result" data-testid="assistant-result">{{ request.output }}</div>
				<p class="assistant-compose__hint">
					<AssistantIcon :size="16" />
					{{ t('sockless_mail', 'Written by AI. Read it before you send it.') }}
				</p>
				<p v-if="mode === 'rewrite' && target.images" class="assistant-compose__hint">
					{{ t('sockless_mail', 'Replacing would remove the images in this text, so the answer can only be inserted.') }}
				</p>
			</template>

			<NcNoteCard v-else
				type="error"
				:text="request.status === 'cancelled' ? t('sockless_mail', 'Cancelled.') : request.error || t('sockless_mail', 'The Assistant could not do this.')" />
		</div>
	</NcDialog>
</template>

<script setup>
import { t } from '@nextcloud/l10n'
import NcCheckboxRadioSwitch from '@nextcloud/vue/components/NcCheckboxRadioSwitch'
import NcDialog from '@nextcloud/vue/components/NcDialog'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import NcNoteCard from '@nextcloud/vue/components/NcNoteCard'
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { assistantState, cancelRequest, isWorking, preferredLanguage, rewriteText, rewriteTones, TONES, writeMessage } from '../store/assistant.js'
import AssistantIcon from './AssistantIcon.vue'

const props = defineProps({
	/** 'write' or 'rewrite' */
	mode: { type: String, required: true },
	/** The rewrite to select first; with start, it runs at once. */
	action: { type: String, default: null },
	start: { type: Boolean, default: false },
	/** What the composer's text offers: { text, selection, images } (RichEditor's assistantTarget). */
	target: { type: Object, required: true },
	/** For writing: { subject, to, senderName, source: { accountId, folderId, messageId, mode } | null } */
	context: { type: Object, default: () => ({}) },
})
/** replace / insert: put the text in the message (in place of the target, or at the cursor). */
const emit = defineEmits(['replace', 'insert', 'close'])

const MAX_INSTRUCTION = 1000
/** After this long the task probably waits for Nextcloud's background jobs. */
const SLOW_AFTER_MS = 15_000

const ACTIONS = [
	{ value: 'tone', name: t('sockless_mail', 'Change the tone') },
	{ value: 'fix', name: t('sockless_mail', 'Fix spelling and grammar') },
	{ value: 'shorter', name: t('sockless_mail', 'Make it shorter') },
	{ value: 'clearer', name: t('sockless_mail', 'Make it clearer') },
	{ value: 'translate', name: t('sockless_mail', 'Translate') },
	{ value: 'custom', name: t('sockless_mail', 'Your own instruction') },
]

const actions = computed(() => ACTIONS.filter((a) => assistantState.rewrite.includes(a.value)))
const tones = computed(() => rewriteTones())
const isReply = computed(() => ['reply', 'replyAll'].includes(props.context.source?.mode))

const instruction = ref('')
const tone = ref('neutral')
const buildOn = ref(true)
const action = ref(props.action ?? actions.value[0]?.value ?? null)
const rewriteTone = ref(tones.value[0]?.value ?? null)
const language = ref(preferredLanguage())
/** The running or finished request (store/assistant.js); null while asking. */
const request = ref(null)
const instructionInput = ref(null)

const working = computed(() => isWorking(request.value))
const now = ref(Date.now())
const clock = window.setInterval(() => { now.value = Date.now() }, 5000)
const slow = computed(() => working.value && now.value - request.value.startedAt > SLOW_AFTER_MS)

const excerpt = computed(() => props.target.text.length > 400 ? `${props.target.text.slice(0, 400)}…` : props.target.text)

const ready = computed(() => {
	if (props.mode === 'write') {
		return isReply.value || instruction.value.trim().length > 0
	}
	return !!action.value && !!props.target.text
		&& (action.value !== 'tone' || !!rewriteTone.value)
		&& (action.value !== 'translate' || !!language.value)
		&& (action.value !== 'custom' || instruction.value.trim().length > 0)
})

function run() {
	now.value = Date.now()
	if (props.mode === 'write') {
		request.value = writeMessage({
			instruction: instruction.value.trim(),
			tone: tone.value,
			subject: props.context.subject,
			to: props.context.to,
			senderName: props.context.senderName,
			current: buildOn.value ? props.target.text : '',
			source: props.context.source,
		})
	} else {
		request.value = rewriteText({
			text: props.target.text,
			action: action.value,
			tone: action.value === 'tone' ? rewriteTone.value : undefined,
			language: action.value === 'translate' ? language.value : undefined,
			instruction: action.value === 'custom' ? instruction.value.trim() : undefined,
		})
	}
}

const succeeded = computed(() => request.value?.status === 'successful')
/** The text the answer can replace: the selection or the user's text, unless images would go with it. */
const canReplace = computed(() => props.mode === 'write' ? !!props.target.text : !props.target.images)

const buttons = computed(() => {
	if (!request.value) {
		return [
			{ label: t('sockless_mail', 'Cancel') },
			{ label: t('sockless_mail', 'Generate'), variant: 'primary', disabled: !ready.value, callback: () => { run(); return false } },
		]
	}
	if (working.value) {
		return [{ label: t('sockless_mail', 'Stop'), callback: () => { cancelRequest(request.value); return false } }]
	}
	const again = [
		{ label: t('sockless_mail', 'Change the request'), callback: () => { request.value = null; return false } },
		{ label: t('sockless_mail', 'Try again'), callback: () => { run(); return false } },
	]
	if (!succeeded.value) {
		return [{ label: t('sockless_mail', 'Close') }, ...again]
	}
	return [
		...again,
		// Writing inserts where the cursor is; a rewrite goes in below the text it was made from.
		{ label: props.mode === 'write' ? t('sockless_mail', 'Insert') : t('sockless_mail', 'Insert below'), variant: canReplace.value ? 'secondary' : 'primary', callback: () => emit('insert', request.value.output) },
		...(canReplace.value
			? [{ label: props.mode === 'write' ? t('sockless_mail', 'Replace my text') : t('sockless_mail', 'Replace'), variant: 'primary', callback: () => emit('replace', request.value.output) }]
			: []),
	]
})

function closed() {
	// Closed while it runs: nobody waits for the answer any more.
	if (working.value) {
		cancelRequest(request.value)
	}
	emit('close')
}

onMounted(async () => {
	if (props.start && ready.value) {
		run()
		return
	}
	await nextTick()
	instructionInput.value?.focus()
})

onBeforeUnmount(() => window.clearInterval(clock))
</script>

<style scoped>
.assistant-compose {
	display: flex;
	flex-direction: column;
	gap: 8px;
	min-height: 120px;
}

.assistant-compose__label {
	margin: 4px 0 0;
	font-weight: 600;
}

.assistant-compose__textarea,
.assistant-compose__select {
	width: 100%;
	margin: 0;
}

.assistant-compose__textarea {
	min-height: 0;
	resize: vertical;
}

.assistant-compose__hint {
	display: flex;
	align-items: center;
	gap: 6px;
	margin: 0;
	color: var(--color-text-maxcontrast);
	font-size: 13px;
}

.assistant-compose__source {
	max-height: 7.5em;
	margin: 0;
	padding: 6px 10px;
	overflow: auto;
	border-inline-start: 3px solid var(--color-border-dark);
	color: var(--color-text-maxcontrast);
	white-space: pre-wrap;
	overflow-wrap: anywhere;
}

.assistant-compose__actions {
	margin: 0;
	padding: 0;
	border: 0;
}

.assistant-compose__working {
	display: flex;
	align-items: center;
	gap: 12px;
	padding: 16px 0;
}

.assistant-compose__result {
	max-height: 40vh;
	padding: 10px 12px;
	overflow: auto;
	border: 1px solid var(--color-border);
	border-radius: var(--border-radius-element, 8px);
	background: var(--color-background-hover);
	white-space: pre-wrap;
	overflow-wrap: anywhere;
}
</style>
