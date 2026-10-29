<template>
	<section class="invitation" :class="{ 'invitation--cancelled': cancelled }" :aria-label="title">
		<NcLoadingIcon v-if="loading" :size="24" />
		<NcNoteCard v-else-if="loadError" type="warning" :text="loadError" />
		<template v-else-if="invitation">
			<div class="invitation__date" aria-hidden="true">
				<span class="invitation__month">{{ badge.month }}</span>
				<span class="invitation__day">{{ badge.day }}</span>
				<span class="invitation__weekday">{{ badge.weekday }}</span>
			</div>

			<div class="invitation__main">
				<p class="invitation__kind">
					<NcIconSvgWrapper :path="kindIcon" :size="16" inline />
					{{ title }}
				</p>
				<h3 class="invitation__summary">
					{{ event.summary }}
				</h3>
				<p class="invitation__line">
					<NcIconSvgWrapper :path="mdiClockOutline" :size="16" inline />
					<span>{{ when }}</span>
				</p>
				<p v-if="repeats" class="invitation__line">
					<NcIconSvgWrapper :path="mdiRepeat" :size="16" inline />
					<span>{{ repeats }}</span>
				</p>
				<p v-if="event.location" class="invitation__line">
					<NcIconSvgWrapper :path="mdiMapMarkerOutline" :size="16" inline />
					<span>{{ event.location }}</span>
				</p>
				<p v-if="invitation.organizer && !invitation.isOrganizer" class="invitation__line">
					<NcIconSvgWrapper :path="mdiAccountTieOutline" :size="16" inline />
					<span>{{ t('sockless_mail', 'Organized by {name}', { name: invitation.organizer.name || invitation.organizer.address }) }}</span>
				</p>
				<details v-if="event.attendees.length" class="invitation__attendees">
					<summary>
						{{ n('sockless_mail', '{count} attendee', '{count} attendees', event.attendees.length, { count: event.attendees.length }) }}
						<span v-if="answerCounts">· {{ answerCounts }}</span>
					</summary>
					<ul>
						<li v-for="attendee in event.attendees" :key="attendee.address">
							<NcIconSvgWrapper :path="statusIcon(attendee.status)" :size="16" inline :name="statusText(attendee.status)" />
							{{ attendee.name || attendee.address }}
							<span v-if="attendee.role === 'OPT-PARTICIPANT'" class="invitation__muted">({{ t('sockless_mail', 'optional') }})</span>
						</li>
					</ul>
				</details>

				<p class="invitation__status">
					<template v-if="invitation.existing">
						<span class="invitation__dot" :style="{ background: invitation.existing.color || 'var(--color-primary-element)' }" />
						{{ t('sockless_mail', 'In your calendar {calendar}', { calendar: invitation.existing.calendarName }) }}
						<template v-if="invitation.existing.myStatus && invitation.method === 'REQUEST'">
							· {{ myAnswer(invitation.existing.myStatus) }}
						</template>
					</template>
					<template v-else>
						{{ t('sockless_mail', 'Not in your calendar') }}
					</template>
				</p>
				<NcNoteCard v-if="invitation.existing?.outdated"
					type="info"
					:text="t('sockless_mail', 'Your calendar has a newer version of this event than this message.')" />
				<NcNoteCard v-if="invitation.method === 'REQUEST' && !invitation.me && !invitation.isOrganizer"
					type="info"
					:text="t('sockless_mail', 'You are not on the list of attendees (the invitation may have reached you through a list). An answer is sent as {address}.', { address: accountAddress })" />

				<div v-if="actions" class="invitation__actions">
					<template v-if="actions === 'respond'">
						<NcButton v-for="answer in answers"
							:key="answer.value"
							:variant="currentAnswer === answer.status ? 'primary' : 'secondary'"
							:pressed="currentAnswer === answer.status"
							:disabled="busy"
							@click="answerWith(answer.value)">
							<template #icon>
								<NcIconSvgWrapper :path="answer.icon" />
							</template>
							{{ answer.label }}
						</NcButton>
					</template>
					<NcButton v-else-if="actions === 'import'" variant="primary" :disabled="busy || !calendarId" @click="addToCalendar">
						<template #icon>
							<NcIconSvgWrapper :path="mdiCalendarPlus" />
						</template>
						{{ importLabel }}
					</NcButton>
					<NcButton v-else-if="actions === 'cancel'" :disabled="busy" @click="apply">
						<template #icon>
							<NcIconSvgWrapper :path="mdiCalendarRemove" />
						</template>
						{{ t('sockless_mail', 'Remove from calendar') }}
					</NcButton>
					<NcButton v-else-if="actions === 'reply'" variant="primary" :disabled="busy" @click="apply">
						<template #icon>
							<NcIconSvgWrapper :path="mdiCalendarCheck" />
						</template>
						{{ t('sockless_mail', 'Update the answer in your calendar') }}
					</NcButton>
					<NcLoadingIcon v-if="busy" :size="20" />
				</div>

				<div v-if="(actions === 'respond' || actions === 'import') && !invitation.existing && writableCalendars.length" class="invitation__options">
					<label class="invitation__calendar">
						<span>{{ t('sockless_mail', 'Calendar') }}</span>
						<select v-model="calendarId" :disabled="busy">
							<option v-for="calendar in writableCalendars" :key="calendar.id" :value="calendar.id">
								{{ calendar.name }}
							</option>
						</select>
					</label>
				</div>
				<NcCheckboxRadioSwitch v-if="actions === 'respond' && invitation.organizer"
					v-model="sendReply"
					:disabled="busy">
					{{ t('sockless_mail', 'Send my answer to {name}', { name: invitation.organizer.name || invitation.organizer.address }) }}
				</NcCheckboxRadioSwitch>
				<NcNoteCard v-if="actionError" type="error" :text="actionError" />
			</div>
		</template>
	</section>
