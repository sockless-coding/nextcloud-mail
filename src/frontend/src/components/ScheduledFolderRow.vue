<template>
	<!-- The Scheduled view in the folder pane: shown while something is scheduled (or it is open). -->
	<div v-if="count || active"
		class="scheduled-folder"
		:class="{ 'scheduled-folder--active': active, 'scheduled-folder--failed': failed > 0 }">
		<button class="scheduled-folder__button"
			data-view="scheduled"
			:aria-current="active ? 'page' : undefined"
			@click="showScheduled(accountId)">
			<NcIconSvgWrapper :path="mdiSendClockOutline" :size="20" />
			<span class="scheduled-folder__name">{{ t('sockless_mail', 'Scheduled') }}</span>
			<NcIconSvgWrapper v-if="failed"
				:path="mdiAlertCircleOutline"
				:size="16"
				class="scheduled-folder__failed"
				:name="n('sockless_mail', '{count} not sent', '{count} not sent', failed, { count: failed })" />
			<span v-if="count" class="scheduled-folder__count">{{ count }}</span>
		</button>
	</div>
</template>

<script setup>
import { mdiAlertCircleOutline, mdiSendClockOutline } from '@mdi/js'
import { n, t } from '@nextcloud/l10n'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import { computed } from 'vue'
import { mailboxState } from '../store/mailbox.js'
import { failedCount, scheduledOf, showScheduled } from '../store/scheduled.js'

const props = defineProps({
	/** The account whose scheduled messages this is; null for all accounts. */
	accountId: { type: String, default: null },
})

const active = computed(() => !!mailboxState.selected.scheduled && mailboxState.selected.accountId === props.accountId)
const count = computed(() => scheduledOf(props.accountId).length)
const failed = computed(() => failedCount(props.accountId))
</script>

<style scoped>
.scheduled-folder {
	display: flex;
	border-radius: var(--border-radius-element, 8px);
}

.scheduled-folder:hover {
	background: var(--color-background-hover);
}

.scheduled-folder--active,
.scheduled-folder--active:hover {
	background: var(--color-primary-element-light);
	color: var(--color-primary-element-light-text);
}

.scheduled-folder__button {
	flex: 1;
	display: flex;
	align-items: center;
	gap: 10px;
	min-height: 34px;
	margin: 0;
	padding: 0 8px;
	border: 0;
	background: none;
	color: inherit;
	font: inherit;
	cursor: pointer;
}

.scheduled-folder__button:focus-visible {
	outline: 2px solid var(--color-primary-element);
	outline-offset: -2px;
	border-radius: var(--border-radius-element, 8px);
}

.scheduled-folder__name {
	flex: 1;
	min-width: 0;
	overflow: hidden;
	text-align: start;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.scheduled-folder--active .scheduled-folder__name {
	font-weight: 600;
}

.scheduled-folder__failed {
	color: var(--color-text-error, var(--color-error));
}

.scheduled-folder__count {
	font-size: 12px;
	font-weight: 600;
	font-variant-numeric: tabular-nums;
	color: var(--color-text-maxcontrast);
}

.scheduled-folder--active .scheduled-folder__count {
	color: inherit;
}
</style>
