<template>
	<!-- Closing goes through NcDialog's own buttons, which hide the modal and release its focus
	     trap before update:open(false); only then may the parent unmount this component. -->
	<NcDialog :name="account ? t('sockless_mail', 'Edit account') : t('sockless_mail', 'Add mail account')"
		size="normal"
		:buttons="buttons"
		:open="true"
		@update:open="(open) => !open && emit('close')">
		<div class="account-dialog">
			<NcTextField v-model="form.emailAddress"
				type="email"
				:label="t('sockless_mail', 'Email address')"
				:error="!!errors.emailAddress"
				:helper-text="errors.emailAddress ?? ''"
				autocomplete="email"
				@update:model-value="suggestFromEmail" />
			<NcTextField v-model="form.displayName"
				:label="t('sockless_mail', 'Account name')"
				:error="!!errors.displayName"
				:helper-text="errors.displayName ?? ''"
				@update:model-value="touched.displayName = true" />
			<NcTextField v-model="form.username"
				:label="t('sockless_mail', 'Username')"
				:error="!!errors.username"
				:helper-text="errors.username ?? ''"
				autocomplete="off"
				@update:model-value="touched.username = true" />
			<NcPasswordField v-model="form.password"
				:label="t('sockless_mail', 'Password')"
				:placeholder="account ? t('sockless_mail', 'Unchanged') : ''"
				:error="!!errors.password"
				:helper-text="errors.password ?? (account ? t('sockless_mail', 'Leave empty to keep the stored password.') : '')"
				autocomplete="new-password" />

			<ServerFields :legend="t('sockless_mail', 'Incoming mail (IMAP)')"
				prefix="imap"
				:server="form.imap"
				:errors="errors"
				@input="touched.imap = true" />
			<ServerFields :legend="t('sockless_mail', 'Outgoing mail (SMTP)')"
				prefix="smtp"
				:server="form.smtp"
				:errors="errors"
				@input="touched.smtp = true" />

			<NcNoteCard v-if="form.imap.security === 'none' || form.smtp.security === 'none'"
				type="warning"
				:text="t('sockless_mail', 'Without encryption, the password and your mail travel over the network unprotected.')" />
			<ConnectionResult v-if="testResult" :result="testResult" />
			<NcNoteCard v-if="generalError" type="error" :text="generalError" />
		</div>
	</NcDialog>
</template>

<script setup>
import { t } from '@nextcloud/l10n'
import NcDialog from '@nextcloud/vue/components/NcDialog'
import NcNoteCard from '@nextcloud/vue/components/NcNoteCard'
import NcPasswordField from '@nextcloud/vue/components/NcPasswordField'
import NcTextField from '@nextcloud/vue/components/NcTextField'
import { computed, reactive, ref } from 'vue'
import { createAccount, testConnection, updateAccount, ValidationError } from '../store/accounts.js'
import ConnectionResult from './ConnectionResult.vue'
import ServerFields from './ServerFields.vue'

const props = defineProps({
	/** The account to edit; null to add a new one. */
	account: { type: Object, default: null },
})
const emit = defineEmits(['close', 'saved'])

const form = reactive(props.account
	? {
		displayName: props.account.displayName,
		emailAddress: props.account.emailAddress,
		username: props.account.username,
		password: '',
		imap: { ...props.account.imap },
		smtp: { ...props.account.smtp },
	}
	: {
		displayName: '',
		emailAddress: '',
		username: '',
		password: '',
		imap: { host: '', port: 993, security: 'sslTls' },
		smtp: { host: '', port: 587, security: 'startTls' },
	})

// Fields the user typed into are no longer filled in from the email address.
const touched = reactive({ displayName: !!props.account, username: !!props.account, imap: !!props.account, smtp: !!props.account })
const errors = ref({})
const generalError = ref('')
const testResult = ref(null)

// NcDialog awaits each callback (showing a spinner); returning false keeps the dialog open.
const buttons = computed(() => [
	{ label: t('sockless_mail', 'Test connection'), callback: async () => { await test(); return false } },
	{ label: t('sockless_mail', 'Cancel') },
	{
		label: props.account ? t('sockless_mail', 'Save') : t('sockless_mail', 'Add account'),
		variant: 'primary',
		callback: save,
	},
])

/**
 * Common defaults while typing the address of a new account: login with the full address,
 * imap.<domain> and smtp.<domain>.
 *
 * @param {string} email the address typed so far
 */
function suggestFromEmail(email) {
	const domain = email.includes('@') ? email.split('@').pop().trim() : ''
	if (!touched.username) {
		form.username = email.trim()
	}
	if (!touched.displayName) {
		form.displayName = email.trim()
	}
	if (!touched.imap && domain) {
		form.imap.host = `imap.${domain}`
	}
	if (!touched.smtp && domain) {
		form.smtp.host = `smtp.${domain}`
	}
}

function payload() {
	return {
		displayName: form.displayName,
		emailAddress: form.emailAddress,
		username: form.username,
		password: form.password || null,
		imap: { ...form.imap },
		smtp: { ...form.smtp },
	}
}

function showError(e) {
	if (e instanceof ValidationError) {
		errors.value = e.fieldErrors
		generalError.value = ''
	} else {
		generalError.value = e.message
	}
}

async function test() {
	errors.value = {}
	generalError.value = ''
	testResult.value = null
	try {
		testResult.value = await testConnection(payload(), props.account?.id ?? null)
	} catch (e) {
		showError(e)
	}
}

/** @return {Promise<false|undefined>} false keeps the dialog open to show the errors */
async function save() {
	errors.value = {}
	generalError.value = ''
	try {
		const saved = props.account
			? await updateAccount(props.account.id, payload())
			: await createAccount(payload())
		emit('saved', saved)
	} catch (e) {
		showError(e)
		return false
	}
}
</script>

<style scoped>
.account-dialog {
	display: flex;
	flex-direction: column;
	gap: 8px;
	padding-bottom: 8px;
}
</style>
