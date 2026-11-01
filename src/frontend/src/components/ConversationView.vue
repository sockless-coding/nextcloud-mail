<template>
	<section class="conversation" :class="{ 'conversation--branched': branched }" :aria-label="t('sockless_mail', 'Conversation')">
		<div class="conversation__bar">
			<span class="conversation__summary">
				{{ n('sockless_mail', '{count} message', '{count} messages', entries.length, { count: entries.length }) }}
				· {{ participants }}
				<template v-if="conversationState.truncated">
					· {{ t('sockless_mail', 'older messages left out') }}
				</template>
			</span>
			<button type="button" class="conversation__text-button" @click="allExpanded ? collapseAll() : expandAll()">
				{{ allExpanded ? t('sockless_mail', 'Collapse all') : t('sockless_mail', 'Expand all') }}
			</button>
		</div>

		<ol class="conversation__list">
			<li v-for="(entry, index) in entries"
				:key="entry.key"
				:ref="(el) => setElement(entry.key, el)"
				class="conversation__entry"
				:class="{
					'conversation__entry--expanded': entry.expanded,
					'conversation__entry--flash': flashing === entry.key,
				}"
				:style="{ '--conversation-lane': laneColor(entry) }"
				:data-message-id="entry.message.id">
				<div v-if="entry.key === firstFresh" class="conversation__new" role="separator">
					<span>{{ t('sockless_mail', 'New since you last looked') }}</span>
				</div>

				<div v-if="markers[index].show" class="conversation__markers">
					<button v-if="markers[index].parent"
						type="button"
						class="conversation__reply-to"
						:title="t('sockless_mail', 'Show the message this one answers')"
						@click="focusEntry(markers[index].parent)">
						<NcIconSvgWrapper :path="mdiReply" :size="16" />
						<span class="conversation__reply-to-text">
							{{ replyingTo(markers[index].parent) }}
							<template v-if="markers[index].siblings > 1">
								· {{ n('sockless_mail', 'one of {count} answers to it', 'one of {count} answers to it', markers[index].siblings, { count: markers[index].siblings }) }}
							</template>
						</span>
						<span v-if="markers[index].parent.message.preview" class="conversation__reply-to-snippet">
							“{{ snippet(markers[index].parent.message.preview) }}”
						</span>
					</button>
					<span v-for="person in markers[index].changes.added"
						:key="`+${person.address}`"
						class="conversation__chip conversation__chip--added"
						:title="person.address">
						{{ t('sockless_mail', '+ {name} added', { name: person.name || person.address }) }}
					</span>
					<span v-for="person in markers[index].changes.removed"
						:key="`-${person.address}`"
						class="conversation__chip conversation__chip--removed"
						:title="person.address">
						{{ t('sockless_mail', '− {name} left out', { name: person.name || person.address }) }}
					</span>
					<span v-if="markers[index].folder" class="conversation__chip conversation__chip--folder">
						<NcIconSvgWrapper :path="mdiFolderOutline" :size="14" inline />
						{{ markers[index].folder }}
					</span>
				</div>

				<button v-if="!entry.expanded"
					type="button"
					class="conversation__row"
					:class="{ 'conversation__row--unread': !entry.message.seen }"
					:aria-expanded="'false'"
					@click="expandEntry(entry)">
					<NcAvatar :display-name="senderOf(entry)"
						:url="bimiUrl(entry.message)"
						is-no-user
						disable-menu
						:size="28" />
					<strong class="conversation__sender">{{ senderOf(entry) }}</strong>
					<span class="conversation__preview">{{ entry.message.preview }}</span>
					<NcIconSvgWrapper v-if="entry.message.hasAttachments" :path="mdiPaperclip" :size="16" :name="t('sockless_mail', 'Has attachments')" />
					<span class="conversation__date">
						<NcDateTime :timestamp="Date.parse(entry.message.date)" :format="{ dateStyle: 'medium', timeStyle: 'short' }" :relative-time="false" />
					</span>
				</button>
				<template v-else>
					<p v-if="errorOf(entry)" class="conversation__error">
						{{ t('sockless_mail', 'Could not open the message: {error}', { error: errorOf(entry) }) }}
						<button v-if="!isOpenEntry(entry)" type="button" class="conversation__text-button" @click="retry(entry)">
							{{ t('sockless_mail', 'Try again') }}
						</button>
					</p>
					<div v-else-if="!fullOf(entry)" class="conversation__loading">
						<NcLoadingIcon :size="28" />
					</div>
					<MessageCard v-else
						:where="entry.where"
						:message="fullOf(entry)"
						collapsible
						fold-quotes
						@collapse="collapseEntry(entry)"
						@show-images="showImages(entry)" />
				</template>
			</li>
		</ol>

		<button v-if="freshLeft.length"
			type="button"
			class="conversation__jump"
			@click="jumpToNew">
			<NcIconSvgWrapper :path="mdiArrowDown" :size="16" />
			{{ n('sockless_mail', '{count} new', '{count} new', freshLeft.length, { count: freshLeft.length }) }}
		</button>

		<ReplyGuardDialog v-if="conversationState.guard" :key="conversationState.guard.entryKey" />
	</section>
