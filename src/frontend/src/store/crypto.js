import { reactive } from 'vue'
import { apiGet, apiSend } from '../api.js'

/**
 * The user's OpenPGP keys and S/MIME certificates (CryptoEndpoints.cs). Private keys stay on the
 * server, protected by their passphrase; unlocking one keeps it usable there for a while
 * (KeyUnlocks.cs). `unlocks` counts unlocks in this tab, so messages that were locked open again.
 */
export const cryptoState = reactive({
	keys: [],
	loaded: false,
	loading: false,
	unlocks: 0,
})

export async function loadKeys() {
	cryptoState.loading = true
	try {
		cryptoState.keys = await apiGet('/crypto/keys')
		cryptoState.loaded = true
	} finally {
		cryptoState.loading = false
	}
}

/**
 * @param {{ text?: string, data?: string, passphrase?: string }} input pasted text, or a file as base64
 * @return {Promise<{ added: object[], updated: object[] }>}
 */
export async function importKey(input) {
	const result = await apiSend('POST', '/crypto/keys/import', input)
	await loadKeys()
	return result
}

/**
 * @param {{ name: string, address: string, passphrase: string, validYears: number }} input the new key
 */
export async function generateKey(input) {
	const key = await apiSend('POST', '/crypto/keys/generate', input)
	await loadKeys()
	cryptoState.unlocks++
	return key
}

/**
 * @param {string} id key id
 * @param {string} passphrase its passphrase
 */
export async function unlockKey(id, passphrase) {
	const key = await apiSend('POST', `/crypto/keys/${id}/unlock`, { passphrase })
	replace(key)
	cryptoState.unlocks++
	return key
}

export async function lockKeys() {
	await apiSend('POST', '/crypto/keys/lock')
	cryptoState.keys = cryptoState.keys.map((k) => ({ ...k, unlockedUntil: null }))
}

/**
 * @param {string} id key id
 */
export async function deleteKey(id) {
	await apiSend('DELETE', `/crypto/keys/${id}`)
	cryptoState.keys = cryptoState.keys.filter((k) => k.id !== id)
}

function replace(key) {
	const index = cryptoState.keys.findIndex((k) => k.id === key.id)
	if (index >= 0) {
		cryptoState.keys.splice(index, 1, key)
	}
}

/**
 * The first validation message of a failed request, or its error.
 *
 * @param {Error} e the axios error
 */
export function errorText(e) {
	const errors = e?.response?.data?.errors
	if (errors) {
		const first = Object.values(errors)[0]
		if (first?.length) {
			return first[0]
		}
	}
	return e?.response?.data?.message ?? e?.response?.data?.detail ?? e?.message ?? String(e)
}

/**
 * A fingerprint in groups of four, as key tools show it.
 *
 * @param {string} fingerprint upper-case hex
 */
export function formatFingerprint(fingerprint) {
	return fingerprint.match(/.{1,4}/g).join(' ')
}

/**
 * A passphrase asked for in a dialog (UnlockKeyDialog in App.vue) because something needs a
 * locked key: answering or continuing encrypted mail, signing. `keys` are the keys that would do.
 */
export const unlockPrompt = reactive({ keys: null, reason: '' })
let unlockWaiting = null

/** The user closed the passphrase dialog: nothing to report, the action just does not happen. */
export class UnlockCancelled extends Error {}

/**
 * Asks the user to unlock one of these keys.
 *
 * @param {object[]} keys the keys (id, fingerprint, addresses) that would do
 * @param {string} reason what it is for, shown in the dialog
 * @return {Promise<void>} resolved once unlocked, rejected when the user gave up
 */
export function askUnlock(keys, reason) {
	unlockWaiting?.reject(new UnlockCancelled('cancelled'))
	return new Promise((resolve, reject) => {
		unlockWaiting = { resolve, reject }
		Object.assign(unlockPrompt, { keys, reason })
	})
}

/**
 * @param {boolean} unlocked whether the key was unlocked
 */
export function finishUnlock(unlocked) {
	const waiting = unlockWaiting
	unlockWaiting = null
	Object.assign(unlockPrompt, { keys: null, reason: '' })
	if (unlocked) {
		waiting?.resolve()
	} else {
		waiting?.reject(new UnlockCancelled('cancelled'))
	}
}

/**
 * Runs an action; when the server says a key is locked, asks for its passphrase and runs it once more.
 *
 * @param {() => Promise<any>} action the request
 * @param {string} reason why the key is needed
 */
export async function withUnlockedKey(action, reason) {
	try {
		return await action()
	} catch (e) {
		if (e?.code !== 'key_locked' || !e.keys?.length) {
			throw e
		}
		await askUnlock(e.keys, reason)
		return await action()
	}
}

/**
 * Which keys these recipients can be encrypted to (RecipientKeysDto per address). With search,
 * the server also looks in Contacts and on the internet (the recipient's domain, keys.openpgp.org).
 *
 * @param {string[]} addresses e-mail addresses
 * @param {boolean} search look beyond the keys already kept
 */
export async function lookupRecipients(addresses, search) {
	if (!addresses.length) {
		return []
	}
	return apiSend('POST', '/crypto/recipients', { addresses, search })
}

/**
 * Saves a key into the contact with its address (a new contact when there is none).
 *
 * @param {string} id key id
 * @return {Promise<{contactId: string, name: string, created: boolean}>}
 */
export function saveKeyToContact(id) {
	return apiSend('POST', `/crypto/keys/${id}/contact`)
}

/**
 * Publishes an own OpenPGP key on keys.openpgp.org, which mails each address a link to confirm.
 *
 * @param {string} id key id
 * @return {Promise<{addresses: Object<string, string>}>} per address: unpublished, pending, published or revoked
 */
export function publishKey(id) {
	return apiSend('POST', `/crypto/keys/${id}/publish`)
}

/**
 * Imports a key attached to a message.
 *
 * @param {{accountId: string, folderId: string}} where the message's folder
 * @param {string} messageId the message
 * @param {string} partId the attachment
 */
export async function importAttachedKey(where, messageId, partId) {
	const result = await apiSend('POST', `/accounts/${where.accountId}/folders/${where.folderId}/messages/${messageId}/parts/${encodeURIComponent(partId)}/import-key`)
	await loadKeys().catch(() => {})
	return result
}

/**
 * Whether an attachment looks like a key or certificate to import.
 *
 * @param {{fileName: string, contentType: string}} attachment the attachment
 */
export function isKeyAttachment(attachment) {
	return ['application/pgp-keys', 'application/pkix-cert', 'application/x-x509-ca-cert', 'application/x-pem-file'].includes(attachment.contentType)
		|| /\.(asc|pem|crt|cer)$/i.test(attachment.fileName)
}
