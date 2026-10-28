<template>
	<div class="message-list" :class="{ 'message-list--compact': layout.density === 'compact' }">
		<header class="message-list__header">
			<h2>{{ search.active ? t('sockless_mail', 'Search') : listName }}</h2>
			<span v-if="!view.loading && !view.error">{{ countText }}</span>
		</header>
		<p v-if="search.active && scope === 'all'" class="message-list__hint">
			{{ t('sockless_mail', 'Finds subjects, senders and recipients of messages this app has already loaded. To search message text and older mail, search a single folder.') }}
		</p>
		<NcNoteCard v-for="failure in accountErrors"
			:key="failure.accountId"
			type="warning"
			class="message-list__account-error">
			{{ t('sockless_mail', '{account}: {error} Showing the messages loaded before.', { account: accountName(failure.accountId), error: failure.message }) }}
		</NcNoteCard>
		<div v-if="view.loading" class="message-list__center">
			<NcLoadingIcon :size="44" />
		</div>
		<NcEmptyContent v-else-if="view.error"
			:name="search.active ? t('sockless_mail', 'Search failed') : t('sockless_mail', 'Could not load the messages')"
			:description="view.error.message" />
		<NcEmptyContent v-else-if="rows.length === 0"
			:name="emptyText">
			<template #icon>
				<NcIconSvgWrapper :path="search.active ? mdiMagnify : mdiEmailOpenOutline" />
			</template>
		</NcEmptyContent>
		<div v-else ref="scroller" class="message-list__rows">
			<ul :aria-label="search.active ? t('sockless_mail', 'Search results') : t('sockless_mail', 'Messages')">
				<template v-for="(row, index) in rows" :key="row.key">
					<li v-if="groupOf(index)" class="message-list__group" aria-hidden="true">
						{{ groupOf(index) }}
					</li>
					<li class="message-row"
						:class="{
							'message-row--unread': !row.message.seen,
							'message-row--open': isOpen(row),
							'message-row--selected': isSelected(row),
							'message-row--selecting': selection.rows.length > 0,
						}"
						:data-key="row.key"
						tabindex="0"
						:aria-current="isOpen(row) ? 'true' : undefined"
						@click="onClick(row, $event)"
						@keydown="onKey(row, index, $event)">
						<span class="message-row__stripe" />
						<span class="message-row__avatar">
							<NcAvatar :display-name="sender(row.message)"
								is-no-user
								disable-menu
								disable-tooltip
								:size="layout.density === 'compact' ? 24 : 36" />
							<span v-if="unifiedShown || scope === 'all' && search.active"
								class="message-row__account"
								:style="{ background: accountColor(row.accountId) }"
								:title="accountName(row.accountId)" />
							<button class="message-row__check"
								role="checkbox"
								:aria-checked="String(isSelected(row))"
								:aria-label="t('sockless_mail', 'Select {subject}', { subject: subjectOf(row.message) })"
								@click.stop="toggleRow(row)">
								<NcIconSvgWrapper :path="isSelected(row) ? mdiCheckCircle : mdiCheckboxBlankCircleOutline" :size="layout.density === 'compact' ? 20 : 24" />
							</button>
						</span>
						<span class="message-row__from">{{ sender(row.message) }}</span>
						<span class="message-row__date">{{ formatDate(row.message.date) }}</span>
						<span class="message-row__subject">
							<NcIconSvgWrapper v-if="row.message.flagged" :path="mdiFlag" :size="16" class="message-row__flag" :name="t('sockless_mail', 'Flagged')" />
							<NcIconSvgWrapper v-if="row.message.answered" :path="mdiReply" :size="16" :name="t('sockless_mail', 'Answered')" />
							<NcIconSvgWrapper v-if="row.message.hasAttachments" :path="mdiPaperclip" :size="16" :name="t('sockless_mail', 'Has attachments')" />
							<span class="message-row__subject-text">{{ subjectOf(row.message) }}</span>
						</span>
						<span v-if="row.location" class="message-row__location">{{ row.location }}</span>
						<!-- Every row knows its folder, so actions work on results from elsewhere too. -->
						<span class="message-row__actions" @click.stop>
							<button class="message-row__action"
								:aria-label="t('sockless_mail', 'Delete')"
								:title="t('sockless_mail', 'Delete')"
								@click="remove(row, [row.message.id])">
								<NcIconSvgWrapper :path="mdiDeleteOutline" :size="18" />
							</button>
							<button class="message-row__action"
								:aria-label="row.message.seen ? t('sockless_mail', 'Mark as unread') : t('sockless_mail', 'Mark as read')"
								:title="row.message.seen ? t('sockless_mail', 'Mark as unread') : t('sockless_mail', 'Mark as read')"
								@click="toggleSeen(row, row.message)">
								<NcIconSvgWrapper :path="row.message.seen ? mdiEmailOutline : mdiEmailOpenOutline" :size="18" />
							</button>
							<button class="message-row__action"
								:aria-label="row.message.flagged ? t('sockless_mail', 'Remove flag') : t('sockless_mail', 'Flag')"
								:title="row.message.flagged ? t('sockless_mail', 'Remove flag') : t('sockless_mail', 'Flag')"
								@click="toggleFlagged(row, row.message)">
								<NcIconSvgWrapper :path="row.message.flagged ? mdiFlagRemoveOutline : mdiFlagOutline" :size="18" />
							</button>
							<button class="message-row__action"
								:aria-label="t('sockless_mail', 'Move…')"
								:title="t('sockless_mail', 'Move…')"
								@click="askMove(row, [row.message.id])">
								<NcIconSvgWrapper :path="mdiFolderMoveOutline" :size="18" />
							</button>
						</span>
					</li>
				</template>
			</ul>
			<div v-if="view.canLoadMore" ref="more" class="message-list__more">
				<NcButton wide :disabled="view.loadingMore" @click="view.loadMore">
					<template #icon>
						<NcLoadingIcon v-if="view.loadingMore" :size="20" />
					</template>
					{{ search.active ? t('sockless_mail', 'More results') : t('sockless_mail', 'Load older messages') }}
				</NcButton>
			</div>
		</div>
	</div>
