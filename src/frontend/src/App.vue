<template>
	<NcContent app-name="sockless_mail">
		<div class="sm-shell">
			<AccountRail />

			<div v-if="accountsState.loading" class="sm-shell__state">
				<NcLoadingIcon :size="64" />
			</div>
			<NcEmptyContent v-else-if="accountsState.loadError"
				class="sm-shell__state"
				:name="t('sockless_mail', 'Could not load your accounts')"
				:description="accountsState.loadError" />
			<NcEmptyContent v-else-if="accountsState.accounts.length === 0"
				class="sm-shell__state"
				:name="t('sockless_mail', 'No mail accounts yet')"
				:description="t('sockless_mail', 'Connect an IMAP/SMTP account to read and send mail.')">
				<template #icon>
					<NcIconSvgWrapper :path="mdiEmailOutline" />
				</template>
				<template #action>
					<NcButton variant="primary" @click="dialogs.accountForm = { account: null }">
						{{ t('sockless_mail', 'Add account') }}
					</NcButton>
				</template>
			</NcEmptyContent>

			<template v-else>
				<div v-if="viewport.narrowFolders && viewport.drawerOpen" class="sm-shell__scrim" @click="closeDrawer" />
				<FolderPane v-show="folderPaneShown" :class="{ 'sm-shell__drawer': viewport.narrowFolders }" />
				<main class="sm-shell__main">
					<CommandBar />
					<div ref="panes"
						class="sm-panes"
						:class="`sm-panes--${readingPane}`"
						:style="paneSizes">
						<section v-show="listShown" class="sm-panes__list" :aria-label="t('sockless_mail', 'Message list')">
							<MessageList v-if="mailboxState.selected.folderId || mailboxState.selected.unified" />
							<NcEmptyContent v-else :name="t('sockless_mail', 'Choose a folder')" />
						</section>
						<div v-if="readingPane !== 'off'"
							class="sm-panes__splitter"
							role="separator"
							tabindex="0"
							:aria-orientation="readingPane === 'right' ? 'vertical' : 'horizontal'"
							:aria-label="t('sockless_mail', 'Resize the message list')"
							@pointerdown="startResize"
							@keydown="resizeByKey" />
						<section v-show="readerShown" class="sm-panes__reader" :aria-label="t('sockless_mail', 'Reading pane')">
							<div v-if="selection.rows.length" class="sm-selection">
								<NcIconSvgWrapper :path="mdiCheckboxMultipleMarkedCircleOutline" :size="48" />
								<h2>{{ n('sockless_mail', '{count} message selected', '{count} messages selected', selection.rows.length, { count: selection.rows.length }) }}</h2>
								<p>{{ t('sockless_mail', 'Use the commands at the top for all of them.') }}</p>
								<NcButton @click="clearSelection">
									{{ t('sockless_mail', 'Clear selection') }}
								</NcButton>
							</div>
							<MessageView v-else-if="mailboxState.open.id" />
							<NcEmptyContent v-else :name="t('sockless_mail', 'No message selected')">
								<template #icon>
									<NcIconSvgWrapper :path="mdiEmailOpenOutline" />
								</template>
							</NcEmptyContent>
						</section>
					</div>
				</main>
			</template>
		</div>

		<Composer v-if="composer.draft" />

		<AccountDialog v-if="dialogs.accountForm"
			:account="dialogs.accountForm.account"
			@saved="accountSaved"
			@close="dialogs.accountForm = null" />

		<OAuthSettingsDialog v-if="dialogs.oauthSettings" @close="dialogs.oauthSettings = false" />

		<ShortcutsDialog v-if="dialogs.shortcuts" @close="dialogs.shortcuts = false" />

		<MoveDialog v-if="dialogs.move"
			:request="dialogs.move"
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

		<NcDialog v-if="dialogs.accountToDelete"
			:name="t('sockless_mail', 'Delete account')"
			:message="t('sockless_mail', 'Remove {name} from Sockless Mail? Mail on the server is not affected.', { name: dialogs.accountToDelete.displayName })"
			:buttons="deleteButtons"
			:open="true"
			@update:open="(isOpen) => !isOpen && (dialogs.accountToDelete = null)" />
	</NcContent>