</template>

<script setup>
import { mdiArrowDown, mdiFolderOutline, mdiPaperclip, mdiReply } from '@mdi/js'
import { getCanonicalLocale, n, t } from '@nextcloud/l10n'
import NcAvatar from '@nextcloud/vue/components/NcAvatar'
import NcDateTime from '@nextcloud/vue/components/NcDateTime'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { bimiUrl } from '../bimi.js'
import {
	collapseAll, collapseEntry, conversationState, expandAll, expandEntry, focusEntry, fromMe, isOpenEntry, participantChanges,
} from '../store/conversation.js'
import { mailboxState, openMessage } from '../store/mailbox.js'
import MessageCard from './MessageCard.vue'
import ReplyGuardDialog from './ReplyGuardDialog.vue'

/**
 * The open message's conversation, oldest first (store/conversation.js). Read messages are one
 * line each; the open, unread and newest ones are expanded with their quotes folded away. So that
 * nothing said is missed:
 * - a line marks where the new messages start, and "↓ n new" steps through them;
 * - a message that answers another than the one above it says which one, and messages that
 *   answer the same one share a coloured rail per branch;
 * - people added or left out compared with the message answered show as chips;
 * - messages kept in another folder (the user's replies in Sent) say where they are.
 */

const entries = computed(() => conversationState.entries)

/** Message-ID → entry. */
const byId = computed(() => new Map(entries.value.filter((e) => e.messageIdHeader).map((e) => [e.messageIdHeader, e])))

function parentOf(entry) {
	return entry.parentIdHeader ? byId.value.get(entry.parentIdHeader) ?? null : null
}

/** Per entry: the message it answers when that is not the one above it, chips, and its folder when not the open one's. */
const markers = computed(() => entries.value.map((entry, index) => {
	const parent = parentOf(entry)
	const siblings = parent ? entries.value.filter((e) => parentOf(e) === parent).length : 0
	const shownParent = parent && entries.value[index - 1] !== parent ? parent : null
	const changes = participantChanges(entry)
	const folder = entry.where.folderId !== mailboxState.open.folderId ? folderLabel(entry) : null
	return {
		parent: shownParent,
		siblings,
		changes,
		folder,
		show: !!(shownParent || changes.added.length || changes.removed.length || folder),
	}
}))

/**
 * Branches: the first answer to a message continues its line, every further answer starts a
 * line of its own, which its answers continue. Line 0 is the main one.
 */
const lanes = computed(() => {
	const lane = new Map()
	let next = 1
	for (const entry of entries.value) {
		const parent = parentOf(entry)
		if (!parent) {
			lane.set(entry.key, 0)
			continue
		}
		const first = entries.value.find((e) => parentOf(e) === parent)
		lane.set(entry.key, first === entry ? (lane.get(parent.key) ?? 0) : next++)
	}
	return lane
})
const branched = computed(() => [...lanes.value.values()].some((l) => l > 0))

const LANE_COLORS = ['#0082c9', '#c9302c', '#2e7d32', '#8e44ad', '#d35400', '#00897b']
function laneColor(entry) {
	const lane = lanes.value.get(entry.key) ?? 0
	return lane === 0 ? 'var(--color-border-dark)' : LANE_COLORS[(lane - 1) % LANE_COLORS.length]
}

function folderLabel(entry) {
	switch (entry.folderRole) {
	case 'inbox': return t('sockless_mail', 'Inbox')
	case 'sent': return t('sockless_mail', 'Sent')
	case 'archive': return t('sockless_mail', 'Archive')
	case 'drafts': return t('sockless_mail', 'Drafts')
	case 'trash': return t('sockless_mail', 'Trash')
	case 'junk': return t('sockless_mail', 'Junk')
	default: {
		const folder = mailboxState.folders[entry.where.accountId]?.list.find((f) => f.id === entry.where.folderId)
		return folder?.name ?? entry.folderName
	}
	}
}

