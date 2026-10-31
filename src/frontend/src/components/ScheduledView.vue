<template>
	<!-- A scheduled message, read-only, with what can still be done about it. -->
	<div class="scheduled-view">
		<div v-if="open.loading" class="scheduled-view__center">
			<NcLoadingIcon :size="44" />
		</div>
		<NcEmptyContent v-else-if="open.error"
			:name="t('sockless_mail', 'Could not open the message')"
			:description="open.error" />
		<article v-else-if="detail">
			<header class="scheduled-view__header">
				<div class="scheduled-view__title">
					<button v-if="readingPane === 'off'"
						class="scheduled-view__icon-button"
						:aria-label="t('sockless_mail', 'Back to the list')"
						:title="t('sockless_mail', 'Back to the list')"
						@click="closeScheduled">
						<NcIconSvgWrapper :path="mdiArrowLeft" :size="20" />
					</button>
					<h2>{{ message.subject || t('sockless_mail', '(no subject)') }}</h2>
				</div>

				<NcNoteCard v-if="status === 'failed'" type="error">
					<p><strong>{{ t('sockless_mail', 'This message was not sent.') }}</strong> {{ message.error }}</p>
					<p>{{ t('sockless_mail', 'Try again now, choose a new time, or edit it.') }}</p>
				</NcNoteCard>
				<NcNoteCard v-else-if="status === 'retrying'"
					type="warning"
					:text="t('sockless_mail', 'The last attempt failed: {error} The next attempt is at {time}.', { error: message.error, time: formatSendTime(new Date(message.dueAt)) })" />
				<p v-else class="scheduled-view__when">
					<NcIconSvgWrapper :path="mdiClockOutline" :size="20" />
					<span>{{ t('sockless_mail', 'Sends {time}', { time: longTime(message.sendAt) }) }}</span>
				</p>

				<dl class="scheduled-view__people">
					<dt>{{ t('sockless_mail', 'From') }}</dt>
					<dd>{{ fromText }}</dd>
					<template v-for="field in addressFields" :key="field.label">
						<dt>{{ field.label }}</dt>
						<dd>{{ field.text }}</dd>
					</template>
				</dl>

				<div class="scheduled-view__actions">
					<NcButton variant="primary" :disabled="busy" @click="sendNow">
						<template #icon>
							<NcLoadingIcon v-if="busy" :size="20" />
							<NcIconSvgWrapper v-else :path="mdiSend" />
						</template>
						{{ status === 'failed' ? t('sockless_mail', 'Try again now') : t('sockless_mail', 'Send now') }}
					</NcButton>
					<NcActions variant="secondary"
						:disabled="busy"
						:menu-name="t('sockless_mail', 'Reschedule')"
						@open="menuOpenedAt = new Date()">
						<template #icon>
							<NcIconSvgWrapper :path="mdiCalendarClock" />
						</template>
						<NcActionButton v-for="option in laterOptions"
							:key="option.preset.id"
							close-after-click
							:description="formatSendTime(option.when, menuOpenedAt)"
							@click="moveTo(option.when)">
							<template #icon>
								<NcIconSvgWrapper :path="mdiClockOutline" />
							</template>
							{{ option.preset.label }}
						</NcActionButton>
						<NcActionButton close-after-click @click="pickingTime = true">
							<template #icon>
								<NcIconSvgWrapper :path="mdiCalendarClock" />
							</template>
							{{ t('sockless_mail', 'Pick date and time…') }}
						</NcActionButton>
					</NcActions>
					<NcButton :disabled="busy" @click="edit">
						<template #icon>
							<NcIconSvgWrapper :path="mdiPencilOutline" />
						</template>
						{{ t('sockless_mail', 'Edit') }}
					</NcButton>
					<NcButton variant="error" :disabled="busy" @click="confirmCancel = true">
						<template #icon>
							<NcIconSvgWrapper :path="mdiCancel" />
						</template>
						{{ t('sockless_mail', 'Cancel sending') }}
					</NcButton>
				</div>
				<p v-if="actionError" class="scheduled-view__error" role="alert">
					{{ actionError }}
				</p>
			</header>

			<ul v-if="detail.attachments.length" class="scheduled-view__attachments" :aria-label="t('sockless_mail', 'Attachments')">
				<li v-for="(attachment, i) in detail.attachments" :key="i" class="scheduled-view__attachment">
					<NcIconSvgWrapper :path="mdiPaperclip" :size="16" />
					<span class="scheduled-view__attachment-name">{{ attachment.fileName }}</span>
					<span class="scheduled-view__attachment-size">{{ formatFileSize(attachment.size) }}</span>
				</li>
			</ul>

			<div class="scheduled-view__body">
				<MessageBody :key="message.id" :html="detail.html || '<p></p>'" />
			</div>
		</article>

		<SendLaterDialog v-if="pickingTime"
			:initial="new Date(message.sendAt)"
			@choose="timeChosen"
			@close="pickTimeClosed" />

		<NcDialog v-if="confirmCancel"
			:name="t('sockless_mail', 'Cancel sending?')"
			:message="t('sockless_mail', 'The message is not sent. It goes back to Drafts, where you can continue it.')"
			:buttons="cancelButtons"
			:open="true"
			@update:open="(isOpen) => !isOpen && cancelClosed()" />
	</div>
