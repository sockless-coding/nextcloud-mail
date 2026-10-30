<template>
	<!-- Closing goes through NcDialog's buttons (see AccountDialog.vue for why). -->
	<NcDialog :name="t('sockless_mail', 'Rules for {account}', { account: account.displayName })"
		:open="true"
		size="large"
		:buttons="buttons"
		@update:open="(open) => !open && emit('close')">
		<div v-if="loading" class="rules__center">
			<NcLoadingIcon :size="32" />
		</div>
		<NcNoteCard v-else-if="loadError" type="error" :text="loadError" />

		<!-- Run on existing messages -->
		<div v-else-if="running" class="rule-run" data-rule-run>
			<p class="rules__hint">
				{{ t('sockless_mail', 'Run "{name}" once on messages that are already in a folder. The newest {count} messages are checked.', { name: running.rule.name, count: 1000 }) }}
			</p>
			<div class="rule-editor__row">
				<label for="rule-run-folder">{{ t('sockless_mail', 'Folder') }}</label>
				<select id="rule-run-folder" v-model="running.folderId" class="rule-editor__folder" @change="running.preview = null">
					<option v-for="folder in allFolders" :key="folder.id" :value="folder.id">
						{{ folder.path }}
					</option>
				</select>
				<NcButton :disabled="running.busy" @click="previewRun">
					<template #icon>
						<NcLoadingIcon v-if="running.busy && !running.preview" :size="18" />
						<NcIconSvgWrapper v-else :path="mdiMagnify" :size="18" />
					</template>
					{{ t('sockless_mail', 'Find matching messages') }}
				</NcButton>
			</div>
			<div v-if="running.preview" class="rule-editor__matches" data-run-preview>
				<p>
					{{ n('sockless_mail', '{matched} of the newest message matches.', '{matched} of the newest {count} messages match.', running.preview.scanned, { matched: running.preview.matched, count: running.preview.scanned }) }}
				</p>
				<ul v-if="running.preview.matches.length">
					<li v-for="match in running.preview.matches" :key="match.id">
						<span class="rule-editor__match-from">{{ match.from[0]?.name || match.from[0]?.address }}</span>
						<span class="rule-editor__match-subject">{{ match.subject || t('sockless_mail', '(no subject)') }}</span>
					</li>
				</ul>
				<p v-if="running.preview.matched > running.preview.matches.length" class="rules__hint">
					{{ t('sockless_mail', 'and {count} more', { count: running.preview.matched - running.preview.matches.length }) }}
				</p>
			</div>
			<NcNoteCard v-if="running.error" type="error" :text="running.error" />
		</div>

		<!-- The list -->
		<div v-else-if="editing === null" class="rules">
			<p class="rules__hint">
				{{ t('sockless_mail', 'Rules sort new mail arriving in the inbox, also while Sockless Mail is closed. They run from top to bottom.') }}
			</p>
			<p v-if="rules.length === 0" class="rules__hint">
				{{ t('sockless_mail', 'No rules yet.') }}
			</p>
			<ol v-else class="rules__list" :aria-label="t('sockless_mail', 'Rules')">
				<li v-for="(rule, index) in rules"
					:key="rule.id"
					class="rules__item"
					:class="{ 'rules__item--off': !rule.enabled, 'rules__item--over': dragOver === index }"
					data-rule
					@dragover.prevent="dragOver = index"
					@dragleave="dragOver = dragOver === index ? null : dragOver"
					@drop.prevent="drop(index)">
					<span class="rules__handle"
						draggable="true"
						:title="t('sockless_mail', 'Drag to reorder')"
						@dragstart="dragFrom = index"
						@dragend="dragFrom = dragOver = null">
						<NcIconSvgWrapper :path="mdiDragVertical" :size="20" />
					</span>
					<NcCheckboxRadioSwitch :model-value="rule.enabled"
						type="switch"
						:aria-label="t('sockless_mail', 'Enable {name}', { name: rule.name })"
						@update:model-value="(on) => update(rules.map((r, i) => i === index ? { ...r, enabled: on } : r))" />
					<button class="rules__text" type="button" @click="edit(index)">
						<span class="rules__name">{{ rule.name }}</span>
						<span class="rules__summary">{{ describeRule(rule, folderName) }}</span>
						<span class="rules__stats">{{ matchStats(rule) }}</span>
						<span v-if="rule.error" class="rules__error">
							<NcIconSvgWrapper :path="mdiAlertCircleOutline" :size="16" />
							{{ rule.error }}
						</span>
					</button>
					<NcActions :aria-label="t('sockless_mail', 'Actions for {name}', { name: rule.name })">
						<NcActionButton close-after-click @click="edit(index)">
							<template #icon>
								<NcIconSvgWrapper :path="mdiPencil" />
							</template>
							{{ t('sockless_mail', 'Edit') }}
						</NcActionButton>
						<NcActionButton close-after-click @click="startRun(rule)">
							<template #icon>
								<NcIconSvgWrapper :path="mdiPlayCircleOutline" />
							</template>
							{{ t('sockless_mail', 'Run on existing messages…') }}
						</NcActionButton>
						<NcActionButton :disabled="index === 0" close-after-click @click="moveRule(index, index - 1)">
							<template #icon>
								<NcIconSvgWrapper :path="mdiArrowUp" />
							</template>
							{{ t('sockless_mail', 'Move up') }}
						</NcActionButton>
						<NcActionButton :disabled="index === rules.length - 1" close-after-click @click="moveRule(index, index + 1)">
							<template #icon>
								<NcIconSvgWrapper :path="mdiArrowDown" />
							</template>
							{{ t('sockless_mail', 'Move down') }}
						</NcActionButton>
						<NcActionButton close-after-click @click="update(rules.filter((_, i) => i !== index))">
							<template #icon>
								<NcIconSvgWrapper :path="mdiDeleteOutline" />
							</template>
							{{ t('sockless_mail', 'Delete') }}
						</NcActionButton>
					</NcActions>
				</li>
			</ol>
		</div>

		<!-- The editor -->
		<form v-else class="rule-editor" @submit.prevent>
			<NcTextField v-model="draft.name"
				:label="t('sockless_mail', 'Name')"
				:error="!!errors.name"
				:helper-text="errors.name"
				autocomplete="off" />

			<fieldset class="rule-editor__group">
				<legend>{{ t('sockless_mail', 'When a new message') }}</legend>
				<div class="rule-editor__match">
					<NcCheckboxRadioSwitch v-model="draft.matchAll" type="radio" name="rule-match" :value="true">
						{{ t('sockless_mail', 'matches all conditions') }}
					</NcCheckboxRadioSwitch>
					<NcCheckboxRadioSwitch v-model="draft.matchAll" type="radio" name="rule-match" :value="false">
						{{ t('sockless_mail', 'matches any condition') }}
					</NcCheckboxRadioSwitch>
				</div>
				<div v-for="(condition, i) in draft.conditions" :key="i" class="rule-editor__row" data-condition>
					<select v-model="condition.field"
						:aria-label="t('sockless_mail', 'Field')"
						@change="fieldChanged(condition)">
						<option v-for="field in FIELDS" :key="field.id" :value="field.id">
							{{ field.label }}
						</option>
					</select>
					<input v-if="condition.field === 'header'"
						v-model="condition.header"
						class="rule-editor__header"
						:class="{ 'rule-editor__invalid': errors[`conditions[${i}].header`] }"
						:aria-label="t('sockless_mail', 'Header name')"
						:placeholder="t('sockless_mail', 'List-Id')">
					<select v-model="condition.op" :aria-label="t('sockless_mail', 'Comparison')">
						<option v-for="op in opsFor(condition.field)" :key="op.id" :value="op.id">
							{{ op.label }}
						</option>
					</select>
					<template v-if="condition.field === 'size'">
						<input :value="Math.round(Number(condition.value || 0) / 1024)"
							class="rule-editor__size"
							:class="{ 'rule-editor__invalid': errors[`conditions[${i}].value`] }"
							type="number"
							min="0"
							:aria-label="t('sockless_mail', 'Size in KB')"
							@input="condition.value = String(Math.max(0, Number($event.target.value) || 0) * 1024)">
						<span>{{ t('sockless_mail', 'KB') }}</span>
					</template>
					<input v-else-if="condition.field !== 'attachment'"
						v-model="condition.value"
						class="rule-editor__value"
						:class="{ 'rule-editor__invalid': errors[`conditions[${i}].value`] }"
						:aria-label="t('sockless_mail', 'Value')"
						:placeholder="condition.op === 'matches' ? t('sockless_mail', 'Regular expression') : ''">
					<NcButton variant="tertiary"
						:disabled="draft.conditions.length === 1"
						:aria-label="t('sockless_mail', 'Remove condition')"
						:title="t('sockless_mail', 'Remove condition')"
						@click="draft.conditions.splice(i, 1)">
						<template #icon>
							<NcIconSvgWrapper :path="mdiClose" :size="18" />
						</template>
					</NcButton>
					<p v-for="message in rowErrors('conditions', i)" :key="message" class="rule-editor__error">
						{{ message }}
					</p>
				</div>
				<p v-if="errors.conditions" class="rule-editor__error">
					{{ errors.conditions }}
				</p>
				<NcButton variant="tertiary" @click="draft.conditions.push(newCondition())">
					<template #icon>
						<NcIconSvgWrapper :path="mdiPlus" :size="18" />
					</template>
					{{ t('sockless_mail', 'Add condition') }}
				</NcButton>
			</fieldset>

			<fieldset class="rule-editor__group">
				<legend>{{ t('sockless_mail', 'Do this') }}</legend>
				<div v-for="(action, i) in draft.actions" :key="i" class="rule-editor__row" data-action>
					<select v-model="action.type" :aria-label="t('sockless_mail', 'Action')">
						<option v-for="type in ACTIONS" :key="type.id" :value="type.id">
							{{ type.label }}
						</option>
					</select>
					<select v-if="action.type === 'move' || action.type === 'copy'"
						v-model="action.folderId"
						class="rule-editor__folder"
						:class="{ 'rule-editor__invalid': errors[`actions[${i}].folderId`] }"
						:aria-label="t('sockless_mail', 'Folder')">
						<option value="" disabled>
							{{ t('sockless_mail', 'Choose a folder') }}
						</option>
						<option v-if="action.folderId && !targets.some((f) => f.id === action.folderId)" :value="action.folderId">
							{{ t('sockless_mail', '{folder} (not found)', { folder: folderName(action.folderId) }) }}
						</option>
						<option v-for="folder in targets" :key="folder.id" :value="folder.id">
							{{ folder.path }}
						</option>
					</select>
					<NcButton variant="tertiary"
						:disabled="draft.actions.length === 1"
						:aria-label="t('sockless_mail', 'Remove action')"
						:title="t('sockless_mail', 'Remove action')"
						@click="draft.actions.splice(i, 1)">
						<template #icon>
							<NcIconSvgWrapper :path="mdiClose" :size="18" />
						</template>
					</NcButton>
					<p v-for="message in rowErrors('actions', i)" :key="message" class="rule-editor__error">
						{{ message }}
					</p>
				</div>
				<p v-if="errors.actions" class="rule-editor__error">
					{{ errors.actions }}
				</p>
				<NcButton variant="tertiary" @click="draft.actions.push({ type: 'flag', folderId: '' })">
					<template #icon>
						<NcIconSvgWrapper :path="mdiPlus" :size="18" />
					</template>
					{{ t('sockless_mail', 'Add action') }}
				</NcButton>
				<NcCheckboxRadioSwitch v-model="draft.stop">
					{{ t('sockless_mail', 'Then stop: do not run later rules on the message') }}
				</NcCheckboxRadioSwitch>
				<p class="rules__hint">
					{{ t('sockless_mail', 'A rule that moves the message is always the last one to run on it.') }}
				</p>
			</fieldset>

			<NcCheckboxRadioSwitch v-model="draft.enabled" type="switch">
				{{ t('sockless_mail', 'Enabled') }}
			</NcCheckboxRadioSwitch>

			<div class="rule-editor__test">
				<NcButton :disabled="testing" @click="test">
					<template #icon>
						<NcLoadingIcon v-if="testing" :size="18" />
						<NcIconSvgWrapper v-else :path="mdiTestTube" :size="18" />
					</template>
					{{ t('sockless_mail', 'Test on recent inbox messages') }}
				</NcButton>
				<div v-if="testResult" class="rule-editor__matches" data-test-result>
					<p>
						{{ n('sockless_mail', 'Matches {matched} of the newest message.', 'Matches {matched} of the newest {count} messages.', testResult.scanned, { matched: testResult.matched, count: testResult.scanned }) }}
					</p>
					<ul v-if="testResult.matches.length">
						<li v-for="match in testResult.matches" :key="match.id">
							<span class="rule-editor__match-from">{{ match.from[0]?.name || match.from[0]?.address }}</span>
							<span class="rule-editor__match-subject">{{ match.subject || t('sockless_mail', '(no subject)') }}</span>
						</li>
					</ul>
				</div>
			</div>
			<NcNoteCard v-if="saveError" type="error" :text="saveError" />
		</form>
	</NcDialog>