</template>

<script setup>
import { mdiAccountTieOutline, mdiCalendarCheck, mdiCalendarClock, mdiCalendarPlus, mdiCalendarRemove, mdiCheck, mdiCheckCircleOutline, mdiClockOutline, mdiClose, mdiCloseCircleOutline, mdiHelp, mdiHelpCircleOutline, mdiMapMarkerOutline, mdiRepeat } from '@mdi/js'
import { showSuccess } from '@nextcloud/dialogs/toast'
import { getCanonicalLocale, n, t } from '@nextcloud/l10n'
import NcButton from '@nextcloud/vue/components/NcButton'
import NcCheckboxRadioSwitch from '@nextcloud/vue/components/NcCheckboxRadioSwitch'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import NcNoteCard from '@nextcloud/vue/components/NcNoteCard'
import { computed, ref, watch } from 'vue'
import { applyToCalendar, getInvitation, importEvents, loadCalendars, preferredCalendar, respond } from '../store/calendar.js'
import { ActionError } from '../store/mailbox.js'

const props = defineProps({
	/** { accountId, folderId } of the message */
	where: { type: Object, required: true },
	messageId: { type: String, required: true },
	/** { partId, method } */
	part: { type: Object, required: true },
	/** The account's address, for "an answer is sent as". */
	accountAddress: { type: String, default: '' },
})

const loading = ref(true)
const loadError = ref('')
const actionError = ref('')
const busy = ref(false)
const invitation = ref(null)
const calendars = ref([])
const calendarId = ref(null)
const sendReply = ref(true)

const event = computed(() => invitation.value?.event)
const writableCalendars = computed(() => calendars.value.filter((c) => c.writable))

async function load() {
	loading.value = true
	loadError.value = ''
	actionError.value = ''
	try {
		const [loaded, list] = await Promise.all([getInvitation(props.where, props.messageId, props.part.partId), loadCalendars()])
		invitation.value = loaded
		calendars.value = list
		calendarId.value = preferredCalendar(list)?.id ?? null
		sendReply.value = loaded.rsvp
	} catch (e) {
		const error = new ActionError(e)
		loadError.value = error.code === 'invalid_data'
			? t('sockless_mail', 'This message contains calendar data that cannot be read.')
			: t('sockless_mail', 'The calendar information could not be loaded: {error}', { error: error.message })
	} finally {
		loading.value = false
	}
}

watch(() => [props.messageId, props.part.partId], load, { immediate: true })

const cancelled = computed(() => invitation.value?.method === 'CANCEL' || event.value?.status === 'CANCELLED')

