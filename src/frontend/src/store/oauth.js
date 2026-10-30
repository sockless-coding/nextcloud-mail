import { t } from '@nextcloud/l10n'
import { reactive } from 'vue'
import { apiBase, apiGet, apiSend } from '../api.js'
import { accountsState } from './accounts.js'

/**
 * Signing in with OAuth2 providers, and their admin settings. The provider's page opens in a
 * popup; the server finishes the sign-in on its callback, and this side polls the flow until it
 * is done, then creates the account (or replaces an account's grant) from it.
 */
export const oauthState = reactive({
	/** Providers an admin has set up: [{ id, name }]. */
	providers: [],
	isAdmin: false,
})

const POLL_MS = 1000

/** The URL providers redirect back to; admins register it with each provider. */
export const callbackUrl = `${window.location.origin}${apiBase.replace(/\/api$/, '')}/oauth/callback`

function errorOf(e) {
	const data = e?.response?.data
	const field = data?.errors ? Object.values(data.errors)[0]?.[0] : null
	const error = new Error(data?.message ?? field ?? (e?.response ? `HTTP ${e.response.status}` : String(e?.message ?? e)))
	error.code = data?.error
	error.fieldErrors = data?.errors ? Object.fromEntries(Object.entries(data.errors).map(([k, v]) => [k, v[0]])) : {}
	return error
}

/** Who the user is (admin or not) and which providers they can sign in with. Failures leave the defaults. */
export async function loadOAuthState() {
	const [me, providers] = await Promise.allSettled([apiGet('/me'), apiGet('/oauth/providers')])
	oauthState.isAdmin = me.status === 'fulfilled' && me.value.isAdmin === true
	oauthState.providers = providers.status === 'fulfilled' ? providers.value : []
}

/**
 * Runs a sign-in in a popup and resolves with the finished flow ({ flowId, email }).
 * Must be called from a click handler: the popup is opened before anything is awaited, or
 * browsers block it.
 *
 * @param {{provider?: string, accountId?: string}} what a provider for a new account, or the account to sign in again
 * @param {AbortSignal} [signal] cancels waiting (the popup is closed)
 */
export async function signIn({ provider, accountId }, signal) {
	const popup = window.open('', 'sockless-mail-oauth', 'popup,width=520,height=680')
	if (!popup) {
		throw new Error(t('sockless_mail', 'The sign-in window was blocked. Allow pop-ups for this site and try again.'))
	}
	try {
		let started
		try {
			started = await apiSend('POST', '/oauth/flows', { provider: provider ?? null, accountId: accountId ?? null })
		} catch (e) {
			throw errorOf(e)
		}
		popup.location.href = started.authorizeUrl
		for (;;) {
			await new Promise((resolve) => setTimeout(resolve, POLL_MS))
			if (signal?.aborted) {
				throw new Error(t('sockless_mail', 'The sign-in was cancelled.'))
			}
			let flow
			try {
				flow = await apiGet(`/oauth/flows/${encodeURIComponent(started.flowId)}`)
			} catch (e) {
				throw errorOf(e)
			}
			if (flow.status === 'done') {
				return { flowId: started.flowId, email: flow.email }
			}
			if (flow.status === 'failed') {
				throw new Error(flow.error)
			}
			// Closed before the provider sent the user back.
			if (popup.closed) {
				throw new Error(t('sockless_mail', 'The sign-in window was closed before the sign-in finished.'))
			}
		}
	} finally {
		popup.close()
	}
}

/**
 * @param {string} flowId a finished sign-in
 * @param {{displayName?: string, senderName?: string}} names optional names; default to the address and the provider's name
 */
export async function createOAuthAccount(flowId, { displayName, senderName } = {}) {
	try {
		const account = await apiSend('POST', '/accounts/oauth', { flowId, displayName: displayName || null, senderName: senderName || null })
		accountsState.accounts.push(account)
		return account
	} catch (e) {
		throw errorOf(e)
	}
}

/**
 * @param {string} accountId account signed in again
 * @param {string} flowId the finished sign-in
 */
export async function replaceGrant(accountId, flowId) {
	try {
		const account = await apiSend('POST', `/accounts/${accountId}/oauth`, { flowId })
		accountsState.accounts.splice(accountsState.accounts.findIndex((a) => a.id === accountId), 1, account)
		return account
	} catch (e) {
		throw errorOf(e)
	}
}

export async function loadOAuthClients() {
	try {
		return await apiGet('/admin/oauth')
	} catch (e) {
		throw errorOf(e)
	}
}

/**
 * @param {string} provider provider id
 * @param {{clientId: string, clientSecret?: string, tenant?: string, redirectUri: string}} input an empty client id switches the provider off; an empty secret keeps the stored one
 */
export async function saveOAuthClient(provider, input) {
	try {
		const clients = await apiSend('PUT', `/admin/oauth/${provider}`, input)
		oauthState.providers = clients.filter((c) => c.configured).map((c) => ({ id: c.provider, name: c.name }))
		return clients
	} catch (e) {
		throw errorOf(e)
	}
}

/** Instance-wide settings ({ domainAgeChecks }). */
export async function loadAdminSettings() {
	try {
		return await apiGet('/admin/settings')
	} catch (e) {
		throw errorOf(e)
	}
}

/**
 * @param {{domainAgeChecks?: boolean}} input the settings to change
 */
export async function saveAdminSettings(input) {
	try {
		return await apiSend('PUT', '/admin/settings', input)
	} catch (e) {
		throw errorOf(e)
	}
}
