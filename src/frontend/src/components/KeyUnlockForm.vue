<template>
	<form class="key-unlock" @submit.prevent="submit">
		<NcPasswordField v-model="passphrase"
			class="key-unlock__field"
			:label="label"
			:error="!!error"
			:helper-text="error"
			autocomplete="off"
			:disabled="busy" />
		<NcButton type="submit" variant="primary" :disabled="busy || !passphrase">
			{{ t('sockless_mail', 'Unlock') }}
		</NcButton>
	</form>
</template>

<script setup>
import { t } from '@nextcloud/l10n'
import NcButton from '@nextcloud/vue/components/NcButton'
import NcPasswordField from '@nextcloud/vue/components/NcPasswordField'
import { ref } from 'vue'
import { errorText, unlockKey } from '../store/crypto.js'

/**
 * Asks for the passphrase of one private key and unlocks it on the server. Emits "unlocked"
 * with the key once the passphrase was right.
 */
const props = defineProps({
	/** The key: { id, kind, addresses, fingerprint }. */
	keyRef: { type: Object, required: true },
})
const emit = defineEmits(['unlocked'])

const passphrase = ref('')
const error = ref('')
const busy = ref(false)

const label = props.keyRef.addresses?.length
	? t('sockless_mail', 'Passphrase for {address}', { address: props.keyRef.addresses[0] })
	: t('sockless_mail', 'Passphrase')

async function submit() {
	busy.value = true
	error.value = ''
	try {
		const key = await unlockKey(props.keyRef.id, passphrase.value)
		passphrase.value = ''
		emit('unlocked', key)
	} catch (e) {
		error.value = errorText(e)
	} finally {
		busy.value = false
	}
}
</script>

<style scoped>
.key-unlock {
	display: flex;
	align-items: flex-start;
	gap: 8px;
	max-width: 480px;
}

.key-unlock__field {
	flex: 1 1 auto;
}

.key-unlock :deep(.button-vue) {
	margin-top: 6px;
}
</style>
