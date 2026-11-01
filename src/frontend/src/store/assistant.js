import { getLanguage, t } from '@nextcloud/l10n'
import { reactive } from 'vue'
import { apiGet, apiSend } from '../api.js'
import { push } from './push.js'

/**
 * The Nextcloud Assistant (AssistantService.cs): tasks run by the AI provider the admin set up in
 * Nextcloud. Nothing runs unless the user asks. Nextcloud tells the server when a task ends, and
 * the server nudges the browser (push.js); without the push channel the browser asks itself.
 */

const NOTHING = { available: false, summarize: false, write: false, rewrite: [], tones: [], languages: [], checkScam: false }

export const assistantState = reactive({
	/** The admin allows it and Nextcloud has a provider for at least one of the tasks. */
	available: false,
	summarize: false,
	/** Writing messages (the free prompt). */
	write: false,
	/** The rewrites on offer: 'tone', 'fix', 'shorter', 'clearer', 'translate', 'custom'. */
	rewrite: [],
	/** The provider's own tones for rewriting ({ name, value }); empty: the app's (TONES). */
	tones: [],
	/** Translation targets ({ name, value }), also for translating messages (when rewrite has 'translate'). */
	languages: [],
	/** The reader's phishing and scam check. */
	checkScam: false,
})

/** Tones for writing, and for rewriting without the provider's own; the values go to the server (AssistantPrompts.Tones). */
export const TONES = [
	{ value: 'neutral', name: t('sockless_mail', 'Neutral') },
	{ value: 'friendly', name: t('sockless_mail', 'Friendly') },
	{ value: 'formal', name: t('sockless_mail', 'Formal') },
	{ value: 'concise', name: t('sockless_mail', 'Concise') },
	{ value: 'warm', name: t('sockless_mail', 'Warm') },
	{ value: 'apologetic', name: t('sockless_mail', 'Apologetic') },
	{ value: 'confident', name: t('sockless_mail', 'Confident') },
	{ value: 'enthusiastic', name: t('sockless_mail', 'Enthusiastic') },
]

/** The tones to rewrite in: the provider's, or the app's without "neutral". */
export const rewriteTones = () => assistantState.tones.length ? assistantState.tones : TONES.filter((tone) => tone.value !== 'neutral')

/**
 * What the Assistant did for messages in this tab, by message key ("accountId/folderId/messageId").
 * Each entry is { taskId, status ('starting', 'scheduled', 'running', 'successful', 'failed',
 * 'cancelled', 'unknown'), output, error, startedAt (ms) } plus its own fields: a summary's scope
 * ('conversation' | 'message') and messageCount, a translation's language and truncated.
 */
export const summaries = reactive({})
export const translations = reactive({})
export const scamChecks = reactive({})

/**
 * The language to translate into: the user's Nextcloud language when the provider offers it (by
 * code, or by English name in the app's own list), else the browser's, else the first one.
 */
export function preferredLanguage() {
	const languages = assistantState.languages
	for (const tag of [getLanguage(), navigator.language]) {
		const code = tag?.split(/[-_]/)[0]
		const english = code && new Intl.DisplayNames(['en'], { type: 'language' }).of(code)
		const found = languages.find((l) => l.value === tag || l.value === code || l.value === english)
		if (found) {
			return found.value
		}
	}
	return languages[0]?.value ?? null
}

/** The user's language by its English name ("German"), for answers the model writes itself. */
export function userLanguageName() {
	const code = getLanguage()?.split(/[-_]/)[0] || 'en'
	try {
		return new Intl.DisplayNames(['en'], { type: 'language' }).of(code) ?? 'English'
	} catch {
		return 'English'
	}
}

/** Waiting without the push channel: often at first, then less. */
const POLL_FAST_MS = 3000
const POLL_SLOW_MS = 10_000
const FAST_FOR_MS = 30_000
/** With the push channel the nudge usually comes first; this only catches a missed one. */
const POLL_PUSHED_MS = 20_000

const FINISHED = new Set(['successful', 'failed', 'cancelled', 'unknown'])

let pollTimer = null

/** Asks the server again; the Assistant's buttons show only when this says so. Failures leave them hidden. */
export async function loadAssistant() {
	try {
		Object.assign(assistantState, await apiGet('/assistant'))
	} catch {
		Object.assign(assistantState, NOTHING)
	}
	return assistantState
}

