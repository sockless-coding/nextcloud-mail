<template>
	<div class="message-list" :class="{ 'message-list--compact': layout.density === 'compact' }">
		<header class="message-list__header">
			<h2>{{ searching ? t('sockless_mail', 'Search') : listName }}</h2>
			<span v-if="!view.loading && !view.error">{{ countText }}</span>
			<button v-if="rows.length"
				class="message-list__select"
				:class="{ 'message-list__select--active': selection.mode }"
				:aria-pressed="String(selection.mode)"
				:title="t('sockless_mail', 'Select messages by clicking them')"
				@click="selection.mode ? endSelectionMode() : (selection.mode = true)">
				<NcIconSvgWrapper :path="mdiCheckboxMultipleMarkedCircleOutline" :size="18" />
				{{ t('sockless_mail', 'Select') }}
			</button>
		</header>
		<!-- A smart folder is a filter already. -->
		<div v-if="!unifiedView?.filter" class="message-list__filters" role="group" :aria-label="t('sockless_mail', 'Show')">
			<button v-for="option in FILTERS"
				:key="option.value"
				class="message-list__filter"
				:class="{ 'message-list__filter--active': search.filter === option.value }"
				:aria-pressed="String(search.filter === option.value)"
				@click="setFilter(option.value)">
				{{ option.label }}
			</button>
		</div>
		<div v-if="selection.mode || selection.rows.length" class="message-list__selection">
			<span class="message-list__selection-count" aria-live="polite">
				{{ n('sockless_mail', '{count} selected', '{count} selected', selection.rows.length, { count: selection.rows.length }) }}
			</span>
			<button class="message-list__selection-button" :disabled="selection.rows.length === rows.length" @click="selectAll">
				{{ t('sockless_mail', 'All') }}
			</button>
			<button class="message-list__selection-button" :disabled="!selection.rows.length" @click="clearSelection">
				{{ t('sockless_mail', 'None') }}
			</button>
			<button class="message-list__selection-button message-list__selection-button--done" @click="endSelectionMode">
				{{ t('sockless_mail', 'Done') }}
			</button>
		</div>
		<p v-if="searching && search.source === 'cache'" class="message-list__hint">
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
			:name="searching ? t('sockless_mail', 'Search failed') : t('sockless_mail', 'Could not load the messages')"
			:description="view.error.message" />
		<NcEmptyContent v-else-if="rows.length === 0"
			:name="emptyText">
			<template #icon>
				<NcIconSvgWrapper :path="searching ? mdiMagnify : mdiEmailOpenOutline" />
			</template>
		</NcEmptyContent>
		<div v-else ref="scroller" class="message-list__rows">
			<ul :aria-label="searching ? t('sockless_mail', 'Search results') : t('sockless_mail', 'Messages')">
				<template v-for="(row, index) in rows" :key="row.key">
					<li v-if="groupOf(index)" class="message-list__group" aria-hidden="true">
						{{ groupOf(index) }}
					</li>
					<li class="message-row"
						:class="{
							'message-row--unread': !row.message.seen,
							'message-row--open': isOpen(row),
							'message-row--selected': isSelected(row),
						}"
						:data-key="row.key"
						tabindex="0"
						:aria-current="isOpen(row) ? 'true' : undefined"
						@click="onClick(row, $event)"
						@keydown="onKey(row, index, $event)"
						@pointerdown="startPress(row, $event)"
						@pointerup="cancelPress"
						@pointercancel="cancelPress"
						@pointerleave="cancelPress"
						@contextmenu="pressed && $event.preventDefault()">
						<span class="message-row__stripe" />
						<span class="message-row__avatar">
							<NcAvatar :display-name="sender(row.message)"
								:url="bimiUrl(row.message)"
								is-no-user
								disable-menu
								disable-tooltip
								:size="layout.density === 'compact' ? 24 : 36" />
							<span v-if="unifiedShown || search.source === 'cache'"
								class="message-row__account"
								:style="{ background: accountColor(row.accountId) }"
								:title="accountName(row.accountId)" />
							<span v-if="isSelected(row)" class="message-row__check">
								<NcIconSvgWrapper :path="mdiCheck" :size="layout.density === 'compact' ? 16 : 22" />
							</span>
						</span>
						<span v-if="isSelected(row)" class="hidden-visually">{{ t('sockless_mail', 'Selected') }}</span>
						<span class="message-row__from">{{ sender(row.message) }}</span>
						<span class="message-row__date">{{ formatDate(row.message.date) }}</span>
						<span class="message-row__subject">
							<NcIconSvgWrapper v-if="row.message.flagged" :path="mdiFlag" :size="16" class="message-row__flag" :name="t('sockless_mail', 'Flagged')" />
							<NcIconSvgWrapper v-if="row.message.answered" :path="mdiReply" :size="16" :name="t('sockless_mail', 'Answered')" />
							<NcIconSvgWrapper v-if="row.message.hasAttachments" :path="mdiPaperclip" :size="16" :name="t('sockless_mail', 'Has attachments')" />
							<span class="message-row__subject-text">{{ subjectOf(row.message) }}</span>
						</span>
						<span v-if="row.location || row.message.preview" class="message-row__preview">
							<span v-if="row.location" class="message-row__location">{{ row.location }}</span>
							{{ row.message.preview }}
						</span>
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
					{{ searching ? t('sockless_mail', 'More results') : t('sockless_mail', 'Load older messages') }}
				</NcButton>
			</div>
		</div>
	</div>
