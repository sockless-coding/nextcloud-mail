<template>
	<NcContent app-name="sockless_mail">
		<NcAppNavigation :aria-label="t('sockless_mail', 'Mail accounts and folders')">
			<template #list>
				<NcAppNavigationNew :text="t('sockless_mail', 'New message')"
					:disabled="accountsState.accounts.length === 0"
					@click="composeNew">
					<template #icon>
						<NcIconSvgWrapper :path="mdiPencilOutline" />
					</template>
				</NcAppNavigationNew>
				<NcAppNavigationItem v-for="(account, index) in accountsState.accounts"
					:key="account.id"
					class="account-entry"
					:name="account.displayName"
					allow-collapse
					:open="!!expanded[account.id]"
					@update:open="(isOpen) => toggleAccount(account, isOpen)"
					@click="selectAccount(account)">
					<template #icon>
						<NcIconSvgWrapper :path="mdiEmailOutline" />
					</template>
					<template #actions>
						<NcActionButton close-after-click @click="openDialog(account)">
							<template #icon>
								<NcIconSvgWrapper :path="mdiPencil" />
							</template>
							{{ t('sockless_mail', 'Edit account') }}
						</NcActionButton>
						<NcActionButton close-after-click @click="newFolder(account)">
							<template #icon>
								<NcIconSvgWrapper :path="mdiFolderPlusOutline" />
							</template>
							{{ t('sockless_mail', 'New folder') }}
						</NcActionButton>
						<NcActionButton close-after-click @click="refreshAccount(account)">
							<template #icon>
								<NcIconSvgWrapper :path="mdiRefresh" />
							</template>
							{{ t('sockless_mail', 'Refresh folders') }}
						</NcActionButton>
						<NcActionButton :disabled="index === 0" close-after-click @click="move(account, -1)">
							<template #icon>
								<NcIconSvgWrapper :path="mdiArrowUp" />
							</template>
							{{ t('sockless_mail', 'Move up') }}
						</NcActionButton>
						<NcActionButton :disabled="index === accountsState.accounts.length - 1" close-after-click @click="move(account, 1)">
							<template #icon>
								<NcIconSvgWrapper :path="mdiArrowDown" />
							</template>
							{{ t('sockless_mail', 'Move down') }}
						</NcActionButton>
						<NcActionButton close-after-click @click="accountToDelete = account">
							<template #icon>
								<NcIconSvgWrapper :path="mdiDelete" />
							</template>
							{{ t('sockless_mail', 'Delete account') }}
						</NcActionButton>
					</template>

					<NcAppNavigationItem v-if="folderState(account.id).loading && !folderState(account.id).list.length"
						:name="t('sockless_mail', 'Loading folders…')"
						loading />
					<NcAppNavigationItem v-else-if="folderState(account.id).error"
						class="account-entry__error"
						:name="folderState(account.id).error.message"
						:title="folderState(account.id).error.message"
						@click="openDialog(account)">
						<template #icon>
							<NcIconSvgWrapper :path="mdiAlertCircleOutline" />
						</template>
					</NcAppNavigationItem>
					<FolderItem v-for="folder in folderTree(account.id)"
						:key="folder.id"
						:account-id="account.id"
						:folder="folder" />
				</NcAppNavigationItem>
			</template>
			<template #footer>
				<div class="navigation-footer">
					<NcButton variant="tertiary" wide @click="openDialog(null)">
						<template #icon>
							<NcIconSvgWrapper :path="mdiPlus" />
						</template>
						{{ t('sockless_mail', 'Add account') }}
					</NcButton>
				</div>
			</template>
		</NcAppNavigation>

		<NcAppContent v-if="accountsState.loading || accountsState.loadError || accountsState.accounts.length === 0">
			<div v-if="accountsState.loading" class="centered">
				<NcLoadingIcon :size="64" />
			</div>
			<NcEmptyContent v-else-if="accountsState.loadError"
				:name="t('sockless_mail', 'Could not load your accounts')"
				:description="accountsState.loadError" />
			<NcEmptyContent v-else
				:name="t('sockless_mail', 'No mail accounts yet')"
				:description="t('sockless_mail', 'Connect an IMAP/SMTP account to read and send mail.')">
				<template #icon>
					<NcIconSvgWrapper :path="mdiEmailOutline" />
				</template>
				<template #action>
					<NcButton variant="primary" @click="openDialog(null)">
						{{ t('sockless_mail', 'Add account') }}
					</NcButton>
				</template>
			</NcEmptyContent>
		</NcAppContent>

		<NcAppContent v-else
			:page-heading="t('sockless_mail', 'Mail')"
			:show-details="!!mailboxState.open.id"
			@update:show-details="(show) => !show && closeMessage()">
			<template #list>
				<MessageList v-if="mailboxState.selected.folderId" />
				<NcEmptyContent v-else :name="t('sockless_mail', 'Choose a folder')" />
			</template>
			<MessageView v-if="mailboxState.open.id" />
			<NcEmptyContent v-else :name="t('sockless_mail', 'No message selected')">
				<template #icon>
					<NcIconSvgWrapper :path="mdiEmailOpenOutline" />
				</template>
			</NcEmptyContent>
		</NcAppContent>

		<Composer v-if="composer.draft" />

		<AccountDialog v-if="dialog.open"
			:account="dialog.account"
			@saved="onSaved"
			@close="dialog.open = false" />

		<MoveDialog v-if="dialogs.move"
			:message-ids="dialogs.move"
			@close="dialogs.move = null" />

		<FolderNameDialog v-if="dialogs.folderName"
			:prompt="dialogs.folderName"
			@close="dialogs.folderName = null" />

		<NcDialog v-if="dialogs.confirmPermanent"
			:name="t('sockless_mail', 'Delete permanently?')"
			:message="t('sockless_mail', 'This account has no Trash folder. The messages will be deleted permanently and cannot be restored.')"
			:buttons="permanentButtons"
			:open="true"
			@update:open="(isOpen) => !isOpen && (dialogs.confirmPermanent = null)" />

		<NcDialog v-if="accountToDelete"
			:name="t('sockless_mail', 'Delete account')"
			:message="t('sockless_mail', 'Remove {name} from Sockless Mail? Mail on the server is not affected.', { name: accountToDelete.displayName })"
			:buttons="deleteButtons"
			:open="true"
			@update:open="(isOpen) => !isOpen && (accountToDelete = null)" />
	</NcContent>
