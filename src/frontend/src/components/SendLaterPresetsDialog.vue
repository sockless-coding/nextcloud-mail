<template>
	<!-- Closing goes through NcDialog's buttons (see AccountDialog.vue for why). -->
	<NcDialog :name="t('sockless_mail', 'Send later presets')"
		:open="true"
		size="normal"
		:buttons="buttons"
		@update:open="(open) => !open && emit('close')">
		<!-- One preset being written -->
		<form v-if="editing" class="preset-editor" data-preset-editor @submit.prevent="finishEditing">
			<NcTextField v-model="editing.draft.label"
				:label="t('sockless_mail', 'Name')"
				:error="!!errors.label"
				:helper-text="errors.label"
				:maxlength="60" />
			<div class="preset-editor__row">
				<label for="preset-kind">{{ t('sockless_mail', 'When') }}</label>
				<select id="preset-kind" v-model="editing.draft.kind">
					<option value="today">
						{{ t('sockless_mail', 'Today') }}
					</option>
					<option value="tomorrow">
						{{ t('sockless_mail', 'Tomorrow') }}
					</option>
					<option value="weekday">
						{{ t('sockless_mail', 'Next weekday') }}
					</option>
					<option value="hours">
						{{ t('sockless_mail', 'Hours from now') }}
					</option>
				</select>
				<select v-if="editing.draft.kind === 'weekday'" v-model.number="editing.draft.weekday" :aria-label="t('sockless_mail', 'Day of the week')">
					<option v-for="w in days" :key="w.day" :value="w.day">
						{{ w.name }}
					</option>
				</select>
				<template v-if="editing.draft.kind === 'hours'">
					<input v-model.number="editing.draft.hours"
						class="preset-editor__hours"
						type="number"
						min="1"
						:max="MAX_HOURS"
						:aria-label="t('sockless_mail', 'Hours')">
					<span>{{ t('sockless_mail', 'hours') }}</span>
				</template>
				<template v-else>
					<label for="preset-time">{{ t('sockless_mail', 'at') }}</label>
					<input id="preset-time" v-model="editing.draft.time" type="time">
				</template>
			</div>
			<p v-if="errors.when" class="preset-editor__error">
				{{ errors.when }}
			</p>
			<fieldset class="preset-editor__days">
				<legend>{{ t('sockless_mail', 'Offer it on') }}</legend>
				<button v-for="w in days"
					:key="w.day"
					type="button"
					class="preset-editor__day"
					:class="{ 'preset-editor__day--on': offeredOn(w.day) }"
					:aria-pressed="String(offeredOn(w.day))"
					:title="w.name"
					@click="toggleDay(w.day)">
					{{ w.name.slice(0, 2) }}
				</button>
			</fieldset>
			<p class="preset-editor__hint">
				{{ previewText }}
			</p>
			<div class="preset-editor__buttons">
				<NcButton @click="editing = null">
					{{ t('sockless_mail', 'Back') }}
				</NcButton>
				<NcButton variant="primary" type="submit">
					{{ t('sockless_mail', 'Done') }}
				</NcButton>
			</div>
		</form>

		<!-- The list -->
		<div v-else class="presets">
			<p class="presets__hint">
				{{ t('sockless_mail', 'Offered in the Send later menu, in this order. Times are in your time zone.') }}
			</p>
			<NcNoteCard v-if="useDefaults" type="info" :text="t('sockless_mail', 'These are the built-in presets.')" />
			<p v-if="list.length === 0" class="presets__empty">
				{{ t('sockless_mail', 'No presets: Send later offers only "Pick date and time".') }}
			</p>
			<ul class="presets__list">
				<li v-for="(preset, i) in list" :key="preset.id" class="preset" data-preset>
					<div class="preset__text">
						<strong>{{ preset.label }}</strong>
						<span class="preset__when">{{ describePreset(preset) }}</span>
					</div>
					<NcButton variant="tertiary" :aria-label="t('sockless_mail', 'Move up')" :disabled="i === 0" @click="move(i, -1)">
						<template #icon>
							<NcIconSvgWrapper :path="mdiArrowUp" />
						</template>
					</NcButton>
					<NcButton variant="tertiary" :aria-label="t('sockless_mail', 'Move down')" :disabled="i === list.length - 1" @click="move(i, 1)">
						<template #icon>
							<NcIconSvgWrapper :path="mdiArrowDown" />
						</template>
					</NcButton>
					<NcButton variant="tertiary" :aria-label="t('sockless_mail', 'Edit {name}', { name: preset.label })" @click="edit(i)">
						<template #icon>
							<NcIconSvgWrapper :path="mdiPencilOutline" />
						</template>
					</NcButton>
					<NcButton variant="tertiary" :aria-label="t('sockless_mail', 'Delete {name}', { name: preset.label })" @click="remove(i)">
						<template #icon>
							<NcIconSvgWrapper :path="mdiDeleteOutline" />
						</template>
					</NcButton>
				</li>
			</ul>
			<NcButton :disabled="list.length >= MAX_PRESETS" @click="edit(-1)">
				<template #icon>
					<NcIconSvgWrapper :path="mdiPlus" />
				</template>
				{{ t('sockless_mail', 'Add preset') }}
			</NcButton>
			<NcNoteCard v-if="saveError" type="error" :text="saveError" />
		</div>
	</NcDialog>
