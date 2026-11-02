<template>
	<div class="encryption">
		<p class="encryption__hint">
			{{ t('sockless_mail', 'Read OpenPGP and S/MIME encrypted mail and check signatures. Private keys are kept on this server, protected by their passphrase; while a key is unlocked, the server can use it to read your encrypted mail.') }}
		</p>

		<div v-if="cryptoState.loading && !cryptoState.loaded" class="encryption__loading">
			<NcLoadingIcon :size="28" />
		</div>
		<p v-else-if="loadError" class="encryption__error">
			{{ loadError }}
		</p>
		<template v-else>
			<h4>{{ t('sockless_mail', 'Your keys') }}</h4>
			<p v-if="!ownKeys.length" class="encryption__hint">
				{{ t('sockless_mail', 'You have no private keys yet. Import one, or create an OpenPGP key below.') }}
			</p>
			<ul class="encryption__keys">
				<li v-for="key in ownKeys" :key="key.id" class="encryption__key">
					<KeyRow :crypto-key="key"
						@unlock="unlocking = unlocking === key.id ? null : key.id"
						@delete="remove(key)"
						@contact="toContact(key)"
						@publish="publish(key)" />
					<KeyUnlockForm v-if="unlocking === key.id"
						class="encryption__unlock"
						:key-ref="key"
						@unlocked="unlocking = null" />
				</li>
			</ul>
			<NcButton v-if="ownKeys.some((k) => k.unlockedUntil)" variant="secondary" @click="lockAll">
				{{ t('sockless_mail', 'Lock all keys now') }}
			</NcButton>

			<h4>{{ t('sockless_mail', 'Other people\'s keys') }}</h4>
			<p v-if="!otherKeys.length" class="encryption__hint">
				{{ t('sockless_mail', 'Import someone\'s public key or certificate to check their signatures.') }}
			</p>
			<ul class="encryption__keys">
				<li v-for="key in otherKeys" :key="key.id" class="encryption__key">
					<KeyRow :crypto-key="key" @delete="remove(key)" @contact="toContact(key)" />
				</li>
			</ul>
		</template>

		<fieldset class="encryption__form">
			<legend>{{ t('sockless_mail', 'Import a key or certificate') }}</legend>
			<p class="encryption__hint">
				{{ t('sockless_mail', 'An OpenPGP key (.asc, .gpg), a PKCS#12 file with your S/MIME certificate and private key (.p12, .pfx), or a certificate (.pem, .crt, .cer). You can also paste an OpenPGP key.') }}
			</p>
			<input ref="fileInput"
				type="file"
				class="encryption__file"
				accept=".asc,.gpg,.pgp,.key,.p12,.pfx,.pem,.crt,.cer,.der"
				:aria-label="t('sockless_mail', 'Key file')"
				@change="chooseFile">
			<textarea v-if="!importForm.file"
				v-model="importForm.text"
				class="encryption__paste"
				rows="4"
				:placeholder="t('sockless_mail', '-----BEGIN PGP PUBLIC KEY BLOCK-----')"
				:aria-label="t('sockless_mail', 'Pasted key')" />
			<NcPasswordField v-model="importForm.passphrase"
				:label="t('sockless_mail', 'Passphrase or file password (for private keys)')"
				:error="importErrors.field === 'passphrase'"
				:helper-text="importErrors.field === 'passphrase' ? importErrors.message : ''"
				autocomplete="off" />
			<p v-if="importErrors.message && importErrors.field !== 'passphrase'" class="encryption__error">
				{{ importErrors.message }}
			</p>
			<NcButton variant="primary" :disabled="importing || (!importForm.file && !importForm.text.trim())" @click="doImport">
				<template v-if="importing" #icon>
					<NcLoadingIcon :size="20" />
				</template>
				{{ t('sockless_mail', 'Import') }}
			</NcButton>
		</fieldset>

		<fieldset v-if="addresses.length" class="encryption__form">
			<legend>{{ t('sockless_mail', 'Create an OpenPGP key') }}</legend>
			<label class="encryption__label">
				{{ t('sockless_mail', 'For') }}
				<select v-model="createForm.address" class="encryption__select">
					<option v-for="address in addresses" :key="address" :value="address">{{ address }}</option>
				</select>
			</label>
			<NcTextField v-model="createForm.name" :label="t('sockless_mail', 'Your name')" />
			<NcPasswordField v-model="createForm.passphrase"
				:label="t('sockless_mail', 'Passphrase (at least 8 characters)')"
				:error="createErrors.field === 'passphrase'"
				:helper-text="createErrors.field === 'passphrase' ? createErrors.message : ''"
				autocomplete="new-password" />
			<NcPasswordField v-model="createForm.repeat"
				:label="t('sockless_mail', 'Repeat the passphrase')"
				:error="mismatch"
				:helper-text="mismatch ? t('sockless_mail', 'The passphrases differ.') : t('sockless_mail', 'Without it the key cannot be used. It cannot be recovered.')"
				autocomplete="new-password" />
			<label class="encryption__label">
				{{ t('sockless_mail', 'Valid for') }}
				<select v-model.number="createForm.validYears" class="encryption__select">
					<option :value="1">{{ t('sockless_mail', '1 year') }}</option>
					<option :value="2">{{ t('sockless_mail', '2 years') }}</option>
					<option :value="3">{{ t('sockless_mail', '3 years') }}</option>
					<option :value="5">{{ t('sockless_mail', '5 years') }}</option>
					<option :value="0">{{ t('sockless_mail', 'Does not expire') }}</option>
				</select>
			</label>
			<p v-if="createErrors.message && createErrors.field !== 'passphrase'" class="encryption__error">
				{{ createErrors.message }}
			</p>
			<NcButton variant="primary" :disabled="creating || !canCreate" @click="create">
				<template v-if="creating" #icon>
					<NcLoadingIcon :size="20" />
				</template>
				{{ creating ? t('sockless_mail', 'Creating the key, this takes a few seconds…') : t('sockless_mail', 'Create key') }}
			</NcButton>
		</fieldset>
	</div>
