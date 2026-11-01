import { reactive } from 'vue'
import { apiSend } from '../api.js'
import { accountsState } from './accounts.js'

/**
 * One row per conversation in the message lists of accounts that show conversations. Lists still
 * load messages; groupRows folds those of one conversation in one folder into the row of the
 * newest. Such a row sums up the conversation's messages in that folder (members, from the
 * server's cache, so also those not loaded yet) and its actions apply to all of them; never to
 * the conversation's messages in other folders (the user's replies in Sent stay where they are).
 * Filtered lists (smart and tag folders) group only what they list.
 *
 * members: "accountId/folderId/conversationId" → { messages: [{ id, seen, flagged, answered,
 * hasAttachments, from, date }], replied }.
 */
export const conversationRows = reactive({ members: {} })

/** Conversations asked for per request (the server's limit). */
const BATCH = 200

export function groupKey(accountId, folderId, conversationId) {
	return `${accountId}/${folderId}/${conversationId}`
}

function conversationsOn(accountId) {
	return accountsState.accounts.find((a) => a.id === accountId)?.conversations === true
}

function ownAddress(accountId) {
	return (accountsState.accounts.find((a) => a.id === accountId)?.emailAddress ?? '').toLowerCase()
}

/** The message ids a row acts on: its conversation's in the folder, or its own. */
export function rowIds(row) {
	return row.ids ?? [row.message.id]
}

/**
 * Folds a list's rows ({ accountId, folderId, message, key, location }, newest first) into one
 * row per conversation. A conversation row keeps the newest message as its message (with the
 * conversation's read, flag, answered and attachment state), and adds ids and conversation:
 * { count, unread, senders }.
 *
 * @param {object[]} rows the list's rows
 * @param {boolean} wholeFolder sum up all the conversation's messages in the folder (members);
 *     false for filtered lists, where the listed ones are the conversation
 */
export function groupRows(rows, wholeFolder) {
	const groups = new Map()
	const out = []
	for (const row of rows) {
		const id = row.message.conversationId
		if (!id || !conversationsOn(row.accountId)) {
			out.push(row)
			continue
		}
		const key = groupKey(row.accountId, row.folderId, id)
		let group = groups.get(key)
		if (!group) {
			group = { row, key, listed: [] }
			groups.set(key, group)
			out.push(group)
		}
		group.listed.push(row.message)
	}
	return out.map((entry) => (entry.listed ? summarize(entry, wholeFolder) : entry))
}

function summarize({ row, key, listed }, wholeFolder) {
	const members = wholeFolder ? conversationRows.members[key] : null
	const byId = new Map(listed.map((m) => [m.id, m]))
	// The listed copies are the ones actions change at once; members fill in the rest.
	const messages = [...listed, ...(members?.messages ?? []).filter((m) => !byId.has(m.id))]
	if (messages.length < 2) {
		return row
	}
	const own = ownAddress(row.accountId)
	const senders = []
	const seen = new Set()
	for (const m of [...messages].sort((a, b) => Date.parse(a.date) - Date.parse(b.date))) {
		const from = m.from[0]
		const address = from?.address?.toLowerCase()
		if (address && !seen.has(address)) {
			seen.add(address)
			senders.push({ ...from, me: address === own })
		}
	}
	const unread = messages.filter((m) => !m.seen).length
	const newest = listed[0]
	return {
		...row,
		key: `${key}#conversation`,
		conversationKey: key,
		ids: messages.map((m) => m.id),
		message: {
			...newest,
			seen: unread === 0,
			flagged: messages.some((m) => m.flagged),
			answered: !!members?.replied || messages.some((m) => m.answered),
			hasAttachments: messages.some((m) => m.hasAttachments),
		},
		conversation: { count: messages.length, unread, senders, newest },
	}
}

/** What was last asked for each conversation: the listed ids, so a changed list asks again. */
const asked = new Map()

/**
 * Loads the members of the conversations among rows (groupRows' input) whose listed messages
 * changed since they were last asked for, or of all of them with refresh (the list was polled).
 */
export async function loadMembers(rows, refresh = false) {
	const wanted = new Map()
	for (const row of rows) {
		const id = row.message.conversationId
		if (!id || !conversationsOn(row.accountId)) {
			continue
		}
		const key = groupKey(row.accountId, row.folderId, id)
		const entry = wanted.get(key) ?? { ref: { accountId: row.accountId, folderId: row.folderId, conversationId: id }, ids: [] }
		entry.ids.push(row.message.id)
		wanted.set(key, entry)
	}
	const due = [...wanted].filter(([key, { ids }]) => refresh || asked.get(key) !== ids.join(','))
	for (const [key, { ids }] of due) {
		asked.set(key, ids.join(','))
	}
	for (let i = 0; i < due.length; i += BATCH) {
		const batch = due.slice(i, i + BATCH)
		try {
			const result = await apiSend('POST', '/conversations/members', { items: batch.map(([, { ref }]) => ref) })
			for (const members of result) {
				conversationRows.members[groupKey(members.accountId, members.folderId, members.conversationId)] = {
					messages: members.messages,
					replied: members.replied,
				}
			}
		} catch {
			// The rows sum up what is listed; the next change asks again.
			batch.forEach(([key]) => asked.delete(key))
		}
	}
}

function entriesOf(where) {
	const prefix = `${where.accountId}/${where.folderId}/`
	return Object.entries(conversationRows.members).filter(([key]) => key.startsWith(prefix))
}

/** Every member copy of these messages, so that setFlags (mailbox.js) changes them too. */
export function memberCopies(where, ids) {
	return entriesOf(where).flatMap(([, members]) => members.messages.filter((m) => ids.has(m.id)))
}

/** Forgets messages that were moved or deleted. */
export function dropMembers(where, ids) {
	for (const [, members] of entriesOf(where)) {
		if (members.messages.some((m) => ids.has(m.id))) {
			members.messages = members.messages.filter((m) => !ids.has(m.id))
		}
	}
}

/** The ids of the conversation the message belongs to in its folder, as the list sums it up; null when unknown. */
export function conversationIdsOf(where, messageId) {
	const found = entriesOf(where).find(([, members]) => members.messages.some((m) => m.id === messageId))
	return found && found[1].messages.length > 1 ? found[1].messages.map((m) => m.id) : null
}

/** After the account's setting changed: lists regroup from scratch. */
export function forgetMembers() {
	conversationRows.members = {}
	asked.clear()
}
