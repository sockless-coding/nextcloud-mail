<template>
	<aside class="folder-pane" :aria-label="t('sockless_mail', 'Folders')">
		<!-- All accounts: each special-use folder of every account as one, then the smart folders. -->
		<template v-if="unifiedPaneShown()">
			<header class="folder-pane__account">
				<NcIconSvgWrapper :path="mdiInboxMultipleOutline" :size="20" class="folder-pane__all-icon" />
				<div class="folder-pane__account-text">
					<h2 class="folder-pane__account-name">{{ t('sockless_mail', 'All accounts') }}</h2>
					<span class="folder-pane__account-address">
						{{ n('sockless_mail', '{count} account', '{count} accounts', unifiedAccounts.length, { count: unifiedAccounts.length }) }}
					</span>
				</div>
			</header>
			<ul v-for="(group, i) in unifiedGroups"
				:key="i"
				class="folder-pane__group"
				:class="{ 'folder-pane__group--smart': i > 0 }"
				:aria-label="i > 0 ? t('sockless_mail', 'Smart folders') : t('sockless_mail', 'Unified folders')">
				<li v-for="view in group" :key="view.id">
					<div class="folder-row" :class="{ 'folder-row--active': mailboxState.selected.unified === view.id, 'folder-row--unread': viewUnread(view.id) > 0 }">
						<button class="folder-pane__unified"
							:data-view="view.id"
							:aria-current="mailboxState.selected.unified === view.id ? 'page' : undefined"
							@click="openUnified(view.id)">
							<NcIconSvgWrapper :path="view.icon" :size="20" />
							<span class="folder-pane__unified-name">{{ view.name }}</span>
							<span v-if="viewUnread(view.id)" class="folder-pane__count">{{ viewUnread(view.id) }}</span>
						</button>
					</div>
				</li>
			</ul>
			<ul v-if="scheduledListed(null)" class="folder-pane__group folder-pane__group--smart">
				<li>
					<ScheduledFolderRow />
				</li>
			</ul>
			<ul v-if="tagsState.list.length" class="folder-pane__group folder-pane__group--smart" :aria-label="t('sockless_mail', 'Tags')">
				<li v-for="tag in tagsState.list" :key="tag.id">
					<TagFolderRow :tag="tag" />
				</li>
			</ul>
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
					<NcActionButton close-after-click @click="dialogs.accountAppearance = account">
						<template #icon>
							<NcIconSvgWrapper :path="mdiPaletteOutline" />
						</template>
						{{ t('sockless_mail', 'Customize…') }}
					</NcActionButton>
					<NcActionButton close-after-click @click="dialogs.rules = { account, draft: null }">
						<template #icon>
							<NcIconSvgWrapper :path="mdiFilterCogOutline" />
						</template>
						{{ t('sockless_mail', 'Rules') }}
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
			<ul v-if="scheduledListed(account.id)" class="folder-pane__group folder-pane__group--smart">
				<li>
					<ScheduledFolderRow :account-id="account.id" />
				</li>
			</ul>
			<ul v-if="tagsState.list.length" class="folder-pane__group folder-pane__group--smart" :aria-label="t('sockless_mail', 'Tags')">
				<li v-for="tag in tagsState.list" :key="tag.id">
					<TagFolderRow :tag="tag" :account-id="account.id" />
				</li>
			</ul>
		</template>
	</aside>
</template>

<script setup>
import { mdiAlertCircleOutline, mdiArrowDown, mdiArrowUp, mdiDelete, mdiFilterCogOutline, mdiFolderPlusOutline, mdiInboxMultipleOutline, mdiPaletteOutline, mdiPencil, mdiRefresh } from '@mdi/js'
import { n, t } from '@nextcloud/l10n'
import NcActionButton from '@nextcloud/vue/components/NcActionButton'
import NcActions from '@nextcloud/vue/components/NcActions'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import { computed } from 'vue'
import { accountColor, accountsState, unifiedAccounts } from '../store/accounts.js'
import { dialogs } from '../store/actions.js'
import { loadFolders, mailboxState } from '../store/mailbox.js'
import { currentAccount, moveAccountBy, newFolder, openUnified } from '../store/navigation.js'
import { scheduledOf } from '../store/scheduled.js'
import { tagsState } from '../store/tags.js'
import { availableViews, unifiedPaneShown, viewUnread } from '../store/unified.js'
import FolderItem from './FolderItem.vue'
import ScheduledFolderRow from './ScheduledFolderRow.vue'
import TagFolderRow from './TagFolderRow.vue'

const EMPTY_FOLDERS = { list: [], loading: false, error: null }

/** The Scheduled entry is there while something is scheduled, or while it is open. */
function scheduledListed(accountId) {
	return scheduledOf(accountId).length > 0 || (!!mailboxState.selected.scheduled && mailboxState.selected.accountId === accountId)
}

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

/** Unified folders, then smart folders. */
const unifiedGroups = computed(() => {
	const views = availableViews()
	return [views.filter((v) => !v.filter), views.filter((v) => v.filter)]
})
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

.folder-pane__all-icon {
	flex: none;
	color: var(--color-text-maxcontrast);
}

.folder-pane__group--smart {
	margin-top: 8px;
	padding-top: 8px;
	border-top: 1px solid var(--color-border);
}

/* The unified rows look like folder rows (FolderItem.vue). */
.folder-row {
	display: flex;
	border-radius: var(--border-radius-element, 8px);
}

.folder-row:hover {
	background: var(--color-background-hover);
}

.folder-row--active,
.folder-row--active:hover {
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
	cursor: pointer;
}

.folder-pane__unified:focus-visible {
	outline: 2px solid var(--color-primary-element);
	outline-offset: -2px;
	border-radius: var(--border-radius-element, 8px);
}

.folder-pane__unified-name {
	flex: 1;
	min-width: 0;
	overflow: hidden;
	text-align: start;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.folder-row--active .folder-pane__unified-name,
.folder-row--unread .folder-pane__unified-name {
	font-weight: 600;
}

.folder-pane__count {
	font-size: 12px;
	font-weight: 600;
	font-variant-numeric: tabular-nums;
	color: var(--color-primary-element);
}

.folder-row--active .folder-pane__count {
	color: inherit;
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