</template>

<script setup>
import { showError, showSuccess } from '@nextcloud/dialogs/toast'
import { n, t } from '@nextcloud/l10n'
import NcButton from '@nextcloud/vue/components/NcButton'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import NcPasswordField from '@nextcloud/vue/components/NcPasswordField'
import NcTextField from '@nextcloud/vue/components/NcTextField'
import { computed, onMounted, reactive, ref } from 'vue'
import { accountsState } from '../store/accounts.js'
import { cryptoState, deleteKey, errorText, generateKey, importKey, loadKeys, lockKeys, publishKey, saveKeyToContact } from '../store/crypto.js'
import KeyRow from './KeyRow.vue'
import KeyUnlockForm from './KeyUnlockForm.vue'

/** Settings → Encryption: the user's keys and certificates, importing them, and making an OpenPGP key. */
const loadError = ref('')
const unlocking = ref(null)

onMounted(async () => {
	try {
		await loadKeys()
	} catch (e) {
		loadError.value = t('sockless_mail', 'Could not load your keys: {error}', { error: errorText(e) })
	}
})

const ownKeys = computed(() => cryptoState.keys.filter((k) => k.own))
const otherKeys = computed(() => cryptoState.keys.filter((k) => !k.own))
const addresses = computed(() => [...new Set(accountsState.accounts.map((a) => a.emailAddress))])

async function remove(key) {
	const what = key.own
		? t('sockless_mail', 'Delete your private key for {address}? Mail encrypted to it cannot be read here any more unless you import it again.', { address: key.addresses[0] ?? key.fingerprint })
		: t('sockless_mail', 'Delete the key for {address}?', { address: key.addresses[0] ?? key.fingerprint })
	if (!window.confirm(what)) {
		return
	}
	try {
		await deleteKey(key.id)
	} catch (e) {
		showError(errorText(e))
	}
}

const RAW = { escape: false, sanitize: false }

async function toContact(key) {
	try {
		const saved = await saveKeyToContact(key.id)
		showSuccess(saved.created
			? t('sockless_mail', 'Saved in a new contact, {name}', { name: saved.name }, undefined, RAW)
			: t('sockless_mail', 'Saved in the contact {name}', { name: saved.name }, undefined, RAW))
	} catch (e) {
		showError(errorText(e))
	}
}

async function publish(key) {
	const addresses = key.addresses.join(', ')
	if (!window.confirm(t('sockless_mail', 'Publish your public key on keys.openpgp.org, so others can find it for {addresses}? The key server sends each address an e-mail with a link; the address is published only once you open it.', { addresses }, undefined, RAW))) {
		return
	}
	try {
		const result = await publishKey(key.id)
		const states = Object.entries(result.addresses)
		const pending = states.filter(([, state]) => state === 'pending').map(([address]) => address)
		const published = states.filter(([, state]) => state === 'published').map(([address]) => address)
		if (pending.length) {
			showSuccess(t('sockless_mail', 'keys.openpgp.org sent a confirmation link to {addresses}. Open it to publish the key.', { addresses: pending.join(', ') }, undefined, RAW))
		} else if (published.length) {
			showSuccess(t('sockless_mail', 'Your key is published for {addresses}.', { addresses: published.join(', ') }, undefined, RAW))
		} else {
			showSuccess(t('sockless_mail', 'Your key was uploaded. keys.openpgp.org publishes addresses only once they are confirmed.'))
		}
	} catch (e) {
		showError(errorText(e))
	}
}