</template>

<script setup>
import { mdiCheckboxMultipleMarkedCircleOutline, mdiEmailOpenOutline, mdiEmailOutline } from '@mdi/js'
import { n, t } from '@nextcloud/l10n'
import NcButton from '@nextcloud/vue/components/NcButton'
import NcContent from '@nextcloud/vue/components/NcContent'
import NcDialog from '@nextcloud/vue/components/NcDialog'
import NcEmptyContent from '@nextcloud/vue/components/NcEmptyContent'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import AccountDialog from './components/AccountDialog.vue'
import AccountRail from './components/AccountRail.vue'
import CommandBar from './components/CommandBar.vue'
import Composer from './components/Composer.vue'
import FolderNameDialog from './components/FolderNameDialog.vue'
import FolderPane from './components/FolderPane.vue'
import MessageList from './components/MessageList.vue'
import MessageView from './components/MessageView.vue'
import MoveDialog from './components/MoveDialog.vue'
import OAuthSettingsDialog from './components/OAuthSettingsDialog.vue'
import ShortcutsDialog from './components/ShortcutsDialog.vue'
import { accountsState, loadAccounts } from './store/accounts.js'
import { dialogs, removePermanently } from './store/actions.js'
import { composer } from './store/compose.js'
import { closeDrawer, folderPaneShown, layout, readingPane, viewport } from './store/layout.js'
import { hasPendingWrites, loadFolders, mailboxState, refreshMessages } from './store/mailbox.js'
import { accountSaved, loadAllFolders, openUnified, removeAccount, selectAccount } from './store/navigation.js'
import { loadOAuthState } from './store/oauth.js'
import { clearSelection, selection } from './store/selection.js'
import { push, startPush } from './store/push.js'
import { installShortcuts } from './store/shortcuts.js'
import { INBOX_ID, refreshUnified } from './store/unified.js'

/**
 * Without push (proxy mode, or the connection is down) new mail is found by polling: the open
 * folder through the cheap changes endpoint every tick, the folder lists (LIST + STATUS on the
 * server, for the counts in the rail and the folder pane) every FOLDER_POLL_EVERY ticks.
 * With push, the server's IDLE watchers announce INBOX changes, so inboxes are only polled every
 * SAFETY_POLL_EVERY ticks in case a watcher is down; other folders have no watcher and keep
 * FOLDER_POLL_EVERY. Everything is refreshed when the tab becomes visible.
 */
const POLL_INTERVAL_MS = 15_000
const FOLDER_POLL_EVERY = 4
const SAFETY_POLL_EVERY = 8
/** Pushes come in bursts (one per synced change); the folder list is reloaded once per burst. */
const FOLDER_RELOAD_DELAY_MS = 2_000
let pollCount = 0
let pollTimer = null
let removeShortcuts = null
let stopPush = null
const folderReloads = new Map()

/** With the reading pane off, the list and the open message take turns. */
const listShown = computed(() => readingPane.value !== 'off' || !mailboxState.open.id || selection.rows.length > 0)
const readerShown = computed(() => readingPane.value !== 'off' || !listShown.value)

const panes = ref(null)

const paneSizes = computed(() => {
	if (readingPane.value === 'right') {
		return { gridTemplateColumns: `${layout.listWidth}px 5px minmax(0, 1fr)` }
	}
	if (readingPane.value === 'bottom') {
		return { gridTemplateRows: `${layout.listHeight}% 5px minmax(0, 1fr)` }
	}
	return {}
})

const MIN_LIST_WIDTH = 300
const MIN_READER_WIDTH = 360

