import { showError } from '@nextcloud/dialogs/toast'
import { t } from '@nextcloud/l10n'
import { accountsState, deleteAccount, moveAccount, unifiedAvailable } from './accounts.js'
import { askNewFolder } from './actions.js'
import { closeDrawer } from './layout.js'
import { forgetAccount, loadFolders, mailboxState, selectFolder } from './mailbox.js'
import { loadScheduled } from './scheduled.js'
import { clearSearch } from './search.js'
import { endSelectionMode } from './selection.js'
import { showUnified, viewOf } from './unified.js'
import { forgetAccountView, lastFolderOf, viewState } from './viewState.js'

/**
 * Moving between accounts and folders, shared by the account rail, the folder pane and the
 * start-up in App.vue.
 */

/** Opens an account and shows the folder last open there, else its inbox. */
export async function selectAccount(account) {
	clearSearch()
	endSelectionMode()
	// Already showing one of this account's folders (checked again after loading, since the user
	// may have picked a folder meanwhile): keep it instead of jumping back to the inbox.
	if (mailboxState.selected.accountId === account.id && !mailboxState.selected.scheduled) {
		return
	}
	const folders = mailboxState.folders[account.id]?.list.length ? mailboxState.folders[account.id].list : await loadFolders(account.id)
	if (mailboxState.selected.accountId === account.id && !mailboxState.selected.scheduled) {
		return
	}
	const remembered = lastFolderOf(account.id)
	const folder = folders.find((f) => f.id === remembered && f.selectable)
		?? folders.find((f) => f.specialUse === 'inbox')
		?? folders.find((f) => f.selectable)
	if (folder) {
		selectFolder(account.id, folder.id)
	} else {
		// No folders (e.g. the login failed): still show the account, with its error.
		mailboxState.selected = { accountId: account.id, folderId: null }
	}
}

/**
 * The start: the view last open (see viewState.js) when it still exists, else all inboxes with
 * several accounts, or the only account.
 */
export async function openStartView() {
	const { accounts } = accountsState
	const last = viewState.lastView
	const account = last?.accountId && accounts.find((a) => a.id === last.accountId)
	if (account) {
		await selectAccount(account)
	} else if (unifiedAvailable.value) {
		openUnified(last?.unified && viewOf(last.unified) ? last.unified : 'inbox')
	} else if (accounts.length) {
		await selectAccount(accounts[0])
	}
}

export function openFolder(accountId, folderId) {
	clearSearch()
	endSelectionMode()
	closeDrawer()
	selectFolder(accountId, folderId)
}

/**
 * @param {string} view an id of UNIFIED_VIEWS (unified.js); by default the unified view already
 *   shown (as selectAccount keeps the account's folder), else the inbox
 */
export function openUnified(view = mailboxState.selected.unified || 'inbox') {
	clearSearch()
	endSelectionMode()
	closeDrawer()
	showUnified(view)
}

/** Folder lists of every account, for the unread badges in the rail. */
export function loadAllFolders() {
	return Promise.all(accountsState.accounts.map((a) => loadFolders(a.id, !!mailboxState.folders[a.id])))
}

/** The account whose folders the folder pane shows, or null in a unified view. */
export function currentAccount() {
	return accountsState.accounts.find((a) => a.id === mailboxState.selected.accountId) ?? null
}

/** The inbox's unread count of an account, once its folders are loaded. */
export function inboxUnread(accountId) {
	return mailboxState.folders[accountId]?.list.find((f) => f.specialUse === 'inbox')?.unread ?? 0
}

/** New or changed settings: drop cached folders and show the account's inbox. */
export async function accountSaved(account) {
	forgetAccount(account.id)
	await selectAccount(account)
}

export async function moveAccountBy(account, delta) {
	try {
		await moveAccount(account.id, delta)
	} catch (e) {
		showError(t('sockless_mail', 'Could not reorder the accounts: {error}', { error: e.message }))
	}
}

export async function removeAccount(account) {
	try {
		await deleteAccount(account.id)
		forgetAccount(account.id)
		forgetAccountView(account.id)
		// The server dropped what the account had scheduled.
		loadScheduled()
	} catch (e) {
		showError(t('sockless_mail', 'Could not delete the account: {error}', { error: e.message }))
		return
	}
	// Show something else than the empty space the account left.
	if (!mailboxState.selected.unified && !mailboxState.selected.scheduled && !mailboxState.selected.accountId) {
		if (unifiedAvailable.value) {
			openUnified()
		} else if (accountsState.accounts.length) {
			await selectAccount(accountsState.accounts[0])
		}
	}
}

/**
 * After an account was taken into or left out of "All accounts": a unified view on screen is
 * reloaded, or, with fewer than two accounts left in it, gives way to the first account.
 */
export async function unifiedAccountsChanged() {
	const { selected } = mailboxState
	if (!selected.unified || selected.accountId) {
		return
	}
	if (unifiedAvailable.value) {
		await showUnified(selected.unified)
	} else if (accountsState.accounts.length) {
		await selectAccount(accountsState.accounts[0])
	}
}

export async function newFolder(account) {
	if (!mailboxState.folders[account.id]) {
		await loadFolders(account.id)
	}
	askNewFolder(account.id)
}
