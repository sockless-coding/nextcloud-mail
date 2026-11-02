<template>
	<section v-if="security" class="message-security" :aria-label="t('sockless_mail', 'Encryption and signatures')">
		<div v-if="encryption"
			class="message-security__row"
			:class="`message-security__row--${encryption.status === 'decrypted' ? 'ok' : 'notice'}`">
			<NcIconSvgWrapper :path="encryption.status === 'decrypted' ? mdiLockOutline : mdiLockAlertOutline" :size="20" class="message-security__icon" />
			<div class="message-security__text">
				<strong>{{ encryptionTitle }}</strong>
				<span v-if="encryptionHint">{{ encryptionHint }}</span>
				<KeyUnlockForm v-if="encryption.status === 'locked' && encryption.keys.length"
					:key="encryption.keys[0].id"
					class="message-security__unlock"
					:key-ref="encryption.keys[0]"
					@unlocked="emit('reload')" />
			</div>
		</div>

		<div v-for="(signature, index) in security.signatures"
			:key="index"
			class="message-security__row"
			:class="`message-security__row--${level(signature)}`">
			<NcIconSvgWrapper :path="iconOf(signature)" :size="20" class="message-security__icon" />
			<div class="message-security__text">
				<strong>{{ signatureTitle(signature) }}</strong>
				<span>{{ signatureHint(signature) }}</span>
				<span v-if="signature.fingerprint" class="message-security__fingerprint">
					{{ t('sockless_mail', 'Fingerprint: {fingerprint}', { fingerprint: formatFingerprint(signature.fingerprint) }, undefined, { escape: false, sanitize: false }) }}
				</span>
			</div>
		</div>
	</section>
</template>

<script setup>
import { mdiAlertOctagonOutline, mdiAlertOutline, mdiCheckDecagram, mdiKeyOutline, mdiLockAlertOutline, mdiLockOutline } from '@mdi/js'
import { t } from '@nextcloud/l10n'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import { computed, watch } from 'vue'
import { cryptoState, formatFingerprint } from '../store/crypto.js'
import KeyUnlockForm from './KeyUnlockForm.vue'

/**
 * What a message's encryption and signatures came to (MessageSecurityDto): decrypted or why not
 * (with a passphrase field when the user's key is locked), and each signature's verdict.
 */
const props = defineProps({
	/** { encryption: { kind, status, keys }, signatures: [...] } or null */
	security: { type: Object, default: null },
})
const emit = defineEmits(['reload'])

const encryption = computed(() => props.security?.encryption ?? null)

/** Unlocked elsewhere (settings, another message): a locked message tries again. */
watch(() => cryptoState.unlocks, () => {
	if (encryption.value?.status === 'locked') {
		emit('reload')
	}
})

/** Placeholders are left as they are: Vue escapes the text it shows. */
const RAW = { escape: false, sanitize: false }

const STANDARDS = { openpgp: 'OpenPGP', smime: 'S/MIME' }

function standard(kind) {
	return STANDARDS[kind] ?? kind
}

const encryptionTitle = computed(() => {
	const kind = standard(encryption.value.kind)
	switch (encryption.value.status) {
	case 'decrypted': return t('sockless_mail', 'Encrypted with {kind}', { kind }, undefined, RAW)
	case 'locked': return t('sockless_mail', 'This message is encrypted with {kind}', { kind }, undefined, RAW)
	case 'no_key': return t('sockless_mail', 'None of your keys can open this message')
	case 'too_large': return t('sockless_mail', 'This encrypted message is too large to decrypt')
	default: return t('sockless_mail', 'This encrypted message could not be decrypted')
	}
})

const encryptionHint = computed(() => {
	switch (encryption.value.status) {
	case 'decrypted': return t('sockless_mail', 'Only you and the other recipients can read it.')
	case 'locked': return t('sockless_mail', 'Enter your passphrase to read it. Your key stays unlocked for 30 minutes after you last use it.')
	case 'no_key': return t('sockless_mail', 'It was encrypted for a key you have not imported. Import the private key under Settings → Encryption.')
	case 'too_large': return t('sockless_mail', 'Download the message (.eml) and open it in a mail program that has your key.')
	default: return t('sockless_mail', 'The encrypted data is damaged or uses a method that is not supported.')
	}
})