const title = computed(() => {
	const i = invitation.value
	if (!i) {
		return t('sockless_mail', 'Event')
	}
	switch (i.method) {
	case 'REQUEST':
		return i.existing && !i.existing.outdated && i.existing.sequence < event.value.sequence
			? t('sockless_mail', 'Updated invitation')
			: t('sockless_mail', 'Invitation')
	case 'CANCEL':
		return t('sockless_mail', 'Cancelled event')
	case 'REPLY':
		return replyText(i.replier)
	default:
		return i.eventCount > 1
			? n('sockless_mail', '{count} event', '{count} events', i.eventCount, { count: i.eventCount })
			: t('sockless_mail', 'Event')
	}
})

const kindIcon = computed(() => (cancelled.value ? mdiCalendarRemove : invitation.value?.method === 'REPLY' ? mdiCalendarCheck : mdiCalendarClock))

/** What the card offers: respond, import, cancel, reply or nothing. */
const actions = computed(() => {
	const i = invitation.value
	if (!i) {
		return null
	}
	if (i.method === 'REQUEST') {
		return i.isOrganizer || (i.existing && !i.existing.writable) ? null : 'respond'
	}
	if (i.method === 'CANCEL') {
		return i.existing?.writable && !i.existing.outdated ? 'cancel' : null
	}
	if (i.method === 'REPLY') {
		return i.isOrganizer && i.existing?.writable && i.replier && i.existing.replierStatus !== i.replier.status ? 'reply' : null
	}
	if (['', 'PUBLISH', 'ADD'].includes(i.method)) {
		return i.existing && !i.existing.writable ? null : 'import'
	}
	return null
})

const currentAnswer = computed(() => invitation.value?.existing?.myStatus ?? null)

const answers = [
	{ value: 'accepted', status: 'ACCEPTED', label: t('sockless_mail', 'Accept'), icon: mdiCheck },
	{ value: 'tentative', status: 'TENTATIVE', label: t('sockless_mail', 'Maybe'), icon: mdiHelp },
	{ value: 'declined', status: 'DECLINED', label: t('sockless_mail', 'Decline'), icon: mdiClose },
]

const importLabel = computed(() => {
	if (invitation.value.existing) {
		return t('sockless_mail', 'Update in calendar')
	}
	return invitation.value.eventCount > 1
		? n('sockless_mail', 'Add {count} event to calendar', 'Add {count} events to calendar', invitation.value.eventCount, { count: invitation.value.eventCount })
		: t('sockless_mail', 'Add to calendar')
})

async function run(action, success) {
	busy.value = true
	actionError.value = ''
	try {
		const result = await action()
		invitation.value = result.invitation ?? result
		showSuccess(success(result))
	} catch (e) {
		actionError.value = new ActionError(e).message
	} finally {
		busy.value = false
	}
}

function answerWith(value) {
	const declining = value === 'declined'
	return run(
		() => respond(props.where, props.messageId, props.part.partId, value, {
			calendarId: invitation.value.existing ? undefined : calendarId.value,
			sendReply: sendReply.value,
		}),
		() => {
			const sent = sendReply.value && invitation.value.organizer
			if (declining) {
				return sent ? t('sockless_mail', 'Declined, and the organizer was told') : t('sockless_mail', 'Declined')
			}
			return sent ? t('sockless_mail', 'Saved in your calendar, and the organizer was told') : t('sockless_mail', 'Saved in your calendar')
		})
}

function addToCalendar() {
	return run(
		() => importEvents(props.where, props.messageId, props.part.partId, calendarId.value),
		(result) => result.added + result.updated === 0
			? t('sockless_mail', 'Nothing was added: the events are in read-only calendars')
			: n('sockless_mail', '{count} event saved in your calendar', '{count} events saved in your calendar', result.added + result.updated, { count: result.added + result.updated }))
}

function apply() {
	const method = invitation.value.method
	return run(
		() => applyToCalendar(props.where, props.messageId, props.part.partId),
		() => method === 'CANCEL' ? t('sockless_mail', 'Removed from your calendar') : t('sockless_mail', 'Answer updated in your calendar'))
}