</template>

<script setup>
import { mdiCheckboxBlankCircleOutline, mdiCheckCircle, mdiDeleteOutline, mdiEmailOpenOutline, mdiEmailOutline, mdiFlag, mdiFlagOutline, mdiFlagRemoveOutline, mdiFolderMoveOutline, mdiMagnify, mdiPaperclip, mdiReply } from '@mdi/js'
import { getCanonicalLocale, getFirstDay, n, t } from '@nextcloud/l10n'
import NcAvatar from '@nextcloud/vue/components/NcAvatar'
import NcButton from '@nextcloud/vue/components/NcButton'
import NcEmptyContent from '@nextcloud/vue/components/NcEmptyContent'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import NcNoteCard from '@nextcloud/vue/components/NcNoteCard'
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { accountColor, accountsState } from '../store/accounts.js'
import { askMove, remove, toggleFlagged, toggleSeen } from '../store/actions.js'
import { layout, readingPane } from '../store/layout.js'
import { loadMore, mailboxState, openMessage } from '../store/mailbox.js'
import { currentScope, searchMore, searchState as search } from '../store/search.js'
import { clearSelection, isSelected, pruneSelection, selectRange, selection, toggleRow } from '../store/selection.js'
import { loadMoreUnified, unifiedState as unified } from '../store/unified.js'

const state = mailboxState.messages
const unifiedShown = computed(() => !!mailboxState.selected.unified)
const scope = computed(() => currentScope())
const listName = computed(() => {
	if (unifiedShown.value) {
		return t('sockless_mail', 'All inboxes')
	}
	const { accountId, folderId } = mailboxState.selected
	return mailboxState.folders[accountId]?.list.find((f) => f.id === folderId)?.name ?? ''
})

/** What the list shows: search results, the unified inbox, or the folder's messages. */
const view = computed(() => {
	if (search.active) {
		return { loading: search.loading, error: search.error, loadingMore: search.loadingMore, canLoadMore: !!search.nextBefore, loadMore: searchMore }
	}
	if (unifiedShown.value) {
		return { loading: unified.loading, error: unified.error, loadingMore: unified.loadingMore, canLoadMore: !!unified.nextBefore, loadMore: loadMoreUnified }
	}
	return { loading: state.loading, error: state.error, loadingMore: state.loadingMore, canLoadMore: !!state.nextBefore, loadMore }
})

/** Every row carries its folder ({ accountId, folderId }), which is where its actions go. */
const rows = computed(() => {
	const hitKey = (h) => `${h.accountId}/${h.folderId}/${h.message.id}`
	if (search.active) {
		return search.hits.map((h) => ({ ...h, key: hitKey(h), location: scope.value === 'all' ? location(h) : null }))
	}
	if (unifiedShown.value) {
		return unified.list.map((h) => ({ ...h, key: hitKey(h), location: accountName(h.accountId) }))
	}
	const { accountId, folderId } = mailboxState.selected
	return state.list.map((message) => ({ accountId, folderId, message, key: `${accountId}/${folderId}/${message.id}`, location: null }))
})

