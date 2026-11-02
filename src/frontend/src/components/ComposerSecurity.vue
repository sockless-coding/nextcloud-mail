<template>
	<div v-if="active" class="composer-security" role="status">
		<p v-if="!ownKey" class="composer-security__line composer-security__line--warning">
			<NcIconSvgWrapper :path="mdiAlertOutline" :size="18" inline />
			{{ t('sockless_mail', 'You have no {kind} key for {address}. Create or import one under Settings → Encryption.', { kind: kindName, address: fromAddress }, undefined, RAW) }}
			<button type="button" class="composer-security__link" @click="dialogs.settings = true">
				{{ t('sockless_mail', 'Open settings') }}
			</button>
		</p>

		<template v-if="draft.security.encrypt">
			<p v-if="looking" class="composer-security__line">
				<NcLoadingIcon :size="16" />
				{{ t('sockless_mail', 'Looking for the recipients\' keys…') }}
			</p>
			<p v-else-if="!addresses.length" class="composer-security__line">
				<NcIconSvgWrapper :path="mdiLockOutline" :size="18" inline />
				{{ t('sockless_mail', 'Encrypted with {kind}. Add recipients to check their keys.', { kind: kindName }, undefined, RAW) }}
			</p>
			<p v-else-if="missing.length" class="composer-security__line composer-security__line--warning">
				<NcIconSvgWrapper :path="mdiKeyRemove" :size="18" inline />
				<span>
					{{ n('sockless_mail', 'No {kind} key was found for {addresses}, who could not read the message.', 'No {kind} keys were found for {addresses}, who could not read the message.', missing.length, { kind: kindName, addresses: missing.join(', ') }, RAW) }}
				</span>
				<button v-if="otherCoversAll" type="button" class="composer-security__link" @click="draft.security.kind = otherKind">
					{{ t('sockless_mail', 'Use {kind} instead', { kind: otherKindName }, undefined, RAW) }}
				</button>
			</p>
			<p v-else class="composer-security__line composer-security__line--ok">
				<NcIconSvgWrapper :path="mdiLockOutline" :size="18" inline />
				{{ n('sockless_mail', 'Encrypted with {kind} for {count} recipient and you.', 'Encrypted with {kind} for {count} recipients and you.', addresses.length, { kind: kindName, count: addresses.length }, RAW) }}
			</p>
			<p v-if="hasBcc" class="composer-security__line composer-security__hint">
				{{ t('sockless_mail', 'Recipients can tell from the encryption how many keys it is for, Bcc included.') }}
			</p>
		</template>

		<p v-if="draft.security.sign && ownKey" class="composer-security__line composer-security__line--ok">
			<NcIconSvgWrapper :path="mdiCertificateOutline" :size="18" inline />
			{{ t('sockless_mail', 'Signed with your {kind} key.', { kind: kindName }, undefined, RAW) }}
		</p>
	</div>
</template>

<script setup>
import { mdiAlertOutline, mdiCertificateOutline, mdiKeyRemove, mdiLockOutline } from '@mdi/js'
import { n, t } from '@nextcloud/l10n'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { accountsState } from '../store/accounts.js'
import { dialogs } from '../store/actions.js'
import { cryptoState, loadKeys, lookupRecipients } from '../store/crypto.js'

/**
 * Under the composer's text while it signs or encrypts: whether every recipient has a key (the
 * server also looks them up in Contacts and on the internet, see KeyDirectory.cs), and whether
 * the sender has a key of their own.
 */
const props = defineProps({
	/** The composer's draft (store/compose.js). */
	draft: { type: Object, required: true },
})

/** Placeholders are left as they are: Vue escapes the text it shows. */
const RAW = { escape: false, sanitize: false }
const NAMES = { openpgp: 'OpenPGP', smime: 'S/MIME' }

const active = computed(() => props.draft.security.encrypt || props.draft.security.sign || props.draft.security.attachKey)
const kindName = computed(() => NAMES[props.draft.security.kind])
const otherKind = computed(() => (props.draft.security.kind === 'openpgp' ? 'smime' : 'openpgp'))
const otherKindName = computed(() => NAMES[otherKind.value])
const fromAddress = computed(() => accountsState.accounts.find((a) => a.id === props.draft.accountId)?.emailAddress ?? '')
const hasBcc = computed(() => !!props.draft.bcc.trim())

onMounted(() => {
	if (!cryptoState.loaded) {
		loadKeys().catch(() => {})
	}
})

/** Watched again after unlocking or creating a key (it may be the sender's first one). */
watch(() => cryptoState.unlocks, () => loadKeys().catch(() => {}))

const ownKey = computed(() => !cryptoState.loaded || cryptoState.keys.some((k) => k.own && k.kind === props.draft.security.kind
	&& k.addresses.includes(fromAddress.value.toLowerCase())))

/** The addresses in To, Cc and Bcc, once each. */
const addresses = computed(() => {
	const found = `${props.draft.to},${props.draft.cc},${props.draft.bcc}`.match(/[^\s<>,;"']+@[^\s<>,;"']+\.[^\s<>,;"']+/g) ?? []
	return [...new Map(found.map((a) => [a.toLowerCase(), a])).values()]
})

/** address (lower case) → { openPgp, smime } as the server answered */
const results = ref(new Map())
const looking = ref(false)
let timer = null
let generation = 0

async function lookup() {
	const wanted = addresses.value
	const asked = ++generation
	looking.value = wanted.some((a) => !results.value.has(a.toLowerCase()))
	try {
		const answer = await lookupRecipients(wanted, true)
		if (asked === generation) {
			results.value = new Map(answer.map((r) => [r.address.toLowerCase(), r]))
		}
	} catch {
		// Shown as missing keys; sending names them again.
	} finally {
		if (asked === generation) {
			looking.value = false
		}
	}
}

// Asked only while encrypting: looking keys up tells key servers whom the user writes to.
watch(() => [props.draft.security.encrypt, addresses.value.join(',')], ([encrypt]) => {
	window.clearTimeout(timer)
	if (encrypt && addresses.value.length) {
		timer = window.setTimeout(lookup, 700)
	}
}, { immediate: true })

onBeforeUnmount(() => window.clearTimeout(timer))

const field = (kind) => (kind === 'openpgp' ? 'openPgp' : 'smime')
const missing = computed(() => addresses.value.filter((a) => !results.value.get(a.toLowerCase())?.[field(props.draft.security.kind)]))
const otherCoversAll = computed(() => addresses.value.length > 0 && addresses.value.every((a) => results.value.get(a.toLowerCase())?.[field(otherKind.value)]))
</script>

<style scoped>
.composer-security {
	display: flex;
	flex-direction: column;
	gap: 4px;
	margin-block: 8px 0;
	font-size: 13px;
}

.composer-security__line {
	display: flex;
	align-items: center;
	flex-wrap: wrap;
	gap: 6px;
	margin: 0;
}

.composer-security__line--ok :deep(.icon-vue) {
	color: var(--color-success-text, var(--color-success));
}

.composer-security__line--warning :deep(.icon-vue) {
	color: var(--color-warning-text, var(--color-warning));
}

.composer-security__hint {
	color: var(--color-text-maxcontrast);
}

.composer-security__link {
	min-height: 0;
	margin: 0;
	padding: 0 4px;
	border: 0;
	background: none;
	color: var(--color-primary-element);
	text-decoration: underline;
	cursor: pointer;
}
</style>