// Dates and times in the reader's language and time zone.
const locale = getCanonicalLocale()
const dateFormat = new Intl.DateTimeFormat(locale, { weekday: 'short', day: 'numeric', month: 'short', year: 'numeric' })
const timeFormat = new Intl.DateTimeFormat(locale, { hour: 'numeric', minute: '2-digit' })

/** A Date for any of the three time forms; floating times are read as local wall-clock time. */
function toDate(time) {
	if (!time) {
		return null
	}
	if (time.instant) {
		return new Date(time.instant)
	}
	if (time.date) {
		const [y, m, d] = time.date.split('-').map(Number)
		return new Date(y, m - 1, d)
	}
	const [date, clock] = time.local.split('T')
	const [y, m, d] = date.split('-').map(Number)
	const [h, min] = clock.split(':').map(Number)
	return new Date(y, m - 1, d, h, min)
}

const sameDay = (a, b) => a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate()

const when = computed(() => {
	const start = toDate(event.value.start)
	if (!start) {
		return t('sockless_mail', 'No date given')
	}
	let end = toDate(event.value.end)
	if (event.value.start.date) {
		// The end of an all-day event is the day after its last day.
		if (end) {
			end = new Date(end.getFullYear(), end.getMonth(), end.getDate() - 1)
		}
		return !end || sameDay(start, end)
			? t('sockless_mail', '{date}, all day', { date: dateFormat.format(start) })
			: t('sockless_mail', '{start} – {end}, all day', { start: dateFormat.format(start), end: dateFormat.format(end) })
	}
	const zone = event.value.start.local && event.value.start.timeZone ? ` (${event.value.start.timeZone})` : ''
	if (!end || end.getTime() === start.getTime()) {
		return `${dateFormat.format(start)}, ${timeFormat.format(start)}${zone}`
	}
	return sameDay(start, end)
		? `${dateFormat.format(start)}, ${timeFormat.format(start)} – ${timeFormat.format(end)}${zone}`
		: `${dateFormat.format(start)}, ${timeFormat.format(start)} – ${dateFormat.format(end)}, ${timeFormat.format(end)}${zone}`
})

const badge = computed(() => {
	const start = toDate(invitation.value && event.value.start) ?? new Date()
	return {
		month: new Intl.DateTimeFormat(locale, { month: 'short' }).format(start),
		day: start.getDate(),
		weekday: new Intl.DateTimeFormat(locale, { weekday: 'short' }).format(start),
	}
})

/** "Repeats weekly", "Repeats every 2 months", from the RRULE. */
const repeats = computed(() => {
	const rule = event.value.recurrence
	if (!rule) {
		return event.value.recurrenceId ? t('sockless_mail', 'One occurrence of a repeating event') : ''
	}
	const parts = Object.fromEntries(rule.split(';').map((p) => p.split('=')))
	const interval = Number(parts.INTERVAL ?? 1)
	const units = {
		DAILY: [t('sockless_mail', 'Repeats daily'), n('sockless_mail', 'Repeats every {count} day', 'Repeats every {count} days', interval, { count: interval })],
		WEEKLY: [t('sockless_mail', 'Repeats weekly'), n('sockless_mail', 'Repeats every {count} week', 'Repeats every {count} weeks', interval, { count: interval })],
		MONTHLY: [t('sockless_mail', 'Repeats monthly'), n('sockless_mail', 'Repeats every {count} month', 'Repeats every {count} months', interval, { count: interval })],
		YEARLY: [t('sockless_mail', 'Repeats yearly'), n('sockless_mail', 'Repeats every {count} year', 'Repeats every {count} years', interval, { count: interval })],
	}[parts.FREQ]
	return units ? units[interval > 1 ? 1 : 0] : t('sockless_mail', 'Repeats')
})

const answerCounts = computed(() => {
	const count = (status) => event.value.attendees.filter((a) => a.status === status).length
	return [
		count('ACCEPTED') && t('sockless_mail', '{count} accepted', { count: count('ACCEPTED') }),
		count('TENTATIVE') && t('sockless_mail', '{count} maybe', { count: count('TENTATIVE') }),
		count('DECLINED') && t('sockless_mail', '{count} declined', { count: count('DECLINED') }),
	].filter(Boolean).join(', ')
})

