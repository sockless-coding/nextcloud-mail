<template>
	<li class="folder-item">
		<div class="folder-row"
			:class="{ 'folder-row--active': active, 'folder-row--unread': folder.unread > 0 }"
			:style="{ '--depth': depth }">
			<button v-if="folder.children.length"
				class="folder-row__toggle"
				:aria-expanded="String(open)"
				:aria-label="open ? t('sockless_mail', 'Collapse {name}', { name: displayName }) : t('sockless_mail', 'Expand {name}', { name: displayName })"
				@click="open = !open">
				<NcIconSvgWrapper :path="open ? mdiChevronDown : mdiChevronRight" :size="16" />
			</button>
			<button class="folder-row__main"
				:aria-current="active ? 'page' : undefined"
				@click="select">
				<NcIconSvgWrapper :path="icon" :size="20" />
				<span class="folder-row__name">{{ displayName }}</span>
				<span v-if="folder.unread" class="folder-row__count">{{ folder.unread }}</span>
			</button>
			<NcActions class="folder-row__actions" :aria-label="t('sockless_mail', 'Folder actions')">
				<NcActionButton close-after-click @click="askNewFolder(accountId, folder)">
					<template #icon>
						<NcIconSvgWrapper :path="mdiFolderPlusOutline" />
					</template>
					{{ t('sockless_mail', 'New subfolder') }}
				</NcActionButton>
				<NcActionButton v-if="folder.specialUse !== 'inbox'" close-after-click @click="askRenameFolder(accountId, folder)">
					<template #icon>
						<NcIconSvgWrapper :path="mdiPencil" />
					</template>
					{{ t('sockless_mail', 'Rename') }}
				</NcActionButton>
			</NcActions>
		</div>
		<ul v-if="open && folder.children.length">
			<FolderItem v-for="child in folder.children"
				:key="child.id"
				:account-id="accountId"
				:folder="child"
				:depth="depth + 1" />
		</ul>
	</li>
</template>

<script setup>
import { mdiChevronDown, mdiChevronRight, mdiFolderOutline, mdiFolderPlusOutline, mdiPencil } from '@mdi/js'
import { t } from '@nextcloud/l10n'
import NcActionButton from '@nextcloud/vue/components/NcActionButton'
import NcActions from '@nextcloud/vue/components/NcActions'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import { computed } from 'vue'
import { USE_ICONS, USE_NAMES } from '../folderUses.js'
import { askNewFolder, askRenameFolder } from '../store/actions.js'
import { mailboxState } from '../store/mailbox.js'
import { openFolder } from '../store/navigation.js'
import { isExpanded, setExpanded } from '../store/viewState.js'

const props = defineProps({
	accountId: { type: String, required: true },
	/** FolderDto plus children: [] (see FolderPane.vue) */
	folder: { type: Object, required: true },
	depth: { type: Number, default: 0 },
})

const open = computed({
	get: () => isExpanded(props.accountId, props.folder.id),
	set: (value) => setExpanded(props.accountId, props.folder.id, value),
})
const icon = computed(() => USE_ICONS[props.folder.specialUse] ?? mdiFolderOutline)
// Special folders get their translated name when the server uses the standard English one.
const displayName = computed(() => (props.folder.specialUse && props.folder.name.toLowerCase() === props.folder.specialUse
	? USE_NAMES[props.folder.specialUse]
	: null) ?? props.folder.name)
const active = computed(() => mailboxState.selected.accountId === props.accountId && mailboxState.selected.folderId === props.folder.id)

function select() {
	if (props.folder.selectable) {
		openFolder(props.accountId, props.folder.id)
	} else {
		open.value = !open.value
	}
}
</script>

<style scoped>
.folder-row {
	display: flex;
	align-items: center;
	min-height: 34px;
	padding-inline-start: calc(var(--depth) * 16px + 20px);
	border-radius: var(--border-radius-element, 8px);
	position: relative;
}

.folder-row:hover {
	background: var(--color-background-hover);
}

.folder-row--active,
.folder-row--active:hover {
	background: var(--color-primary-element-light);
	color: var(--color-primary-element-light-text);
}

.folder-row__toggle {
	position: absolute;
	inset-inline-start: calc(var(--depth) * 16px);
	display: grid;
	place-items: center;
	width: 20px;
	height: 28px;
	min-height: 0;
	margin: 0;
	padding: 0;
	border: 0;
	background: none;
	color: var(--color-text-maxcontrast);
	cursor: pointer;
}

.folder-row__main {
	flex: 1;
	min-width: 0;
	display: flex;
	align-items: center;
	gap: 10px;
	min-height: 34px;
	margin: 0;
	padding: 0 8px 0 4px;
	border: 0;
	background: none;
	color: inherit;
	font: inherit;
	text-align: start;
	cursor: pointer;
}

.folder-row__main:focus-visible,
.folder-row__toggle:focus-visible {
	outline: 2px solid var(--color-primary-element);
	outline-offset: -2px;
	border-radius: var(--border-radius-element, 8px);
}

.folder-row__name {
	flex: 1;
	min-width: 0;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.folder-row--active .folder-row__name,
.folder-row--unread .folder-row__name {
	font-weight: 600;
}

.folder-row__count {
	font-size: 12px;
	font-weight: 600;
	font-variant-numeric: tabular-nums;
	color: var(--color-primary-element);
}

.folder-row--active .folder-row__count {
	color: inherit;
}

.folder-row__actions {
	opacity: 0;
}

.folder-row:hover .folder-row__actions,
.folder-row:focus-within .folder-row__actions,
.folder-row__actions:has([aria-expanded='true']) {
	opacity: 1;
}

@media (hover: none) {
	.folder-row__actions {
		opacity: 1;
	}
}
</style>
