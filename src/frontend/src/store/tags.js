import { reactive } from 'vue'
import { apiGet, apiSend } from '../api.js'
import { ActionError } from './mailbox.js'

/**
 * The user's tags (name, color, IMAP keyword), shared by all accounts. Messages carry keywords
 * (message.keywords); a tag is shown for each keyword that belongs to one, compared without case
 * as servers do.
 */
export const tagsState = reactive({
	list: [],
	loaded: false,
	/** Messages per tag and account in the tag folders: [{ tagId, accountId, total, unread }]. */
	counts: [],
})

/** The colors offered for tags: readable chips in light and dark themes alike. */
export const TAG_COLORS = [
	'#0082c9', '#2e7d32', '#c62828', '#ef6c00', '#6a1b9a', '#00838f',
	'#ad1457', '#5d4037', '#f9a825', '#283593', '#558b2f', '#546e7a',
]

async function call(method, path, body) {
	try {
		return await apiSend(method, path, body)
	} catch (e) {
		throw new ActionError(e)
	}
}

export async function loadTags() {
	try {
		tagsState.list = await apiGet('/tags')
		tagsState.loaded = true
	} catch {
		// Without tags messages just show none; the next load tries again.
	}
}

/** The tag folders' counts; polled with the folder lists. */
export async function loadTagCounts() {
	if (!tagsState.list.length) {
		tagsState.counts = []
		return
	}
	try {
		tagsState.counts = await apiGet('/tags/counts')
	} catch {
		// The badges keep their last values.
	}
}

/** Unread messages with a tag, in one account or all. */
export function tagUnread(tagId, accountId = null) {
	return tagsState.counts
		.filter((c) => c.tagId === tagId && (!accountId || c.accountId === accountId))
		.reduce((sum, c) => sum + c.unread, 0)
}

/** @param {{name: string, color: string, keyword?: string}} input a new tag, or a keyword to adopt */
export async function createTag(input) {
	const tag = await call('POST', '/tags', input)
	tagsState.list = [...tagsState.list, tag]
	return tag
}

/** @param {string} id tag @param {{name?: string, color?: string}} input changes */
export async function updateTag(id, input) {
	const tag = await call('PUT', `/tags/${id}`, input)
	tagsState.list = tagsState.list.map((t) => (t.id === id ? tag : t))
	return tag
}

/** @param {string[]} ids every tag id, in the new order */
export async function reorderTags(ids) {
	const previous = tagsState.list
	const byId = new Map(previous.map((t) => [t.id, t]))
	tagsState.list = ids.map((id) => byId.get(id)).filter(Boolean)
	try {
		tagsState.list = await call('PUT', '/tags/order', { ids })
	} catch (e) {
		tagsState.list = previous
		throw e
	}
}

/**
 * @param {string} id tag
 * @param {boolean} fromMessages take the tag off every message first
 */
export async function deleteTag(id, fromMessages) {
	await call('DELETE', `/tags/${id}${fromMessages ? '?fromMessages=true' : ''}`)
	const tag = tagsState.list.find((t) => t.id === id)
	tagsState.list = tagsState.list.filter((t) => t.id !== id)
	return tag
}

export function loadSuggestions() {
	return apiGet('/tags/suggestions').catch((e) => {
		throw new ActionError(e)
	})
}

/** The tags of a message, in the user's order. */
export function tagsOf(message) {
	const keywords = message?.keywords
	if (!keywords?.length) {
		return []
	}
	const own = new Set(keywords.map((k) => k.toLowerCase()))
	return tagsState.list.filter((t) => own.has(t.keyword.toLowerCase()))
}

export function hasTag(message, tag) {
	return (message?.keywords ?? []).some((k) => k.toLowerCase() === tag.keyword.toLowerCase())
}

/** Black or white text, whichever reads better on the color. */
export function textOn(color) {
	const value = parseInt(color.slice(1), 16)
	const channel = (c) => {
		c /= 255
		return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4
	}
	const luminance = 0.2126 * channel(value >> 16) + 0.7152 * channel((value >> 8) & 255) + 0.0722 * channel(value & 255)
	return luminance > 0.4 ? '#000000' : '#ffffff'
}
