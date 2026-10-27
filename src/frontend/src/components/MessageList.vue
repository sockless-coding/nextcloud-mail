<template>
	<div class="message-list">
		<!-- Leaves room for the navigation toggle Nextcloud places at the pane's top left. -->
		<header class="message-list__header">
			<h2>{{ search.active ? t('sockless_mail', 'Search') : folderName }}</h2>
			<span v-if="!view.loading && !view.error">{{ countText }}</span>
		</header>
		<form class="message-list__search" role="search" @submit.prevent="runSearch">
			<NcTextField v-model="search.query"
				type="search"
				:label="search.scope === 'folder' ? t('sockless_mail', 'Search in {folder}', { folder: folderName }) : t('sockless_mail', 'Search all accounts')"
				:show-trailing-button="search.query !== '' || search.active"
				trailing-button-icon="close"
				:trailing-button-label="t('sockless_mail', 'Clear search')"
				@trailing-button-click="clearSearch" />
			<NcCheckboxRadioSwitch :model-value="search.scope === 'all'"
				type="switch"
				@update:model-value="setScope">
				{{ t('sockless_mail', 'All accounts and folders') }}
			</NcCheckboxRadioSwitch>
			<p v-if="search.scope === 'all'" class="message-list__hint">
				{{ t('sockless_mail', 'Finds subjects, senders and recipients of messages this app has already loaded. To search message text and older mail, search a single folder.') }}
			</p>
		</form>
		<div v-if="view.loading" class="message-list__center">
			<NcLoadingIcon :size="44" />
		</div>
		<NcEmptyContent v-else-if="view.error"
			:name="search.active ? t('sockless_mail', 'Search failed') : t('sockless_mail', 'Could not load the messages')"
			:description="view.error.message" />
		<NcEmptyContent v-else-if="rows.length === 0"
			:name="search.active ? t('sockless_mail', 'No messages found') : t('sockless_mail', 'No messages in this folder')">
			<template #icon>
				<NcIconSvgWrapper :path="search.active ? mdiMagnify : mdiEmailOpenOutline" />
			</template>
		</NcEmptyContent>
		<template v-else>
			<ul :aria-label="search.active ? t('sockless_mail', 'Search results') : t('sockless_mail', 'Messages')">
				<NcListItem v-for="row in rows"
					:key="row.key"
					:name="sender(row.message)"
					:bold="!row.message.seen"
					:active="row.message.id === openId && isSelected(row)"
					:details="formatDate(row.message.date)"
					@click="open(row)">
					<template #icon>
						<NcAvatar :display-name="sender(row.message)" is-no-user disable-menu :size="40" />
					</template>
					<!-- Actions work on the selected folder; results from elsewhere get them once opened. -->
					<template v-if="isSelected(row)" #actions>
						<NcActionButton close-after-click @click="toggleSeen(row.message)">
							<template #icon>
								<NcIconSvgWrapper :path="row.message.seen ? mdiEmailOutline : mdiEmailOpenOutline" />
							</template>
							{{ row.message.seen ? t('sockless_mail', 'Mark as unread') : t('sockless_mail', 'Mark as read') }}
						</NcActionButton>
						<NcActionButton close-after-click @click="toggleFlagged(row.message)">
							<template #icon>
								<NcIconSvgWrapper :path="row.message.flagged ? mdiStarOff : mdiStarOutline" />
							</template>
							{{ row.message.flagged ? t('sockless_mail', 'Remove flag') : t('sockless_mail', 'Flag') }}
						</NcActionButton>
						<NcActionButton close-after-click @click="askMove([row.message.id])">
							<template #icon>
								<NcIconSvgWrapper :path="mdiFolderMoveOutline" />
							</template>
							{{ t('sockless_mail', 'Move…') }}
						</NcActionButton>
						<NcActionButton close-after-click @click="remove([row.message.id])">
							<template #icon>
								<NcIconSvgWrapper :path="mdiDeleteOutline" />
							</template>
							{{ t('sockless_mail', 'Delete') }}
						</NcActionButton>
					</template>
					<template #subname>
						<span class="message-list__subject">
							<NcIconSvgWrapper v-if="row.message.flagged" :path="mdiStar" :size="16" class="message-list__flag" :name="t('sockless_mail', 'Flagged')" />
							<NcIconSvgWrapper v-if="row.message.hasAttachments" :path="mdiPaperclip" :size="16" :name="t('sockless_mail', 'Has attachments')" />
							{{ row.message.subject || t('sockless_mail', '(no subject)') }}
						</span>
						<span v-if="row.location" class="message-list__location">{{ row.location }}</span>
					</template>
				</NcListItem>
			</ul>
			<div v-if="view.canLoadMore" class="message-list__more">
				<NcButton wide :disabled="view.loadingMore" @click="view.loadMore">
					<template #icon>
						<NcLoadingIcon v-if="view.loadingMore" :size="20" />
					</template>
					{{ search.active ? t('sockless_mail', 'More results') : t('sockless_mail', 'Load older messages') }}
				</NcButton>
			</div>
		</template>
	</div>
</template>

