<template>
	<div class="key-row">
		<span class="key-row__kind" :class="`key-row__kind--${cryptoKey.kind}`" aria-hidden="true">
			{{ cryptoKey.kind === 'openpgp' ? 'PGP' : 'S/MIME' }}
		</span>
		<span class="key-row__text">
			<span class="key-row__name">
				{{ cryptoKey.addresses.join(', ') || cryptoKey.name || t('sockless_mail', 'No address') }}
				<NcIconSvgWrapper v-if="cryptoKey.own && cryptoKey.hasPassphrase"
					:path="cryptoKey.unlockedUntil ? mdiLockOpenVariantOutline : mdiLockOutline"
					:size="16"
					inline
					:name="cryptoKey.unlockedUntil ? t('sockless_mail', 'Unlocked') : t('sockless_mail', 'Locked')" />
			</span>
			<span class="key-row__details">
				{{ details }}
			</span>
			<span class="key-row__fingerprint">{{ formatFingerprint(cryptoKey.fingerprint) }}</span>
		</span>
		<NcActions :aria-label="t('sockless_mail', 'Key actions')">
			<NcActionButton v-if="cryptoKey.own && cryptoKey.hasPassphrase && !cryptoKey.unlockedUntil" close-after-click @click="emit('unlock')">
				<template #icon>
					<NcIconSvgWrapper :path="mdiLockOpenVariantOutline" />
				</template>
				{{ t('sockless_mail', 'Unlock') }}
			</NcActionButton>
			<NcActionLink :href="apiUrl(`crypto/keys/${cryptoKey.id}/public`)" download close-after-click>
				<template #icon>
					<NcIconSvgWrapper :path="mdiDownload" />
				</template>
				{{ cryptoKey.kind === 'openpgp' ? t('sockless_mail', 'Download public key') : t('sockless_mail', 'Download certificate') }}
			</NcActionLink>
			<NcActionButton v-if="cryptoKey.addresses.length" close-after-click @click="emit('contact')">
				<template #icon>
					<NcIconSvgWrapper :path="mdiAccountPlusOutline" />
				</template>
				{{ t('sockless_mail', 'Save to contact') }}
			</NcActionButton>
			<NcActionButton v-if="cryptoKey.own && cryptoKey.kind === 'openpgp'" close-after-click @click="emit('publish')">
				<template #icon>
					<NcIconSvgWrapper :path="mdiCloudUploadOutline" />
				</template>
				{{ t('sockless_mail', 'Publish on keys.openpgp.org…') }}
			</NcActionButton>
			<NcActionButton close-after-click @click="emit('delete')">
				<template #icon>
					<NcIconSvgWrapper :path="mdiDeleteOutline" />
				</template>
				{{ t('sockless_mail', 'Delete') }}
			</NcActionButton>
		</NcActions>
	</div>
</template>

<script setup>
import { mdiAccountPlusOutline, mdiCloudUploadOutline, mdiDeleteOutline, mdiDownload, mdiLockOpenVariantOutline, mdiLockOutline } from '@mdi/js'
import { t } from '@nextcloud/l10n'
import NcActionButton from '@nextcloud/vue/components/NcActionButton'
import NcActionLink from '@nextcloud/vue/components/NcActionLink'
import NcActions from '@nextcloud/vue/components/NcActions'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import { computed } from 'vue'
import { formatFingerprint } from '../store/crypto.js'
import { apiUrl } from '../store/mailbox.js'

/** One key in Settings → Encryption: what it is for, its state and fingerprint, and its actions. */
const props = defineProps({
	cryptoKey: { type: Object, required: true },
})
const emit = defineEmits(['unlock', 'delete', 'contact', 'publish'])

const dateFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' })

/** Where keys the app found itself came from (KeySources.cs); imported and created ones say nothing. */
const SOURCES = {
	contacts: t('sockless_mail', 'From Contacts'),
	wkd: t('sockless_mail', 'Published by the recipient\'s domain'),
	keyserver: t('sockless_mail', 'From keys.openpgp.org'),
	message: t('sockless_mail', 'From a signed message'),
	autocrypt: t('sockless_mail', 'From an Autocrypt header'),
}

const details = computed(() => {
	const key = props.cryptoKey
	const parts = []
	if (key.name && key.addresses.length) {
		parts.push(key.name)
	}
	const source = SOURCES[key.source]
	if (source) {
		parts.push(source)
	}
	if (key.expiresAt) {
		const expires = new Date(key.expiresAt)
		parts.push(expires < new Date()
			? t('sockless_mail', 'Expired {date}', { date: dateFormat.format(expires) })
			: t('sockless_mail', 'Expires {date}', { date: dateFormat.format(expires) }))
	}
	if (key.own && key.hasPassphrase) {
		parts.push(key.unlockedUntil
			? t('sockless_mail', 'Unlocked until {time}', { time: new Date(key.unlockedUntil).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) })
			: t('sockless_mail', 'Locked'))
	} else if (key.own) {
		parts.push(t('sockless_mail', 'No passphrase'))
	}
	return parts.join(' · ')
})
</script>

<style scoped>
.key-row {
	display: flex;
	align-items: center;
	gap: 10px;
	padding: 6px 4px;
	border-radius: var(--border-radius-element, 8px);
}

.key-row:hover {
	background: var(--color-background-hover);
}

.key-row__kind {
	flex: none;
	min-width: 34px;
	padding: 2px 6px;
	border-radius: 6px;
	color: #fff;
	font-size: 11px;
	font-weight: bold;
	text-align: center;
}

.key-row__kind--openpgp {
	background: #0b7a75;
}

.key-row__kind--smime {
	background: #5b4aa8;
}

.key-row__text {
	display: flex;
	flex: 1 1 auto;
	flex-direction: column;
	min-width: 0;
}

.key-row__name {
	font-weight: 600;
	overflow-wrap: anywhere;
}

.key-row__details {
	color: var(--color-text-maxcontrast);
	font-size: 13px;
}

.key-row__fingerprint {
	color: var(--color-text-maxcontrast);
	font-family: var(--font-face-monospace, monospace);
	font-size: 11px;
	overflow-wrap: anywhere;
}
</style>
