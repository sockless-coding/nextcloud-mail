import { reactive } from 'vue'
import { apiGet } from '../api.js'
import { mailboxState } from './mailbox.js'
import { hasTag, tagsState } from './tags.js'
import { viewOf } from './unified.js'

const PAGE_SIZE = 50

/**
 * A search or a quick filter (unread, flagged, attachments), shown in place of the message list.
 * Results are hits: { accountId, folderId, folderFullName, message }. Where they come from:
 * - folder: the selected folder, searched on the mail server (words in the whole message and/or
 *   the filter, over the whole folder);
 * - cache: words across every account and folder, from the app's header cache (subject, names,
 *   addresses of messages already synced), with the filter applied there too;
 * - unified: the filter alone over the unified folder shown (every account's inbox, sent, …),
 *   asked of each mail server;
 * - tag: a tag filter ("tag:{id}"), with or without words, read from the app's tag index for the
 *   folder or unified folder shown.
 * With only a filter, polling keeps the results current (mergeFolderChanges, mergeUnifiedChanges).
 */
export const searchState = reactive({
	query: '',
	scope: 'folder',
	/** '', 'unread', 'flagged', 'attachments' or 'tag:{tagId}' */
	filter: '',
	active: false,
	/** folder | cache | unified: where the shown results came from. */
	source: null,
	hits: [],
	/** null when not every match was counted. */
	total: 0,
	nextBefore: null,
	more: false,
	loading: false,
	loadingMore: false,
	error: null,
})

/** Bumped by every search; a slower earlier answer must not replace a newer one. */
let generation = 0

function toError(e) {
	const data = e?.response?.data
	const field = data?.errors ? Object.values(data.errors)[0]?.[0] : null
	return { message: data?.message ?? field ?? String(e?.message ?? e) }
}

function params(entries) {
	return Object.entries(entries)
		.filter(([, value]) => value !== '' && value !== null && value !== undefined)
		.map(([key, value]) => `${key}=${encodeURIComponent(value)}`)
		.join('&')
}

function folderPath(before) {
	const { accountId, folderId } = mailboxState.selected
	return `/accounts/${accountId}/folders/${folderId}/search?${params({ q: searchState.query.trim(), filter: searchState.filter, limit: PAGE_SIZE, before })}`
}

function unifiedPath(before) {
	return `/unified/${mailboxState.selected.unified}/filter?${params({ filter: searchState.filter, limit: PAGE_SIZE, before })}`
}

/** The tag of a tag filter, or null. */
export function filterTag() {
	return searchState.filter.startsWith('tag:') ? tagsState.list.find((t) => t.id === searchState.filter.slice(4)) ?? null : null
}

function tagPath(before) {
	const { accountId, folderId, unified } = mailboxState.selected
	const scope = unified ? { role: viewOf(unified)?.roles[0] } : { accountId, folderId }
	return `/tags/${searchState.filter.slice(4)}/messages?${params({ ...scope, q: searchState.query.trim(), limit: PAGE_SIZE, before })}`
}

function folderHits(page) {
	const { accountId, folderId } = mailboxState.selected
	return page.messages.map((message) => ({ accountId, folderId, folderFullName: null, message }))
}

/** The scope a search runs in: a unified folder has no single folder, so it searches everywhere. */
export function currentScope() {
	return mailboxState.selected.unified ? 'all' : searchState.scope
}

/** Whether a message passes the filter, for merging polled changes. */
function passes(message) {
	const tag = filterTag()
	if (tag) {
		return hasTag(message, tag)
	}
	return {
		unread: !message.seen,
		flagged: message.flagged,
		attachments: message.hasAttachments,
	}[searchState.filter] ?? true
}

export async function runSearch() {
	const query = searchState.query.trim()
	if (!query && !searchState.filter) {
		clearSearch()
		return
	}
	const run = ++generation
	const source = searchState.filter.startsWith('tag:')
		? 'tag'
		: query && currentScope() === 'all' ? 'cache' : mailboxState.selected.unified ? 'unified' : 'folder'
	Object.assign(searchState, { active: true, source, loading: true, error: null, hits: [], total: 0, nextBefore: null, more: false })
	try {
		if (source === 'folder') {
			const page = await apiGet(folderPath())
			if (run === generation) {
				Object.assign(searchState, { hits: folderHits(page), total: page.total, nextBefore: page.nextBefore })
			}
		} else if (source === 'unified' || source === 'tag') {
			const page = await apiGet(source === 'tag' ? tagPath() : unifiedPath())
			if (run === generation) {
				Object.assign(searchState, { hits: page.messages, total: page.total, nextBefore: page.nextBefore })
			}
		} else {
			const result = await apiGet(`/search?${params({ q: query, filter: searchState.filter, limit: PAGE_SIZE })}`)
			if (run === generation) {
				Object.assign(searchState, { hits: result.hits, total: result.hits.length, more: result.more })
			}
		}
	} catch (e) {
		if (run === generation) {
			searchState.error = toError(e)
		}
	} finally {
		if (run === generation) {
			searchState.loading = false
		}
	}
}

