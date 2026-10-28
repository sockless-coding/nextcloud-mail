<template>
	<aside class="folder-pane" :aria-label="t('sockless_mail', 'Folders')">
		<!-- All inboxes: one shortcut per account to its main folders. -->
		<template v-if="mailboxState.selected.unified">
			<ul class="folder-pane__group">
				<li>
					<div class="folder-row folder-row--active">
						<button class="folder-pane__unified" aria-current="page" @click="openUnified">
							<NcIconSvgWrapper :path="mdiInboxMultipleOutline" :size="20" />
							<span class="folder-pane__unified-name">{{ t('sockless_mail', 'All inboxes') }}</span>
							<span v-if="unifiedState.unread" class="folder-pane__count">{{ unifiedState.unread }}</span>
						</button>
					</div>
				</li>
			</ul>
			<section v-for="account in accountsState.accounts" :key="account.id" class="folder-pane__section">
				<h3 class="folder-pane__heading">
					<button class="folder-pane__heading-button" @click="selectAccount(account)">
						<span class="folder-pane__dot" :style="{ background: accountColor(account.id) }" />
						<span class="folder-pane__heading-name">{{ account.displayName }}</span>
					</button>
				</h3>
				<ul class="folder-pane__group">
					<FolderItem v-for="folder in shortcuts(account.id)"
						:key="folder.id"
						:account-id="account.id"
						:folder="folder"
						compact />
				</ul>
			</section>
		</template>

		<template v-else-if="account">
			<header class="folder-pane__account">
				<span class="folder-pane__dot folder-pane__dot--large" :style="{ background: accountColor(account.id) }" />
				<div class="folder-pane__account-text">
					<h2 class="folder-pane__account-name">{{ account.displayName }}</h2>
					<span class="folder-pane__account-address">{{ account.emailAddress }}</span>
				</div>
				<NcActions :aria-label="t('sockless_mail', 'Account actions')">
					<NcActionButton close-after-click @click="dialogs.accountForm = { account }">
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
					<NcActionButton close-after-click @click="loadFolders(account.id)">
						<template #icon>
							<NcIconSvgWrapper :path="mdiRefresh" />
						</template>
						{{ t('sockless_mail', 'Refresh folders') }}
					</NcActionButton>
					<NcActionButton :disabled="index === 0" close-after-click @click="moveAccountBy(account, -1)">
						<template #icon>
							<NcIconSvgWrapper :path="mdiArrowUp" />
						</template>
						{{ t('sockless_mail', 'Move up') }}
					</NcActionButton>
					<NcActionButton :disabled="index === accountsState.accounts.length - 1" close-after-click @click="moveAccountBy(account, 1)">
						<template #icon>
							<NcIconSvgWrapper :path="mdiArrowDown" />
						</template>
						{{ t('sockless_mail', 'Move down') }}
					</NcActionButton>
					<NcActionButton close-after-click @click="dialogs.accountToDelete = account">
						<template #icon>
							<NcIconSvgWrapper :path="mdiDelete" />
						</template>
						{{ t('sockless_mail', 'Delete account') }}
					</NcActionButton>
				</NcActions>
			</header>
			<div v-if="folders.loading && !folders.list.length" class="folder-pane__center">
				<NcLoadingIcon :size="28" :name="t('sockless_mail', 'Loading folders…')" />
			</div>
			<button v-else-if="folders.error"
				class="folder-pane__error"
				:title="t('sockless_mail', 'Edit account')"
				@click="dialogs.accountForm = { account }">
				<NcIconSvgWrapper :path="mdiAlertCircleOutline" :size="20" />
				<span>{{ folders.error.message }}</span>
			</button>
			<ul class="folder-pane__group">
				<FolderItem v-for="folder in tree"
					:key="folder.id"
					:account-id="account.id"
					:folder="folder" />
			</ul>
		</template>
	</aside>
</template>

<script setup>
import { mdiAlertCircleOutline, mdiArrowDown, mdiArrowUp, mdiDelete, mdiFolderPlusOutline, mdiInboxMultipleOutline, mdiPencil, mdiRefresh } from '@mdi/js'
import { t } from '@nextcloud/l10n'
import NcActionButton from '@nextcloud/vue/components/NcActionButton'
import NcActions from '@nextcloud/vue/components/NcActions'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import { computed } from 'vue'
import { accountColor, accountsState } from '../store/accounts.js'
import { dialogs } from '../store/actions.js'
import { loadFolders, mailboxState } from '../store/mailbox.js'
import { currentAccount, moveAccountBy, newFolder, openUnified, selectAccount } from '../store/navigation.js'
import { unifiedState } from '../store/unified.js'
import FolderItem from './FolderItem.vue'

