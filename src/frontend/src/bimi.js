import { apiBase } from './api.js'
import { layout } from './store/layout.js'

/**
 * The URL of a message's sender logo (BIMI), or undefined when it has none to show: the server
 * only names a domain when the receiving mail server verified the message with DMARC. The
 * browser caches the answer per domain, so a list asks once per sender domain.
 *
 * @param {object} [message] a message summary or the open message
 * @return {string|undefined} undefined, not null: NcAvatar treats any defined url as an image
 */
export function bimiUrl(message) {
	const bimi = message?.bimi
	if (!layout.senderLogos || !bimi) {
		return undefined
	}

	const selector = bimi.selector ? `?s=${encodeURIComponent(bimi.selector)}` : ''
	return `${apiBase}/bimi/${encodeURIComponent(bimi.domain)}${selector}`
}