function signer(signature) {
	const address = signature.signerAddresses[0]
	if (signature.signerName && address) {
		return `${signature.signerName} <${address}>`
	}
	return signature.signerName || address || t('sockless_mail', 'an unknown signer')
}

function level(signature) {
	if (signature.status === 'bad') {
		return 'error'
	}
	return signature.status === 'valid' && signature.senderMatches ? 'ok' : 'notice'
}

function iconOf(signature) {
	switch (signature.status) {
	case 'valid': return signature.senderMatches ? mdiCheckDecagram : mdiAlertOutline
	case 'bad': return mdiAlertOctagonOutline
	case 'unknown_key': return mdiKeyOutline
	default: return mdiAlertOutline
	}
}

function signatureTitle(signature) {
	const kind = standard(signature.kind)
	switch (signature.status) {
	case 'valid': return t('sockless_mail', 'Signed by {signer} ({kind})', { signer: signer(signature), kind }, undefined, RAW)
	case 'untrusted': return t('sockless_mail', 'Signed by {signer}, but the certificate is not trusted', { signer: signer(signature) }, undefined, RAW)
	case 'unknown_key': return t('sockless_mail', 'Signed with {kind}, by a key you do not have', { kind }, undefined, RAW)
	case 'not_checked': return t('sockless_mail', 'Signed with {kind}', { kind }, undefined, RAW)
	default: return t('sockless_mail', 'The signature is not valid')
	}
}

function signatureHint(signature) {
	switch (signature.status) {
	case 'valid': return signature.senderMatches
		? t('sockless_mail', 'The message has not been changed since it was signed.')
		: t('sockless_mail', 'The signature is intact, but the key does not belong to the sender\'s address.')
	case 'untrusted': return signature.detail
		? t('sockless_mail', 'The message is unchanged, but its certificate could not be checked: {reason}', { reason: signature.detail }, undefined, RAW)
		: t('sockless_mail', 'The message is unchanged, but its certificate was not issued by an authority this server trusts.')
	case 'unknown_key': return signature.keyId
		? t('sockless_mail', 'Import the sender\'s public key (key ID {keyId}) to check it.', { keyId: signature.keyId }, undefined, RAW)
		: t('sockless_mail', 'Import the sender\'s public key to check it.')
	case 'not_checked': return t('sockless_mail', 'The message is too large to check its signature here.')
	default: return t('sockless_mail', 'The message may have been changed after it was signed. Be careful with what it asks.')
	}
}
</script>

<style scoped>
.message-security {
	display: flex;
	flex-direction: column;
	gap: 6px;
	margin-block: 12px;
}

.message-security__row {
	display: flex;
	gap: 10px;
	padding: 8px 12px;
	border-radius: var(--border-radius-large, 10px);
	border: 1px solid var(--color-border);
	background: var(--color-background-hover);
}

.message-security__row--ok .message-security__icon {
	color: var(--color-success-text, var(--color-success));
}

.message-security__row--notice .message-security__icon {
	color: var(--color-warning-text, var(--color-warning));
}

.message-security__row--error {
	border-color: var(--color-error);
	background: var(--color-error-hover, var(--color-background-hover));
}

.message-security__row--error .message-security__icon {
	color: var(--color-error-text, var(--color-error));
}

.message-security__icon {
	flex: none;
	margin-top: 1px;
}

.message-security__text {
	display: flex;
	flex-direction: column;
	gap: 2px;
	min-width: 0;
	overflow-wrap: anywhere;
}

.message-security__fingerprint {
	color: var(--color-text-maxcontrast);
	font-family: var(--font-face-monospace, monospace);
	font-size: 12px;
}

.message-security__unlock {
	margin-top: 6px;
}
</style>
