<template>
	<NcAppNavigationItem :name="displayName"
		:active="active"
		:allow-collapse="folder.children.length > 0"
		:open="open"
		@update:open="open = $event"
		@click="select">
		<template #icon>
			<NcIconSvgWrapper :path="icon" />
		</template>
		<template v-if="folder.unread" #counter>
			<NcCounterBubble :count="folder.unread" />
		</template>
		<template #actions>
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
		</template>
		<FolderItem v-for="child in folder.children"
			:key="child.id"
			:account-id="accountId"
			:folder="child" />
	</NcAppNavigationItem>
</template>

<script setup>
import { mdiAlertOctagonOutline, mdiArchiveOutline, mdiDeleteOutline, mdiEmailMultipleOutline, mdiFileDocumentEditOutline, mdiFolderOutline, mdiFolderPlusOutline, mdiInbox, mdiPencil, mdiSendOutline, mdiStarOutline } from '@mdi/js'
import { t } from '@nextcloud/l10n'
import NcActionButton from '@nextcloud/vue/components/NcActionButton'
import NcAppNavigationItem from '@nextcloud/vue/components/NcAppNavigationItem'
import NcCounterBubble from '@nextcloud/vue/components/NcCounterBubble'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import { computed, ref } from 'vue'
import { askNewFolder, askRenameFolder } from '../store/actions.js'
import { mailboxState, selectFolder } from '../store/mailbox.js'

const props = defineProps({
	accountId: { type: String, required: true },
	/** FolderDto plus children: [] (built in App.vue) */
	folder: { type: Object, required: true },
})

const ICONS = {
	inbox: mdiInbox,
	drafts: mdiFileDocumentEditOutline,
	sent: mdiSendOutline,
	archive: mdiArchiveOutline,
	junk: mdiAlertOctagonOutline,
	trash: mdiDeleteOutline,
	all: mdiEmailMultipleOutline,
	flagged: mdiStarOutline,
}

const NAMES = {
	inbox: t('sockless_mail', 'Inbox'),
	drafts: t('sockless_mail', 'Drafts'),
	sent: t('sockless_mail', 'Sent'),
	junk: t('sockless_mail', 'Junk'),
	trash: t('sockless_mail', 'Trash'),
	archive: t('sockless_mail', 'Archive'),
}

const open = ref(false)
const icon = computed(() => ICONS[props.folder.specialUse] ?? mdiFolderOutline)
// Special folders get their translated name when the server uses the standard English one.
const displayName = computed(() => (props.folder.specialUse && props.folder.name.toLowerCase() === props.folder.specialUse
	? NAMES[props.folder.specialUse]
	: null) ?? props.folder.name)
const active = computed(() => mailboxState.selected.accountId === props.accountId && mailboxState.selected.folderId === props.folder.id)

function select() {
	if (props.folder.selectable) {
		selectFolder(props.accountId, props.folder.id)
	} else {
		open.value = !open.value
	}
}
</script>