/** Next page of a folder search or a unified filter (the cached search returns one page). */
export async function searchMore() {
	if (!searchState.nextBefore || searchState.loadingMore) {
		return
	}
	const run = generation
	searchState.loadingMore = true
	try {
		if (searchState.source === 'unified' || searchState.source === 'tag') {
			const page = await apiGet(searchState.source === 'tag' ? tagPath(searchState.nextBefore) : unifiedPath(searchState.nextBefore))
			if (run === generation) {
				searchState.hits.push(...page.messages)
				searchState.nextBefore = page.nextBefore
			}
		} else {
			const page = await apiGet(folderPath(searchState.nextBefore))
			if (run === generation) {
				searchState.hits.push(...folderHits(page))
				searchState.nextBefore = page.nextBefore
			}
		}
	} catch (e) {
		if (run === generation) {
			searchState.error = toError(e)
		}
	} finally {
		searchState.loadingMore = false
	}
}

/** Back to the folder's message list: no words, no filter. */
export function clearSearch() {
	generation++
	Object.assign(searchState, { query: '', filter: '', active: false, source: null, hits: [], total: 0, nextBefore: null, more: false, loading: false, loadingMore: false, error: null })
}

/** Drops the words; a filter stays and is shown on its own. */
export function clearQuery() {
	searchState.query = ''
	runSearch()
}

/** @param {string} filter '', 'unread', 'flagged' or 'attachments' */
export function setFilter(filter) {
	searchState.filter = filter
	runSearch()
}

/** Messages moved or deleted leave the results too. */
export function dropHits(messageIds, accountId, folderId) {
	const ids = new Set(messageIds)
	searchState.hits = searchState.hits.filter((h) => !(ids.has(h.message.id) && h.accountId === accountId && h.folderId === folderId))
}

/** Only a filter can be decided on a message alone; results of words are left as they are. */
function merges() {
	return searchState.active && !searchState.loading && searchState.filter && !searchState.query.trim()
}

function mergeHits(changed, removedKeys, sort) {
	const key = (h) => `${h.accountId}/${h.folderId}/${h.message.id}`
	let hits = removedKeys.size ? searchState.hits.filter((h) => !removedKeys.has(key(h))) : searchState.hits
	const removedCount = searchState.hits.length - hits.length
	const known = new Map(hits.map((h, i) => [key(h), i]))
	const fresh = []
	for (const hit of changed) {
		if (known.has(key(hit))) {
			// Kept even when it no longer passes (read in the Unread filter): it leaves when the filter is shown again.
			hits[known.get(key(hit))] = { ...hits[known.get(key(hit))], message: hit.message }
		} else if (passes(hit.message)) {
			fresh.push(hit)
		}
	}
	// New matches older than the last loaded one belong to pages not loaded yet.
	const last = hits.at(-1)
	const inRange = fresh.filter((h) => !searchState.nextBefore || !last || sort(h, last) < 0)
	if (inRange.length) {
		hits = [...inRange, ...hits].sort(sort)
		if (searchState.total !== null) {
			searchState.total += inRange.length
		}
	}
	searchState.hits = hits
	if (removedCount && searchState.total !== null) {
		searchState.total = Math.max(0, searchState.total - removedCount)
	}
}

const byUid = (a, b) => Number(b.message.id.split('-')[1]) - Number(a.message.id.split('-')[1])
const byDate = (a, b) => Date.parse(b.message.date) - Date.parse(a.message.date) || byUid(a, b)

/** Polled changes of the selected folder (see refreshMessages). */
export function mergeFolderChanges({ accountId, folderId }, messages, removed) {
	if (!merges() || searchState.source !== 'folder') {
		return
	}
	const hits = messages.map((message) => ({ accountId, folderId, folderFullName: null, message }))
	mergeHits(hits, new Set(removed.map((id) => `${accountId}/${folderId}/${id}`)), byUid)
}

/** Polled changes of the unified folder shown (see refreshUnified). */
export function mergeUnifiedChanges(hits, removed) {
	if (!merges() || searchState.source !== 'unified') {
		return
	}
	mergeHits(hits, new Set(removed.map((r) => `${r.accountId}/${r.folderId}/${r.messageId}`)), byDate)
}