</template>

<script setup>
import { mdiAlertCircleOutline, mdiArrowDown, mdiArrowUp, mdiClose, mdiDeleteOutline, mdiDragVertical, mdiMagnify, mdiPencil, mdiPlayCircleOutline, mdiPlus, mdiTestTube } from '@mdi/js'
import { showError, showSuccess } from '@nextcloud/dialogs/toast'
import { n, t } from '@nextcloud/l10n'
import NcActionButton from '@nextcloud/vue/components/NcActionButton'
import NcActions from '@nextcloud/vue/components/NcActions'
import NcButton from '@nextcloud/vue/components/NcButton'
import NcCheckboxRadioSwitch from '@nextcloud/vue/components/NcCheckboxRadioSwitch'
import NcDialog from '@nextcloud/vue/components/NcDialog'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import NcNoteCard from '@nextcloud/vue/components/NcNoteCard'
import NcTextField from '@nextcloud/vue/components/NcTextField'
import { computed, onMounted, ref } from 'vue'
import { moveTargets } from '../store/actions.js'
import { ActionError, loadFolders, mailboxState, refreshMessages } from '../store/mailbox.js'
import { ACTIONS, describeRule, FIELDS, loadRules, newCondition, newRule, opsFor, previewRun as loadPreview, runRule, saveRules, testRule } from '../store/rules.js'

