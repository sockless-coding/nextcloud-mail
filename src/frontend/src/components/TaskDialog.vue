<template>
	<!-- Closing goes through NcDialog's buttons (see AccountDialog.vue for why). -->
	<NcDialog :name="t('sockless_mail', 'Create task')"
		:open="true"
		size="small"
		:buttons="buttons"
		@update:open="(open) => !open && emit('close')">
		<NcLoadingIcon v-if="loading" :size="32" />
		<template v-else>
			<NcNoteCard v-if="!writableLists.length"
				type="warning"
				:text="t('sockless_mail', 'You have no task list you can add tasks to. Create one in the Tasks app.')" />
			<div v-else class="task-dialog">
				<NcTextField v-model="title"
					:label="t('sockless_mail', 'Title')"
					:error="!!fieldErrors.title"
					:helper-text="fieldErrors.title"
					autocomplete="off" />
				<NcSelect v-if="writableLists.length > 1"
					v-model="list"
					:options="writableLists"
					label="name"
					:clearable="false"
					:input-label="t('sockless_mail', 'Task list')" />

				<fieldset class="task-dialog__due">
					<legend>{{ t('sockless_mail', 'Due') }}</legend>
					<div class="task-dialog__quick">
						<button v-for="option in QUICK"
							:key="option.id"
							type="button"
							class="task-dialog__chip"
							:class="{ 'task-dialog__chip--active': quick === option.id }"
							:aria-pressed="String(quick === option.id)"
							@click="pick(option)">
							{{ option.label }}
						</button>
					</div>
					<div v-if="dueDate" class="task-dialog__when">
						<NcDateTimePickerNative v-model="dueDate"
							type="date"
							:label="t('sockless_mail', 'Date')"
							@update:model-value="quick = 'date'" />
						<NcCheckboxRadioSwitch v-model="withTime">
							{{ t('sockless_mail', 'At a time') }}
						</NcCheckboxRadioSwitch>
						<NcDateTimePickerNative v-if="withTime"
							v-model="dueTime"
							type="time"
							:label="t('sockless_mail', 'Time')" />
					</div>
					<p v-if="fieldErrors.due" class="task-dialog__error">
						{{ fieldErrors.due }}
					</p>
				</fieldset>

				<NcCheckboxRadioSwitch v-model="includeText">
					{{ t('sockless_mail', 'Include the message text in the notes') }}
				</NcCheckboxRadioSwitch>
			</div>
			<NcNoteCard v-if="saveError" type="error" :text="saveError" />
		</template>
	</NcDialog>
</template>

<script setup>
import { showError, showSuccess } from '@nextcloud/dialogs/toast'
import { getFirstDay, t } from '@nextcloud/l10n'
import NcCheckboxRadioSwitch from '@nextcloud/vue/components/NcCheckboxRadioSwitch'
import NcDateTimePickerNative from '@nextcloud/vue/components/NcDateTimePickerNative'
import NcDialog from '@nextcloud/vue/components/NcDialog'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import NcNoteCard from '@nextcloud/vue/components/NcNoteCard'
import NcSelect from '@nextcloud/vue/components/NcSelect'
import NcTextField from '@nextcloud/vue/components/NcTextField'
import { computed, onMounted, ref } from 'vue'
import { ActionError } from '../store/mailbox.js'
import { createTask, loadTasks, preferredList, taskUrl } from '../store/tasks.js'

const props = defineProps({
	/** { where, message } from dialogs.createTask */
	request: { type: Object, required: true },
})
const emit = defineEmits(['close'])

const DAY = 24 * 60 * 60 * 1000

function today() {
	const now = new Date()
	return new Date(now.getFullYear(), now.getMonth(), now.getDate())
}

/** The first day of next week, by the user's locale (Monday or Sunday). */
function nextWeek() {
	const start = today()
	const ahead = (getFirstDay() - start.getDay() + 7) % 7 || 7
	return new Date(start.getTime() + ahead * DAY)
}