function senderOf(entry) {
	if (fromMe(entry)) {
		return t('sockless_mail', 'Me')
	}
	const from = entry.message.from[0]
	return from?.name || from?.address || t('sockless_mail', 'Unknown sender')
}

const participants = computed(() => {
	const names = [...new Set(entries.value.map(senderOf))]
	return names.length > 5 ? `${names.slice(0, 4).join(', ')} +${names.length - 4}` : names.join(', ')
})

const whenFormat = new Intl.DateTimeFormat(getCanonicalLocale(), { weekday: 'short', day: 'numeric', month: 'short', hour: 'numeric', minute: '2-digit' })

function replyingTo(parent) {
	const when = whenFormat.format(new Date(parent.message.date))
	return fromMe(parent)
		? t('sockless_mail', 'Answers your message from {when}', { when })
		: t('sockless_mail', 'Answers {name}’s message from {when}', { name: senderOf(parent), when })
}

function snippet(text) {
	return text.length > 90 ? `${text.slice(0, 90).trimEnd()}…` : text
}

function fullOf(entry) {
	return isOpenEntry(entry) ? mailboxState.open.message : entry.full
}

function errorOf(entry) {
	return isOpenEntry(entry) ? mailboxState.open.error?.message : entry.error
}

function retry(entry) {
	entry.error = null
	expandEntry(entry)
}

const allExpanded = computed(() => entries.value.every((e) => e.expanded))

function showImages(entry) {
	if (isOpenEntry(entry)) {
		openMessage(entry.where, entry.message.id, true)
	} else {
		expandEntry(entry, true)
	}
}

/** The first message that was new when the conversation opened; the line goes above it. */
const firstFresh = computed(() => entries.value.find((e) => conversationState.fresh.includes(e.key))?.key ?? null)

const elements = new Map()
function setElement(key, el) {
	if (el) {
		elements.set(key, el)
	} else {
		elements.delete(key)
	}
}

const flashing = ref(null)
let flashTimer = 0

function bringIntoSight(key, flash) {
	const el = elements.get(key)
	if (!el) {
		return
	}
	el.scrollIntoView({ block: 'start', behavior: 'smooth' })
	if (flash) {
		flashing.value = key
		clearTimeout(flashTimer)
		flashTimer = setTimeout(() => { flashing.value = null }, 1600)
	}
}

/** New messages not stepped to yet with "↓ n new". */
const visited = ref(new Set())
const freshLeft = computed(() => conversationState.fresh.filter((k) => !visited.value.has(k) && entries.value.some((e) => e.key === k)))

function jumpToNew() {
	const key = freshLeft.value[0]
	visited.value = new Set([...visited.value, key])
	const entry = entries.value.find((e) => e.key === key)
	if (entry) {
		focusEntry(entry)
	}
}

watch(() => conversationState.focus, async (focus) => {
	if (focus) {
		await nextTick()
		bringIntoSight(focus.key, true)
	}
})

/**
 * When a conversation opens: the open message when it was chosen among others, or, when it is the
 * newest (a conversation opened as a whole), the first new one.
 */
watch(() => conversationState.entries.length > 0 && conversationState.key, async (key, before) => {
	if (!key || key === before) {
		return
	}
	visited.value = new Set()
	const open = entries.value.find(isOpenEntry)
	const target = open === entries.value[entries.value.length - 1] && firstFresh.value ? firstFresh.value : open?.key
	if (!target || target === entries.value[0].key) {
		return
	}
	await nextTick()
	bringIntoSight(target, false)
	// Cards above it grow as their text arrives.
	setTimeout(() => bringIntoSight(target, false), 400)
}, { immediate: true })

onBeforeUnmount(() => clearTimeout(flashTimer))
</script>

<style scoped>
.conversation {
	position: relative;
}

.conversation__bar {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 12px;
	margin-bottom: 8px;
	color: var(--color-text-maxcontrast);
	font-size: 13px;
}

.conversation__list {
	margin: 0;
	padding: 0;
	list-style: none;
}

.conversation__entry {
	margin-bottom: 6px;
	border-radius: var(--border-radius-element, 8px);
	scroll-margin-top: 12px;
	transition: background-color 0.4s;
}

.conversation--branched .conversation__entry {
	padding-inline-start: 10px;
	border-inline-start: 3px solid var(--conversation-lane);
}

