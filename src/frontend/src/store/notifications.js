import { n, t } from '@nextcloud/l10n'
import { reactive } from 'vue'
import { accountsState } from './accounts.js'
import { layout } from './layout.js'
import { mailboxState, openMessage } from './mailbox.js'
import { openFolder } from './navigation.js'
import { inUnifiedView } from './unified.js'

/**
 * Desktop notifications for new mail (the operating system's own, e.g. Windows toasts). The
 * server announces unread INBOX arrivals of accounts that have notifications on (MailPush.cs);
 * whether this browser shows them is a setting of the device (store/layout.js), since the
 * permission belongs to the browser too. Only while a tab or the installed app is open.
 */

const supported = typeof window.Notification === 'function'

/** The browser's permission: default (not asked yet), granted, denied, or unsupported. */
export const notificationState = reactive({
	permission: supported ? Notification.permission : 'unsupported',
})

// The permission can be changed in the browser's site settings while the app is open.
navigator.permissions?.query({ name: 'notifications' }).then((status) => {
	status.onchange = () => {
		notificationState.permission = Notification.permission
	}
}).catch(() => {})

/**
 * Turns notifications on this device on or off; on asks for the permission, which browsers only
 * allow from a click.
 *
 * @param {boolean} enabled whether to show notifications
 * @return {Promise<boolean>} whether they are on now
 */
export async function setDesktopNotifications(enabled) {
	if (!enabled || !supported) {
		layout.notifications = false
		return false
	}
	if (Notification.permission === 'default') {
		await Notification.requestPermission()
	}
	notificationState.permission = Notification.permission
	layout.notifications = Notification.permission === 'granted'
	return layout.notifications
}

/**
 * Shows a notification at once, past every check of notifyNewMail: whether the browser and the
 * operating system let them through (Windows' Do not disturb, its per-app notification settings).
 *
 * @return {boolean} whether the browser accepted it
 */
export function testNotification() {
	try {
		new Notification(t('sockless_mail', 'Sockless Mail'), {
			body: t('sockless_mail', 'Notifications for new mail look like this.'),
			tag: 'sockless_mail:test',
		})
		return true
	} catch {
		return false
	}
}

/** Whether the user is looking at the list the new mail lands in. */
function watching(accountId, folderId) {
	if (document.visibilityState !== 'visible' || !document.hasFocus()) {
		return false
	}
	const { selected } = mailboxState
	return selected.unified ? inUnifiedView(accountId, folderId) : selected.accountId === accountId && selected.folderId === folderId
}

function senderOf(message) {
	return message.from?.name || message.from?.address || t('sockless_mail', 'Unknown sender')
}

function subjectOf(message) {
	return message.subject || t('sockless_mail', '(No subject)')
}

/** Shows the message: in the list on screen when it is there, else in the account's inbox. */
async function show(accountId, folderId, messageId) {
	window.focus()
	if (!mailboxState.selected.unified || !inUnifiedView(accountId, folderId)) {
		await openFolder(accountId, folderId)
	}
	if (messageId) {
		openMessage({ accountId, folderId }, messageId)
	}
}

/**
 * Announces new mail (the server's newMail push).
 *
 * @param {object} mail what arrived
 * @param {string} mail.accountId the account
 * @param {string} mail.folderId its INBOX
 * @param {number} mail.count how many messages arrived
 * @param {Array<{id: string, from: ?{name: ?string, address: string}, subject: string}>} mail.messages the newest of them
 */
export function notifyNewMail({ accountId, folderId, count, messages }) {
	if (!layout.notifications || !supported || Notification.permission !== 'granted' || !messages.length || watching(accountId, folderId)) {
		return
	}
	const account = accountsState.accounts.find((a) => a.id === accountId)
	if (!account) {
		return
	}
	// With several accounts, say which one; the user's own name for it is the shortest.
	const where = accountsState.accounts.length > 1 ? account.displayName : ''
	const single = count === 1
	const title = single
		? senderOf(messages[0])
		: n('sockless_mail', '%n new message', '%n new messages', count)
	const lines = single
		? [subjectOf(messages[0])]
		: messages.slice(0, 3).map((m) => `${senderOf(m)}: ${subjectOf(m)}`)
	const body = [...lines, ...(where ? [where] : [])].join('\n')
	try {
		// Every open tab hears the push; the same tag makes their notifications one.
		const notification = new Notification(title, {
			body,
			tag: `sockless_mail:${accountId}:${messages[0].id}`,
			lang: document.documentElement.lang || undefined,
		})
		notification.onclick = () => {
			notification.close()
			show(accountId, folderId, single ? messages[0].id : null)
		}
	} catch {
		// Some browsers (Chrome on Android) only notify through a service worker; nothing to show then.
	}
}