export const summaryKey = (where, messageId) => `${where.accountId}/${where.folderId}/${messageId}`

/** Whether the entry is still waiting for its task. */
export const isWorking = (entry) => !!entry && !FINISHED.has(entry.status)

function errorOf(e) {
	const data = e?.response?.data
	return data?.message ?? (e?.response ? `HTTP ${e.response.status}` : String(e?.message ?? e))
}

/**
 * Starts a task for a message, kept in map under the message's key. One running before for the
 * same message is cancelled.
 *
 * @param {object} map summaries, translations or scamChecks
 * @param {{accountId: string, folderId: string}} where the message's folder
 * @param {string} messageId the message
 * @param {string} action the endpoint under the message ('summary', 'translation', 'scam-check')
 * @param {object} body the request
 * @param {object} fields the entry's own fields from the start
 */
async function runForMessage(map, where, messageId, action, body, fields = {}) {
	const key = summaryKey(where, messageId)
	if (isWorking(map[key])) {
		cancelRequest(map[key])
	}
	map[key] = { taskId: null, status: 'starting', output: null, error: null, startedAt: Date.now(), ...fields }
	const entry = map[key]
	try {
		const answer = await apiSend('POST', `/accounts/${where.accountId}/folders/${where.folderId}/messages/${messageId}/${action}`, body)
		const { task, ...rest } = answer.task ? answer : { task: answer }
		// Dismissed or asked again meanwhile: this answer is no longer wanted.
		if (map[key] !== entry || entry.status !== 'starting') {
			apiSend('POST', `/assistant/tasks/${task.id}/cancel`).catch(() => {})
			return
		}
		Object.assign(entry, rest)
		apply(entry, task)
		track(entry)
	} catch (e) {
		if (entry.status === 'starting') {
			Object.assign(entry, { status: 'failed', error: errorOf(e) })
		}
	}
}

/**
 * Asks for a summary of the message, or of its whole conversation (the default).
 *
 * @param {{accountId: string, folderId: string}} where the message's folder
 * @param {string} messageId the message
 * @param {'conversation'|'message'} scope what to summarize
 */
export function summarize(where, messageId, scope = 'conversation') {
	const messageCount = summaries[summaryKey(where, messageId)]?.messageCount ?? 0
	return runForMessage(summaries, where, messageId, 'summary',
		{ scope, timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone }, { scope, messageCount })
}

/**
 * Translates the message (subject and text) into one of assistantState.languages.
 *
 * @param {{accountId: string, folderId: string}} where the message's folder
 * @param {string} messageId the message
 * @param {string} language a language's value
 */
export function translateMessage(where, messageId, language = preferredLanguage()) {
	return runForMessage(translations, where, messageId, 'translation', { language }, { language, truncated: false })
}

/**
 * Asks whether the message is phishing or a scam; the answer is JSON (see scamVerdict).
 *
 * @param {{accountId: string, folderId: string}} where the message's folder
 * @param {string} messageId the message
 */
export function checkScam(where, messageId) {
	return runForMessage(scamChecks, where, messageId, 'scam-check', { language: userLanguageName() })
}

/**
 * The scam check's answer: { verdict ('safe' | 'suspicious' | 'scam' | null), summary, reasons,
 * advice }. Models wrap JSON in code fences or add words around it; an answer that is no JSON
 * at all comes back as its text, without a verdict.
 *
 * @param {string} output the task's output
 */
export function scamVerdict(output) {
	const text = (output ?? '').trim()
	const start = text.indexOf('{')
	const end = text.lastIndexOf('}')
	try {
		const parsed = JSON.parse(text.slice(start, end + 1))
		const verdict = ['safe', 'suspicious', 'scam'].includes(parsed.verdict) ? parsed.verdict : null
		const reasons = Array.isArray(parsed.reasons) ? parsed.reasons.filter((r) => typeof r === 'string' && r.trim()).slice(0, 8) : []
		return {
			verdict,
			summary: typeof parsed.summary === 'string' ? parsed.summary : '',
			reasons,
			advice: typeof parsed.advice === 'string' ? parsed.advice : '',
		}
	} catch {
		return { verdict: null, summary: text, reasons: [], advice: '' }
	}
}