</template>

<script setup>
import { mdiCheck, mdiCheckboxMultipleMarkedCircleOutline, mdiDeleteOutline, mdiEmailOpenOutline, mdiEmailOutline, mdiFlag, mdiFlagOutline, mdiFlagRemoveOutline, mdiFolderMoveOutline, mdiMagnify, mdiPaperclip, mdiReply } from '@mdi/js'
import { getCanonicalLocale, getFirstDay, n, t } from '@nextcloud/l10n'
import NcAvatar from '@nextcloud/vue/components/NcAvatar'
import NcButton from '@nextcloud/vue/components/NcButton'
import NcEmptyContent from '@nextcloud/vue/components/NcEmptyContent'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import NcNoteCard from '@nextcloud/vue/components/NcNoteCard'
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { bimiUrl } from '../bimi.js'
import { accountColor, accountsState } from '../store/accounts.js'
import { askMove, remove, toggleFlagged, toggleSeen } from '../store/actions.js'
import { layout, readingPane } from '../store/layout.js'
import { loadMore, mailboxState, openMessage } from '../store/mailbox.js'
import { searchMore, searchState as search, setFilter } from '../store/search.js'
import { clearSelection, endSelectionMode, extendSelection, isSelected, pruneSelection, selectRange, selection, toggleRow } from '../store/selection.js'
import { loadMoreUnified, unifiedState as unified, viewOf } from '../store/unified.js'

const FILTERS = [
	{ value: '', label: t('sockless_mail', 'All') },
	{ value: 'unread', label: t('sockless_mail', 'Unread') },
	{ value: 'flagged', label: t('sockless_mail', 'Flagged') },
	{ value: 'attachments', label: t('sockless_mail', 'Attachments') },
]

const state = mailboxState.messages
/** Words were searched for (not only a filter). */
const searching = computed(() => search.active && search.query.trim() !== '')
const unifiedShown = computed(() => !!mailboxState.selected.unified)
const unifiedView = computed(() => viewOf(mailboxState.selected.unified))
const listName = computed(() => {
	if (unifiedShown.value) {
		return unifiedView.value?.name ?? ''
	}
	const { accountId, folderId } = mailboxState.selected
	return mailboxState.folders[accountId]?.list.find((f) => f.id === folderId)?.name ?? ''
})

/** What the list shows: search results, a unified view, or the folder's messages. */
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
		return search.hits.map((h) => ({
			...h,
			key: hitKey(h),
			location: search.source === 'cache' ? location(h) : search.source === 'unified' ? accountName(h.accountId) : null,
		}))
	}
	if (unifiedShown.value) {
		// Flagged spans several folders of each account: say which.
		const where = unifiedView.value?.roles.length > 1 ? location : (h) => accountName(h.accountId)
		return unified.list.map((h) => ({ ...h, key: hitKey(h), location: where(h) }))
	}
	const { accountId, folderId } = mailboxState.selected
	return state.list.map((message) => ({ accountId, folderId, message, key: `${accountId}/${folderId}/${message.id}`, location: null }))
})

