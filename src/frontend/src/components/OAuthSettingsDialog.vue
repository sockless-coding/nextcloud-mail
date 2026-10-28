<template>
	<!-- For Nextcloud admins: the app's registration with each OAuth2 provider. The server checks
	     admin rights again; this dialog is only offered to admins. -->
	<NcDialog :name="t('sockless_mail', 'Sign-in providers')"
		size="normal"
		:buttons="[{ label: t('sockless_mail', 'Close') }]"
		:open="true"
		@update:open="(open) => !open && emit('close')">
		<div class="oauth-settings">
			<p>
				{{ t('sockless_mail', 'Let users add Google and Microsoft accounts by signing in, instead of typing app passwords. Register Sockless Mail as a web application with the provider, allow this callback URL, and enter the client id and secret here.') }}
			</p>
			<NcTextField :model-value="callbackUrl"
				:label="t('sockless_mail', 'Callback URL to register')"
				readonly
				@focus="$event.target.select()" />
			<div v-if="loading" class="oauth-settings__center">
				<NcLoadingIcon :size="32" />
			</div>
			<NcNoteCard v-else-if="loadError" type="error" :text="loadError" />
			<form v-for="client in clients"
				v-else
				:key="client.provider"
				class="oauth-settings__provider"
				@submit.prevent="save(client)">
				<h3>
					{{ client.name }}
					<span class="oauth-settings__status">{{ client.configured ? t('sockless_mail', 'enabled') : t('sockless_mail', 'not set up') }}</span>
				</h3>
				<NcTextField v-model="drafts[client.provider].clientId"
					:label="t('sockless_mail', 'Client id')"
					:error="!!fieldErrors[client.provider]?.clientId"
					:helper-text="fieldErrors[client.provider]?.clientId ?? t('sockless_mail', 'Leave empty and save to turn this provider off.')"
					autocomplete="off" />
				<NcPasswordField v-model="drafts[client.provider].clientSecret"
					:label="t('sockless_mail', 'Client secret')"
					:placeholder="client.hasClientSecret ? t('sockless_mail', 'Unchanged') : ''"
					:error="!!fieldErrors[client.provider]?.clientSecret"
					:helper-text="fieldErrors[client.provider]?.clientSecret ?? (client.hasClientSecret ? t('sockless_mail', 'Stored encrypted. Leave empty to keep it.') : '')"
					autocomplete="new-password" />
				<NcTextField v-if="client.usesTenant"
					v-model="drafts[client.provider].tenant"
					:label="t('sockless_mail', 'Tenant')"
					:error="!!fieldErrors[client.provider]?.tenant"
					:helper-text="fieldErrors[client.provider]?.tenant ?? t('sockless_mail', '“common” for work and personal accounts, “organizations”, “consumers”, or your tenant id.')"
					autocomplete="off" />
				<NcTextField v-model="drafts[client.provider].redirectUri"
					:label="t('sockless_mail', 'Callback URL')"
					:error="!!fieldErrors[client.provider]?.redirectUri"
					:helper-text="fieldErrors[client.provider]?.redirectUri ?? t('sockless_mail', 'Exactly as registered with the provider.')"
					autocomplete="off" />
				<div class="oauth-settings__actions">
					<NcButton type="submit" variant="primary" :disabled="saving === client.provider">
						<template #icon>
							<NcLoadingIcon v-if="saving === client.provider" :size="20" />
						</template>
						{{ t('sockless_mail', 'Save {provider}', { provider: client.name }) }}
					</NcButton>
				</div>
			</form>
		</div>
	</NcDialog>
</template>

<script setup>
import { showError, showSuccess } from '@nextcloud/dialogs/toast'
import { t } from '@nextcloud/l10n'
import NcButton from '@nextcloud/vue/components/NcButton'
import NcDialog from '@nextcloud/vue/components/NcDialog'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import NcNoteCard from '@nextcloud/vue/components/NcNoteCard'
import NcPasswordField from '@nextcloud/vue/components/NcPasswordField'
import NcTextField from '@nextcloud/vue/components/NcTextField'
import { onMounted, reactive, ref } from 'vue'
import { callbackUrl, loadOAuthClients, saveOAuthClient } from '../store/oauth.js'

const emit = defineEmits(['close'])

const clients = ref([])
/** provider -> the form's values */
const drafts = reactive({})
/** provider -> { field: message } */
const fieldErrors = reactive({})
const loading = ref(true)
const loadError = ref('')
const saving = ref(null)

function setClients(list) {
	clients.value = list
	for (const client of list) {
		drafts[client.provider] = {
			clientId: client.clientId ?? '',
			clientSecret: '',
			tenant: client.tenant ?? '',
			// A provider not set up yet starts with this instance's callback URL.
			redirectUri: client.redirectUri ?? callbackUrl,
		}
	}
}

async function save(client) {
	saving.value = client.provider
	fieldErrors[client.provider] = {}
	const draft = drafts[client.provider]
	try {
		setClients(await saveOAuthClient(client.provider, {
			clientId: draft.clientId,
			clientSecret: draft.clientSecret || null,
			tenant: draft.tenant || null,
			redirectUri: draft.redirectUri,
		}))
		showSuccess(draft.clientId
			? t('sockless_mail', '{provider} sign-in saved', { provider: client.name })
			: t('sockless_mail', '{provider} sign-in turned off', { provider: client.name }))
	} catch (e) {
		if (Object.keys(e.fieldErrors ?? {}).length) {
			fieldErrors[client.provider] = e.fieldErrors
		} else {
			showError(t('sockless_mail', 'Could not save: {error}', { error: e.message }))
		}
	} finally {
		saving.value = null
	}
}

onMounted(async () => {
	try {
		setClients(await loadOAuthClients())
	} catch (e) {
		loadError.value = e.message
	} finally {
		loading.value = false
	}
})
</script>

<style scoped>
.oauth-settings {
	display: flex;
	flex-direction: column;
	gap: 8px;
	padding-bottom: 8px;
}

.oauth-settings__center {
	display: flex;
	justify-content: center;
	padding: 16px;
}

.oauth-settings__provider {
	display: flex;
	flex-direction: column;
	gap: 8px;
	margin-top: 12px;
}

.oauth-settings__provider h3 {
	margin: 0;
	font-size: 1.1em;
}

.oauth-settings__status {
	margin-inline-start: 8px;
	font-weight: normal;
	font-size: 0.9em;
	color: var(--color-text-maxcontrast);
}

.oauth-settings__actions {
	display: flex;
	justify-content: flex-end;
}
</style>
