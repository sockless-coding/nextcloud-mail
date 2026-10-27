<template>
	<div class="connection-result">
		<NcNoteCard v-for="item in items"
			:key="item.label"
			:type="item.ok ? 'success' : 'error'"
			:heading="item.ok ? t('sockless_mail', '{protocol}: connected', { protocol: item.label }) : t('sockless_mail', '{protocol}: failed', { protocol: item.label })"
			:text="item.ok ? '' : item.message" />
	</div>
</template>

<script setup>
import { t } from '@nextcloud/l10n'
import NcNoteCard from '@nextcloud/vue/components/NcNoteCard'
import { computed } from 'vue'

const props = defineProps({
	/** Result of POST /api/accounts/test: { imap: {ok, errorCode, message}, smtp: {...} } */
	result: { type: Object, required: true },
})

const items = computed(() => [
	{ label: 'IMAP', ...props.result.imap },
	{ label: 'SMTP', ...props.result.smtp },
])
</script>

<style scoped>
.connection-result :deep(.notecard) {
	margin: 8px 0;
}
</style>
