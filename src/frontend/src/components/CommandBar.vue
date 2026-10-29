<template>
	<div class="command-bar">
		<div class="command-bar__commands" role="toolbar" :aria-label="t('sockless_mail', 'Message actions')">
			<button class="command command--icon"
				:aria-label="folderPaneShown ? t('sockless_mail', 'Hide folders') : t('sockless_mail', 'Show folders')"
				:aria-expanded="String(folderPaneShown)"
				:title="folderPaneShown ? t('sockless_mail', 'Hide folders') : t('sockless_mail', 'Show folders')"
				@click="toggleFolderPane">
				<NcIconSvgWrapper :path="mdiMenu" :size="20" />
			</button>
			<button class="command command--primary"
				:disabled="accountsState.accounts.length === 0"
				@click="composeNew">
				<NcIconSvgWrapper :path="mdiPencilOutline" :size="20" />
				<span class="command__label">{{ t('sockless_mail', 'New message') }}</span>
			</button>
			<span class="command-bar__separator" />
			<button v-for="mode in REPLIES"
				:key="mode.id"
				class="command"
				:disabled="!canReply"
				@click="reply(mode.id)">
				<NcIconSvgWrapper :path="mode.icon" :size="20" />
				<span class="command__label">{{ mode.label }}</span>
			</button>
			<span class="command-bar__separator" />
			<button class="command command--danger" :disabled="!targets.length" @click="removeRows(targets)">
				<NcIconSvgWrapper :path="mdiDeleteOutline" :size="20" />
				<span class="command__label">{{ t('sockless_mail', 'Delete') }}</span>
			</button>
			<button class="command" :disabled="!archiveFolder" @click="archive">
				<NcIconSvgWrapper :path="mdiArchiveArrowDownOutline" :size="20" />
				<span class="command__label">{{ t('sockless_mail', 'Archive') }}</span>
			</button>
			<button class="command" :disabled="!canMoveRows(targets)" @click="askMoveRows(targets)">
				<NcIconSvgWrapper :path="mdiFolderMoveOutline" :size="20" />
				<span class="command__label">{{ t('sockless_mail', 'Move to') }}</span>
			</button>
			<span class="command-bar__separator" />
			<button class="command" :disabled="!targets.length" @click="setSeenRows(targets, markRead)">
				<NcIconSvgWrapper :path="markRead ? mdiEmailOpenOutline : mdiEmailOutline" :size="20" />
				<span class="command__label">{{ markRead ? t('sockless_mail', 'Mark as read') : t('sockless_mail', 'Mark as unread') }}</span>
			</button>
			<button class="command"
				:disabled="!targets.length"
				:aria-pressed="targets.length ? String(!flag) : undefined"
				@click="setFlaggedRows(targets, flag)">
				<NcIconSvgWrapper :path="flag ? mdiFlagOutline : mdiFlag" :size="20" />
				<span class="command__label">{{ t('sockless_mail', 'Flag') }}</span>
			</button>
		</div>

		<form class="command-bar__search" role="search" @submit.prevent="runSearch">
			<NcIconSvgWrapper :path="mdiMagnify" :size="20" />
			<select v-if="!mailboxState.selected.unified"
				:value="search.scope"
				class="command-bar__scope"
				:aria-label="t('sockless_mail', 'Search scope')"
				@change="setScope($event.target.value)">
				<option value="folder">
					{{ t('sockless_mail', 'This folder') }}
				</option>
				<option value="all">
					{{ t('sockless_mail', 'All accounts') }}
				</option>
			</select>
			<input v-model="search.query"
				type="search"
				:aria-label="searchLabel"
				:placeholder="searchLabel"
				@keydown.esc="clearQuery">
			<button v-if="search.query !== ''"
				type="button"
				class="command command--icon"
				:aria-label="t('sockless_mail', 'Clear search')"
				@click="clearQuery">
				<NcIconSvgWrapper :path="mdiClose" :size="18" />
			</button>
		</form>

		<NcActions class="command-bar__view"
			:aria-label="t('sockless_mail', 'View settings')"
			:title="t('sockless_mail', 'View settings')">
			<template #icon>
				<NcIconSvgWrapper :path="mdiViewSplitVertical" />
			</template>
			<NcActionCaption :name="t('sockless_mail', 'Reading pane')" />
			<NcActionRadio v-for="option in PANES"
				:key="option.value"
				name="sockless-reading-pane"
				:value="option.value"
				:model-value="layout.readingPane"
				:disabled="viewport.narrowPanes"
				@update:model-value="layout.readingPane = option.value">
				{{ option.label }}
			</NcActionRadio>
			<NcActionSeparator />
			<NcActionCaption :name="t('sockless_mail', 'Density')" />
			<NcActionRadio v-for="option in DENSITIES"
				:key="option.value"
				name="sockless-density"
				:value="option.value"
				:model-value="layout.density"
				@update:model-value="layout.density = option.value">
				{{ option.label }}
			</NcActionRadio>
			<NcActionCheckbox :model-value="layout.senderLogos"
				@update:model-value="layout.senderLogos = $event">
				{{ t('sockless_mail', 'Sender logos') }}
			</NcActionCheckbox>
			<NcActionSeparator />
			<NcActionButton close-after-click @click="dialogs.shortcuts = true">
				<template #icon>
					<NcIconSvgWrapper :path="mdiKeyboardOutline" />
				</template>
				{{ t('sockless_mail', 'Keyboard shortcuts') }}
			</NcActionButton>
		</NcActions>
	</div>