watch(rows, (list) => pruneSelection(list))

const accountErrors = computed(() => (unifiedShown.value && !search.active ? unified.errors : []))

const countText = computed(() => {
	if (search.active) {
		return search.more
			? t('sockless_mail', 'First {count} results', { count: search.hits.length })
			: n('sockless_mail', '{count} result', '{count} results', search.total, { count: search.total })
	}
	const total = unifiedShown.value ? unified.total : state.total
	return n('sockless_mail', '{count} message', '{count} messages', total, { count: total })
})

const emptyText = computed(() => {
	if (search.active) {
		return t('sockless_mail', 'No messages found')
	}
	return unifiedShown.value ? t('sockless_mail', 'No messages in your inboxes') : t('sockless_mail', 'No messages in this folder')
})

function accountName(accountId) {
	return accountsState.accounts.find((a) => a.id === accountId)?.displayName ?? ''
}

/** "Account › Folder" for results from everywhere. */
function location(hit) {
	const folder = mailboxState.folders[hit.accountId]?.list.find((f) => f.id === hit.folderId)?.name
		?? (hit.folderFullName.toUpperCase() === 'INBOX' ? t('sockless_mail', 'Inbox') : hit.folderFullName)
	return `${accountName(hit.accountId)} › ${folder}`
}

function isOpen(row) {
	const open = mailboxState.open
	return row.message.id === open.id && row.accountId === open.accountId && row.folderId === open.folderId
}

/** Opens the message where it is listed; the selected folder stays as it is. */
function open(row) {
	openMessage(row, row.message.id)
}

/** Click opens; Ctrl/Cmd-click adds to the selection, Shift-click selects a range (as in Outlook). */
function onClick(row, event) {
	const openRow = rows.value.find(isOpen)
	if (event.shiftKey) {
		if (!selection.anchor && openRow) {
			selection.anchor = openRow.key
		}
		selectRange(rows.value, row)
	} else if (event.ctrlKey || event.metaKey) {
		// The open message is part of what the user sees as selected.
		if (!selection.rows.length && openRow && openRow !== row) {
			toggleRow(openRow)
		}
		toggleRow(row)
	} else {
		clearSelection()
		selection.anchor = row.key
		open(row)
	}
}

/** Enter opens; the arrow keys go to the next or previous message and open it when the reading pane shows it. */
function onKey(row, index, event) {
	if (event.target !== event.currentTarget) {
		return
	}
	if (event.key === 'Enter' || event.key === ' ') {
		event.preventDefault()
		onClick(row, event)
		return
	}
	const step = { ArrowDown: 1, ArrowUp: -1 }[event.key]
	const next = step && rows.value[index + step]
	if (!next) {
		return
	}
	event.preventDefault()
	event.currentTarget.parentElement.querySelector(`[data-key="${CSS.escape(next.key)}"]`)?.focus()
	if (readingPane.value !== 'off') {
		clearSelection()
		selection.anchor = next.key
		open(next)
	}
}

const timeFormat = new Intl.DateTimeFormat(getCanonicalLocale(), { hour: 'numeric', minute: '2-digit' })
const weekdayFormat = new Intl.DateTimeFormat(getCanonicalLocale(), { weekday: 'short' })
const dateFormat = new Intl.DateTimeFormat(getCanonicalLocale(), { day: 'numeric', month: 'short' })
const yearFormat = new Intl.DateTimeFormat(getCanonicalLocale(), { day: 'numeric', month: 'short', year: 'numeric' })
const weekdayNameFormat = new Intl.DateTimeFormat(getCanonicalLocale(), { weekday: 'long' })
const monthFormat = new Intl.DateTimeFormat(getCanonicalLocale(), { month: 'long' })
const monthYearFormat = new Intl.DateTimeFormat(getCanonicalLocale(), { month: 'long', year: 'numeric' })

const DAY = 24 * 60 * 60 * 1000

function startOfDay(date) {
	return new Date(date.getFullYear(), date.getMonth(), date.getDate())
}

