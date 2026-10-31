<template>
	<!-- Picks the date and time a message is sent at, in the browser's time zone. -->
	<NcDialog :name="t('sockless_mail', 'Send later')"
		:open="true"
		size="small"
		:buttons="buttons"
		@update:open="(open) => !open && emit('close')">
		<form class="send-later" @submit.prevent="choose">
			<NcDateTimePickerNative id="sockless-send-later"
				v-model="when"
				type="datetime-local"
				:label="t('sockless_mail', 'Date and time')"
				:min="earliest" />
			<p class="send-later__hint" :class="{ 'send-later__hint--error': problem }">
				{{ problem || t('sockless_mail', 'Sent by the server: you can close the browser meanwhile. Time zone: {zone}', { zone }) }}
			</p>
		</form>
	</NcDialog>
</template>

<script setup>
import { t } from '@nextcloud/l10n'
import NcDateTimePickerNative from '@nextcloud/vue/components/NcDateTimePickerNative'
import NcDialog from '@nextcloud/vue/components/NcDialog'
import { computed, ref } from 'vue'
import { earliestSendTime, formatSendTime } from '../store/sendLater.js'

const props = defineProps({
	/** Where to start: e.g. the current time of a message being rescheduled. */
	initial: { type: Date, default: null },
})

const emit = defineEmits(['choose', 'close'])

const earliest = earliestSendTime()

/** Tomorrow at 08:00 unless told otherwise: a sensible place to start picking from. */
function defaultTime() {
	const d = new Date()
	d.setDate(d.getDate() + 1)
	d.setHours(8, 0, 0, 0)
	return d
}

const when = ref(props.initial ?? defaultTime())
const zone = Intl.DateTimeFormat().resolvedOptions().timeZone

const problem = computed(() => {
	if (!when.value || Number.isNaN(when.value.getTime())) {
		return t('sockless_mail', 'Choose a date and a time.')
	}
	if (when.value < earliestSendTime()) {
		return t('sockless_mail', 'Choose a time at least a few minutes from now.')
	}
	return ''
})

function choose() {
	if (!problem.value) {
		emit('choose', when.value)
	}
}

const buttons = computed(() => [
	{ label: t('sockless_mail', 'Cancel') },
	{
		label: problem.value ? t('sockless_mail', 'Schedule') : t('sockless_mail', 'Send {time}', { time: formatSendTime(when.value) }),
		variant: 'primary',
		disabled: !!problem.value,
		callback: () => { choose() },
	},
])
</script>

<style scoped>
.send-later {
	display: flex;
	flex-direction: column;
	gap: 8px;
	padding-bottom: 8px;
}

.send-later__hint {
	color: var(--color-text-maxcontrast);
	margin: 0;
}

.send-later__hint--error {
	color: var(--color-text-error, var(--color-error));
}
</style>