const QUICK = [
	{ id: 'none', label: t('sockless_mail', 'No due date'), date: () => null },
	{ id: 'today', label: t('sockless_mail', 'Today'), date: today },
	{ id: 'tomorrow', label: t('sockless_mail', 'Tomorrow'), date: () => new Date(today().getTime() + DAY) },
	{ id: 'nextWeek', label: t('sockless_mail', 'Next week'), date: nextWeek },
	{ id: 'date', label: t('sockless_mail', 'Pick a date'), date: () => new Date(today().getTime() + DAY) },
]

const loading = ref(true)
const lists = ref([])
const list = ref(null)
const title = ref(props.request.message.subject || '')
const quick = ref('none')
const dueDate = ref(null)
const withTime = ref(false)
const dueTime = ref(new Date(2000, 0, 1, 9, 0))
const includeText = ref(true)
const saveError = ref('')
const fieldErrors = ref({})

const writableLists = computed(() => lists.value.filter((l) => l.writable))

onMounted(async () => {
	// Fresh lists: one may have been added in Tasks since the page loaded.
	const state = await loadTasks()
	lists.value = state.lists
	list.value = preferredList(state.lists)
	loading.value = false
})

function pick(option) {
	quick.value = option.id
	// "Pick a date" keeps a date already chosen.
	if (option.id !== 'date' || !dueDate.value) {
		dueDate.value = option.date()
	}
}

const pad = (value) => String(value).padStart(2, '0')

/** "yyyy-MM-dd" for a day; with a time, the moment in UTC (the browser knows the zone). */
function due() {
	const date = dueDate.value
	if (!date) {
		return null
	}
	if (withTime.value && dueTime.value) {
		return new Date(date.getFullYear(), date.getMonth(), date.getDate(), dueTime.value.getHours(), dueTime.value.getMinutes()).toISOString()
	}
	return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`
}

const buttons = computed(() => [
	{ label: t('sockless_mail', 'Cancel') },
	{
		label: t('sockless_mail', 'Create task'),
		variant: 'primary',
		disabled: loading.value || !list.value || !title.value.trim(),
		callback: async () => {
			saveError.value = ''
			fieldErrors.value = {}
			const { where, message } = props.request
			try {
				const created = await createTask(where, message.id, {
					calendarId: list.value.id,
					title: title.value,
					due: due(),
					includeText: includeText.value,
				})
				const url = taskUrl(created)
				showSuccess(t('sockless_mail', 'Task created in {list}. Click to open it in Tasks.', { list: list.value.name }), {
					timeout: 8000,
					onClick: () => window.open(url, '_blank', 'noopener'),
				})
			} catch (e) {
				const error = new ActionError(e)
				fieldErrors.value = error.fieldErrors
				if (error.code === 'tasks_unavailable') {
					showError(error.message)
					loadTasks()
					return
				}
				saveError.value = error.fieldErrors.title || error.fieldErrors.due ? '' : error.message
				return false
			}
		},
	},
])
</script>

<style scoped>
.task-dialog {
	display: flex;
	flex-direction: column;
	gap: 12px;
}

.task-dialog__due {
	margin: 0;
	padding: 0;
	border: 0;
}

.task-dialog__due legend {
	margin-bottom: 6px;
	font-weight: 600;
}

.task-dialog__quick {
	display: flex;
	flex-wrap: wrap;
	gap: 6px;
}

.task-dialog__chip {
	--sm-button-background: transparent;
	min-height: 0;
	height: 30px;
	margin: 0;
	padding: 0 12px;
	border: 1px solid var(--color-border-dark);
	border-radius: 15px;
	background: transparent;
	color: var(--color-main-text);
	font: inherit;
	cursor: pointer;
}

.task-dialog__chip:hover {
	background: var(--color-background-hover);
}

.task-dialog__chip--active,
.task-dialog__chip--active:hover {
	border-color: var(--color-primary-element);
	background: var(--color-primary-element-light);
	color: var(--color-primary-element-light-text);
}

.task-dialog__when {
	display: flex;
	flex-wrap: wrap;
	align-items: flex-end;
	gap: 8px 16px;
	margin-top: 10px;
}

.task-dialog__error {
	margin: 6px 0 0;
	color: var(--color-error-text);
}
</style>