function statusIcon(status) {
	return { ACCEPTED: mdiCheckCircleOutline, DECLINED: mdiCloseCircleOutline }[status] ?? mdiHelpCircleOutline
}

function statusText(status) {
	return {
		ACCEPTED: t('sockless_mail', 'Accepted'),
		TENTATIVE: t('sockless_mail', 'Maybe'),
		DECLINED: t('sockless_mail', 'Declined'),
	}[status] ?? t('sockless_mail', 'No answer yet')
}

function myAnswer(status) {
	return {
		ACCEPTED: t('sockless_mail', 'You accepted'),
		TENTATIVE: t('sockless_mail', 'You said maybe'),
		DECLINED: t('sockless_mail', 'You declined'),
	}[status] ?? t('sockless_mail', 'You have not answered')
}

function replyText(replier) {
	if (!replier) {
		return t('sockless_mail', 'Answer to an invitation')
	}
	const name = replier.name || replier.address
	return {
		ACCEPTED: t('sockless_mail', '{name} accepted', { name }),
		TENTATIVE: t('sockless_mail', '{name} said maybe', { name }),
		DECLINED: t('sockless_mail', '{name} declined', { name }),
	}[replier.status] ?? t('sockless_mail', '{name} answered', { name })
}
</script>

<style scoped>
.invitation {
	display: flex;
	gap: 16px;
	margin-top: 16px;
	padding: 14px 16px;
	border: 1px solid var(--color-border);
	border-radius: var(--border-radius-container, 12px);
	background: var(--color-background-hover);
}

.invitation__date {
	display: flex;
	flex: none;
	flex-direction: column;
	align-items: center;
	width: 56px;
	height: fit-content;
	overflow: hidden;
	border: 1px solid var(--color-border);
	border-radius: 8px;
	background: var(--color-main-background);
	line-height: 1.2;
}

.invitation__month {
	width: 100%;
	padding: 2px 0;
	background: var(--color-primary-element);
	color: var(--color-primary-element-text);
	font-size: 11px;
	font-weight: 600;
	text-align: center;
	text-transform: uppercase;
}

.invitation__day {
	font-size: 22px;
	font-weight: 600;
}

.invitation__weekday {
	padding-bottom: 4px;
	font-size: 11px;
	color: var(--color-text-maxcontrast);
}

.invitation__main {
	display: flex;
	flex: 1;
	flex-direction: column;
	gap: 4px;
	min-width: 0;
}

.invitation__kind {
	display: flex;
	align-items: center;
	gap: 4px;
	margin: 0;
	font-size: 12px;
	font-weight: 600;
	color: var(--color-text-maxcontrast);
}

.invitation__summary {
	margin: 0 0 4px;
	font-size: 16px;
	font-weight: 600;
	overflow-wrap: anywhere;
}

.invitation--cancelled .invitation__summary {
	text-decoration: line-through;
}

.invitation__line {
	display: flex;
	align-items: flex-start;
	gap: 6px;
	margin: 0;
	font-size: 13px;
}

.invitation__line :deep(.icon-vue),
.invitation__kind :deep(.icon-vue) {
	flex: none;
	color: var(--color-text-maxcontrast);
}

.invitation__attendees {
	font-size: 13px;
}

.invitation__attendees summary {
	cursor: pointer;
	color: var(--color-text-maxcontrast);
}

.invitation__attendees ul {
	margin: 4px 0 0 4px;
	padding: 0;
	list-style: none;
}

.invitation__muted {
	color: var(--color-text-maxcontrast);
}

.invitation__status {
	display: flex;
	align-items: center;
	gap: 6px;
	margin: 6px 0 0;
	font-size: 13px;
}

.invitation__dot {
	width: 10px;
	height: 10px;
	border-radius: 50%;
}

.invitation__actions {
	display: flex;
	flex-wrap: wrap;
	align-items: center;
	gap: 8px;
	margin-top: 8px;
}

.invitation__options {
	margin-top: 4px;
}

.invitation__calendar {
	display: flex;
	align-items: center;
	gap: 8px;
	font-size: 13px;
}

.invitation__calendar select {
	min-width: 160px;
	margin: 0;
}
</style>
