import { n, t } from '@nextcloud/l10n'
import { reactive } from 'vue'
import { apiGet, apiSend } from '../api.js'
import { layout } from './layout.js'

/**
 * Suspicious-sender warnings (look-alike domains and the like, see SenderChecker.cs). The open
 * message brings its own (with Reply-To and the DMARC result); list rows are checked in batches,
 * once per sender for the session; a sender whose domain age the server is still looking up
 * (RDAP, in the background) is asked about again a few times. Turned off with the "Warn about
 * suspicious senders" setting.
 */
const MAX_BATCH = 200
const MAX_REMEMBERED = 5000
/** When to ask again about a sender whose domain age is pending: after the 1st, 2nd and 3rd answer. */
const RECHECK_MS = [3000, 10000, 30000]

/** "name\naddress" → warning, or null when the sender looks fine. Missing: not checked yet. */
const verdicts = reactive(new Map())
const pending = new Map()
const inFlight = new Set()
/** key → answers so far that said "pending" */
const rechecks = new Map()
/** Registered domains trusted in this session, so warnings already loaded disappear at once. */
const trustedNow = reactive(new Set())
let timer = null

function keyOf(sender) {
	return `${sender.name ?? ''}\n${sender.address.toLowerCase()}`
}

/**
 * Asks for the senders of these messages that were not checked yet.
 *
 * @param {object[]} messages message summaries
 */
export function checkSenders(messages) {
	if (!layout.suspiciousSenders) {
		return
	}

	for (const message of messages) {
		const sender = message?.from?.[0]
		if (!sender?.address) {
			continue
		}

		const key = keyOf(sender)
		if (!verdicts.has(key) && !inFlight.has(key)) {
			pending.set(key, { name: sender.name ?? null, address: sender.address })
		}
	}

	if (pending.size && !timer) {
		// Rows arrive in bursts (page, changes, search); ask once for all of them.
		timer = setTimeout(flush, 50)
	}
}

async function flush() {
	timer = null
	const batch = [...pending.entries()].slice(0, MAX_BATCH)
	batch.forEach(([key]) => {
		pending.delete(key)
		inFlight.add(key)
	})

	if (verdicts.size > MAX_REMEMBERED) {
		verdicts.clear()
	}

	try {
		const { warnings, pending: waiting } = await apiSend('POST', '/sender-checks', { senders: batch.map(([, sender]) => sender) })
		const later = new Set(waiting ?? [])
		batch.forEach(([key, sender], index) => {
			verdicts.set(key, warnings[index] ?? null)
			if (later.has(index)) {
				recheck(key, sender)
			} else {
				rechecks.delete(key)
			}
		})
	} catch {
		// No warning rather than a broken list; not asked again this session.
		batch.forEach(([key]) => verdicts.set(key, null))
	} finally {
		batch.forEach(([key]) => inFlight.delete(key))
	}

	if (pending.size) {
		flush()
	}
}

function recheck(key, sender) {
	const attempt = rechecks.get(key) ?? 0
	if (attempt >= RECHECK_MS.length) {
		rechecks.delete(key)
		return
	}

	rechecks.set(key, attempt + 1)
	setTimeout(() => {
		if (!layout.suspiciousSenders || inFlight.has(key)) {
			return
		}

		pending.set(key, sender)
		if (!timer) {
			timer = setTimeout(flush, 50)
		}
	}, RECHECK_MS[attempt])
}

function shown(warning) {
	return layout.suspiciousSenders && warning && !trustedNow.has(warning.registered) ? warning : null
}

/**
 * @param {object} [message] a message summary
 * @return {object|null} the warning for its sender
 */
export function listWarning(message) {
	const sender = message?.from?.[0]
	return sender?.address ? shown(verdicts.get(keyOf(sender))) : null
}

/**
 * @param {object} [message] the open message
 * @return {object|null} its warning
 */
export function messageWarning(message) {
	return shown(message?.senderWarning)
}

/**
 * Stops warning about a domain and its subdomains.
 *
 * @param {string} domain a registered domain (warning.registered)
 */
export async function trustDomain(domain) {
	const { domain: trusted } = await apiSend('POST', '/trusted-senders', { domain })
	trustedNow.add(trusted)
}

export function listTrusted() {
	return apiGet('/trusted-senders')
}

export async function distrustDomain(domain) {
	await apiSend('DELETE', `/trusted-senders/${encodeURIComponent(domain)}`)
	trustedNow.delete(domain)
	// Senders of that domain (and look-alikes of it) need a fresh verdict.
	verdicts.clear()
}

/**
 * One reason in words.
 *
 * @param {object} reason { code, domain, looksLike, detail, field }
 * @return {string}
 */
export function reasonText(reason) {
	const values = { domain: reason.domain, known: reason.looksLike }
	if (reason.field === 'replyTo') {
		return t('sockless_mail', 'Replies would go to {domain}, which imitates {known}.', values)
	}

	switch (reason.code) {
	case 'lookalike':
		switch (reason.detail) {
		case 'alphabet':
			// The domain reads as what it imitates, so do not show it twice.
			return t('sockless_mail', 'The sender\'s domain is not {known}: it imitates it with letters from another alphabet.', values)
		case 'characters':
			return t('sockless_mail', '{domain} imitates {known} with look-alike characters.', values)
		case 'ending':
			return t('sockless_mail', '{domain} has the name of {known} with another ending.', values)
		default:
			return t('sockless_mail', '{domain} is one letter away from {known}.', values)
		}
	case 'brandName':
		return t('sockless_mail', '{domain} contains the name of {known} but does not belong to it.', values)
	case 'mixedScripts':
		return t('sockless_mail', '{domain} mixes letters from different alphabets.', values)
	case 'nameShowsDomain':
		return t('sockless_mail', 'The sender\'s name shows {known}, but the message comes from {domain}.', values)
	case 'dmarcFail':
		return t('sockless_mail', 'Your mail server could not verify that this message really comes from {domain}.', values)
	case 'replyTo':
		return t('sockless_mail', 'Replies would go to {domain}.', values)
	case 'newDomain': {
		const days = Number(reason.detail)
		return days === 0
			? t('sockless_mail', '{domain} was registered today.', values)
			: n('sockless_mail', '{domain} was registered only {days} day ago.', '{domain} was registered only {days} days ago.', days, { ...values, days })
	}
	default:
		return t('sockless_mail', 'Something about {domain} looks suspicious.', values)
	}
}

/**
 * @param {object} warning a warning
 * @return {string} its heading
 */
export function warningTitle(warning) {
	return warning.level === 'danger'
		? t('sockless_mail', 'This message may be a phishing attempt')
		: t('sockless_mail', 'Be careful with this sender')
}
