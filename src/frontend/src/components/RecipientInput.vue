<template>
	<div class="recipients" :class="{ 'recipients--error': !!error }">
		<label class="recipients__label" :for="id">{{ label }}</label>
		<div class="recipients__field" role="group" :aria-label="t('sockless_mail', '{field} recipients', { field: label })" @click="input.focus()">
			<span v-for="(chip, index) in chips"
				:key="`${index}-${chip}`"
				class="recipients__chip"
				:class="{ 'recipients__chip--invalid': !parse(chip).valid }"
				:title="parse(chip).address">
				<span class="recipients__chip-name">{{ parse(chip).display }}</span>
				<button type="button"
					class="recipients__remove"
					:aria-label="t('sockless_mail', 'Remove {name}', { name: parse(chip).display })"
					@click.stop="removeChip(index)">
					<NcIconSvgWrapper :path="mdiClose" :size="14" />
				</button>
			</span>
			<input :id="id"
				ref="input"
				v-model="pending"
				class="recipients__input"
				type="text"
				autocomplete="off"
				:aria-describedby="error ? `${id}-error` : undefined"
				:aria-invalid="error ? 'true' : undefined"
				@input="onInput"
				@keydown="onKeydown"
				@blur="onBlur">
		</div>
		<ul v-if="suggestions.length" class="recipients__suggestions" role="listbox" :aria-label="t('sockless_mail', 'Suggestions')">
			<li v-for="(suggestion, index) in suggestions"
				:key="suggestion.address"
				role="option"
				:aria-selected="String(index === highlighted)"
				class="recipients__suggestion"
				:class="{ 'recipients__suggestion--active': index === highlighted }"
				@mousedown.prevent="pick(suggestion)">
				<strong v-if="suggestion.name">{{ suggestion.name }}</strong>
				<span>{{ suggestion.address }}</span>
			</li>
		</ul>
		<p v-if="error" :id="`${id}-error`" class="recipients__error">
			{{ error }}
		</p>
	</div>
</template>

<script setup>
import { mdiClose } from '@mdi/js'
import { t } from '@nextcloud/l10n'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import { computed, ref, watch } from 'vue'
import { knownAddresses } from '../store/compose.js'

/**
 * Recipients as chips, like desktop mail programs. The model stays the plain comma-separated
 * text the server parses, including what is still being typed, so nothing is lost when the
 * user sends without finishing an entry.
 */
const props = defineProps({
	modelValue: { type: String, default: '' },
	label: { type: String, required: true },
	error: { type: String, default: '' },
})
const emit = defineEmits(['update:modelValue'])

const id = `recipients-${Math.random().toString(36).slice(2)}`
const input = ref(null)
const chips = ref([])
const pending = ref('')
const highlighted = ref(0)

/** Splits on commas and semicolons outside quotes and angle brackets. */
function split(text) {
	const parts = []
	let current = ''
	let quoted = false
	let angle = false
	for (const c of text) {
		if (c === '"') {
			quoted = !quoted
		} else if (c === '<' && !quoted) {
			angle = true
		} else if (c === '>' && !quoted) {
			angle = false
		}
		if ((c === ',' || c === ';') && !quoted && !angle) {
			parts.push(current)
			current = ''
		} else {
			current += c
		}
	}
	parts.push(current)
	return parts.map((p) => p.trim())
}

/** "Name <address>" or a bare address. */
function parse(entry) {
	const match = entry.match(/^(.*?)\s*<([^<>]+)>$/)
	const name = (match ? match[1] : '').replace(/^"|"$/g, '').trim()
	const address = (match ? match[2] : entry).trim()
	return { name, address, display: name || address, valid: /^[^\s@<>]+@[^\s@<>]+$/.test(address) }
}

function joined() {
	return [...chips.value, pending.value.trim()].filter(Boolean).join(', ')
}

function load(value) {
	const parts = split(value ?? '').filter(Boolean)
	chips.value = parts
	pending.value = ''
}

load(props.modelValue)
// Changes from outside (a reply filling in the sender) replace what is shown.
watch(() => props.modelValue, (value) => {
	if (value !== joined()) {
		load(value)
	}
})

function update() {
	emit('update:modelValue', joined())
}

function commit() {
	const parts = split(pending.value).filter(Boolean)
	if (parts.length) {
		chips.value = [...chips.value, ...parts]
	}
	pending.value = ''
	update()
}

function onInput() {
	highlighted.value = 0
	// Pasted or typed separators turn everything before the last one into chips.
	if (/[,;]/.test(pending.value)) {
		const parts = split(pending.value)
		const last = parts.pop()
		chips.value = [...chips.value, ...parts.filter(Boolean)]
		pending.value = last
	}
	update()
}

