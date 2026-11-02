import { computed, reactive, watch } from 'vue'
import { apiGet, apiSend } from '../api.js'
import { unifiedAccounts } from './accounts.js'
import { layout } from './layout.js'
import { inboxUnread } from './navigation.js'
import { push } from './push.js'

/**
 * The count on the app's icon, once installed from /apps/socklessmail/ (Nextcloud's manifest
 * for that page makes it installable), and in the tab's title. It covers the inboxes of the
 * accounts in "All accounts" (with one account, just that inbox) and counts, by layout.appBadge:
 * - new: unread mail that arrived since the user last looked at the app, on any device. The
 *   server keeps where each inbox was then (NewMailEndpoints.cs); looking means an app window
 *   or tab that is visible and focused, and while it is, arrivals are seen at once.
 * - unread: every unread message, which with years of unread mail is no use.
 * - off.
 * Browsers without the Badging API only get the title.
 */

const newMail = reactive({ counts: {} })

/** Polled when there is no push connection (proxy mode, or while it reconnects). */
const POLL_MS = 60_000

/** Lets a burst of pushes (several accounts, a sync's nudges) end in one request. */
const DEBOUNCE_MS = 1_000

/** Whether the inboxes may hold mail the user has not looked at since the last mark. */
let unseen = true

const count = computed(() => {
	const accounts = unifiedAccounts.value
	switch (layout.appBadge) {
	case 'new':
		return accounts.reduce((sum, a) => sum + (newMail.counts[a.id] ?? 0), 0)
	case 'unread':
		return accounts.reduce((sum, a) => sum + inboxUnread(a.id), 0)
	default:
		return 0
	}
})

/** The title without a count; Nextcloud sets it per page and nothing here changes it otherwise. */
const baseTitle = document.title

function show(n) {
	document.title = n > 0 ? `(${n}) ${baseTitle}` : baseTitle
	if (!navigator.setAppBadge) {
		return
	}
	// Rejects outside an installed app in some browsers; there is no icon to badge then.
	const done = n > 0 ? navigator.setAppBadge(n) : navigator.clearAppBadge()
	done.catch(() => {})
}

function looking() {
	return document.visibilityState === 'visible' && document.hasFocus()
}

async function loadCounts() {
	try {
		newMail.counts = (await apiGet('/new-mail')).counts
	} catch {
		// The count stays as it was until the next change.
	}
}

async function markSeen() {
	if (!unseen) {
		return
	}
	unseen = false
	newMail.counts = {}
	try {
		await apiSend('PUT', '/new-mail/seen', { accountIds: unifiedAccounts.value.map((a) => a.id) })
	} catch {
		unseen = true
	}
}

let timer = null

/** Marks the inboxes seen while the user is looking, else fetches the counts; debounced. */
function update() {
	if (layout.appBadge !== 'new') {
		return
	}
	window.clearTimeout(timer)
	timer = window.setTimeout(() => (looking() ? markSeen() : loadCounts()), DEBOUNCE_MS)
}

/** An inbox changed (a push): mail may have arrived. */
export function inboxChanged() {
	unseen = true
	update()
}

/** The inboxes were seen in another tab or on another device. */
export function inboxesSeen() {
	update()
}

/**
 * Keeps the icon and title in step with the counts.
 *
 * @return {() => void} stops and clears the count
 */
export function startBadge() {
	const stopWatch = watch(count, show, { immediate: true })
	const stopMode = watch(() => layout.appBadge, () => inboxChanged())
	const onLook = () => {
		if (looking()) {
			update()
		}
	}
	window.addEventListener('focus', onLook)
	document.addEventListener('visibilitychange', onLook)
	const poll = window.setInterval(() => {
		if (!push.connected) {
			inboxChanged()
		}
	}, POLL_MS)
	// The mark only moves on a look; the first may be now.
	loadCounts().then(onLook)
	return () => {
		stopWatch()
		stopMode()
		window.removeEventListener('focus', onLook)
		document.removeEventListener('visibilitychange', onLook)
		window.clearInterval(poll)
		window.clearTimeout(timer)
		show(0)
	}
}