const EMPTY_FOLDERS = { list: [], loading: false, error: null }
/** The folders the unified overview links to, in this order. */
const SHORTCUTS = ['inbox', 'drafts', 'sent']

const account = computed(() => currentAccount())
const index = computed(() => accountsState.accounts.indexOf(account.value))
const folders = computed(() => mailboxState.folders[account.value?.id] ?? EMPTY_FOLDERS)

/** Nests the flat, server-ordered folder list. */
const tree = computed(() => {
	const nodes = folders.value.list.map((f) => ({ ...f, children: [] }))
	const byId = new Map(nodes.map((n) => [n.id, n]))
	const roots = []
	for (const node of nodes) {
		(byId.get(node.parentId)?.children ?? roots).push(node)
	}
	return roots
})

function shortcuts(accountId) {
	const list = mailboxState.folders[accountId]?.list ?? []
	return SHORTCUTS.map((use) => list.find((f) => f.specialUse === use))
		.filter(Boolean)
		.map((f) => ({ ...f, children: [] }))
}
</script>

<style scoped>
.folder-pane {
	display: flex;
	flex-direction: column;
	gap: 4px;
	width: 248px;
	flex: none;
	padding: 8px;
	overflow-y: auto;
	background: var(--sm-pane-background);
	border-inline-end: 1px solid var(--color-border);
}

.folder-pane ul {
	margin: 0;
	padding: 0;
	list-style: none;
}

.folder-pane__account {
	display: flex;
	align-items: center;
	gap: 10px;
	padding: 4px 0 8px 6px;
}

.folder-pane__account-text {
	flex: 1;
	min-width: 0;
}

.folder-pane__account-name {
	margin: 0;
	font-size: 15px;
	font-weight: 600;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.folder-pane__account-address {
	display: block;
	font-size: 12px;
	color: var(--color-text-maxcontrast);
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.folder-pane__dot {
	width: 10px;
	height: 10px;
	flex: none;
	border-radius: 50%;
}

.folder-pane__dot--large {
	width: 12px;
	height: 12px;
}

.folder-pane__section {
	margin-top: 8px;
}

.folder-pane__heading {
	margin: 0;
}

.folder-pane__heading-button {
	display: flex;
	align-items: center;
	gap: 8px;
	width: 100%;
	min-height: 28px;
	margin: 0;
	padding: 0 8px;
	border: 0;
	border-radius: var(--border-radius-element, 8px);
	background: none;
	font-size: 12px;
	font-weight: 600;
	letter-spacing: 0.04em;
	text-transform: uppercase;
	text-align: start;
	color: var(--color-text-maxcontrast);
	cursor: pointer;
}

.folder-pane__heading-button:hover {
	--sm-button-background: var(--color-background-hover);
	--sm-button-color: var(--color-main-text);
	color: var(--color-main-text);
	background: var(--color-background-hover);
}

.folder-pane__heading-name {
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

/* The unified entry looks like a folder row (FolderItem.vue). */
.folder-row {
	display: flex;
	border-radius: var(--border-radius-element, 8px);
}

.folder-row--active {
	background: var(--color-primary-element-light);
	color: var(--color-primary-element-light-text);
}

.folder-pane__unified {
	flex: 1;
	display: flex;
	align-items: center;
	gap: 10px;
	min-height: 34px;
	margin: 0;
	padding: 0 8px 0 24px;
	border: 0;
	background: none;
	color: inherit;
	font: inherit;
	font-weight: 600;
	cursor: pointer;
}

.folder-pane__unified-name {
	flex: 1;
	text-align: start;
}

.folder-pane__count {
	font-size: 12px;
	font-variant-numeric: tabular-nums;
}

.folder-pane__center {
	display: flex;
	justify-content: center;
	padding: 16px;
}

.folder-pane__error {
	--sm-button-color: var(--color-error-text);
	display: flex;
	align-items: flex-start;
	gap: 8px;
	margin: 0 0 4px;
	padding: 8px;
	border: 0;
	border-radius: var(--border-radius-element, 8px);
	background: none;
	color: var(--color-error-text);
	text-align: start;
	font: inherit;
	cursor: pointer;
}

.folder-pane__error:hover {
	--sm-button-background: var(--color-background-hover);
	background: var(--color-background-hover);
}
</style>