</template>

<script setup>
import { mdiArchiveArrowDownOutline, mdiClose, mdiDeleteOutline, mdiEmailOpenOutline, mdiEmailOutline, mdiFlag, mdiFlagOutline, mdiFolderMoveOutline, mdiKeyboardOutline, mdiMagnify, mdiMenu, mdiPencilOutline, mdiReply, mdiReplyAll, mdiShare, mdiViewSplitVertical } from '@mdi/js'
import { showError } from '@nextcloud/dialogs/toast'
import { t } from '@nextcloud/l10n'
import NcActionButton from '@nextcloud/vue/components/NcActionButton'
import NcActionCaption from '@nextcloud/vue/components/NcActionCaption'
import NcActionCheckbox from '@nextcloud/vue/components/NcActionCheckbox'
import NcActionRadio from '@nextcloud/vue/components/NcActionRadio'
import NcActions from '@nextcloud/vue/components/NcActions'
import NcActionSeparator from '@nextcloud/vue/components/NcActionSeparator'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import { computed } from 'vue'
import { accountsState } from '../store/accounts.js'
import { askMoveRows, canMoveRows, dialogs, moveTo, removeRows, setFlaggedRows, setSeenRows } from '../store/actions.js'
import { composeNew, composeReply } from '../store/compose.js'
import { folderPaneShown, layout, toggleFolderPane, viewport } from '../store/layout.js'
import { mailboxState } from '../store/mailbox.js'
import { clearQuery, currentScope, runSearch, searchState as search } from '../store/search.js'
import { commandTargets, selection } from '../store/selection.js'

const REPLIES = [
	{ id: 'reply', icon: mdiReply, label: t('sockless_mail', 'Reply') },
	{ id: 'replyAll', icon: mdiReplyAll, label: t('sockless_mail', 'Reply all') },
	{ id: 'forward', icon: mdiShare, label: t('sockless_mail', 'Forward') },
]

const PANES = [
	{ value: 'right', label: t('sockless_mail', 'Right') },
	{ value: 'bottom', label: t('sockless_mail', 'Bottom') },
	{ value: 'off', label: t('sockless_mail', 'Off') },
]

const DENSITIES = [
	{ value: 'comfortable', label: t('sockless_mail', 'Comfortable') },
	{ value: 'compact', label: t('sockless_mail', 'Compact') },
]

const open = mailboxState.open

const targets = computed(() => commandTargets())

const canReply = computed(() => !selection.rows.length && !!open.message)
// Mixed states do what most of them need: read if any is unread, flag if any is not flagged.
const markRead = computed(() => !targets.value.length || targets.value.some((r) => !r.message.seen))
const flag = computed(() => !targets.value.length || targets.value.some((r) => !r.message.flagged))

/** The archive folder of the targets' account, when they come from one other folder of it. */
const archiveFolder = computed(() => {
	if (!canMoveRows(targets.value)) {
		return null
	}
	const { accountId, folderId } = targets.value[0]
	const folder = mailboxState.folders[accountId]?.list.find((f) => f.specialUse === 'archive')
	return folder && folder.id !== folderId ? folder : null
})

const searchLabel = computed(() => {
	if (currentScope() === 'all') {
		return t('sockless_mail', 'Search all accounts')
	}
	const { accountId, folderId } = mailboxState.selected
	const folder = mailboxState.folders[accountId]?.list.find((f) => f.id === folderId)?.name
	return folder ? t('sockless_mail', 'Search in {folder}', { folder }) : t('sockless_mail', 'Search mail')
})