async function lockAll() {
	try {
		await lockKeys()
	} catch (e) {
		showError(errorText(e))
	}
}

const fileInput = ref(null)
const importForm = reactive({ file: null, text: '', passphrase: '' })
const importErrors = reactive({ field: null, message: '' })
const importing = ref(false)

function chooseFile(event) {
	importForm.file = event.target.files?.[0] ?? null
}

/** The file's bytes as base64, for the JSON body. */
function readBase64(file) {
	return new Promise((resolve, reject) => {
		const reader = new FileReader()
		reader.onload = () => resolve(String(reader.result).replace(/^data:[^,]*,/, ''))
		reader.onerror = () => reject(reader.error)
		reader.readAsDataURL(file)
	})
}

async function doImport() {
	importing.value = true
	Object.assign(importErrors, { field: null, message: '' })
	try {
		const input = importForm.file
			? { data: await readBase64(importForm.file), passphrase: importForm.passphrase || null }
			: { text: importForm.text, passphrase: importForm.passphrase || null }
		const result = await importKey(input)
		const count = result.added.length + result.updated.length
		showSuccess(n('sockless_mail', 'Imported {count} key', 'Imported {count} keys', count, { count }))
		Object.assign(importForm, { file: null, text: '', passphrase: '' })
		if (fileInput.value) {
			fileInput.value.value = ''
		}
	} catch (e) {
		const errors = e?.response?.data?.errors
		Object.assign(importErrors, { field: errors ? Object.keys(errors)[0] : null, message: errorText(e) })
	} finally {
		importing.value = false
	}
}

const createForm = reactive({ address: '', name: '', passphrase: '', repeat: '', validYears: 3 })
const createErrors = reactive({ field: null, message: '' })
const creating = ref(false)
const mismatch = computed(() => createForm.repeat.length > 0 && createForm.repeat !== createForm.passphrase)
const canCreate = computed(() => createForm.address && createForm.passphrase.length >= 8 && createForm.repeat === createForm.passphrase)

onMounted(() => {
	const account = accountsState.accounts[0]
	if (account) {
		createForm.address = account.emailAddress
		createForm.name = account.senderName || ''
	}
})

async function create() {
	creating.value = true
	Object.assign(createErrors, { field: null, message: '' })
	try {
		await generateKey({ address: createForm.address, name: createForm.name, passphrase: createForm.passphrase, validYears: createForm.validYears })
		showSuccess(t('sockless_mail', 'Your key for {address} is ready', { address: createForm.address }))
		Object.assign(createForm, { passphrase: '', repeat: '' })
	} catch (e) {
		const errors = e?.response?.data?.errors
		Object.assign(createErrors, { field: errors ? Object.keys(errors)[0] : null, message: errorText(e) })
	} finally {
		creating.value = false
	}
}
</script>

<style scoped>
.encryption h4 {
	margin-block: 16px 4px;
	font-weight: bold;
}

.encryption__hint {
	color: var(--color-text-maxcontrast);
	margin-block: 4px;
}

.encryption__error {
	color: var(--color-error-text, var(--color-error));
	margin-block: 4px;
}

.encryption__loading {
	display: flex;
	justify-content: center;
	padding: 16px;
}

.encryption__keys {
	display: flex;
	flex-direction: column;
	gap: 4px;
	margin-block: 4px 8px;
}

.encryption__unlock {
	margin: 4px 0 8px 44px;
}

.encryption__form {
	display: flex;
	flex-direction: column;
	gap: 8px;
	max-width: 520px;
	margin-block-start: 20px;
}

.encryption__form legend {
	font-weight: bold;
	margin-block-end: 4px;
}

.encryption__form :deep(.button-vue) {
	align-self: flex-start;
}

.encryption__paste {
	width: 100%;
	font-family: var(--font-face-monospace, monospace);
	font-size: 12px;
}

.encryption__label {
	display: flex;
	align-items: center;
	gap: 8px;
}

.encryption__select {
	min-width: 200px;
}
</style>
