<template>
	<!-- Closing goes through NcDialog's buttons (see AccountDialog.vue for why). -->
	<NcDialog :name="kind === 'headers' ? t('sockless_mail', 'Message headers') : t('sockless_mail', 'Message source')"
		:open="true"
		size="large"
		:buttons="buttons"
		@update:open="(open) => !open && emit('close')">
		<div class="message-source">
			<p class="message-source__subject">
				{{ subject || t('sockless_mail', '(no subject)') }}
			</p>
			<div v-if="loading" class="message-source__center">
				<NcLoadingIcon :size="32" />
			</div>
			<NcNoteCard v-else-if="error" type="error">
				{{ error }}
			</NcNoteCard>
			<template v-else>
				<NcNoteCard v-if="truncated" type="info">
					{{ t('sockless_mail', 'Showing the first {shown} of {size}. Download the message to see all of it.', { shown: formatFileSize(shownBytes), size: formatFileSize(size) }) }}
				</NcNoteCard>
				<!-- One block per header field, its folded lines kept, so long fields hang under their name. -->
				<div v-if="kind === 'headers'" class="message-source__text" data-sockless-raw="headers">
					<div v-for="(field, index) in fields" :key="index" class="message-source__field">
						<span class="message-source__name">{{ field.name }}</span>{{ field.rest }}
					</div>
				</div>
				<pre v-else class="message-source__text" data-sockless-raw="source">{{ text }}</pre>
			</template>
		</div>
	</NcDialog>
</template>

<script setup>
import { showError, showSuccess } from '@nextcloud/dialogs/toast'
import { t } from '@nextcloud/l10n'
import NcDialog from '@nextcloud/vue/components/NcDialog'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import NcNoteCard from '@nextcloud/vue/components/NcNoteCard'
import { computed, ref } from 'vue'
import { formatFileSize } from '../fileSize.js'
import { emlUrl, loadRawMessage } from '../store/mailbox.js'

const props = defineProps({
	/** { accountId, folderId } of the message */
	where: { type: Object, required: true },
	messageId: { type: String, required: true },
	subject: { type: String, default: '' },
	/** 'headers' or 'source' */
	kind: { type: String, required: true },
})
const emit = defineEmits(['close'])

const loading = ref(true)
const error = ref('')
const text = ref('')
const size = ref(0)
const truncated = ref(false)
const shownBytes = computed(() => new TextEncoder().encode(text.value).length)

/** Header fields, split where a line does not start with white space (RFC 5322 folding). */
const fields = computed(() => {
	const result = []
	for (const line of text.value.replace(/\r\n/g, '\n').replace(/\n+$/, '').split('\n')) {
		if (/^[ \t]/.test(line) && result.length) {
			result[result.length - 1].rest += '\n' + line
			continue
		}
		const colon = line.indexOf(':')
		result.push(colon > 0
			? { name: line.slice(0, colon + 1), rest: line.slice(colon + 1) }
			: { name: '', rest: line })
	}
	return result
})

load()

async function load() {
	try {
		const data = await loadRawMessage(props.where, props.messageId, props.kind)
		if (props.kind === 'headers') {
			text.value = data.headers
		} else {
			text.value = data.text
			size.value = data.size
			truncated.value = data.truncated
		}
	} catch (e) {
		error.value = e.message
	} finally {
		loading.value = false
	}
}

async function copy() {
	try {
		await navigator.clipboard.writeText(text.value)
		showSuccess(truncated.value
			? t('sockless_mail', 'Copied the part that is shown')
			: t('sockless_mail', 'Copied to the clipboard'))
	} catch {
		showError(t('sockless_mail', 'Could not copy to the clipboard'))
	}
}

function download() {
	const link = document.createElement('a')
	link.href = emlUrl(props.where, props.messageId)
	link.download = ''
	link.click()
}

// NcDialog closes after a callback unless it returns false.
const buttons = computed(() => [
	{ label: t('sockless_mail', 'Download .eml'), callback: () => { download(); return false } },
	{ label: t('sockless_mail', 'Copy'), variant: 'primary', disabled: loading.value || !!error.value, callback: () => { copy(); return false } },
])
</script>

<style scoped>
.message-source {
	display: flex;
	flex-direction: column;
	gap: 8px;
	min-height: 0;
}

.message-source__subject {
	margin: 0;
	color: var(--color-text-maxcontrast);
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.message-source__center {
	display: flex;
	justify-content: center;
	padding: 32px;
}

.message-source__text {
	max-height: 60vh;
	margin: 0;
	padding: 10px 12px;
	overflow: auto;
	border-radius: var(--border-radius-element, 8px);
	background: var(--color-background-dark);
	color: var(--color-main-text);
	font-family: var(--font-face-monospace, monospace);
	font-size: 12px;
	line-height: 1.5;
	white-space: pre-wrap;
	overflow-wrap: anywhere;
	user-select: text;
}

.message-source__field {
	padding-inline-start: 2em;
	text-indent: -2em;
}

.message-source__field + .message-source__field {
	margin-top: 2px;
}

.message-source__name {
	font-weight: 600;
}
</style>
