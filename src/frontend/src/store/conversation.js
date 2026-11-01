import { reactive } from 'vue'
import { apiGet } from '../api.js'
import { accountsState } from './accounts.js'
import { composeReply } from './compose.js'
import { mailboxState, openLocation, setFlags } from './mailbox.js'

/**
 * The conversation of the open message, for accounts that show their mail as conversations
 * (account.conversations). The server finds it in every folder of the account (the user's replies
 * in Sent, a late answer in Archive); the reader shows it oldest first, with the open message and
 * the unread ones expanded.
 *
 * An entry: { key, where, folderName, folderRole, message (the list summary, with cc),
 * messageIdHeader, parentIdHeader, expanded, full (the message with its html, once loaded),
 * loading, error }. The open message's entry shows mailboxState.open.message instead of full.
 */
export const conversationState = reactive({
	/** "accountId/folderId/messageId" of the message the conversation was loaded for. */
	key: null,
	accountId: null,
	loading: false,
	error: null,
	entries: [],
	truncated: false,
	/** Keys of the entries that were unread when the conversation was opened: the "new" ones. */
	fresh: [],
	/** A reply waiting for the user's answer to the reply guard: see guardFor. */
	guard: null,
	/** Bumped to ask the view to bring an entry into sight: { key, at }. */
	focus: null,
})

/** At most this many messages are expanded (and loaded) when a conversation opens. */
const MAX_EXPANDED = 8

export function conversationsOn(accountId) {
	return accountsState.accounts.find((a) => a.id === accountId)?.conversations === true
}

function entryKey(folderId, messageId) {
	return `${folderId}/${messageId}`
}

/** Whether the entry is the open message. */
export function isOpenEntry(entry) {
	const open = mailboxState.open
	return entry.where.accountId === open.accountId && entry.where.folderId === open.folderId && entry.message.id === open.id
}

function reset(key = null, accountId = null) {
	Object.assign(conversationState, { key, accountId, loading: false, error: null, entries: [], truncated: false, fresh: [], guard: null, focus: null })
}

/**
 * Loads the conversation of the open message, or forgets it when the account does not show
 * conversations. A message already in the shown conversation keeps it: only the focus moves.
 */
export async function loadConversation() {
	const { accountId, folderId, id } = mailboxState.open
	if (!id || !conversationsOn(accountId)) {
		reset()
		return
	}
	const key = `${accountId}/${folderId}/${id}`
	if (key === conversationState.key) {
		return
	}
	const known = conversationState.accountId === accountId && conversationState.entries.find((e) => e.key === entryKey(folderId, id))
	if (known) {
		conversationState.key = key
		known.expanded = true
		conversationState.focus = { key: known.key, at: Date.now() }
		return
	}

	reset(key, accountId)
	conversationState.loading = true
	try {
		const result = await apiGet(`/accounts/${accountId}/folders/${folderId}/messages/${id}/conversation`)
		if (conversationState.key !== key) {
			return
		}
		const entries = result.messages.map((m) => ({
			key: entryKey(m.folderId, m.message.id),
			where: { accountId, folderId: m.folderId },
			folderName: m.folderName,
			folderRole: m.folderRole,
			message: { ...m.message, cc: m.cc },
			messageIdHeader: m.messageIdHeader,
			parentIdHeader: m.parentIdHeader,
			expanded: false,
			full: null,
			loading: false,
			error: null,
		}))
		// One message alone reads as before.
		if (entries.length < 2) {
			conversationState.loading = false
			return
		}
		conversationState.fresh = entries.filter((e) => !e.message.seen && !isOpenEntry(e)).map((e) => e.key)
		conversationState.truncated = result.truncated
		conversationState.entries = entries

		// The open message, the unread ones and the newest are expanded; unread ones the newest first.
		const wanted = [
			...entries.filter(isOpenEntry),
			entries[entries.length - 1],
			...entries.filter((e) => conversationState.fresh.includes(e.key)).reverse(),
		]
		for (const entry of new Set(wanted.slice(0, MAX_EXPANDED))) {
			expandEntry(conversationState.entries.find((e) => e.key === entry.key))
		}
	} catch (e) {
		if (conversationState.key === key) {
			conversationState.error = e
		}
	} finally {
		if (conversationState.key === key) {
			conversationState.loading = false
		}
	}
}

/** Shows a message of the conversation in full, reading it (marks it as read) like opening it does. */
export function expandEntry(entry, allowRemote = false) {
	entry.expanded = true
	if (isOpenEntry(entry) || (entry.full && !allowRemote) || entry.loading) {
		return
	}
	loadEntry(entry, allowRemote)
}

export function collapseEntry(entry) {
	entry.expanded = false
}

async function loadEntry(entry, allowRemote) {
	entry.loading = true
	entry.error = null
	const { accountId, folderId } = entry.where
	try {
		entry.full = await apiGet(`/accounts/${accountId}/folders/${folderId}/messages/${entry.message.id}${allowRemote ? '?remote=allow' : ''}`)
		if (!entry.message.seen) {
			setFlags(entry.where, [entry.message.id], { seen: true }).catch(() => {})
		}
	} catch (e) {
		entry.error = e?.response?.data?.message ?? String(e?.message ?? e)
	} finally {
		entry.loading = false
	}
}

export function expandAll() {
	conversationState.entries.forEach((e) => expandEntry(e))
}