const INBOX = 'SU5CT1g'

const props = defineProps({
	account: { type: Object, required: true },
	/** A new rule to open in the editor right away ("Create rule…" on a message). */
	draft: { type: Object, default: null },
})
const emit = defineEmits(['close'])

const rules = ref([])
const loading = ref(true)
const loadError = ref('')

/** Index of the rule being edited, -1 for a new one, null in the list. */
const editing = ref(null)
const draft = ref(null)
const errors = ref({})
const saveError = ref('')
const testing = ref(false)
const testResult = ref(null)

const dragFrom = ref(null)
const dragOver = ref(null)

/** "Run on existing messages": { rule, folderId, preview, busy, error }, null otherwise. */
const running = ref(null)

const targets = computed(() => moveTargets({ accountId: props.account.id, folderId: INBOX }))
const allFolders = computed(() => moveTargets({ accountId: props.account.id, folderId: null }))

onMounted(async () => {
	if (!mailboxState.folders[props.account.id]?.list?.length) {
		loadFolders(props.account.id)
	}
	try {
		rules.value = await loadRules(props.account.id)
		if (props.draft) {
			edit(-1, props.draft)
		}
	} catch (e) {
		loadError.value = t('sockless_mail', 'Could not load the rules: {error}', { error: new ActionError(e).message })
	} finally {
		loading.value = false
	}
})