</template>

<script setup>
import { mdiArrowDown, mdiArrowUp, mdiDeleteOutline, mdiPencilOutline, mdiPlus } from '@mdi/js'
import { showSuccess } from '@nextcloud/dialogs/toast'
import { t } from '@nextcloud/l10n'
import NcButton from '@nextcloud/vue/components/NcButton'
import NcDialog from '@nextcloud/vue/components/NcDialog'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import NcNoteCard from '@nextcloud/vue/components/NcNoteCard'
import NcTextField from '@nextcloud/vue/components/NcTextField'
import { computed, ref } from 'vue'
import { ActionError } from '../store/mailbox.js'
import { currentPresets, defaultPresets, describePreset, formatSendTime, presetTime, presetsState, resetPresets, savePresets, weekdays } from '../store/sendLater.js'

const emit = defineEmits(['close'])

const MAX_PRESETS = 20
const MAX_HOURS = 24 * 30

const days = weekdays()
const copy = (presets) => presets.map((p) => ({ ...p, showOn: p.showOn ? [...p.showOn] : null }))

const list = ref(copy(currentPresets()))
/** The built-in presets, unchanged: saving goes back to them rather than storing a copy. */
const useDefaults = ref(presetsState.own === null)
/** { index (-1 for a new one), draft } */
const editing = ref(null)
const errors = ref({})
const saveError = ref('')

function changed() {
	useDefaults.value = false
}

function move(i, delta) {
	const items = list.value
	;[items[i], items[i + delta]] = [items[i + delta], items[i]]
	changed()
}

function remove(i) {
	list.value.splice(i, 1)
	changed()
}

function edit(index) {
	errors.value = {}
	const preset = index >= 0
		? list.value[index]
		: { id: `p-${Date.now().toString(36)}`, label: '', kind: 'tomorrow', time: '08:00', weekday: 1, hours: 1, showOn: null }
	editing.value = {
		index,
		draft: { weekday: 1, hours: 1, time: '08:00', ...preset, showOn: preset.showOn ? [...preset.showOn] : [] },
	}
}

/** No day chosen means every day. */
function offeredOn(day) {
	const showOn = editing.value.draft.showOn
	return showOn.length === 0 || showOn.includes(day)
}

function toggleDay(day) {
	const draft = editing.value.draft
	const current = draft.showOn.length === 0 ? days.map((w) => w.day) : draft.showOn
	const next = current.includes(day) ? current.filter((d) => d !== day) : [...current, day]
	// At least one day: none would read as "every day".
	if (next.length > 0) {
		draft.showOn = next.length === 7 ? [] : next
	}
}

/** The draft as stored: only the fields of its kind. */
function cleaned(draft) {
	const showOn = draft.showOn.length ? [...draft.showOn].sort() : null
	const base = { id: draft.id, label: draft.label.trim(), kind: draft.kind, showOn }
	if (draft.kind === 'hours') {
		return { ...base, hours: draft.hours }
	}
	return draft.kind === 'weekday' ? { ...base, time: draft.time, weekday: draft.weekday } : { ...base, time: draft.time }
}