</template>

<script setup>
import { mdiAlertCircleOutline, mdiArrowDown, mdiArrowUp, mdiDelete, mdiEmailOpenOutline, mdiEmailOutline, mdiFolderPlusOutline, mdiPencil, mdiPencilOutline, mdiPlus, mdiRefresh } from '@mdi/js'
import { showError } from '@nextcloud/dialogs/toast'
import { t } from '@nextcloud/l10n'
import NcActionButton from '@nextcloud/vue/components/NcActionButton'
import NcAppContent from '@nextcloud/vue/components/NcAppContent'
import NcAppNavigation from '@nextcloud/vue/components/NcAppNavigation'
import NcAppNavigationItem from '@nextcloud/vue/components/NcAppNavigationItem'
import NcAppNavigationNew from '@nextcloud/vue/components/NcAppNavigationNew'
import NcButton from '@nextcloud/vue/components/NcButton'
import NcContent from '@nextcloud/vue/components/NcContent'
import NcDialog from '@nextcloud/vue/components/NcDialog'
import NcEmptyContent from '@nextcloud/vue/components/NcEmptyContent'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import AccountDialog from './components/AccountDialog.vue'
import Composer from './components/Composer.vue'
import FolderItem from './components/FolderItem.vue'
import FolderNameDialog from './components/FolderNameDialog.vue'
import MessageList from './components/MessageList.vue'
import MessageView from './components/MessageView.vue'
import MoveDialog from './components/MoveDialog.vue'
import { accountsState, deleteAccount, loadAccounts, moveAccount } from './store/accounts.js'
import { askNewFolder, dialogs, removePermanently } from './store/actions.js'
import { composeNew, composer } from './store/compose.js'
import { closeMessage, forgetAccount, hasPendingWrites, loadFolders, mailboxState, refreshMessages, selectFolder } from './store/mailbox.js'

/**
 * No push channel to the browser (see README); new mail is found by polling. The open folder is
 * polled through the cheap changes endpoint, the folder list (LIST + STATUS on the server) only
 * every FOLDER_POLL_EVERY ticks and when the tab becomes visible.
 */
