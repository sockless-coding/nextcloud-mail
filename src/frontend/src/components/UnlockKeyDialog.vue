<template>
	<NcDialog :name="t('sockless_mail', 'Unlock your key')"
		:open="true"
		size="normal"
		@update:open="(open) => !open && finishUnlock(false)">
		<p class="unlock-dialog__reason">
			{{ unlockPrompt.reason }}
		</p>
		<KeyUnlockForm :key-ref="unlockPrompt.keys[0]" @unlocked="finishUnlock(true)" />
	</NcDialog>
</template>

<script setup>
import { t } from '@nextcloud/l10n'
import NcDialog from '@nextcloud/vue/components/NcDialog'
import { finishUnlock, unlockPrompt } from '../store/crypto.js'
import KeyUnlockForm from './KeyUnlockForm.vue'

/** Asks for a key's passphrase when an action needs it (see askUnlock in store/crypto.js). */
</script>

<style scoped>
.unlock-dialog__reason {
	margin-block-end: 12px;
}
</style>
