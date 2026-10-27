<template>
	<div class="account-details">
		<h2>{{ account.displayName }}</h2>
		<p class="account-details__email">
			{{ account.emailAddress }}
		</p>
		<dl>
			<dt>IMAP</dt>
			<dd>{{ describe(account.imap) }}</dd>
			<dt>SMTP</dt>
			<dd>{{ describe(account.smtp) }}</dd>
			<dt>{{ t('sockless_mail', 'Username') }}</dt>
			<dd>{{ account.username }}</dd>
		</dl>
		<div class="account-details__actions">
			<NcButton :disabled="testing" @click="test">
				<template #icon>
					<NcLoadingIcon v-if="testing" :size="20" />
				</template>
				{{ t('sockless_mail', 'Test connection') }}
			</NcButton>
			<NcButton @click="emit('edit')">
				{{ t('sockless_mail', 'Edit') }}
			</NcButton>
		</div>
		<ConnectionResult v-if="result" :result="result" />
		<NcNoteCard v-if="error" type="error" :text="error" />
		<NcNoteCard type="info" :text="t('sockless_mail', 'Folders and messages arrive in the next version.')" />
	</div>
</template>

<script setup>
import { t } from '@nextcloud/l10n'
import NcButton from '@nextcloud/vue/components/NcButton'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import NcNoteCard from '@nextcloud/vue/components/NcNoteCard'
import { ref, watch } from 'vue'
import { testStoredAccount } from '../store/accounts.js'
import ConnectionResult from './ConnectionResult.vue'

const props = defineProps({
	account: { type: Object, required: true },
})
const emit = defineEmits(['edit'])

const testing = ref(false)
const result = ref(null)
const error = ref('')

// A different account (or edited settings) invalidates the last result.
watch(() => props.account, () => {
	result.value = null
	error.value = ''
})

const SECURITY = { sslTls: 'SSL/TLS', startTls: 'STARTTLS', none: t('sockless_mail', 'unencrypted') }

function describe(server) {
	return `${server.host}:${server.port} (${SECURITY[server.security]})`
}

async function test() {
	testing.value = true
	result.value = null
	error.value = ''
	try {
		result.value = await testStoredAccount(props.account.id)
	} catch (e) {
		error.value = e.message
	} finally {
		testing.value = false
	}
}
</script>

<style scoped>
.account-details {
	max-width: 640px;
	margin: 0 auto;
	padding: 24px 16px;
}

.account-details__email {
	color: var(--color-text-maxcontrast);
	margin-bottom: 16px;
}

.account-details dl {
	display: grid;
	grid-template-columns: max-content 1fr;
	gap: 4px 16px;
	margin-bottom: 16px;
}

.account-details dt {
	font-weight: bold;
}

.account-details__actions {
	display: flex;
	gap: 8px;
	margin-bottom: 8px;
}
</style>
