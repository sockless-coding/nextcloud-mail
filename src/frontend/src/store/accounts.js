import { computed, reactive } from 'vue'
import { apiGet, apiSend } from '../api.js'

/**
 * The user's mail accounts. The server is the source of truth: every change goes through the
 * API first and the list is updated from its answer.
 */
export const accountsState = reactive({
	accounts: [],
	loading: true,
	loadError: '',
})

/** Field errors from a 400 response, keyed by field path (e.g. "imap.host"). */
export class ValidationError extends Error {
	constructor(fieldErrors) {
		super('Validation failed')
		this.fieldErrors = fieldErrors
	}
}

/**
 * Turns an axios error into something the UI can show.
 *
 * @param {Error} e the error
 */
function rethrow(e) {
	const data = e?.response?.data
	if (e?.response?.status === 400 && data?.errors) {
		const fieldErrors = Object.fromEntries(Object.entries(data.errors).map(([k, v]) => [k, v[0]]))
		throw new ValidationError(fieldErrors)
	}
	throw new Error(data?.message ?? (e?.response ? `HTTP ${e.response.status}` : String(e)))
}

export async function loadAccounts() {
	accountsState.loading = true
	accountsState.loadError = ''
	try {
		accountsState.accounts = await apiGet('/accounts')
	} catch (e) {
		accountsState.loadError = e?.response ? `HTTP ${e.response.status}` : String(e)
	} finally {
		accountsState.loading = false
	}
}

/**
 * @param {object} input account settings including the password
 */
export async function createAccount(input) {
	try {
		const account = await apiSend('POST', '/accounts', input)
		accountsState.accounts.push(account)
		return account
	} catch (e) {
		rethrow(e)
	}
}

/**
 * @param {string} id account id
 * @param {object} input settings; an empty password keeps the stored one
 */
export async function updateAccount(id, input) {
	try {
		const account = await apiSend('PUT', `/accounts/${id}`, input)
		const index = accountsState.accounts.findIndex((a) => a.id === id)
		accountsState.accounts.splice(index, 1, account)
		return account
	} catch (e) {
		rethrow(e)
	}
}

/**
 * @param {string} id account id
 */
export async function deleteAccount(id) {
	try {
		await apiSend('DELETE', `/accounts/${id}`)
	} catch (e) {
		rethrow(e)
	}
	accountsState.accounts = accountsState.accounts.filter((a) => a.id !== id)
}

/**
 * Moves an account one place up (-1) or down (+1).
 *
 * @param {string} id account id
 * @param {number} delta -1 or +1
 */
export async function moveAccount(id, delta) {
	const from = accountsState.accounts.findIndex((a) => a.id === id)
	await moveAccountTo(id, from + delta)
}

/**
 * Moves an account to a place in the list (dragging it there). Shown at once; put back when
 * the server refuses.
 *
 * @param {string} id account id
 * @param {number} to its new index
 */
export async function moveAccountTo(id, to) {
	const previous = accountsState.accounts
	const order = [...previous]
	const from = order.findIndex((a) => a.id === id)
	if (from < 0 || to < 0 || to >= order.length || to === from) {
		return
	}
	order.splice(to, 0, order.splice(from, 1)[0])
	accountsState.accounts = order
	try {
		await apiSend('PUT', '/accounts/order', { accountIds: order.map((a) => a.id) })
	} catch (e) {
		accountsState.accounts = previous
		rethrow(e)
	}
}

/**
 * How an account is shown: colour, badge (text or icon instead of the initials), and whether
 * it is part of "All accounts".
 *
 * @param {string} id account id
 * @param {{color: string|null, badgeText: string|null, badgeIcon: string|null, includeInUnified: boolean}} appearance what to show
 */
export async function setAppearance(id, appearance) {
	try {
		const account = await apiSend('PUT', `/accounts/${id}/appearance`, appearance)
		const index = accountsState.accounts.findIndex((a) => a.id === id)
		accountsState.accounts.splice(index, 1, account)
		return account
	} catch (e) {
		rethrow(e)
	}
}

/**
 * Turns grouping into conversations on or off for one account.
 *
 * @param {string} id account id
 * @param {boolean} enabled whether the account's mail is shown as conversations
 */
export async function setConversations(id, enabled) {
	try {
		const account = await apiSend('PUT', `/accounts/${id}/conversations`, { enabled })
		const index = accountsState.accounts.findIndex((a) => a.id === id)
		accountsState.accounts.splice(index, 1, account)
		return account
	} catch (e) {
		rethrow(e)
	}
}

/** The accounts the "All accounts" views take in. */
export const unifiedAccounts = computed(() => accountsState.accounts.filter((a) => a.includeInUnified !== false))

/** "All accounts" is offered with at least two accounts in it. */
export const unifiedAvailable = computed(() => unifiedAccounts.value.length > 1)

/**
 * Tests unsaved settings. Without a password, the stored one of accountId is used.
 *
 * @param {object} input account settings
 * @param {string|null} accountId account being edited, if any
 */
export async function testConnection(input, accountId) {
	try {
		return await apiSend('POST', '/accounts/test', { ...input, accountId })
	} catch (e) {
		if (e?.response?.status === 429) {
			throw new Error('Too many connection tests. Wait a minute and try again.')
		}
		rethrow(e)
	}
}

/**
 * A colour per account for the account rail and the dots in mixed lists: the one the user chose,
 * or else one derived from the id, so it stays the same across sessions and devices. The derived
 * ones all carry white text in both themes.
 */
export const ACCOUNT_COLORS = ['#0f62d6', '#1d7f52', '#c13a3a', '#7346c2', '#a85a0b', '#0e7584', '#a3306f', '#4f5d75']

export function accountColor(id) {
	const chosen = accountsState.accounts.find((a) => a.id === id)?.color
	return chosen || defaultColor(id)
}

/** Black or white, whichever reads better on the colour (WCAG relative luminance). */
export function textColorOn(color) {
	const channel = (i) => {
		const c = parseInt(color.slice(1 + 2 * i, 3 + 2 * i), 16) / 255
		return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4
	}
	const luminance = 0.2126 * channel(0) + 0.7152 * channel(1) + 0.0722 * channel(2)
	// Contrast with white is (1.05) / (L + 0.05), with black (L + 0.05) / 0.05; equal at L ≈ 0.179.
	return luminance > 0.179 ? '#000' : '#fff'
}

export function defaultColor(id) {
	let hash = 0
	for (const c of String(id)) {
		hash = (hash * 31 + c.charCodeAt(0)) >>> 0
	}
	return ACCOUNT_COLORS[hash % ACCOUNT_COLORS.length]
}

/** One or two letters for the account rail. */
export function accountInitials(account) {
	const words = (account.displayName || account.emailAddress || '?').split(/[\s@._-]+/).filter(Boolean)
	return (words.length > 1 ? words[0][0] + words[1][0] : words[0].slice(0, 2)).toUpperCase()
}
