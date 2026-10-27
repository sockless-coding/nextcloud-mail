import axios from '@nextcloud/axios'
import { loadState } from '@nextcloud/initial-state'
import { generateUrl, getRootUrl } from '@nextcloud/router'

export const EX_APP_ID = 'sockless_mail'

// Provided by the ExApp when it is enabled (UiRegistration.cs). AppAPI serves top-menu
// initial state under its own app id, and stores the value JSON-encoded, so it arrives
// as a JSON string (observed on AppAPI 35) rather than as an object.
const rawConfig = loadState('app_api', 'sockless_mail_config', '{}')
const config = typeof rawConfig === 'string' ? JSON.parse(rawConfig) : rawConfig

/**
 * HaRP serves ExApps directly under <webroot>/exapps/<appid>; the PHP proxy route
 * works for every daemon type and is the fallback.
 */
export const apiMode = config.apiMode === 'harp' ? 'harp' : 'proxy'
export const apiBase = apiMode === 'harp'
	? `${getRootUrl()}/exapps/${EX_APP_ID}/api`
	: generateUrl(`/apps/app_api/proxy/${EX_APP_ID}/api`)

// The ExApp rejects state-changing requests without this header (CSRF guard, ApiRequestGuard.cs).
const headers = { 'X-Sockless-Request': '1' }

/**
 * @param {string} path API path, e.g. "/me"
 */
export async function apiGet(path) {
	const { data } = await axios.get(apiBase + path, { headers })
	return data
}

/**
 * @param {string} method HTTP method; PATCH is not available through the PHP proxy
 * @param {string} path API path
 * @param {object} [body] JSON body
 */
export async function apiSend(method, path, body) {
	const { data } = await axios.request({ method, url: apiBase + path, data: body, headers })
	return data
}
