<template>
	<!-- Closing goes through NcDialog's buttons (see AccountDialog.vue for why). -->
	<NcDialog :name="prompt.title"
		:open="true"
		size="small"
		:buttons="buttons"
		@update:open="(open) => !open && emit('close')">
		<NcTextField v-model="name"
			:label="t('sockless_mail', 'Folder name')"
			:error="!!error"
			:helper-text="error"
			autocomplete="off" />
	</NcDialog>
</template>

<script setup>
import { t } from '@nextcloud/l10n'
import NcDialog from '@nextcloud/vue/components/NcDialog'
import NcTextField from '@nextcloud/vue/components/NcTextField'
import { computed, ref } from 'vue'

const props = defineProps({
	/** { title, value, submitLabel, submit(name) -> Promise } from store/actions.js */
	prompt: { type: Object, required: true },
})
const emit = defineEmits(['close'])

const name = ref(props.prompt.value)
const error = ref('')

const buttons = computed(() => [
	{ label: t('sockless_mail', 'Cancel') },
	{
		label: props.prompt.submitLabel,
		variant: 'primary',
		callback: async () => {
			error.value = ''
			try {
				await props.prompt.submit(name.value)
			} catch (e) {
				error.value = e.fieldErrors?.name ?? e.message
				return false
			}
		},
	},
])
</script>