/** Removes what the Assistant did for a message (cancelling it while it runs). */
export function dismiss(map, key) {
	if (isWorking(map[key])) {
		cancelRequest(map[key])
	}
	delete map[key]
}

/**
 * Starts a composer request: a reactive { taskId, status, output, error, startedAt } like a
 * summary's, which follows its task until it ends.
 *
 * @param {string} path '/assistant/write' or '/assistant/rewrite'
 * @param {object} body the request (WriteInput or RewriteInput on the server)
 */
function startRequest(path, body) {
	const entry = reactive({ taskId: null, status: 'starting', output: null, error: null, startedAt: Date.now() })
	apiSend('POST', path, body).then((task) => {
		// Cancelled meanwhile: the task started all the same, so stop it too.
		if (entry.status !== 'starting') {
			apiSend('POST', `/assistant/tasks/${task.id}/cancel`).catch(() => {})
			return
		}
		apply(entry, task)
		track(entry)
	}, (e) => {
		if (entry.status === 'starting') {
			Object.assign(entry, { status: 'failed', error: errorOf(e) })
		}
	})
	return entry
}

/**
 * A message written from what the user says it should say: a reply (with the conversation),
 * the note for a forward, or a new message.
 *
 * @param {{instruction: string, tone: string, subject?: string, to?: string, senderName?: string, current?: string,
 *   source?: {accountId: string, folderId: string, messageId: string, mode: string}}} input what to write
 */
export const writeMessage = (input) => startRequest('/assistant/write', { ...input, timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone })

/**
 * The text changed as asked.
 *
 * @param {{text: string, action: string, tone?: string, language?: string, instruction?: string}} input what to change
 */
export const rewriteText = (input) => startRequest('/assistant/rewrite', input)

/** Stops waiting for a request or summary; its task is cancelled in Nextcloud too. */
export async function cancelRequest(entry) {
	if (!isWorking(entry)) {
		return
	}
	entry.status = 'cancelled'
	if (entry.taskId) {
		try {
			await apiSend('POST', `/assistant/tasks/${entry.taskId}/cancel`)
		} catch {
			// Finished meanwhile, or gone: either way nothing waits for it any more.
		}
	}
}


/** Requests and summaries whose task has not ended yet. */
const tracked = new Set()

function track(entry) {
	if (isWorking(entry)) {
		tracked.add(entry)
	}
	schedulePoll()
}

/** The push channel's nudge: a task ended. */
export function assistantTaskChanged({ id }) {
	if ([...tracked].some((entry) => entry.taskId === id && isWorking(entry))) {
		refresh(id)
	}
}

function apply(entry, task) {
	entry.taskId = task.id
	entry.status = task.status
	if (task.status === 'successful') {
		entry.output = task.output ?? ''
	} else if (task.status === 'failed') {
		entry.error = task.error || t('sockless_mail', 'The Assistant could not do this.')
	}
}

async function refresh(taskId) {
	try {
		const task = await apiGet(`/assistant/tasks/${taskId}`)
		for (const entry of tracked) {
			if (entry.taskId === taskId && isWorking(entry)) {
				apply(entry, task)
			}
		}
	} catch (e) {
		if (e?.response?.status === 404) {
			for (const entry of tracked) {
				if (entry.taskId === taskId && isWorking(entry)) {
					Object.assign(entry, { status: 'failed', error: t('sockless_mail', 'The Assistant task no longer exists.') })
				}
			}
		}
		// Anything else: asked again on the next round.
	}
}

function schedulePoll() {
	window.clearTimeout(pollTimer)
	for (const entry of tracked) {
		if (!isWorking(entry)) {
			tracked.delete(entry)
		}
	}
	const waiting = [...tracked].filter((entry) => entry.taskId)
	if (!waiting.length) {
		pollTimer = null
		return
	}
	const youngest = Math.max(...waiting.map((s) => s.startedAt))
	const delay = push.connected ? POLL_PUSHED_MS : Date.now() - youngest < FAST_FOR_MS ? POLL_FAST_MS : POLL_SLOW_MS
	pollTimer = window.setTimeout(async () => {
		await Promise.all([...new Set(waiting.map((s) => s.taskId))].map(refresh))
		schedulePoll()
	}, delay)
}