.conversation__entry--expanded {
	padding-block: 8px;
	border-bottom: 1px solid var(--color-border);
}

.conversation__entry--flash {
	background: var(--color-primary-element-light);
}

.conversation__new {
	display: flex;
	align-items: center;
	gap: 8px;
	margin: 12px 0 8px;
	color: var(--color-primary-element);
	font-size: 12px;
	font-weight: 600;
}

.conversation__new::before,
.conversation__new::after {
	content: "";
	flex: 1;
	border-top: 1px solid var(--color-primary-element);
}

.conversation__markers {
	display: flex;
	flex-wrap: wrap;
	align-items: center;
	gap: 4px 6px;
	margin: 4px 0;
	font-size: 12px;
}

.conversation__reply-to {
	--sm-button-background: transparent;
	display: inline-flex;
	align-items: center;
	gap: 6px;
	max-width: 100%;
	min-height: 0;
	margin: 0;
	padding: 2px 8px;
	border: 0;
	border-radius: var(--border-radius-element, 8px);
	background: var(--color-background-hover);
	color: var(--color-main-text);
	font: inherit;
	text-align: start;
	cursor: pointer;
}

.conversation__reply-to:hover {
	--sm-button-background: var(--color-background-dark);
	background: var(--color-background-dark);
}

.conversation__reply-to-text {
	font-weight: 600;
	white-space: nowrap;
}

.conversation__reply-to-snippet {
	min-width: 0;
	overflow: hidden;
	color: var(--color-text-maxcontrast);
	text-overflow: ellipsis;
	white-space: nowrap;
}

.conversation__chip {
	display: inline-flex;
	align-items: center;
	gap: 4px;
	height: 22px;
	padding: 0 8px;
	border-radius: 11px;
	background: var(--color-background-hover);
	color: var(--color-text-maxcontrast);
	white-space: nowrap;
}

.conversation__chip--added {
	color: var(--color-success-text);
}

.conversation__chip--removed {
	color: var(--color-warning-text);
}

.conversation__row {
	--sm-button-background: transparent;
	display: flex;
	align-items: center;
	gap: 10px;
	width: 100%;
	min-height: 0;
	margin: 0;
	padding: 6px 8px;
	border: 0;
	border-radius: var(--border-radius-element, 8px);
	background: none;
	color: var(--color-main-text);
	font: inherit;
	text-align: start;
	cursor: pointer;
}

.conversation__row:hover {
	--sm-button-background: var(--color-background-hover);
	background: var(--color-background-hover);
}

.conversation__row:focus-visible,
.conversation__reply-to:focus-visible,
.conversation__text-button:focus-visible,
.conversation__jump:focus-visible {
	outline: 2px solid var(--color-primary-element);
	outline-offset: 1px;
}

.conversation__sender {
	flex: none;
	max-width: 30%;
	overflow: hidden;
	font-weight: 500;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.conversation__row--unread .conversation__sender {
	font-weight: 700;
}

.conversation__preview {
	flex: 1;
	min-width: 0;
	overflow: hidden;
	color: var(--color-text-maxcontrast);
	text-overflow: ellipsis;
	white-space: nowrap;
}

.conversation__date {
	flex: none;
	color: var(--color-text-maxcontrast);
	font-size: 12px;
}

.conversation__loading {
	display: flex;
	justify-content: center;
	padding: 24px;
}

.conversation__error {
	padding: 8px;
	color: var(--color-error-text);
}

.conversation__text-button {
	--sm-button-background: transparent;
	min-height: 0;
	margin: 0;
	padding: 2px 8px;
	border: 0;
	border-radius: var(--border-radius-element, 8px);
	background: none;
	color: var(--color-primary-element);
	font: inherit;
	font-weight: 600;
	cursor: pointer;
}

.conversation__text-button:hover {
	--sm-button-background: var(--color-primary-element-light);
	background: var(--color-primary-element-light);
}

.conversation__jump {
	position: sticky;
	bottom: 12px;
	display: flex;
	align-items: center;
	gap: 4px;
	margin: 8px 0 0 auto;
	min-height: 0;
	padding: 4px 12px;
	border: 0;
	border-radius: 16px;
	background: var(--color-primary-element);
	color: var(--color-primary-element-text);
	box-shadow: 0 2px 6px rgba(0, 0, 0, 0.2);
	font: inherit;
	font-weight: 600;
	cursor: pointer;
}
</style>
