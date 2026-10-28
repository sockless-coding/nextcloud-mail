<template>
	<!-- Closing goes through NcDialog's own buttons, which hide the modal and release its focus
	     trap before update:open(false); only then may the parent unmount this component. -->
	<NcDialog :name="account ? t('sockless_mail', 'Edit account') : t('sockless_mail', 'Add mail account')"
		size="normal"
		:buttons="buttons"
		:open="true"
		@update:open="(open) => !open && emit('close')">
		<div class="account-dialog">
			<div v-if="!account && oauthState.providers.length" class="account-dialog__providers">
				<NcButton v-for="provider in oauthState.providers"
					:key="provider.id"
					wide
					:disabled="!!signingIn"
					@click="signInWith(provider)">
					<template #icon>
						<NcLoadingIcon v-if="signingIn === provider.id" :size="20" />
						<NcIconSvgWrapper v-else :path="mdiLogin" />
					</template>
					{{ t('sockless_mail', 'Sign in with {provider}', { provider: provider.name }) }}
				</NcButton>
				<p class="account-dialog__hint">
					{{ t('sockless_mail', 'Or enter the server settings of any other provider:') }}
				</p>
			</div>
			<NcNoteCard v-if="isOAuth" type="info">
				<div class="account-dialog__oauth">
					<span>{{ t('sockless_mail', 'This account signs in through its provider as {address}; no password is stored.', { address: account.username }) }}</span>
					<NcButton :disabled="!!signingIn" @click="signInAgain">
						<template #icon>
							<NcLoadingIcon v-if="signingIn" :size="20" />
							<NcIconSvgWrapper v-else :path="mdiLogin" />
						</template>
						{{ t('sockless_mail', 'Sign in again') }}
					</NcButton>
				</div>
			</NcNoteCard>
			<NcTextField v-model="form.emailAddress"
				type="email"
				:label="t('sockless_mail', 'Email address')"
				:disabled="isOAuth"
				:error="!!errors.emailAddress"
				:helper-text="errors.emailAddress ?? ''"
				autocomplete="email"
				@update:model-value="suggestFromEmail" />
			<NcTextField v-model="form.displayName"
				:label="t('sockless_mail', 'Account name')"
				:error="!!errors.displayName"
				:helper-text="errors.displayName ?? ''"
				@update:model-value="touched.displayName = true" />
			<NcTextField v-model="form.senderName"
				:label="t('sockless_mail', 'Your name')"
				:error="!!errors.senderName"
				:helper-text="errors.senderName ?? t('sockless_mail', 'Shown to recipients next to your address.')"
				autocomplete="name" />
			<!-- Accounts signed in with OAuth use the provider's servers and the address that signed in. -->
			<template v-if="!isOAuth">
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
			</template>

			<NcNoteCard v-if="form.imap.security === 'none' || form.smtp.security === 'none'"
				type="warning"
				:text="t('sockless_mail', 'Without encryption, the password and your mail travel over the network unprotected.')" />
			<ConnectionResult v-if="testResult" :result="testResult" />
			<NcNoteCard v-if="generalError" type="error" :text="generalError" />
		</div>
	</NcDialog>
</template>

<script setup>
import { mdiLogin } from '@mdi/js'
import { t } from '@nextcloud/l10n'
import NcButton from '@nextcloud/vue/components/NcButton'
import NcDialog from '@nextcloud/vue/components/NcDialog'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import NcNoteCard from '@nextcloud/vue/components/NcNoteCard'
import NcPasswordField from '@nextcloud/vue/components/NcPasswordField'
import NcTextField from '@nextcloud/vue/components/NcTextField'
import { computed, onBeforeUnmount, reactive, ref } from 'vue'
import { createAccount, testConnection, updateAccount, ValidationError } from '../store/accounts.js'
import { createOAuthAccount, oauthState, replaceGrant, signIn } from '../store/oauth.js'
import ConnectionResult from './ConnectionResult.vue'
import ServerFields from './ServerFields.vue'

const props = defineProps({
	/** The account to edit; null to add a new one. */
	account: { type: Object, default: null },
})
const emit = defineEmits(['close', 'saved'])

const isOAuth = computed(() => props.account?.authMethod === 'oAuth2')
/** The provider id (new account) or true (signing in again) while a sign-in runs. */
const signingIn = ref(null)
const cancelSignIn = new AbortController()
onBeforeUnmount(() => cancelSignIn.abort())

/** A new account from a provider sign-in; names typed in the form so far are kept. */
async function signInWith(provider) {
	errors.value = {}
	generalError.value = ''
	signingIn.value = provider.id
	try {
		const { flowId } = await signIn({ provider: provider.id }, cancelSignIn.signal)
		const account = await createOAuthAccount(flowId, {
			displayName: touched.displayName ? form.displayName : null,
			senderName: form.senderName,
		})
		emit('saved', account)
	} catch (e) {
		if (!cancelSignIn.signal.aborted) {
			generalError.value = e.message
		}
	} finally {
		signingIn.value = null
	}
}

async function signInAgain() {
	errors.value = {}
	generalError.value = ''
	signingIn.value = true
	try {
		const { flowId } = await signIn({ accountId: props.account.id }, cancelSignIn.signal)
		emit('saved', await replaceGrant(props.account.id, flowId))
	} catch (e) {
		if (!cancelSignIn.signal.aborted) {
			generalError.value = e.message
		}
	} finally {
		signingIn.value = null
	}
}

const form = reactive(props.account
	? {
		displayName: props.account.displayName,
		senderName: props.account.senderName ?? '',
		emailAddress: props.account.emailAddress,
		username: props.account.username,
		password: '',
		imap: { ...props.account.imap },
		smtp: { ...props.account.smtp },
	}
	: {
		displayName: '',
		senderName: '',
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
		senderName: form.senderName || null,
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

.account-dialog__providers {
	display: flex;
	flex-direction: column;
	gap: 8px;
}

.account-dialog__hint {
	margin-top: 8px;
	color: var(--color-text-maxcontrast);
}

.account-dialog__oauth {
	display: flex;
	flex-wrap: wrap;
	align-items: center;
	justify-content: space-between;
	gap: 8px;
}
</style>