<script setup>
import { mdiDeleteOutline, mdiEmailOpenOutline, mdiEmailOutline, mdiFolderMoveOutline, mdiMagnify, mdiPaperclip, mdiStar, mdiStarOff, mdiStarOutline } from '@mdi/js'
import { getCanonicalLocale, n, t } from '@nextcloud/l10n'
import NcActionButton from '@nextcloud/vue/components/NcActionButton'
import NcAvatar from '@nextcloud/vue/components/NcAvatar'
import NcButton from '@nextcloud/vue/components/NcButton'
import NcCheckboxRadioSwitch from '@nextcloud/vue/components/NcCheckboxRadioSwitch'
import NcEmptyContent from '@nextcloud/vue/components/NcEmptyContent'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import NcListItem from '@nextcloud/vue/components/NcListItem'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import NcTextField from '@nextcloud/vue/components/NcTextField'
import { computed } from 'vue'
import { askMove, remove, toggleFlagged, toggleSeen } from '../store/actions.js'
import { accountsState } from '../store/accounts.js'
import { loadMore, mailboxState, openMessage, selectFolder } from '../store/mailbox.js'
import { clearSearch, runSearch, searchMore, searchState as search } from '../store/search.js'

const state = mailboxState.messages
const openId = computed(() => mailboxState.open.id)
const folderName = computed(() => {
	const { accountId, folderId } = mailboxState.selected
	return mailboxState.folders[accountId]?.list.find((f) => f.id === folderId)?.name ?? ''
})

/** What the list shows: the folder's messages, or search results. */
const view = computed(() => search.active
	? { loading: search.loading, error: search.error, loadingMore: search.loadingMore, canLoadMore: !!search.nextBefore, loadMore: searchMore }
	: { loading: state.loading, error: state.error, loadingMore: state.loadingMore, canLoadMore: !!state.nextBefore, loadMore })

const rows = computed(() => search.active
	? search.hits.map((h) => ({ ...h, key: `${h.accountId}/${h.folderId}/${h.message.id}`, location: search.scope === 'all' ? location(h) : null }))
	: state.list.map((message) => ({ ...mailboxState.selected, message, key: message.id, location: null })))

const countText = computed(() => {
	if (!search.active) {
		return n('sockless_mail', '{count} message', '{count} messages', state.total, { count: state.total })
	}
	return search.more
		? t('sockless_mail', 'First {count} results', { count: search.hits.length })
		: n('sockless_mail', '{count} result', '{count} results', search.total, { count: search.total })
})

/** "Account › Folder" for results from everywhere. */
function location(hit) {
	const account = accountsState.accounts.find((a) => a.id === hit.accountId)?.displayName ?? ''
	const folder = mailboxState.folders[hit.accountId]?.list.find((f) => f.id === hit.folderId)?.name
		?? (hit.folderFullName.toUpperCase() === 'INBOX' ? t('sockless_mail', 'Inbox') : hit.folderFullName)
	return `${account} › ${folder}`
}

function isSelected(row) {
	return row.accountId === mailboxState.selected.accountId && row.folderId === mailboxState.selected.folderId
}

/** A result from another folder opens there, so the message view's actions apply to its folder. */
function open(row) {
	if (!isSelected(row)) {
		selectFolder(row.accountId, row.folderId)
	}
	openMessage(row.message.id)
}

/** Switching the scope searches again, or just remembers the choice when there is nothing to search. */
function setScope(all) {
	search.scope = all ? 'all' : 'folder'
	if (search.query.trim()) {
		runSearch()
	}
}

const timeFormat = new Intl.DateTimeFormat(getCanonicalLocale(), { hour: 'numeric', minute: '2-digit' })
const dateFormat = new Intl.DateTimeFormat(getCanonicalLocale(), { day: 'numeric', month: 'short' })
const yearFormat = new Intl.DateTimeFormat(getCanonicalLocale(), { day: 'numeric', month: 'short', year: 'numeric' })

function formatDate(value) {
	const date = new Date(value)
	const now = new Date()
	if (date.toDateString() === now.toDateString()) {
		return timeFormat.format(date)
	}
	return (date.getFullYear() === now.getFullYear() ? dateFormat : yearFormat).format(date)
}

function sender(message) {
	const from = message.from[0]
	return from ? (from.name || from.address) : t('sockless_mail', 'Unknown sender')
}
</script>

<style scoped>
.message-list ul {
	padding: 4px;
}

.message-list__header {
	display: flex;
	align-items: baseline;
	gap: 8px;
	min-height: var(--default-clickable-area, 34px);
	padding: 10px 8px 4px calc(var(--default-clickable-area, 34px) + 16px);
	color: var(--color-text-maxcontrast);
}

.message-list__header h2 {
	margin: 0;
	font-size: 18px;
	color: var(--color-main-text);
}

.message-list__center {
	display: flex;
	justify-content: center;
	padding: 32px;
}

.message-list__subject {
	display: inline-flex;
	align-items: center;
	gap: 4px;
}

.message-list__flag {
	color: var(--color-warning);
}

.message-list__more {
	padding: 8px;
}

.message-list__search {
	padding: 0 8px 4px;
}

.message-list__hint {
	color: var(--color-text-maxcontrast);
	font-size: 0.9em;
}

.message-list__location {
	display: block;
	color: var(--color-text-maxcontrast);
	font-size: 0.9em;
}
</style>
