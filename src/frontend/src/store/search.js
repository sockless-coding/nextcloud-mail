import { reactive } from 'vue'
import { apiGet } from '../api.js'
import { mailboxState } from './mailbox.js'

const PAGE_SIZE = 50

/**
 * A search shown in place of the message list. Two scopes:
 * - folder: the selected folder, searched on the mail server (message text included, all of it);
 * - all: every account and folder, from the app's header cache (subject, names, addresses of
 *   messages already synced), answered without asking the mail servers.
 * Results are hits: { accountId, folderId, folderFullName, message }.
 */
export const searchState = reactive({
	query: '',
	scope: 'folder',
	active: false,
	hits: [],
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

function folderPath(query, before) {
	const { accountId, folderId } = mailboxState.selected
	return `/accounts/${accountId}/folders/${folderId}/search?q=${encodeURIComponent(query)}&limit=${PAGE_SIZE}${before ? `&before=${before}` : ''}`
}

function folderHits(page) {
	const { accountId, folderId } = mailboxState.selected
	return page.messages.map((message) => ({ accountId, folderId, folderFullName: null, message }))
}

export async function runSearch() {
	const query = searchState.query.trim()
	if (!query) {
		clearSearch()
		return
	}
	const run = ++generation
	Object.assign(searchState, { active: true, loading: true, error: null, hits: [], total: 0, nextBefore: null, more: false })
	try {
		if (searchState.scope === 'folder') {
			const page = await apiGet(folderPath(query))
			if (run === generation) {
				Object.assign(searchState, { hits: folderHits(page), total: page.total, nextBefore: page.nextBefore })
			}
		} else {
			const result = await apiGet(`/search?q=${encodeURIComponent(query)}&limit=${PAGE_SIZE}`)
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

/** Next page of a folder search (the cached search returns one page). */
export async function searchMore() {
	if (!searchState.nextBefore || searchState.loadingMore) {
		return
	}
	const run = generation
	searchState.loadingMore = true
	try {
		const page = await apiGet(folderPath(searchState.query.trim(), searchState.nextBefore))
		if (run === generation) {
			searchState.hits.push(...folderHits(page))
			searchState.nextBefore = page.nextBefore
		}
	} catch (e) {
		if (run === generation) {
			searchState.error = toError(e)
		}
	} finally {
		searchState.loadingMore = false
	}
}

/** Back to the folder's message list. */
export function clearSearch() {
	generation++
	Object.assign(searchState, { query: '', active: false, hits: [], total: 0, nextBefore: null, more: false, loading: false, loadingMore: false, error: null })
}

/** Messages moved or deleted leave the results too. */
export function dropHits(messageIds, accountId, folderId) {
	const ids = new Set(messageIds)
	searchState.hits = searchState.hits.filter((h) => !(ids.has(h.message.id) && h.accountId === accountId && h.folderId === folderId))
}