</template>

<script setup>
import { mdiArrowLeft, mdiCalendarClock, mdiCancel, mdiClockOutline, mdiPaperclip, mdiPencilOutline, mdiSend } from '@mdi/js'
import { showSuccess, showWarning } from '@nextcloud/dialogs/toast'
import { getCanonicalLocale, t } from '@nextcloud/l10n'
import NcActionButton from '@nextcloud/vue/components/NcActionButton'
import NcActions from '@nextcloud/vue/components/NcActions'
import NcButton from '@nextcloud/vue/components/NcButton'
import NcDialog from '@nextcloud/vue/components/NcDialog'
import NcEmptyContent from '@nextcloud/vue/components/NcEmptyContent'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import NcNoteCard from '@nextcloud/vue/components/NcNoteCard'
import { computed, ref } from 'vue'
import { formatFileSize } from '../fileSize.js'
import { accountsState } from '../store/accounts.js'
import { composeEdit, composerInUse } from '../store/compose.js'
import { readingPane } from '../store/layout.js'
import { cancelScheduled, closeScheduled, reschedule, scheduledState, scheduleStatus, sendScheduledNow } from '../store/scheduled.js'
import { formatSendTime, offeredPresets } from '../store/sendLater.js'
import MessageBody from './MessageBody.vue'
import SendLaterDialog from './SendLaterDialog.vue'

const open = scheduledState.open
const detail = computed(() => open.detail)
const message = computed(() => open.detail?.message)
const status = computed(() => message.value && scheduleStatus(message.value))
const busy = computed(() => !!message.value && !!scheduledState.busy[message.value.id])
const actionError = ref('')

const account = computed(() => accountsState.accounts.find((a) => a.id === message.value?.accountId))
const fromText = computed(() => account.value
	? (account.value.senderName ? `${account.value.senderName} <${account.value.emailAddress}>` : account.value.emailAddress)
	: '')

const list = (addresses) => addresses.map((a) => (a.name ? `${a.name} <${a.address}>` : a.address)).join(', ')
const addressFields = computed(() => [
	{ label: t('sockless_mail', 'To'), text: list(detail.value.to) },
	{ label: t('sockless_mail', 'Cc'), text: list(detail.value.cc) },
	{ label: t('sockless_mail', 'Bcc'), text: list(detail.value.bcc) },
].filter((f) => f.text))

const longFormat = new Intl.DateTimeFormat(getCanonicalLocale(), { dateStyle: 'full', timeStyle: 'short' })
const longTime = (iso) => longFormat.format(new Date(iso))

const menuOpenedAt = ref(new Date())
const laterOptions = computed(() => offeredPresets(undefined, menuOpenedAt.value))

async function run(action) {
	actionError.value = ''
	try {
		await action()
	} catch (e) {
		actionError.value = e.message
	}
}