const POLL_INTERVAL_MS = 15_000
const FOLDER_POLL_EVERY = 4
let pollCount = 0

const dialog = reactive({ open: false, account: null })
const accountToDelete = ref(null)
const expanded = reactive({})
let pollTimer = null

const EMPTY_FOLDERS = { list: [], loading: false, error: null }

function folderState(accountId) {
	return mailboxState.folders[accountId] ?? EMPTY_FOLDERS
}

/** Nests the flat, server-ordered folder list. */
function folderTree(accountId) {
	const nodes = folderState(accountId).list.map((f) => ({ ...f, children: [] }))
	const byId = new Map(nodes.map((n) => [n.id, n]))
	const roots = []
	for (const node of nodes) {
		(byId.get(node.parentId)?.children ?? roots).push(node)
	}
	return roots
}

async function toggleAccount(account, isOpen) {
	expanded[account.id] = isOpen
	if (isOpen && !mailboxState.folders[account.id]) {
		await loadFolders(account.id)
	}
}

/** Clicking an account opens it and shows its inbox. */
async function selectAccount(account) {
	expanded[account.id] = true
	// Already showing one of this account's folders (checked again after loading, since the user
	// may have picked a folder meanwhile): keep it instead of jumping back to the inbox.
	if (mailboxState.selected.accountId === account.id) {
		return
	}
	const folders = mailboxState.folders[account.id]?.list.length ? mailboxState.folders[account.id].list : await loadFolders(account.id)
	if (mailboxState.selected.accountId === account.id) {
		return
	}
	const inbox = folders.find((f) => f.specialUse === 'inbox') ?? folders.find((f) => f.selectable)
	if (inbox) {
		selectFolder(account.id, inbox.id)
	}
}

async function refreshAccount(account) {
	await loadFolders(account.id)
}

const deleteButtons = computed(() => [
	{ label: t('sockless_mail', 'Cancel') },
	{
		label: t('sockless_mail', 'Delete'),
		variant: 'error',
		callback: async () => {
			const id = accountToDelete.value.id
			try {
				await deleteAccount(id)
				forgetAccount(id)
			} catch (e) {
				showError(t('sockless_mail', 'Could not delete the account: {error}', { error: e.message }))
			}
		},
	},
])

const permanentButtons = computed(() => [
	{ label: t('sockless_mail', 'Cancel') },
	{
		label: t('sockless_mail', 'Delete permanently'),
		variant: 'error',
		callback: () => {
			removePermanently(dialogs.confirmPermanent)
		},
	},
])

async function newFolder(account) {
	if (!mailboxState.folders[account.id]) {
		await loadFolders(account.id)
	}
	expanded[account.id] = true
	askNewFolder(account.id)
}

function openDialog(account) {
	dialog.account = account
	dialog.open = true
}

/** New or changed settings: drop cached folders and show the account's inbox. */
async function onSaved(account) {
	forgetAccount(account.id)
	await selectAccount(account)
}

async function move(account, delta) {
	try {
		await moveAccount(account.id, delta)
	} catch (e) {
		showError(t('sockless_mail', 'Could not reorder the accounts: {error}', { error: e.message }))
	}
}

async function poll(event) {
	if (document.hidden || hasPendingWrites()) {
		return
	}
	const accountId = mailboxState.selected.accountId
	const withFolders = event?.type === 'visibilitychange' || ++pollCount % FOLDER_POLL_EVERY === 0
	await Promise.all([
		refreshMessages(),
		accountId && withFolders ? loadFolders(accountId, true) : Promise.resolve(),
	])
}

onMounted(async () => {
	await loadAccounts()
	if (accountsState.accounts.length) {
		await selectAccount(accountsState.accounts[0])
	}
	pollTimer = window.setInterval(poll, POLL_INTERVAL_MS)
	document.addEventListener('visibilitychange', poll)
})

onBeforeUnmount(() => {
	window.clearInterval(pollTimer)
	document.removeEventListener('visibilitychange', poll)
})
</script>

<style scoped>
.centered {
	display: flex;
	align-items: center;
	justify-content: center;
	height: 100%;
}

.navigation-footer {
	padding: 8px;
}

.account-entry__error :deep(.app-navigation-entry__name) {
	color: var(--color-error-text);
}
</style>
