<template>
	<!-- No close button and no Escape: a half-written message must not vanish by accident.
	     The dialog closes itself through its buttons; App.vue unmounts it afterwards. -->
	<NcDialog :name="title"
		size="large"
		no-close
		:open="true"
		:buttons="buttons"
		@update:open="(open) => !open && closeComposer(sent)">
		<div class="composer">
			<label class="composer__from">
				<span>{{ t('sockless_mail', 'From') }}</span>
				<select v-model="draft.accountId" :disabled="!!draft.source">
					<option v-for="account in accountsState.accounts" :key="account.id" :value="account.id">
						{{ account.senderName ? `${account.senderName} <${account.emailAddress}>` : account.emailAddress }}
					</option>
				</select>
			</label>
			<div class="composer__row">
				<NcTextField v-model="draft.to"
					class="composer__grow"
					:label="t('sockless_mail', 'To')"
					:error="!!errors.to"
					:helper-text="errors.to ?? ''"
					autocomplete="off" />
				<NcButton v-if="!showCopies" variant="tertiary" @click="showCopies = true">
					{{ t('sockless_mail', 'Cc/Bcc') }}
				</NcButton>
			</div>
			<template v-if="showCopies">
				<NcTextField v-model="draft.cc"
					:label="t('sockless_mail', 'Cc')"
					:error="!!errors.cc"
					:helper-text="errors.cc ?? ''"
					autocomplete="off" />
				<NcTextField v-model="draft.bcc"
					:label="t('sockless_mail', 'Bcc')"
					:error="!!errors.bcc"
					:helper-text="errors.bcc ?? t('sockless_mail', 'Bcc recipients are hidden from everyone else.')"
					autocomplete="off" />
			</template>
			<NcTextField v-model="draft.subject"
				:label="t('sockless_mail', 'Subject')"
				:error="!!errors.subject"
				:helper-text="errors.subject ?? ''" />
			<NcTextArea ref="body"
				v-model="draft.text"
				class="composer__body"
				:label="t('sockless_mail', 'Message')"
				:error="!!errors.text"
				:helper-text="errors.text ?? ''"
				resize="vertical"
				rows="14" />

			<div class="composer__attachments">
				<input ref="fileInput"
					type="file"
					multiple
					hidden
					@change="onFiles">
				<NcButton variant="secondary" @click="fileInput.click()">
					<template #icon>
						<NcIconSvgWrapper :path="mdiPaperclip" />
					</template>
					{{ t('sockless_mail', 'Attach files') }}
				</NcButton>
				<ul v-if="draft.uploads.length || draft.forwarded.length" :aria-label="t('sockless_mail', 'Attachments')">
					<li v-for="item in draft.forwarded" :key="`fwd-${item.partId}`" class="composer__chip" :class="{ 'composer__chip--off': !item.include }">
						<span class="composer__chip-name">{{ item.fileName }}</span>
						<NcButton variant="tertiary"
							:aria-label="item.include ? t('sockless_mail', 'Do not forward {name}', { name: item.fileName }) : t('sockless_mail', 'Forward {name}', { name: item.fileName })"
							@click="item.include = !item.include">
							<template #icon>
								<NcIconSvgWrapper :path="item.include ? mdiClose : mdiUndo" />
							</template>
						</NcButton>
					</li>
					<li v-for="upload in draft.uploads" :key="upload.key" class="composer__chip" :class="{ 'composer__chip--error': upload.error }">
						<span class="composer__chip-name">{{ upload.fileName }}</span>
						<span v-if="upload.error" class="composer__chip-meta">{{ upload.error }}</span>
						<span v-else-if="!upload.id" class="composer__chip-meta">{{ Math.round(upload.progress * 100) }} %</span>
						<NcButton variant="tertiary" :aria-label="t('sockless_mail', 'Remove {name}', { name: upload.fileName })" @click="detach(upload)">
							<template #icon>
								<NcIconSvgWrapper :path="mdiClose" />
							</template>
						</NcButton>
					</li>
				</ul>
			</div>

			<NcNoteCard v-if="errors.attachmentIds" type="error" :text="errors.attachmentIds" />
			<NcNoteCard v-if="generalError" type="error" :text="generalError" />
		</div>
	</NcDialog>
</template>

<script setup>
import { mdiClose, mdiPaperclip, mdiUndo } from '@mdi/js'
import { t } from '@nextcloud/l10n'
import NcButton from '@nextcloud/vue/components/NcButton'
import NcDialog from '@nextcloud/vue/components/NcDialog'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import NcNoteCard from '@nextcloud/vue/components/NcNoteCard'
import NcTextArea from '@nextcloud/vue/components/NcTextArea'
import NcTextField from '@nextcloud/vue/components/NcTextField'
import { computed, ref } from 'vue'
import { accountsState } from '../store/accounts.js'
import { attach, closeComposer, composer, detach, send } from '../store/compose.js'

const draft = composer.draft
const fileInput = ref(null)
const showCopies = ref(!!(draft.cc || draft.bcc))
const errors = ref({})
const generalError = ref('')
let sent = false

const title = computed(() => ({
	reply: t('sockless_mail', 'Reply'),
	replyAll: t('sockless_mail', 'Reply to all'),
	forward: t('sockless_mail', 'Forward'),
})[draft.source?.mode] ?? t('sockless_mail', 'New message'))

const uploading = computed(() => draft.uploads.some((u) => !u.id && !u.error))

const buttons = computed(() => [
	{ label: t('sockless_mail', 'Discard') },
	{
		label: uploading.value ? t('sockless_mail', 'Uploading…') : t('sockless_mail', 'Send'),
		variant: 'primary',
		disabled: uploading.value,
		callback: async () => {
			errors.value = {}
			generalError.value = ''
			try {
				await send()
				sent = true
			} catch (e) {
				errors.value = e.fieldErrors ?? {}
				if (errors.value.cc || errors.value.bcc) {
					showCopies.value = true
				}
				if (!Object.keys(errors.value).length) {
					generalError.value = e.message
				}
				return false
			}
		},
	},
])

function onFiles(event) {
	for (const file of event.target.files) {
		attach(file)
	}
	event.target.value = ''
}
</script>

<style scoped>
.composer {
	display: flex;
	flex-direction: column;
	gap: 8px;
	padding-bottom: 8px;
}

.composer__from {
	display: flex;
	align-items: center;
	gap: 8px;
}

.composer__from select {
	flex: 1;
	max-width: 480px;
}

.composer__row {
	display: flex;
	align-items: flex-end;
	gap: 8px;
}

.composer__grow {
	flex: 1;
}

.composer__body :deep(textarea) {
	min-height: 240px;
	font-family: var(--font-face-monospace, monospace);
}

.composer__attachments ul {
	display: flex;
	flex-wrap: wrap;
	gap: 6px;
	margin-top: 8px;
}

.composer__chip {
	display: inline-flex;
	align-items: center;
	gap: 6px;
	padding-inline-start: 12px;
	border: 1px solid var(--color-border);
	border-radius: var(--border-radius-element, 8px);
}

.composer__chip--off .composer__chip-name {
	text-decoration: line-through;
	color: var(--color-text-maxcontrast);
}

.composer__chip--error {
	border-color: var(--color-error);
}

.composer__chip-meta {
	color: var(--color-text-maxcontrast);
}
</style>
