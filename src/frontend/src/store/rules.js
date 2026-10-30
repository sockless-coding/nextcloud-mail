import { t } from '@nextcloud/l10n'
import { apiGet, apiSend } from '../api.js'

/**
 * Mail rules, which the server runs on new INBOX mail (also while nobody has the app open).
 * The list is saved as a whole, so order, switches and edits are one request each.
 */

/**
 * @param {string} accountId the account
 * @return {Promise<object[]>} its rules in order
 */
export async function loadRules(accountId) {
	return (await apiGet(`/accounts/${accountId}/rules`)).rules
}

/**
 * @param {string} accountId the account
 * @param {object[]} rules the whole list, in order
 * @return {Promise<object[]>} the saved list
 */
export async function saveRules(accountId, rules) {
	return (await apiSend('PUT', `/accounts/${accountId}/rules`, { rules: rules.map(toInput) })).rules
}

/**
 * Tries a rule on the newest INBOX messages without acting on them.
 *
 * @param {string} accountId the account
 * @param {object} rule the rule as edited
 * @return {Promise<{scanned: number, matched: number, matches: object[]}>}
 */
export async function testRule(accountId, rule) {
	return apiSend('POST', `/accounts/${accountId}/rules/test`, { rule: toInput(rule) })
}

/**
 * What a saved rule would match among a folder's newest messages.
 *
 * @param {string} accountId the account
 * @param {string} ruleId the rule
 * @param {string} folderId the folder
 * @return {Promise<{scanned: number, matched: number, matches: object[], messageIds: string[]}>}
 */
export async function previewRun(accountId, ruleId, folderId) {
	return apiSend('POST', `/accounts/${accountId}/rules/${ruleId}/preview`, { folderId })
}

/**
 * Runs a saved rule on messages already in a folder (those of a preview).
 *
 * @return {Promise<{applied: number}>}
 */
export async function runRule(accountId, ruleId, folderId, messageIds) {
	return apiSend('POST', `/accounts/${accountId}/rules/${ruleId}/run`, { folderId, messageIds })
}

/**
 * A new rule for mail like this message: by its mailing list (List-Id) when it came from one,
 * otherwise by its sender. The folder to move to is left for the user to choose.
 *
 * @param {object} message the message summary
 * @param {string} rawHeaders its header block, or '' when it could not be read
 */
export function ruleFromMessage(message, rawHeaders) {
	const rule = newRule()
	const unfolded = rawHeaders.replace(/\r?\n[ \t]+/g, ' ')
	const listId = unfolded.match(/^List-Id:[ \t]*(.*)$/im)?.[1]?.trim()
	const id = listId?.match(/<([^>]+)>/)?.[1] ?? listId
	if (id) {
		const label = listId.replace(/<[^>]*>/, '').trim().replace(/^"(.*)"$/, '$1').trim()
		rule.name = label || id
		rule.conditions = [{ field: 'header', header: 'List-Id', op: 'contains', value: id }]
		return rule
	}
	const sender = message.from?.[0]
	if (sender) {
		rule.name = sender.name || sender.address
		rule.conditions = [{ field: 'from', header: '', op: 'is', value: sender.address }]
	}
	return rule
}

function toInput(rule) {
	return {
		id: rule.id ?? null,
		name: rule.name,
		enabled: rule.enabled,
		matchAll: rule.matchAll,
		stop: rule.stop,
		conditions: rule.conditions.map(({ field, header, op, value }) => ({
			field,
			header: field === 'header' ? header : null,
			op,
			value: field === 'attachment' ? null : value,
		})),
		actions: rule.actions.map(({ type, folderId }) => ({
			type,
			folderId: type === 'move' || type === 'copy' ? folderId : null,
		})),
	}
}

export const FIELDS = [
	{ id: 'from', label: t('sockless_mail', 'From') },
	{ id: 'to', label: t('sockless_mail', 'To') },
	{ id: 'cc', label: t('sockless_mail', 'Cc') },
	{ id: 'recipient', label: t('sockless_mail', 'To or Cc') },
	{ id: 'subject', label: t('sockless_mail', 'Subject') },
	{ id: 'header', label: t('sockless_mail', 'Header') },
	{ id: 'size', label: t('sockless_mail', 'Size') },
	{ id: 'attachment', label: t('sockless_mail', 'Attachments') },
]

export const TEXT_OPS = [
	{ id: 'contains', label: t('sockless_mail', 'contains') },
	{ id: 'notContains', label: t('sockless_mail', 'does not contain') },
	{ id: 'is', label: t('sockless_mail', 'is') },
	{ id: 'isNot', label: t('sockless_mail', 'is not') },
	{ id: 'startsWith', label: t('sockless_mail', 'starts with') },
	{ id: 'endsWith', label: t('sockless_mail', 'ends with') },
	{ id: 'matches', label: t('sockless_mail', 'matches regex') },
]

export const SIZE_OPS = [
	{ id: 'greaterThan', label: t('sockless_mail', 'is larger than') },
	{ id: 'lessThan', label: t('sockless_mail', 'is smaller than') },
]

export const ATTACHMENT_OPS = [
	{ id: 'has', label: t('sockless_mail', 'has attachments') },
	{ id: 'hasNot', label: t('sockless_mail', 'has no attachments') },
]

export const ACTIONS = [
	{ id: 'move', label: t('sockless_mail', 'Move to folder') },
	{ id: 'copy', label: t('sockless_mail', 'Copy to folder') },
	{ id: 'markRead', label: t('sockless_mail', 'Mark as read') },
	{ id: 'flag', label: t('sockless_mail', 'Flag') },
	{ id: 'trash', label: t('sockless_mail', 'Move to Trash') },
]

/** The operators that fit a field. */
export function opsFor(field) {
	return field === 'size' ? SIZE_OPS : field === 'attachment' ? ATTACHMENT_OPS : TEXT_OPS
}

export function newCondition() {
	return { field: 'from', header: '', op: 'contains', value: '' }
}

export function newRule() {
	return {
		id: null,
		name: '',
		enabled: true,
		matchAll: true,
		stop: false,
		conditions: [newCondition()],
		actions: [{ type: 'move', folderId: '' }],
	}
}

/**
 * One line describing a rule, for the list.
 *
 * @param {object} rule the rule
 * @param {(folderId: string) => string} folderName names a folder id
 */
export function describeRule(rule, folderName) {
	const condition = (c) => {
		const field = c.field === 'header' ? c.header : FIELDS.find((f) => f.id === c.field)?.label
		const op = opsFor(c.field).find((o) => o.id === c.op)?.label ?? c.op
		if (c.field === 'attachment') {
			return op
		}
		const value = c.field === 'size' ? formatSize(Number(c.value)) : `"${c.value}"`
		return `${field} ${op} ${value}`
	}
	const action = (a) => {
		const label = ACTIONS.find((x) => x.id === a.type)?.label ?? a.type
		return a.folderId ? `${label} ${folderName(a.folderId)}` : label
	}
	const joiner = rule.matchAll ? t('sockless_mail', ' and ') : t('sockless_mail', ' or ')
	return t('sockless_mail', 'If {conditions}: {actions}', {
		conditions: rule.conditions.map(condition).join(joiner),
		actions: rule.actions.map(action).join(', '),
	}, undefined, { escape: false })
}

/** Sizes are kept in bytes; the editor shows KB. */
export function formatSize(bytes) {
	return bytes >= 1024 * 1024 ? `${Math.round(bytes / 1024 / 102.4) / 10} MB` : `${Math.round(bytes / 1024)} KB`
}