function clampWidth(width) {
	const total = panes.value?.clientWidth ?? 1200
	return Math.round(Math.max(MIN_LIST_WIDTH, Math.min(width, total - MIN_READER_WIDTH)))
}

function clampHeight(percent) {
	return Math.round(Math.max(20, Math.min(percent, 80)))
}

/** Dragging the splitter between the list and the reading pane. */
function startResize(event) {
	const rect = panes.value.getBoundingClientRect()
	const handle = event.currentTarget
	handle.setPointerCapture(event.pointerId)
	const move = (e) => {
		if (readingPane.value === 'right') {
			layout.listWidth = clampWidth(e.clientX - rect.left)
		} else {
			layout.listHeight = clampHeight(((e.clientY - rect.top) / rect.height) * 100)
		}
	}
	const stop = () => {
		handle.removeEventListener('pointermove', move)
		handle.removeEventListener('pointerup', stop)
	}
	handle.addEventListener('pointermove', move)
	handle.addEventListener('pointerup', stop)
	event.preventDefault()
}

function resizeByKey(event) {
	const step = { ArrowLeft: -1, ArrowUp: -1, ArrowRight: 1, ArrowDown: 1 }[event.key]
	if (!step) {
		return
	}
	event.preventDefault()
	if (readingPane.value === 'right') {
		layout.listWidth = clampWidth(layout.listWidth + step * 24)
	} else {
		layout.listHeight = clampHeight(layout.listHeight + step * 3)
	}
}

