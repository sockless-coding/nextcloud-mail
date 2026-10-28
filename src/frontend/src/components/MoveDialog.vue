<template>
	<!-- Closing goes through NcDialog's buttons (see AccountDialog.vue for why). -->
	<NcDialog :name="n('sockless_mail', 'Move message', 'Move {count} messages', request.messageIds.length, { count: request.messageIds.length })"
		:open="true"
		size="small"
		:buttons="buttons"
		@update:open="(open) => !open && emit('close')">
		<NcTextField v-model="filter"
			:label="t('sockless_mail', 'Find folder')"
			autocomplete="off" />
		<div class="move-dialog__folders" role="radiogroup" :aria-label="t('sockless_mail', 'Target folder')">
			<NcCheckboxRadioSwitch v-for="folder in shown"
				:key="folder.id"
				v-model="target"
				type="radio"
				name="move-target"
				:value="folder.id">
				{{ folder.path }}
			</NcCheckboxRadioSwitch>
			<p v-if="shown.length === 0" class="move-dialog__empty">
				{{ t('sockless_mail', 'No matching folder') }}
			</p>
		</div>
	</NcDialog>
</template>

<script setup>
import { n, t } from '@nextcloud/l10n'
import NcCheckboxRadioSwitch from '@nextcloud/vue/components/NcCheckboxRadioSwitch'
import NcDialog from '@nextcloud/vue/components/NcDialog'
import NcTextField from '@nextcloud/vue/components/NcTextField'
import { computed, ref } from 'vue'
import { moveTargets, moveTo } from '../store/actions.js'

const props = defineProps({
	/** { where, messageIds } from dialogs.move */
	request: { type: Object, required: true },
})
const emit = defineEmits(['close'])

const filter = ref('')
const target = ref('')
const targets = moveTargets(props.request.where)
const shown = computed(() => {
	const term = filter.value.trim().toLowerCase()
	return term ? targets.filter((f) => f.path.toLowerCase().includes(term)) : targets
})

const buttons = computed(() => [
	{ label: t('sockless_mail', 'Cancel') },
	{
		label: t('sockless_mail', 'Move'),
		variant: 'primary',
		disabled: !target.value,
		// The list updates at once; failures arrive as a toast after the dialog closed.
		callback: () => {
			moveTo(props.request, target.value)
		},
	},
])
</script>

<style scoped>
.move-dialog__folders {
	margin-top: 8px;
	max-height: 50vh;
	overflow-y: auto;
}

.move-dialog__empty {
	padding: 8px;
	color: var(--color-text-maxcontrast);
}
</style>