function onKeydown(event) {
	if (suggestions.value.length && (event.key === 'ArrowDown' || event.key === 'ArrowUp')) {
		event.preventDefault()
		const step = event.key === 'ArrowDown' ? 1 : -1
		highlighted.value = (highlighted.value + step + suggestions.value.length) % suggestions.value.length
	} else if ((event.key === 'Enter' || event.key === 'Tab') && suggestions.value.length) {
		event.preventDefault()
		pick(suggestions.value[highlighted.value])
	} else if (event.key === 'Enter' && pending.value.trim()) {
		event.preventDefault()
		commit()
	} else if (event.key === 'Backspace' && pending.value === '' && chips.value.length) {
		removeChip(chips.value.length - 1)
	}
}

function onBlur() {
	if (pending.value.trim()) {
		commit()
	}
}

function removeChip(index) {
	chips.value = chips.value.filter((_, i) => i !== index)
	update()
}

function pick(suggestion) {
	const name = suggestion.name ? `"${suggestion.name.replace(/"/g, '')}" ` : ''
	chips.value = [...chips.value, suggestion.name ? `${name}<${suggestion.address}>` : suggestion.address]
	pending.value = ''
	update()
	input.value?.focus()
}

/** Addresses from mail already loaded that match what is being typed. */
const suggestions = computed(() => {
	const query = pending.value.trim().toLowerCase()
	if (query.length < 2) {
		return []
	}
	const chosen = new Set(chips.value.map((c) => parse(c).address.toLowerCase()))
	return knownAddresses()
		.filter((a) => !chosen.has(a.address.toLowerCase()))
		.filter((a) => a.address.toLowerCase().includes(query) || a.name?.toLowerCase().includes(query))
		.slice(0, 6)
})
</script>

<style scoped>
.recipients {
	position: relative;
	display: grid;
	grid-template-columns: 64px minmax(0, 1fr);
	align-items: start;
	border-bottom: 1px solid var(--color-border);
}

.recipients__label {
	padding: 10px 0 0 14px;
	color: var(--color-text-maxcontrast);
}

.recipients__field {
	display: flex;
	flex-wrap: wrap;
	align-items: center;
	gap: 4px;
	min-height: 40px;
	padding: 4px 8px 4px 0;
	cursor: text;
}

.recipients__chip {
	display: inline-flex;
	align-items: center;
	gap: 2px;
	max-width: 100%;
	height: 26px;
	padding-inline-start: 10px;
	border-radius: 13px;
	background: var(--color-primary-element-light);
	color: var(--color-primary-element-light-text);
	font-size: 13px;
}

.recipients__chip--invalid {
	background: color-mix(in srgb, var(--color-element-error, var(--color-error)) 15%, transparent);
	color: var(--color-error-text);
	text-decoration: underline wavy;
	text-decoration-thickness: 1px;
}

.recipients__chip-name {
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.recipients__remove {
	--sm-button-background: transparent;
	display: grid;
	place-items: center;
	width: 22px;
	height: 22px;
	min-height: 0;
	margin: 0;
	padding: 0;
	border: 0;
	border-radius: 50%;
	background: none;
	color: inherit;
	cursor: pointer;
}

.recipients__remove:hover {
	--sm-button-background: var(--color-background-hover);
	background: var(--color-background-hover);
}

.recipients__input {
	flex: 1;
	min-width: 120px;
	height: 30px;
	min-height: 0;
	margin: 0;
	padding: 0 4px;
	border: 0;
	outline: none;
	background: none;
	box-shadow: none;
	color: var(--color-main-text);
	font: inherit;
}

.recipients__suggestions {
	position: absolute;
	top: 100%;
	inset-inline: 64px 8px;
	z-index: 2;
	margin: 0;
	padding: 4px;
	list-style: none;
	border: 1px solid var(--color-border);
	border-radius: var(--border-radius-element, 8px);
	background: var(--color-main-background);
	box-shadow: 0 4px 16px rgb(0 0 0 / 15%);
}

.recipients__suggestion {
	display: flex;
	flex-direction: column;
	padding: 6px 10px;
	border-radius: 6px;
	cursor: pointer;
}

.recipients__suggestion span {
	font-size: 12.5px;
	color: var(--color-text-maxcontrast);
}

.recipients__suggestion--active,
.recipients__suggestion:hover {
	background: var(--color-background-hover);
}

.recipients__error {
	grid-column: 2;
	margin: 0 0 6px;
	font-size: 13px;
	color: var(--color-error-text);
}
</style>