const deleteButtons = computed(() => [
	{ label: t('sockless_mail', 'Cancel') },
	{
		label: t('sockless_mail', 'Delete'),
		variant: 'error',
		callback: () => removeAccount(dialogs.accountToDelete),
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

function refreshList() {
	return mailboxState.selected.unified ? refreshUnified() : refreshMessages()
}

async function poll(event) {
	if (document.hidden || hasPendingWrites()) {
		return
	}
	const tick = ++pollCount
	const everything = event?.type === 'visibilitychange'
	const inbox = mailboxState.selected.unified || mailboxState.selected.folderId === INBOX_ID
	const withList = everything || !push.connected || tick % (inbox ? SAFETY_POLL_EVERY : FOLDER_POLL_EVERY) === 0
	const withFolders = everything || tick % (push.connected ? SAFETY_POLL_EVERY : FOLDER_POLL_EVERY) === 0
	await Promise.all([
		...(withList ? [refreshList()] : []),
		...(withFolders ? Object.keys(mailboxState.folders).map((id) => loadFolders(id, true)) : []),
	])
}

/** A push: refresh the list if it shows that folder, and the account's counts. */
function folderChanged({ accountId, folderId }) {
	// Hidden tabs catch up when they become visible.
	if (document.hidden) {
		return
	}
	const { selected } = mailboxState
	if (selected.unified ? folderId === INBOX_ID : selected.accountId === accountId && selected.folderId === folderId) {
		refreshList()
	}
	if (mailboxState.folders[accountId] && !folderReloads.has(accountId)) {
		folderReloads.set(accountId, window.setTimeout(() => {
			folderReloads.delete(accountId)
			loadFolders(accountId, true)
		}, FOLDER_RELOAD_DELAY_MS))
	}
}

onMounted(async () => {
	// Not awaited: the mail view does not depend on it.
	loadOAuthState()
	await loadAccounts()
	// The rail shows every account's unread count; not awaited either.
	loadAllFolders()
	// With several accounts, start with all their inboxes together.
	if (accountsState.accounts.length > 1) {
		openUnified()
	} else if (accountsState.accounts.length) {
		await selectAccount(accountsState.accounts[0])
	}
	pollTimer = window.setInterval(poll, POLL_INTERVAL_MS)
	document.addEventListener('visibilitychange', poll)
	stopPush = startPush({ folderChanged, reconnected: () => poll({ type: 'visibilitychange' }) })
	removeShortcuts = installShortcuts()
})

onBeforeUnmount(() => {
	window.clearInterval(pollTimer)
	stopPush?.()
	folderReloads.forEach((timer) => window.clearTimeout(timer))
	document.removeEventListener('visibilitychange', poll)
	removeShortcuts?.()
})
</script>

<style scoped>
.sm-shell {
	/* Side panes: a shade off the main background, in either theme. */
	--sm-pane-background: color-mix(in srgb, var(--color-main-text) 3%, var(--color-main-background));
	position: relative;
	display: flex;
	flex: 1;
	width: 100%;
	height: 100%;
	min-width: 0;
	min-height: 0;
	overflow: hidden;
	background: var(--color-main-background);
	color: var(--color-main-text);
}

.sm-shell__state {
	flex: 1;
	display: flex;
	align-items: center;
	justify-content: center;
}

.sm-shell__drawer {
	position: absolute;
	top: 0;
	bottom: 0;
	inset-inline-start: 60px;
	z-index: 20;
	box-shadow: 0 0 24px rgb(0 0 0 / 25%);
}

.sm-shell__scrim {
	position: absolute;
	inset: 0;
	z-index: 19;
	background: rgb(0 0 0 / 20%);
}

.sm-shell__main {
	display: flex;
	flex-direction: column;
	flex: 1;
	min-width: 0;
	min-height: 0;
}

.sm-panes {
	display: grid;
	flex: 1;
	min-height: 0;
	grid-template-columns: minmax(0, 1fr);
	grid-template-rows: minmax(0, 1fr);
}

.sm-panes__list,
.sm-panes__reader {
	min-width: 0;
	min-height: 0;
}

.sm-panes__list {
	display: flex;
	flex-direction: column;
	overflow: hidden;
}

.sm-panes__reader {
	overflow-y: auto;
}

.sm-panes__splitter {
	position: relative;
	background: var(--color-border);
	cursor: col-resize;
	touch-action: none;
	outline: none;
}

.sm-panes--right .sm-panes__splitter {
	width: 1px;
	margin: 0 2px;
}

.sm-panes--bottom .sm-panes__splitter {
	height: 1px;
	margin: 2px 0;
	cursor: row-resize;
}

/* A wider grab area than the visible line. */
.sm-panes__splitter::after {
	content: '';
	position: absolute;
	inset: -4px;
}

.sm-panes__splitter:hover,
.sm-panes__splitter:focus-visible {
	background: var(--color-primary-element);
}

.sm-selection {
	display: flex;
	flex-direction: column;
	align-items: center;
	justify-content: center;
	gap: 8px;
	height: 100%;
	padding: 32px;
	text-align: center;
	color: var(--color-text-maxcontrast);
}

.sm-selection h2 {
	margin: 0;
	font-size: 18px;
	color: var(--color-main-text);
}

.sm-selection p {
	margin: 0 0 8px;
}

@media (max-width: 800px) {
	.sm-shell__drawer {
		inset-inline-start: 52px;
	}
}
</style>

<style>
/*
 * Nextcloud's server styles give every plain button a background and text colour on hover,
 * focus, press and when disabled, with selectors no component rule outweighs. The app's own
 * buttons draw their states themselves, through --sm-button-background and --sm-button-color.
 */
#content-vue .sm-shell button:not(.button-vue):is(:hover, :focus, :active, :disabled) {
	border-color: transparent;
	background-color: var(--sm-button-background, transparent);
	color: var(--sm-button-color, inherit);
}

/*
 * The server styles also take NcIconSvgWrapper's "icon-vue" span in a plain button for a legacy
 * icon-* class: inline-block on the text bottom at half opacity, so the icon sits high and faded.
 * Give it back its centred box, sized to the icon; the button's own padding and gap place it.
 */
#content-vue .sm-shell button:not(.button-vue) > .icon-vue {
	display: flex;
	min-width: 0;
	min-height: 0;
	opacity: 1;
}
</style>