async function reply(mode) {
	try {
		await composeReply(mode)
	} catch (e) {
		showError(t('sockless_mail', 'Could not prepare the message: {error}', { error: e.message }))
	}
}

/** Switching the scope searches again, or just remembers the choice when there is nothing to search. */
function setScope(value) {
	search.scope = value
	if (search.query.trim()) {
		runSearch()
	}
}

function archive() {
	const rows = targets.value
	const { accountId, folderId } = rows[0]
	moveTo({ where: { accountId, folderId }, messageIds: rows.map((r) => r.message.id) }, archiveFolder.value.id)
}
</script>

<style scoped>
.command-bar {
	container-type: inline-size;
	display: flex;
	align-items: center;
	gap: 8px;
	min-height: 52px;
	padding: 0 8px;
	border-bottom: 1px solid var(--color-border);
	background: var(--color-main-background);
}

.command-bar__commands {
	display: flex;
	align-items: center;
	gap: 2px;
	flex: none;
	min-width: 0;
	overflow-x: auto;
	scrollbar-width: none;
}

.command {
	display: inline-flex;
	align-items: center;
	gap: 6px;
	height: 36px;
	min-height: 36px;
	margin: 0;
	padding: 0 10px;
	border: 0;
	border-radius: var(--border-radius-element, 8px);
	background: none;
	color: var(--color-main-text);
	font: inherit;
	white-space: nowrap;
	cursor: pointer;
	flex: none;
}

.command:hover:not(:disabled) {
	--sm-button-background: var(--color-background-hover);
	background: var(--color-background-hover);
}

.command:focus-visible {
	outline: 2px solid var(--color-primary-element);
	outline-offset: -2px;
}

.command:disabled {
	opacity: 0.45;
	cursor: default;
}

.command--icon {
	padding: 0 8px;
}

.command--primary,
.command--primary:hover:not(:disabled) {
	--sm-button-background: var(--color-primary-element);
	--sm-button-color: var(--color-primary-element-text);
	padding: 0 14px;
	font-weight: 600;
	color: var(--color-primary-element-text);
	background: var(--color-primary-element);
}

.command--primary:hover:not(:disabled) {
	--sm-button-background: var(--color-primary-element-hover);
	background: var(--color-primary-element-hover);
}

.command--danger:not(:disabled) {
	--sm-button-color: var(--color-error-text);
	color: var(--color-error-text);
}

.command-bar__separator {
	width: 1px;
	height: 24px;
	margin: 0 4px;
	flex: none;
	background: var(--color-border);
}

.command-bar__search {
	display: flex;
	align-items: center;
	gap: 6px;
	margin-inline-start: auto;
	min-width: 120px;
	max-width: 380px;
	flex: 1 1 380px;
	height: 36px;
	padding: 0 2px 0 10px;
	border-radius: var(--border-radius-element, 8px);
	background: var(--color-background-hover);
	color: var(--color-text-maxcontrast);
}

.command-bar__search:focus-within {
	outline: 2px solid var(--color-primary-element);
	background: var(--color-main-background);
}

.command-bar__scope {
	flex: none;
	width: auto;
	height: 28px;
	min-height: 0;
	margin: 0;
	padding: 0 4px;
	border: 0;
	border-inline-end: 1px solid var(--color-border);
	border-radius: 0;
	background: none;
	color: var(--color-main-text);
	font: inherit;
	font-size: 13px;
}

.command-bar__search input {
	flex: 1;
	min-width: 0;
	height: 32px;
	min-height: 0;
	margin: 0;
	padding: 0;
	border: 0;
	outline: none;
	background: none;
	color: var(--color-main-text);
	font: inherit;
	box-shadow: none;
}

.command-bar__search input::-webkit-search-cancel-button {
	display: none;
}

/* Narrow command bars keep the icons; the labels stay for screen readers. */
@container (max-width: 1100px) {
	.command:not(.command--primary) .command__label {
		position: absolute;
		width: 1px;
		height: 1px;
		overflow: hidden;
		clip-path: inset(50%);
		white-space: nowrap;
	}

	.command:not(.command--primary) {
		padding: 0 8px;
	}
}

@container (max-width: 640px) {
	.command--primary .command__label {
		position: absolute;
		width: 1px;
		height: 1px;
		overflow: hidden;
		clip-path: inset(50%);
	}

	.command--primary {
		padding: 0 8px;
	}

	.command-bar__separator {
		display: none;
	}

	/* Too narrow for everything: the commands scroll sideways. */
	.command-bar__commands {
		flex: 0 1 auto;
	}
}
</style>