/** A folder's path, or its name decoded from the id when the account no longer lists it. */
function folderName(folderId) {
	const known = targets.value.find((f) => f.id === folderId)
	if (known) {
		return known.path
	}
	try {
		const base64 = folderId.replace(/-/g, '+').replace(/_/g, '/')
		return new TextDecoder().decode(Uint8Array.from(atob(base64), (c) => c.charCodeAt(0)))
	} catch {
		return folderId
	}
}

/**
 * Saves go out one at a time: each sends the whole list, so two in flight (a switch flipped while
 * a reorder is saving) could reach the server in either order and the older list would win.
 */
let saving = Promise.resolve()
let pending = 0

function queueSave(next) {
	pending++
	const saved = saving.then(() => saveRules(props.account.id, next)).finally(() => pending--)
	saving = saved.catch(() => {})
	return saved
}

/** Saves a changed list (switch, order, delete) right away; the list shows the change at once. */
async function update(next) {
	const previous = rules.value
	rules.value = next
	try {
		const saved = await queueSave(next)
		// A later change is already showing; its own save brings the final list.
		if (pending === 0) {
			rules.value = saved
		}
	} catch (e) {
		rules.value = previous
		showError(t('sockless_mail', 'Could not save the rules: {error}', { error: new ActionError(e).message }))
	}
}

