import { apiGet, apiSend } from '../api.js'

/**
 * Nextcloud Contacts: the user's address books, searched on the server (which keeps them in
 * memory and refetches a book only when it changed).
 */

const EMPTY = Object.freeze({ contacts: [], groups: [] })
const MAX_CACHED = 50
/** Recent search results by query, so typing back and forth does not ask again. */
const cache = new Map()

/**
 * Contacts ({id, name, address, addressBook}) and groups ({name, members: [{name, address}]})
 * matching the query. Failures count as no matches: suggestions are a convenience.
 *
 * @param {string} query what is being typed
 */
export async function searchContacts(query) {
	const q = query.trim().toLowerCase()
	if (q.length < 2) {
		return EMPTY
	}
	if (!cache.has(q)) {
		cache.set(q, apiGet(`/contacts/search?q=${encodeURIComponent(q)}`).catch(() => {
			cache.delete(q)
			return EMPTY
		}))
		if (cache.size > MAX_CACHED) {
			cache.delete(cache.keys().next().value)
		}
	}
	return cache.get(q)
}

/** The user's address books: [{id, name, writable}]. */
export function addressBooks() {
	return apiGet('/contacts/address-books')
}

/**
 * Contacts that already have this address: [{id, name, address, addressBook}].
 *
 * @param {string} address email address
 */
export function contactsWithAddress(address) {
	return apiGet(`/contacts/by-address?address=${encodeURIComponent(address)}`)
}

/**
 * @param {string} addressBookId where to create it
 * @param {string} name display name
 * @param {string} address email address
 */
export async function createContact(addressBookId, name, address) {
	const result = await apiSend('POST', '/contacts', { addressBookId, name, address })
	cache.clear()
	return result
}

/**
 * @param {string} contactId "book/card.vcf"
 * @param {string} address email address to add
 */
export async function addAddressToContact(contactId, address) {
	const result = await apiSend('POST', '/contacts/addresses', { contactId, address })
	cache.clear()
	return result
}
