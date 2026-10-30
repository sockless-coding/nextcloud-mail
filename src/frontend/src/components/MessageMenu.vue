<template>
	<!-- One menu for all rows, opened at the pointer (or under a button) on an invisible anchor. -->
	<div class="message-menu" :style="{ left: `${x}px`, top: `${y}px` }">
		<NcActions :open="open"
			force-menu
			placement="bottom-start"
			:aria-label="t('sockless_mail', 'Message actions')"
			@update:open="$emit('update:open', $event)"
			@closed="$emit('closed')">
			<template v-if="single">
				<NcActionButton v-if="!isOpen(single)" close-after-click @click="openMessage(single, single.message.id)">
					<template #icon>
						<NcIconSvgWrapper :path="mdiEmailOpenOutline" />
					</template>
					{{ t('sockless_mail', 'Open') }}
				</NcActionButton>
				<NcActionButton v-for="mode in REPLIES"
					:key="mode.id"
					close-after-click
					@click="reply(mode.id)">
					<template #icon>
						<NcIconSvgWrapper :path="mode.icon" />
					</template>
					{{ mode.label }}
				</NcActionButton>
				<NcActionSeparator />
			</template>
			<NcActionButton close-after-click @click="setSeenRows(rows, someUnread)">
				<template #icon>
					<NcIconSvgWrapper :path="someUnread ? mdiEmailOpenOutline : mdiEmailOutline" />
				</template>
				{{ someUnread ? t('sockless_mail', 'Mark as read') : t('sockless_mail', 'Mark as unread') }}
			</NcActionButton>
			<NcActionButton close-after-click @click="setFlaggedRows(rows, someUnflagged)">
				<template #icon>
					<NcIconSvgWrapper :path="someUnflagged ? mdiFlagOutline : mdiFlagRemoveOutline" />
				</template>
				{{ someUnflagged ? t('sockless_mail', 'Flag') : t('sockless_mail', 'Remove flag') }}
			</NcActionButton>
			<NcActionButton v-if="canMoveRows(rows)" close-after-click @click="askMoveRows(rows)">
				<template #icon>
					<NcIconSvgWrapper :path="mdiFolderMoveOutline" />
				</template>
				{{ t('sockless_mail', 'Move…') }}
			</NcActionButton>
			<NcActionButton close-after-click @click="askTags(rows)">
				<template #icon>
					<NcIconSvgWrapper :path="mdiTagOutline" />
				</template>
				{{ t('sockless_mail', 'Tags…') }}
			</NcActionButton>
			<NcActionButton close-after-click @click="removeRows(rows)">
				<template #icon>
					<NcIconSvgWrapper :path="mdiDeleteOutline" />
				</template>
				{{ rows.length > 1 ? n('sockless_mail', 'Delete {count} message', 'Delete {count} messages', rows.length, { count: rows.length }) : t('sockless_mail', 'Delete') }}
			</NcActionButton>
			<template v-if="single">
				<NcActionSeparator />
				<NcActionButton v-if="tasksState.available"
					close-after-click
					@click="dialogs.createTask = { where: { accountId: single.accountId, folderId: single.folderId }, message: single.message }">
					<template #icon>
						<NcIconSvgWrapper :path="mdiClipboardCheckOutline" />
					</template>
					{{ t('sockless_mail', 'Create task…') }}
				</NcActionButton>
				<NcActionButton close-after-click
					@click="createRuleFrom({ accountId: single.accountId, folderId: single.folderId }, single.message)">
					<template #icon>
						<NcIconSvgWrapper :path="mdiFilterPlusOutline" />
					</template>
					{{ t('sockless_mail', 'Create rule…') }}
				</NcActionButton>
				<NcActionButton v-if="single.message.from[0]"
					close-after-click
					@click="dialogs.addContact = { name: single.message.from[0].name, address: single.message.from[0].address }">
					<template #icon>
						<NcIconSvgWrapper :path="mdiAccountPlusOutline" />
					</template>
					{{ t('sockless_mail', 'Add sender to contacts') }}
				</NcActionButton>
				<NcActionLink :href="emlUrl(single, single.message.id)" download close-after-click>
					<template #icon>
						<NcIconSvgWrapper :path="mdiDownload" />
					</template>
					{{ t('sockless_mail', 'Download message (.eml)') }}
				</NcActionLink>
			</template>
		</NcActions>
	</div>
</template>

<script setup>
import { mdiAccountPlusOutline, mdiClipboardCheckOutline, mdiDeleteOutline, mdiDownload, mdiEmailOpenOutline, mdiEmailOutline, mdiFilterPlusOutline, mdiFlagOutline, mdiFlagRemoveOutline, mdiFolderMoveOutline, mdiReply, mdiReplyAll, mdiShare, mdiTagOutline } from '@mdi/js'
import { showError } from '@nextcloud/dialogs/toast'
import { n, t } from '@nextcloud/l10n'
import NcActionButton from '@nextcloud/vue/components/NcActionButton'
import NcActionLink from '@nextcloud/vue/components/NcActionLink'
import NcActions from '@nextcloud/vue/components/NcActions'
import NcActionSeparator from '@nextcloud/vue/components/NcActionSeparator'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import { computed } from 'vue'
import { askMoveRows, askTags, canMoveRows, createRuleFrom, dialogs, removeRows, setFlaggedRows, setSeenRows } from '../store/actions.js'
import { composeReply } from '../store/compose.js'
import { emlUrl, mailboxState, openMessage } from '../store/mailbox.js'
import { tasksState } from '../store/tasks.js'

const props = defineProps({
	/** The rows ({ accountId, folderId, message }) the menu acts on: one, or a selection. */
	rows: { type: Array, required: true },
	open: { type: Boolean, default: false },
	/** Where the menu opens, in viewport pixels. */
	x: { type: Number, default: 0 },
	y: { type: Number, default: 0 },
})

defineEmits(['update:open', 'closed'])

const REPLIES = [
	{ id: 'reply', icon: mdiReply, label: t('sockless_mail', 'Reply') },
	{ id: 'replyAll', icon: mdiReplyAll, label: t('sockless_mail', 'Reply all') },
	{ id: 'forward', icon: mdiShare, label: t('sockless_mail', 'Forward') },
]

/** The row, when the menu is for one message; actions on a single message need it. */
const single = computed(() => (props.rows.length === 1 ? props.rows[0] : null))
// Mixed rows get the action that changes something, as the Insert shortcut does.
const someUnread = computed(() => props.rows.some((r) => !r.message.seen))
const someUnflagged = computed(() => props.rows.some((r) => !r.message.flagged))

function isOpen(row) {
	const open = mailboxState.open
	return row.message.id === open.id && row.accountId === open.accountId && row.folderId === open.folderId
}

async function reply(mode) {
	const row = single.value
	try {
		await composeReply(mode, { accountId: row.accountId, folderId: row.folderId }, row.message.id)
	} catch (e) {
		showError(t('sockless_mail', 'Could not prepare the message: {error}', { error: e.message }))
	}
}
</script>

<style scoped>
.message-menu {
	position: fixed;
	width: 0;
	height: 0;
	z-index: 1;
}

/* The trigger only anchors the popover; the row or the ⋯ button is what the user sees. */
.message-menu :deep(.action-item),
.message-menu :deep(.action-item__menutoggle) {
	width: 1px !important;
	min-width: 0 !important;
	height: 1px !important;
	min-height: 0 !important;
	padding: 0 !important;
	overflow: hidden;
	opacity: 0;
	pointer-events: none;
}
</style>