function moveRule(from, to) {
	const next = [...rules.value]
	next.splice(to, 0, ...next.splice(from, 1))
	update(next)
}

function drop(index) {
	if (dragFrom.value !== null && dragFrom.value !== index) {
		moveRule(dragFrom.value, index)
	}
	dragFrom.value = dragOver.value = null
}

/**
 * @param {number} index the rule to edit, -1 for a new one
 * @param {object} [template] what a new rule starts with
 */
function edit(index, template = null) {
	// A plain copy: structuredClone refuses Vue's reactive proxies.
	const { error, errorAt, ...source } = JSON.parse(JSON.stringify(index >= 0 ? rules.value[index] : template ?? newRule()))
	draft.value = {
		...source,
		conditions: source.conditions.map((c) => ({ ...c, header: c.header ?? '', value: c.value ?? '' })),
		actions: source.actions.map((a) => ({ ...a, folderId: a.folderId ?? '' })),
	}
	errors.value = {}
	saveError.value = ''
	testResult.value = null
	editing.value = index
}

function matchStats(rule) {
	if (!rule.matchCount) {
		return t('sockless_mail', 'No matches yet')
	}
	const last = new Date(rule.lastMatchedAt).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })
	return n('sockless_mail', 'Matched {count} message, last on {date}', 'Matched {count} messages, last on {date}', rule.matchCount, { count: rule.matchCount, date: last })
}

function startRun(rule) {
	running.value = { rule, folderId: INBOX, preview: null, busy: false, error: '' }
}

async function previewRun() {
	const run = running.value
	run.busy = true
	run.error = ''
	run.preview = null
	try {
		run.preview = await loadPreview(props.account.id, run.rule.id, run.folderId)
	} catch (e) {
		run.error = new ActionError(e).message
	} finally {
		run.busy = false
	}
}

async function applyRun() {
	const run = running.value
	run.busy = true
	run.error = ''
	try {
		const { applied } = await runRule(props.account.id, run.rule.id, run.folderId, run.preview.messageIds)
		showSuccess(n('sockless_mail', '"{name}" was applied to {count} message', '"{name}" was applied to {count} messages', applied, { name: run.rule.name, count: applied }))
		running.value = null
		rules.value = await loadRules(props.account.id)
		if (mailboxState.selected.accountId === props.account.id) {
			refreshMessages()
		}
	} catch (e) {
		run.error = new ActionError(e).message
		run.busy = false
	}
}

function fieldChanged(condition) {
	const ops = opsFor(condition.field)
	if (!ops.some((o) => o.id === condition.op)) {
		condition.op = ops[0].id
		condition.value = condition.field === 'size' ? String(1024 * 1024) : ''
	}
}

/** Messages for one condition or action row, from the server's field names. */
function rowErrors(kind, i) {
	const prefix = `${kind}[${i}]`
	return Object.entries(errors.value).filter(([key]) => key === prefix || key.startsWith(`${prefix}.`)).map(([, message]) => message)
}

/** Server field names relative to the rule ("rules[2].conditions[0].value" → "conditions[0].value"). */
function showErrors(e, prefix) {
	const error = new ActionError(e)
	errors.value = Object.fromEntries(Object.entries(error.fieldErrors)
		.filter(([key]) => key.startsWith(prefix))
		.map(([key, message]) => [key.slice(prefix.length).replace(/^\./, ''), message]))
	saveError.value = Object.keys(errors.value).length ? '' : error.message
}

async function save() {
	const next = [...rules.value]
	const index = editing.value >= 0 ? editing.value : next.length
	next.splice(index, editing.value >= 0 ? 1 : 0, draft.value)
	try {
		rules.value = await queueSave(next)
		editing.value = null
		return true
	} catch (e) {
		showErrors(e, `rules[${index}]`)
		return false
	}
}

async function test() {
	testing.value = true
	testResult.value = null
	try {
		testResult.value = await testRule(props.account.id, draft.value)
		errors.value = {}
		saveError.value = ''
	} catch (e) {
		showErrors(e, 'rule')
	} finally {
		testing.value = false
	}
}