watch(rows, (list) => pruneSelection(list))

const accountErrors = computed(() => (unifiedShown.value && !search.active ? unified.errors : []))

const countText = computed(() => {
	if (search.active) {
		if (search.more) {
			return t('sockless_mail', 'First {count} results', { count: search.hits.length })
		}
		// Not every match is counted for attachments (see the API); say nothing rather than guess.
		if (search.total === null) {
			return ''
		}
		return searching.value
			? n('sockless_mail', '{count} result', '{count} results', search.total, { count: search.total })
			: n('sockless_mail', '{count} message', '{count} messages', search.total, { count: search.total })
	}
	const total = unifiedShown.value ? unified.total : state.total
	// A smart folder does not always count every match.
	if (total === null) {
		return ''
	}
	return n('sockless_mail', '{count} message', '{count} messages', total, { count: total })
})

const emptyText = computed(() => {
	if (searching.value) {
		return t('sockless_mail', 'No messages found')
	}
	if (search.active) {
		return {
			unread: t('sockless_mail', 'No unread messages'),
			flagged: t('sockless_mail', 'No flagged messages'),
			attachments: t('sockless_mail', 'No messages with attachments'),
		}[search.filter]
	}
	if (unifiedShown.value) {
		return {
			inbox: t('sockless_mail', 'No messages in your inboxes'),
			drafts: t('sockless_mail', 'No drafts'),
			sent: t('sockless_mail', 'No sent messages'),
			archive: t('sockless_mail', 'No archived messages'),
			junk: t('sockless_mail', 'No junk'),
			trash: t('sockless_mail', 'Trash is empty'),
			unread: t('sockless_mail', 'No unread messages'),
			flagged: t('sockless_mail', 'No flagged messages'),
		}[mailboxState.selected.unified]
	}
	return t('sockless_mail', 'No messages in this folder')
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

/**
 * Click opens; Ctrl/Cmd-click adds to the selection, Shift-click selects a range (as in Outlook).
 * In selection mode a plain click toggles the row.
 */
function onClick(row, event) {
	if (pressed) {
		// The long press already selected it.
		pressed = false
		return
	}
	const openRow = rows.value.find(isOpen)
	if (event.shiftKey) {
		if (!selection.anchor && openRow) {
			selection.anchor = openRow.key
		}
		selectRange(rows.value, row)
	} else if (selection.mode) {
		toggleRow(row)
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

function selectAll() {
	selection.rows = [...rows.value]
}

/**
 * Enter opens and Space selects; the arrow keys go to the next or previous message and open it
 * when the reading pane shows it, and with Shift extend the selection instead.
 */
function onKey(row, index, event) {
	if (event.target !== event.currentTarget) {
		return
	}
	if (event.key === 'Enter') {
		event.preventDefault()
		onClick(row, event)
		return
	}
	if (event.key === ' ') {
		event.preventDefault()
		event.shiftKey ? selectRange(rows.value, row) : toggleRow(row)
		return
	}
	if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'a') {
		event.preventDefault()
		selectAll()
		return
	}
	const step = { ArrowDown: 1, ArrowUp: -1 }[event.key]
	const next = step && rows.value[index + step]
	if (!next) {
		return
	}
	event.preventDefault()
	event.currentTarget.parentElement.querySelector(`[data-key="${CSS.escape(next.key)}"]`)?.focus()
	if (event.shiftKey) {
		if (!selection.rows.length || !selection.anchor) {
			selection.anchor = row.key
		}
		extendSelection(rows.value, next)
		return
	}
	if (selection.mode) {
		return
	}
	if (readingPane.value !== 'off') {
		clearSelection()
		selection.anchor = next.key
		open(next)
	}
}

/** A long press on a touch screen starts selection mode with that row selected. */
const LONG_PRESS = 500
let pressTimer = null
let pressed = false

function startPress(row, event) {
	pressed = false
	if (event.pointerType !== 'touch' || event.target.closest('button')) {
		return
	}
	pressTimer = setTimeout(() => {
		pressed = true
		selection.mode = true
		if (!isSelected(row)) {
			toggleRow(row)
		}
	}, LONG_PRESS)
}

function cancelPress() {
	clearTimeout(pressTimer)
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

.message-list__select {
	--sm-button-background: transparent;
	--sm-button-color: var(--color-text-maxcontrast);
	display: inline-flex;
	align-items: center;
	gap: 4px;
	align-self: center;
	min-height: 0;
	height: 28px;
	margin: 0 0 0 auto;
	padding: 0 10px;
	border: 0;
	border-radius: 14px;
	background: transparent;
	color: var(--color-text-maxcontrast);
	font: inherit;
	font-size: 13px;
	cursor: pointer;
}

.message-list__select:hover {
	--sm-button-background: var(--color-background-hover);
	background: var(--color-background-hover);
}

.message-list__select--active,
.message-list__select--active:hover {
	--sm-button-background: var(--color-primary-element-light);
	--sm-button-color: var(--color-primary-element-light-text);
	background: var(--color-primary-element-light);
	color: var(--color-primary-element-light-text);
}

.message-list__select:focus-visible,
.message-list__selection-button:focus-visible {
	outline: 2px solid var(--color-primary-element);
	outline-offset: 1px;
}

.message-list__selection {
	display: flex;
	align-items: center;
	gap: 4px;
	margin: 0 12px 8px;
	padding: 4px 4px 4px 12px;
	border-radius: var(--border-radius-large);
	background: var(--color-primary-element-light);
	color: var(--color-primary-element-light-text);
}

.message-list__selection-count {
	flex: 1;
	font-weight: 600;
}

.message-list__selection-button {
	--sm-button-background: transparent;
	--sm-button-color: var(--color-primary-element-light-text);
	min-height: 0;
	height: 28px;
	margin: 0;
	padding: 0 10px;
	border: 0;
	border-radius: 14px;
	background: transparent;
	color: var(--color-primary-element-light-text);
	font: inherit;
	cursor: pointer;
}

.message-list__selection-button:hover:not(:disabled) {
	--sm-button-background: var(--color-primary-element-light-hover);
	background: var(--color-primary-element-light-hover);
}

.message-list__selection-button:disabled {
	opacity: 0.5;
	cursor: default;
}

.message-list__selection-button--done,
.message-list__selection-button--done:hover:not(:disabled) {
	--sm-button-background: var(--color-primary-element);
	--sm-button-color: var(--color-primary-element-text);
	background: var(--color-primary-element);
	color: var(--color-primary-element-text);
	font-weight: 600;
}

.message-list__filters {
	display: flex;
	flex-wrap: wrap;
	gap: 6px;
	padding: 0 16px 8px;
}

.message-list__filter {
	--sm-button-background: transparent;
	min-height: 0;
	height: 26px;
	margin: 0;
	padding: 0 12px;
	border: 1px solid var(--color-border-dark, var(--color-border));
	border-radius: 13px;
	background: transparent;
	color: var(--color-text-maxcontrast);
	font: inherit;
	font-size: 13px;
	cursor: pointer;
}

.message-list__filter:hover {
	--sm-button-background: var(--color-background-hover);
	background: var(--color-background-hover);
}

.message-list__filter:focus-visible {
	outline: 2px solid var(--color-primary-element);
	outline-offset: 1px;
}

.message-list__filter--active,
.message-list__filter--active:hover {
	--sm-button-background: var(--color-primary-element-light);
	--sm-button-color: var(--color-primary-element-light-text);
	border-color: transparent;
	background: var(--color-primary-element-light);
	color: var(--color-primary-element-light-text);
	font-weight: 600;
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
		'stripe avatar preview preview';
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

/* Selected rows show a check in place of the avatar. */
.message-row__check {
	position: absolute;
	inset: 0;
	display: grid;
	place-items: center;
	border-radius: 50%;
	background: var(--color-primary-element);
	color: var(--color-primary-element-text);
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
	min-width: 0;
	min-height: 0;
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

.message-row__preview {
	grid-area: preview;
	min-width: 0;
	font-size: 13px;
	color: var(--color-text-maxcontrast);
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

/* Where a message from a mixed list lives, ahead of its text. */
.message-row__location {
	font-weight: 600;
}

.message-row__location:not(:last-child)::after {
	content: ' \00B7 ';
	font-weight: normal;
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

/* Compact: two lines, small avatars, no preview line. */
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

.message-list--compact .message-row__preview {
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
