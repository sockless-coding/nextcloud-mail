import { getCanonicalLocale, getDayNames, getFirstDay, n, t } from '@nextcloud/l10n'
import { reactive } from 'vue'
import { apiGet, apiSend } from '../api.js'

/**
 * Send later presets. A preset says when in words the browser understands ("tomorrow at 08:00");
 * the browser turns it into a moment in the user's time zone, and the server only ever sees that
 * moment. Kinds:
 * - today: at `time`; not offered once that time has passed
 * - tomorrow: at `time`
 * - weekday: the next `weekday` (0 Sunday … 6 Saturday, never today) at `time`
 * - hours: `hours` from now
 * `showOn` (weekdays) limits the days a preset is offered on.
 *
 * The built-in presets apply until the user saves their own (kept on the server, for every
 * device); their names are translated, which saved ones no longer are.
 */
export function defaultPresets() {
	return [
		{ id: 'tomorrow-morning', label: t('sockless_mail', 'Tomorrow morning'), kind: 'tomorrow', time: '08:00' },
		{ id: 'after-lunch', label: t('sockless_mail', 'After lunch'), kind: 'today', time: '13:00' },
		{ id: 'monday-morning', label: t('sockless_mail', 'Monday morning'), kind: 'weekday', weekday: 1, time: '08:00', showOn: [5, 6, 0] },
		{ id: 'in-an-hour', label: t('sockless_mail', 'In 1 hour'), kind: 'hours', hours: 1 },
	]
}

export const presetsState = reactive({
	/** The user's own presets, or null for the built-in ones. */
	own: null,
	loaded: false,
})

/** The presets in use: the user's own, else the built-in ones. */
export function currentPresets() {
	return presetsState.own ?? defaultPresets()
}

/** Not awaited by anything: until it answers, the built-in presets are offered. */
export async function loadPresets() {
	try {
		presetsState.own = (await apiGet('/send-later/presets')).presets
		presetsState.loaded = true
	} catch {
		// The built-in presets work without it.
	}
}

/** @param {object[]} presets the whole list, in order */
export async function savePresets(presets) {
	presetsState.own = (await apiSend('PUT', '/send-later/presets', { presets })).presets
}

export async function resetPresets() {
	await apiSend('DELETE', '/send-later/presets')
	presetsState.own = null
}

/** The weekdays, 0 Sunday … 6 Saturday, starting with the locale's first day of the week. */
export function weekdays() {
	const names = getDayNames()
	const first = getFirstDay()
	return [0, 1, 2, 3, 4, 5, 6].map((i) => (first + i) % 7).map((day) => ({ day, name: names[day] }))
}

/** A preset in words: "Next Monday at 08:00, offered on Friday, Saturday, Sunday". */
export function describePreset(preset) {
	const names = getDayNames()
	const when = {
		today: () => t('sockless_mail', 'Today at {time}', { time: preset.time }),
		tomorrow: () => t('sockless_mail', 'Tomorrow at {time}', { time: preset.time }),
		weekday: () => t('sockless_mail', 'Next {day} at {time}', { day: names[preset.weekday], time: preset.time }),
		hours: () => n('sockless_mail', 'In {count} hour', 'In {count} hours', preset.hours, { count: preset.hours }),
	}[preset.kind]?.() ?? ''
	if (!preset.showOn?.length || preset.showOn.length === 7) {
		return when
	}
	const days = weekdays().filter((w) => preset.showOn.includes(w.day)).map((w) => w.name).join(', ')
	return t('sockless_mail', '{when}, offered on {days}', { when, days })
}

/** How far ahead the server wants a scheduled message (it refuses less than a minute). */
const MIN_LEAD_MS = 2 * 60 * 1000

/**
 * When the preset would send, or null when it is not offered right now.
 *
 * @param {object} preset see defaultPresets
 * @param {Date} now the current time
 */
export function presetTime(preset, now = new Date()) {
	if (preset.showOn && !preset.showOn.includes(now.getDay())) {
		return null
	}
	let when
	if (preset.kind === 'hours') {
		when = new Date(now.getTime() + preset.hours * 60 * 60 * 1000)
	} else {
		const [hours, minutes] = (preset.time ?? '08:00').split(':').map(Number)
		when = new Date(now.getFullYear(), now.getMonth(), now.getDate(), hours, minutes)
		if (preset.kind === 'tomorrow') {
			when.setDate(when.getDate() + 1)
		} else if (preset.kind === 'weekday') {
			when.setDate(when.getDate() + (((preset.weekday - now.getDay()) + 6) % 7) + 1)
		}
	}
	return when.getTime() - now.getTime() >= MIN_LEAD_MS ? when : null
}

/** The presets offered now, each with its time. */
export function offeredPresets(presets = currentPresets(), now = new Date()) {
	return presets
		.map((preset) => ({ preset, when: presetTime(preset, now) }))
		.filter((p) => p.when)
}

/** The earliest time the picker allows. */
export function earliestSendTime(now = new Date()) {
	return new Date(now.getTime() + MIN_LEAD_MS)
}

const timeOnly = () => new Intl.DateTimeFormat(getCanonicalLocale(), { hour: 'numeric', minute: '2-digit' })
const withDay = () => new Intl.DateTimeFormat(getCanonicalLocale(), { weekday: 'short', hour: 'numeric', minute: '2-digit' })
const withDate = () => new Intl.DateTimeFormat(getCanonicalLocale(), { weekday: 'short', day: 'numeric', month: 'short', hour: 'numeric', minute: '2-digit' })
const withYear = () => new Intl.DateTimeFormat(getCanonicalLocale(), { day: 'numeric', month: 'short', year: 'numeric', hour: 'numeric', minute: '2-digit' })

/**
 * A send time as briefly as it is still clear: "13:00" today, "Mon 08:00" within a week,
 * the date beyond that, the year when it is not this one.
 *
 * @param {Date} when the time
 * @param {Date} now the current time
 */
export function formatSendTime(when, now = new Date()) {
	const startOfToday = new Date(now.getFullYear(), now.getMonth(), now.getDate())
	const days = (new Date(when.getFullYear(), when.getMonth(), when.getDate()) - startOfToday) / (24 * 60 * 60 * 1000)
	if (days === 0) {
		return timeOnly().format(when)
	}
	if (days > 0 && days < 7) {
		return withDay().format(when)
	}
	return when.getFullYear() === now.getFullYear() ? withDate().format(when) : withYear().format(when)
}