const buttons = computed(() => running.value
	? [
		{
			label: t('sockless_mail', 'Back'),
			callback: () => {
				running.value = null
				return false
			},
		},
		{
			label: running.value.preview
				? n('sockless_mail', 'Apply to {count} message', 'Apply to {count} messages', running.value.preview.matched, { count: running.value.preview.matched })
				: t('sockless_mail', 'Apply'),
			variant: 'primary',
			disabled: !running.value.preview?.matched || running.value.busy,
			callback: () => {
				applyRun()
				return false
			},
		},
	]
	: editing.value === null
	? [
		{ label: t('sockless_mail', 'Done') },
		{
			label: t('sockless_mail', 'New rule'),
			variant: 'primary',
			disabled: loading.value || !!loadError.value,
			callback: () => {
				edit(-1)
				return false
			},
		},
	]
	: [
		{
			label: t('sockless_mail', 'Back'),
			callback: () => {
				editing.value = null
				return false
			},
		},
		{
			label: t('sockless_mail', 'Save rule'),
			variant: 'primary',
			callback: async () => {
				await save()
				return false
			},
		},
	])
</script>

<style scoped>
.rules__hint {
	margin-block: 8px;
	color: var(--color-text-maxcontrast);
}

.rules__center {
	display: flex;
	justify-content: center;
	padding: 16px;
}

.rules__list {
	margin-block-end: 12px;
}

.rules__item {
	display: flex;
	align-items: center;
	gap: 8px;
	padding: 4px;
	border-bottom: 1px solid var(--color-border);
}

.rules__item--off .rules__text {
	opacity: .6;
}

.rules__item--over {
	box-shadow: inset 0 2px 0 var(--color-primary-element);
}

.rules__handle {
	cursor: grab;
	color: var(--color-text-maxcontrast);
	display: flex;
}

.rules__text {
	all: unset;
	flex: 1;
	min-width: 0;
	display: flex;
	flex-direction: column;
	cursor: pointer;
	padding: 4px;
	border-radius: var(--border-radius);
}

.rules__text:hover,
.rules__text:focus-visible {
	background: var(--color-background-hover);
}

.rules__text:focus-visible {
	outline: 2px solid var(--color-main-text);
}

.rules__name {
	font-weight: bold;
}

.rules__summary {
	color: var(--color-text-maxcontrast);
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.rules__stats {
	color: var(--color-text-maxcontrast);
	font-size: var(--font-size-small, 13px);
}

.rule-run {
	display: flex;
	flex-direction: column;
	gap: 8px;
	padding-block-end: 8px;
}

.rules__error {
	display: flex;
	align-items: center;
	gap: 4px;
	color: var(--color-error-text);
}

.rule-editor {
	display: flex;
	flex-direction: column;
	gap: 8px;
	padding-block-end: 8px;
}

.rule-editor__group {
	display: flex;
	flex-direction: column;
	gap: 4px;
	margin-block-start: 8px;
}

.rule-editor__group legend {
	font-weight: bold;
	margin-block-end: 4px;
}

.rule-editor__match {
	display: flex;
	gap: 16px;
}

.rule-editor__row {
	display: flex;
	flex-wrap: wrap;
	align-items: center;
	gap: 8px;
}

.rule-editor__row select,
.rule-editor__row input {
	margin: 0;
}

.rule-editor__value,
.rule-editor__folder {
	flex: 1;
	min-width: 160px;
}

.rule-editor__header {
	width: 140px;
}

.rule-editor__size {
	width: 100px;
}

.rule-editor__invalid {
	border-color: var(--color-error) !important;
}

.rule-editor__error {
	flex-basis: 100%;
	color: var(--color-error-text);
	margin: 0;
}

.rule-editor__test {
	display: flex;
	flex-direction: column;
	align-items: flex-start;
	gap: 8px;
	margin-block-start: 8px;
}

.rule-editor__matches {
	width: 100%;
}

.rule-editor__matches ul {
	max-height: 200px;
	overflow-y: auto;
	border: 1px solid var(--color-border);
	border-radius: var(--border-radius);
}

.rule-editor__matches li {
	display: flex;
	gap: 8px;
	padding: 4px 8px;
	border-bottom: 1px solid var(--color-border);
}

.rule-editor__match-from {
	flex: 0 0 30%;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
	font-weight: bold;
}

.rule-editor__match-subject {
	flex: 1;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}
</style>