/** Day boundaries for grouping, computed once per render of the list. */
const bounds = computed(() => {
	// Depends on the rows so a list shown across midnight regroups when it changes.
	void rows.value
	const today = startOfDay(new Date())
	const weekStart = new Date(today.getTime() - ((today.getDay() - getFirstDay() + 7) % 7) * DAY)
	return { today, yesterday: new Date(today.getTime() - DAY), weekStart, lastWeekStart: new Date(weekStart.getTime() - 7 * DAY) }
})

/** Outlook's grouping: Today, Yesterday, the days of this week, Last week, then months. */
function groupLabel(value) {
	const date = new Date(value)
	const { today, yesterday, weekStart, lastWeekStart } = bounds.value
	if (date >= today) {
		return t('sockless_mail', 'Today')
	}
	if (date >= yesterday) {
		return t('sockless_mail', 'Yesterday')
	}
	if (date >= weekStart) {
		return weekdayNameFormat.format(date)
	}
	if (date >= lastWeekStart) {
		return t('sockless_mail', 'Last week')
	}
	return (date.getFullYear() === today.getFullYear() ? monthFormat : monthYearFormat).format(date)
}

/** The group heading to show before a row: when it starts a new group. */
function groupOf(index) {
	const label = groupLabel(rows.value[index].message.date)
	return index === 0 || groupLabel(rows.value[index - 1].message.date) !== label ? label : null
}

/** Times today, weekdays this week, dates before. */
function formatDate(value) {
	const date = new Date(value)
	const { today, weekStart } = bounds.value
	if (date >= today) {
		return timeFormat.format(date)
	}
	if (date >= weekStart) {
		return `${weekdayFormat.format(date)} ${timeFormat.format(date)}`
	}
	return (date.getFullYear() === today.getFullYear() ? dateFormat : yearFormat).format(date)
}

function sender(message) {
	const from = message.from[0]
	return from ? (from.name || from.address) : t('sockless_mail', 'Unknown sender')
}

function subjectOf(message) {
	return message.subject || t('sockless_mail', '(no subject)')
}

/** Loads older messages when the end of the list scrolls into view; the button stays as a fallback. */
const scroller = ref(null)
const more = ref(null)
let observer = null

watch([scroller, more], ([root, target]) => {
	observer?.disconnect()
	observer = null
	if (root && target) {
		observer = new IntersectionObserver((entries) => {
			if (entries.some((e) => e.isIntersecting) && !view.value.loadingMore) {
				view.value.loadMore()
			}
		}, { root, rootMargin: '200px' })
		observer.observe(target)
	}
}, { flush: 'post' })

// A new list starts at the top.
watch(() => [mailboxState.selected.accountId, mailboxState.selected.folderId, mailboxState.selected.unified, search.active], () => {
	nextTick(() => scroller.value?.scrollTo({ top: 0 }))
})

onBeforeUnmount(() => observer?.disconnect())
</script>

<style scoped>
.message-list {
	display: flex;
	flex-direction: column;
	flex: 1;
	min-height: 0;
}

.message-list__header {
	display: flex;
	align-items: baseline;
	gap: 8px;
	padding: 12px 16px 6px;
	color: var(--color-text-maxcontrast);
}