const sendNow = () => run(() => sendScheduledNow(message.value))
const moveTo = (when) => run(() => reschedule(message.value, when))

/** Back to Drafts and into the composer, to be sent or scheduled again from there. */
async function edit() {
	if (composerInUse()) {
		return
	}
	const target = message.value
	await run(async () => {
		const draft = await cancelScheduled(target)
		if (draft) {
			await composeEdit({ accountId: target.accountId, folderId: draft.folderId }, draft.messageId)
		} else {
			showWarning(t('sockless_mail', 'The message is back in Drafts. Open it there to continue.'))
		}
	})
}

// Both dialogs act once they have closed: unmounting an NcDialog while it closes breaks its focus trap.
const pickingTime = ref(false)
let pickedTime = null

function timeChosen(when) {
	pickedTime = when
}

function pickTimeClosed() {
	pickingTime.value = false
	const when = pickedTime
	pickedTime = null
	if (when) {
		moveTo(when)
	}
}

const confirmCancel = ref(false)
let cancelChosen = false

function cancelClosed() {
	confirmCancel.value = false
	if (cancelChosen) {
		cancelChosen = false
		const target = message.value
		run(async () => {
			await cancelScheduled(target)
			showSuccess(t('sockless_mail', 'Not sent. The message is back in Drafts.'))
		})
	}
}

const cancelButtons = computed(() => [
	{ label: t('sockless_mail', 'Keep it scheduled') },
	{ label: t('sockless_mail', 'Cancel sending'), variant: 'error', callback: () => { cancelChosen = true } },
])
</script>

<style scoped>
.scheduled-view {
	height: 100%;
	overflow-y: auto;
}

.scheduled-view__center {
	display: grid;
	place-items: center;
	height: 100%;
}

.scheduled-view__header {
	display: flex;
	flex-direction: column;
	gap: 12px;
	padding: 16px 20px 12px;
	border-bottom: 1px solid var(--color-border);
}

.scheduled-view__title {
	display: flex;
	align-items: center;
	gap: 8px;
}

.scheduled-view__title h2 {
	margin: 0;
	font-size: 20px;
	font-weight: 600;
	overflow-wrap: anywhere;
}

.scheduled-view__icon-button {
	display: grid;
	place-items: center;
	width: 34px;
	height: 34px;
	min-height: 0;
	margin: 0;
	padding: 0;
	border: 0;
	border-radius: 50%;
	background: transparent;
	color: var(--color-main-text);
	cursor: pointer;
}

.scheduled-view__icon-button:hover {
	background: var(--color-background-hover);
}

.scheduled-view__when {
	display: flex;
	align-items: center;
	gap: 8px;
	margin: 0;
	padding: 8px 12px;
	border-radius: var(--border-radius-large);
	background: var(--color-primary-element-light);
	color: var(--color-primary-element-light-text);
	font-weight: 600;
}

.scheduled-view__people {
	display: grid;
	grid-template-columns: auto minmax(0, 1fr);
	gap: 2px 12px;
	margin: 0;
}

.scheduled-view__people dt {
	color: var(--color-text-maxcontrast);
}

.scheduled-view__people dd {
	margin: 0;
	overflow-wrap: anywhere;
}

.scheduled-view__actions {
	display: flex;
	flex-wrap: wrap;
	gap: 8px;
}

.scheduled-view__error {
	margin: 0;
	color: var(--color-text-error, var(--color-error));
}

.scheduled-view__attachments {
	display: flex;
	flex-wrap: wrap;
	gap: 8px;
	padding: 12px 20px 0;
}

.scheduled-view__attachment {
	display: inline-flex;
	align-items: center;
	gap: 6px;
	padding: 4px 10px;
	border: 1px solid var(--color-border);
	border-radius: var(--border-radius-large);
}

.scheduled-view__attachment-size {
	color: var(--color-text-maxcontrast);
	font-size: 13px;
}

.scheduled-view__body {
	padding: 12px 20px 20px;
}
</style>