export function collapseAll() {
	conversationState.entries.filter((e) => !isOpenEntry(e)).forEach(collapseEntry)
}

/** Expands the entry and asks the view to scroll it into sight. */
export function focusEntry(entry) {
	expandEntry(entry)
	conversationState.focus = { key: entry.key, at: Date.now() }
}

/** Every copy of these messages the conversation holds, for setFlags and friends (mailbox.js). */
export function conversationCopies(where, ids) {
	return conversationState.entries
		.filter((e) => e.where.accountId === where.accountId && e.where.folderId === where.folderId && ids.has(e.message.id))
		.flatMap((e) => [e.message, e.full].filter(Boolean))
}

/** Drops messages that were moved or deleted; a conversation of one is no conversation. */
export function dropFromConversation(where, ids) {
	const remaining = conversationState.entries.filter((e) =>
		!(e.where.accountId === where.accountId && e.where.folderId === where.folderId && ids.has(e.message.id)))
	if (remaining.length !== conversationState.entries.length) {
		conversationState.entries = remaining.length > 1 ? remaining : []
	}
}

function addressesOf(message) {
	return [...message.from, ...message.to, ...(message.cc ?? [])]
}

/** Lower-cased address → the address object, everyone on the message but the account itself. */
function people(messages, own) {
	const map = new Map()
	for (const a of messages.flatMap(addressesOf)) {
		const key = a.address.toLowerCase()
		if (key !== own && !map.has(key)) {
			map.set(key, a)
		}
	}
	return map
}

function ownAddress(accountId) {
	return (accountsState.accounts.find((a) => a.id === accountId)?.emailAddress ?? '').toLowerCase()
}

/** Whether the entry was sent by the account itself. */
export function fromMe(entry) {
	const own = ownAddress(entry.where.accountId)
	return entry.message.from.some((a) => a.address.toLowerCase() === own)
}

/**
 * Who was added and who was left out compared with the message this one replies to (or the one
 * before it): { added: [address], removed: [address] }.
 */
export function participantChanges(entry) {
	const entries = conversationState.entries
	const index = entries.indexOf(entry)
	const parent = entries.find((e) => e.messageIdHeader && e.messageIdHeader === entry.parentIdHeader) ?? (index > 0 ? entries[index - 1] : null)
	if (!parent) {
		return { added: [], removed: [] }
	}
	const own = ownAddress(entry.where.accountId)
	const now = people([entry.message], own)
	const before = people([parent.message], own)
	return {
		added: [...now].filter(([k]) => !before.has(k)).map(([, a]) => a),
		removed: [...before].filter(([k]) => !now.has(k)).map(([, a]) => a),
	}
}

/** Everyone in the conversation that a reply all to the entry would leave out. */
function droppedBy(entry) {
	const own = ownAddress(entry.where.accountId)
	const reached = people([entry.message], own)
	return [...people(conversationState.entries.map((e) => e.message), own)].filter(([k]) => !reached.has(k)).map(([, a]) => a)
}

/**
 * What to point out before replying to a message of the shown conversation: newer messages from
 * others (the reply may miss what they said) and, for reply all, people the conversation had who
 * would not get it. Null when there is nothing to say.
 */
export function guardFor(mode, where, messageId) {
	const entries = conversationState.entries
	const index = entries.findIndex((e) => e.where.accountId === where.accountId && e.where.folderId === where.folderId && e.message.id === messageId)
	if (mode === 'forward' || index < 0) {
		return null
	}
	const entry = entries[index]
	const newer = entries.slice(index + 1).filter((e) => !fromMe(e))
	const dropped = mode === 'replyAll' ? droppedBy(entry) : []
	if (!newer.length && !dropped.length) {
		return null
	}
	return { mode, entryKey: entry.key, newer: newer.map((e) => e.key), latestKey: newer.length ? newer[newer.length - 1].key : null, dropped }
}

/**
 * Reply, reply all or forward, by default to the open message. In a conversation, first asks
 * when newer messages or left-out people make that worth a second thought (ReplyGuardDialog).
 */
export async function replyFrom(mode, where = openLocation(), messageId = mailboxState.open.id) {
	const guard = guardFor(mode, where, messageId)
	if (guard) {
		conversationState.guard = guard
		return
	}
	await composeReply(mode, where, messageId)
}

/**
 * Answers the reply guard. The dialog clears conversationState.guard itself once it has closed.
 *
 * @param {object} guard what guardFor returned
 * @param {'this'|'latest'} target reply to the message asked about, or to the newest from others
 * @param {boolean} addDropped put the left-out people back on Cc (reply all)
 */
export async function answerGuard(guard, target, addDropped) {
	const entry = conversationState.entries.find((e) => e.key === (target === 'latest' ? guard.latestKey : guard.entryKey))
	if (!entry) {
		return
	}
	const addCc = addDropped && guard.mode === 'replyAll' ? droppedBy(entry) : []
	await composeReply(guard.mode, entry.where, entry.message.id, { addCc })
}

/** "Show them": brings the newer messages the guard named into sight. */
export function showNewer(guard) {
	const keys = new Set(guard?.newer ?? [])
	const newer = conversationState.entries.filter((e) => keys.has(e.key))
	newer.forEach((e) => expandEntry(e))
	if (newer.length) {
		focusEntry(newer[0])
	}
}

export function entryByKey(key) {
	return conversationState.entries.find((e) => e.key === key)
}