.message-list__header h2 {
	margin: 0;
	font-size: 18px;
	font-weight: 600;
	color: var(--color-main-text);
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.message-list__hint {
	margin: 0 16px 6px;
	color: var(--color-text-maxcontrast);
	font-size: 0.9em;
}

.message-list__account-error {
	margin: 4px 12px;
}

.message-list__center {
	display: flex;
	justify-content: center;
	padding: 32px;
}

.message-list__rows {
	flex: 1;
	min-height: 0;
	overflow-y: auto;
}

.message-list__rows ul {
	margin: 0;
	padding: 0 0 8px;
	list-style: none;
}

.message-list__group {
	position: sticky;
	top: 0;
	z-index: 1;
	padding: 10px 16px 4px;
	font-size: 12px;
	font-weight: 600;
	color: var(--color-text-maxcontrast);
	background: var(--color-main-background);
}

/* One row: stripe | avatar | sender, date / subject / location, as a grid. */
.message-row {
	position: relative;
	display: grid;
	grid-template-columns: 4px 36px minmax(0, 1fr) auto;
	grid-template-areas:
		'stripe avatar from date'
		'stripe avatar subject subject'
		'stripe avatar location location';
	column-gap: 12px;
	align-items: center;
	padding: 8px 14px 8px 0;
	border-bottom: 1px solid var(--color-border);
	cursor: pointer;
	outline: none;
}

.message-row:hover {
	background: var(--color-background-hover);
}

.message-row:focus-visible {
	box-shadow: inset 0 0 0 2px var(--color-primary-element);
}

.message-row--open,
.message-row--selected,
.message-row--open:hover,
.message-row--selected:hover {
	background: var(--color-primary-element-light);
}

.message-row__stripe {
	grid-area: stripe;
	align-self: stretch;
	border-radius: 0 2px 2px 0;
}

.message-row--unread .message-row__stripe {
	background: var(--color-primary-element);
}

.message-row__avatar {
	grid-area: avatar;
	align-self: start;
	position: relative;
	display: grid;
	place-items: center;
	width: 36px;
	height: 36px;
}

.message-row__account {
	position: absolute;
	inset-inline-end: -3px;
	bottom: -3px;
	width: 12px;
	height: 12px;
	border: 2px solid var(--color-main-background);
	border-radius: 50%;
}

/* Checkbox over the avatar: on hover, and on every row while something is selected. */
.message-row__check {
	position: absolute;
	inset: 0;
	display: none;
	place-items: center;
	min-height: 0;
	margin: 0;
	padding: 0;
	border: 0;
	border-radius: 50%;
	--sm-button-background: var(--color-main-background);
	--sm-button-color: var(--color-text-maxcontrast);
	background: var(--color-main-background);
	color: var(--color-text-maxcontrast);
	cursor: pointer;
}

.message-row:hover .message-row__check,
.message-row--selecting .message-row__check,
.message-row__check:focus-visible {
	display: grid;
}

.message-row--selected .message-row__check {
	--sm-button-color: var(--color-primary-element);
	color: var(--color-primary-element);
}

.message-row__from {
	grid-area: from;
	min-width: 0;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.message-row__date {
	grid-area: date;
	font-size: 12px;
	font-variant-numeric: tabular-nums;
	color: var(--color-text-maxcontrast);
	white-space: nowrap;
}

.message-row__subject {
	grid-area: subject;
	display: flex;
	align-items: center;
	gap: 4px;
	min-width: 0;
	color: var(--color-main-text);
}

.message-row__subject :deep(.icon-vue) {
	flex: none;
	color: var(--color-text-maxcontrast);
}

.message-row__subject .message-row__flag {
	color: var(--color-error-text);
}

.message-row__subject-text {
	min-width: 0;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.message-row__location {
	grid-area: location;
	min-width: 0;
	font-size: 12.5px;
	color: var(--color-text-maxcontrast);
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.message-row--unread .message-row__from,
.message-row--unread .message-row__subject-text {
	font-weight: 600;
}

.message-row--unread .message-row__subject-text,
.message-row--unread .message-row__date {
	color: var(--color-primary-element);
}

.message-row--unread .message-row__date {
	font-weight: 600;
}

/* Quick actions on hover, over the date. */
.message-row__actions {
	position: absolute;
	top: 4px;
	inset-inline-end: 8px;
	display: none;
	gap: 2px;
	padding: 2px;
	border: 1px solid var(--color-border);
	border-radius: var(--border-radius-element, 8px);
	background: var(--color-main-background);
	box-shadow: 0 2px 8px rgb(0 0 0 / 10%);
}

.message-row:hover .message-row__actions,
.message-row:focus-within .message-row__actions {
	display: flex;
}

.message-row__action {
	display: grid;
	place-items: center;
	width: 30px;
	height: 28px;
	min-height: 0;
	margin: 0;
	padding: 0;
	border: 0;
	border-radius: 6px;
	background: none;
	color: var(--color-text-maxcontrast);
	cursor: pointer;
}

.message-row__action:hover,
.message-row__action:focus-visible {
	--sm-button-background: var(--color-background-hover);
	--sm-button-color: var(--color-main-text);
	background: var(--color-background-hover);
	color: var(--color-main-text);
}

/* Compact: two lines, small avatars, no location line. */
.message-list--compact .message-row {
	grid-template-columns: 4px 24px minmax(0, 1fr) auto;
	grid-template-areas:
		'stripe avatar from date'
		'stripe avatar subject subject';
	column-gap: 10px;
	padding-block: 4px;
}

.message-list--compact .message-row__avatar {
	width: 24px;
	height: 24px;
}

.message-list--compact .message-row__account {
	width: 9px;
	height: 9px;
}

.message-list--compact .message-row__location {
	display: none;
}

.message-list--compact .message-row__actions {
	top: 2px;
}

.message-list__more {
	padding: 8px 12px;
}

@media (hover: none) {
	.message-row__actions {
		display: none !important;
	}
}
</style>
