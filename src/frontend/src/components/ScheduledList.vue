<template>
	<!-- Messages the server will send later, soonest first. -->
	<div class="scheduled-list">
		<header class="scheduled-list__header">
			<h2>{{ t('sockless_mail', 'Scheduled') }}</h2>
			<span v-if="scheduledState.loaded">{{ n('sockless_mail', '{count} message', '{count} messages', rows.length, { count: rows.length }) }}</span>
		</header>
		<p class="scheduled-list__hint">
			{{ t('sockless_mail', 'Sent by the server at their time, whether or not the app is open.') }}
		</p>
		<NcNoteCard v-if="scheduledState.error" type="error" :text="scheduledState.error" />
		<div v-if="!scheduledState.loaded && scheduledState.loading" class="scheduled-list__center">
			<NcLoadingIcon :size="44" />
		</div>
		<NcEmptyContent v-else-if="rows.length === 0"
			:name="t('sockless_mail', 'Nothing scheduled')"
			:description="t('sockless_mail', 'Use the clock button next to Send to send a message later.')">
			<template #icon>
				<NcIconSvgWrapper :path="mdiSendClockOutline" />
			</template>
		</NcEmptyContent>
		<ul v-else class="scheduled-list__rows" :aria-label="t('sockless_mail', 'Scheduled messages')">
			<li v-for="message in rows"
				:key="message.id"
				class="scheduled-row"
				:class="{ 'scheduled-row--open': scheduledState.open.id === message.id, 'scheduled-row--failed': message.status === 'failed' }"
				:data-id="message.id"
				tabindex="0"
				:aria-current="scheduledState.open.id === message.id ? 'true' : undefined"
				@click="openScheduled(message.id)"
				@keydown.enter.prevent="openScheduled(message.id)">
				<span class="scheduled-row__to">
					<span v-if="!accountId"
						class="scheduled-row__account"
						:style="{ background: accountColor(message.accountId) }"
						:title="accountName(message.accountId)" />
					{{ recipientsText(message) }}
				</span>
				<span class="scheduled-row__when" :class="`scheduled-row__when--${state(message)}`">
					<NcIconSvgWrapper :path="state(message) === 'failed' ? mdiAlertCircleOutline : mdiClockOutline" :size="16" />
					{{ whenText(message) }}
				</span>
				<span class="scheduled-row__subject">
					<NcIconSvgWrapper v-if="message.hasAttachments" :path="mdiPaperclip" :size="16" :name="t('sockless_mail', 'Has attachments')" />
					{{ message.subject || t('sockless_mail', '(no subject)') }}
				</span>
				<span v-if="message.error || message.preview" class="scheduled-row__preview" :class="{ 'scheduled-row__preview--error': message.error }">
					{{ message.error || message.preview }}
				</span>
			</li>
		</ul>
	</div>
</template>

<script setup>
import { mdiAlertCircleOutline, mdiClockOutline, mdiPaperclip, mdiSendClockOutline } from '@mdi/js'
import { n, t } from '@nextcloud/l10n'
import NcEmptyContent from '@nextcloud/vue/components/NcEmptyContent'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import NcNoteCard from '@nextcloud/vue/components/NcNoteCard'
import { computed } from 'vue'
import { accountColor, accountsState } from '../store/accounts.js'
import { mailboxState } from '../store/mailbox.js'
import { openScheduled, recipientsText, scheduledOf, scheduledState, scheduleStatus } from '../store/scheduled.js'
import { formatSendTime } from '../store/sendLater.js'

const accountId = computed(() => mailboxState.selected.accountId)
const rows = computed(() => scheduledOf(accountId.value))

const state = scheduleStatus

function accountName(id) {
	return accountsState.accounts.find((a) => a.id === id)?.displayName ?? ''
}

function whenText(message) {
	const when = formatSendTime(new Date(message.sendAt))
	return {
		failed: t('sockless_mail', 'Not sent'),
		retrying: t('sockless_mail', 'Retrying, was due {time}', { time: when }),
		sending: t('sockless_mail', 'Sending…'),
	}[state(message)] ?? when
}
</script>

<style scoped>
.scheduled-list {
	display: flex;
	flex-direction: column;
	flex: 1;
	min-height: 0;
}

.scheduled-list__header {
	display: flex;
	align-items: baseline;
	gap: 8px;
	padding: 12px 16px 2px;
	color: var(--color-text-maxcontrast);
}

.scheduled-list__header h2 {
	margin: 0;
	font-size: 18px;
	font-weight: 600;
	color: var(--color-main-text);
}

.scheduled-list__hint {
	margin: 0;
	padding: 0 16px 8px;
	color: var(--color-text-maxcontrast);
	font-size: 13px;
}

.scheduled-list__center {
	display: grid;
	place-items: center;
	padding: 48px 0;
}

.scheduled-list__rows {
	flex: 1;
	min-height: 0;
	overflow-y: auto;
	border-top: 1px solid var(--color-border);
}

.scheduled-row {
	display: grid;
	grid-template-columns: minmax(0, 1fr) auto;
	grid-template-areas:
		'to when'
		'subject subject'
		'preview preview';
	column-gap: 12px;
	padding: 8px 14px 8px 16px;
	border-bottom: 1px solid var(--color-border);
	cursor: pointer;
	outline: none;
}

.scheduled-row:hover {
	background: var(--color-background-hover);
}

.scheduled-row:focus-visible {
	box-shadow: inset 0 0 0 2px var(--color-primary-element);
}

.scheduled-row--open {
	outline: 2px solid var(--color-primary-element);
	outline-offset: -2px;
}

.scheduled-row__to {
	grid-area: to;
	display: flex;
	align-items: center;
	gap: 6px;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
	font-weight: 600;
}

.scheduled-row__account {
	flex: none;
	width: 10px;
	height: 10px;
	border-radius: 50%;
}

.scheduled-row__when {
	grid-area: when;
	display: inline-flex;
	align-items: center;
	gap: 4px;
	color: var(--color-primary-element);
	font-size: 13px;
	white-space: nowrap;
}

.scheduled-row__when--failed {
	color: var(--color-text-error, var(--color-error));
	font-weight: 600;
}

.scheduled-row__when--retrying {
	color: var(--color-warning-text, var(--color-warning));
}

.scheduled-row__subject {
	grid-area: subject;
	display: flex;
	align-items: center;
	gap: 4px;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.scheduled-row__preview {
	grid-area: preview;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
	color: var(--color-text-maxcontrast);
	font-size: 13px;
}

.scheduled-row__preview--error {
	color: var(--color-text-error, var(--color-error));
}
</style>