const previewText = computed(() => {
	const draft = editing.value?.draft
	if (!draft) {
		return ''
	}
	const when = presetTime(cleaned(draft))
	return when
		? t('sockless_mail', 'Offered now as {time}.', { time: formatSendTime(when) })
		: t('sockless_mail', 'Not offered right now.')
})

function finishEditing() {
	const draft = editing.value.draft
	const result = {}
	if (!draft.label.trim()) {
		result.label = t('sockless_mail', 'Give the preset a name.')
	}
	if (draft.kind === 'hours' ? !(Number.isInteger(draft.hours) && draft.hours >= 1 && draft.hours <= MAX_HOURS) : !/^\d\d:\d\d$/.test(draft.time ?? '')) {
		result.when = draft.kind === 'hours'
			? t('sockless_mail', 'Choose between 1 and {max} hours.', { max: MAX_HOURS })
			: t('sockless_mail', 'Choose a time.')
	}
	errors.value = result
	if (Object.keys(result).length) {
		return
	}
	const preset = cleaned(draft)
	if (editing.value.index >= 0) {
		list.value[editing.value.index] = preset
	} else {
		list.value.push(preset)
	}
	editing.value = null
	changed()
}

function restoreDefaults() {
	list.value = copy(defaultPresets())
	useDefaults.value = true
}

async function save() {
	saveError.value = ''
	try {
		if (useDefaults.value) {
			if (presetsState.own !== null) {
				await resetPresets()
			}
		} else {
			await savePresets(list.value)
		}
		showSuccess(t('sockless_mail', 'Presets saved'))
	} catch (e) {
		saveError.value = new ActionError(e).message
		return false
	}
}

const buttons = computed(() => editing.value
	? []
	: [
		...(useDefaults.value ? [] : [{ label: t('sockless_mail', 'Restore defaults'), callback: () => { restoreDefaults(); return false } }]),
		{ label: t('sockless_mail', 'Cancel') },
		{ label: t('sockless_mail', 'Save'), variant: 'primary', callback: save },
	])
</script>

<style scoped>
.presets,
.preset-editor {
	display: flex;
	flex-direction: column;
	gap: 10px;
	padding-bottom: 8px;
}

.presets__hint,
.presets__empty,
.preset-editor__hint {
	margin: 0;
	color: var(--color-text-maxcontrast);
}

.presets__list {
	margin: 0;
	padding: 0;
	list-style: none;
}

.preset {
	display: flex;
	align-items: center;
	gap: 2px;
	padding: 4px 0;
	border-bottom: 1px solid var(--color-border);
}

.preset__text {
	flex: 1;
	min-width: 0;
	display: flex;
	flex-direction: column;
}

.preset__when {
	color: var(--color-text-maxcontrast);
	font-size: 13px;
}

.preset-editor__row {
	display: flex;
	flex-wrap: wrap;
	align-items: center;
	gap: 8px;
}

.preset-editor__row select,
.preset-editor__row input {
	margin: 0;
}

.preset-editor__hours {
	width: 90px;
}

.preset-editor__error {
	margin: 0;
	color: var(--color-text-error, var(--color-error));
}

.preset-editor__days {
	display: flex;
	flex-wrap: wrap;
	gap: 6px;
	margin: 0;
	padding: 0;
	border: 0;
}

.preset-editor__days legend {
	margin-bottom: 6px;
}

.preset-editor__day {
	min-width: 40px;
	min-height: 0;
	height: 32px;
	margin: 0;
	padding: 0 8px;
	border: 1px solid var(--color-border-dark, var(--color-border));
	border-radius: 16px;
	background: transparent;
	color: var(--color-text-maxcontrast);
	font: inherit;
	cursor: pointer;
}

.preset-editor__day--on {
	border-color: transparent;
	background: var(--color-primary-element-light);
	color: var(--color-primary-element-light-text);
	font-weight: 600;
}

.preset-editor__buttons {
	display: flex;
	justify-content: flex-end;
	gap: 8px;
}
</style>
