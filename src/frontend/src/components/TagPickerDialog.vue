<template>
	<!-- Each tick applies at once, as categories do in Outlook: the dialog only closes. -->
	<NcDialog :name="request.rows.length > 1
			? n('sockless_mail', 'Tag {count} message', 'Tag {count} messages', request.rows.length, { count: request.rows.length })
			: t('sockless_mail', 'Tags')"
		:open="true"
		size="small"
		:buttons="buttons"
		@update:open="(open) => !open && emit('close')">
		<NcTextField v-model="filter"
			:label="tagsState.list.length ? t('sockless_mail', 'Find or create a tag') : t('sockless_mail', 'New tag')"
			autocomplete="off"
			@keydown.enter.prevent="createFromFilter" />
		<div class="tag-picker__list">
			<NcCheckboxRadioSwitch v-for="tag in shown"
				:key="tag.id"
				:model-value="state(tag) === 'all'"
				:indeterminate="state(tag) === 'some'"
				@update:model-value="toggle(tag)">
				<span class="tag-picker__tag">
					<span class="tag-picker__dot" :style="{ background: tag.color }" aria-hidden="true" />
					{{ tag.name }}
				</span>
			</NcCheckboxRadioSwitch>
			<NcButton v-if="canCreate"
				variant="tertiary"
				wide
				:disabled="creating"
				@click="createFromFilter">
				<template #icon>
					<NcIconSvgWrapper :path="mdiTagPlusOutline" />
				</template>
				{{ t('sockless_mail', 'Create tag "{name}"', { name: filter.trim() }) }}
			</NcButton>
			<p v-else-if="shown.length === 0" class="tag-picker__empty">
				{{ t('sockless_mail', 'No tags yet. Type a name to create one.') }}
			</p>
		</div>
	</NcDialog>
</template>

<script setup>
import { mdiTagPlusOutline } from '@mdi/js'
import { showError } from '@nextcloud/dialogs/toast'
import { n, t } from '@nextcloud/l10n'
import NcButton from '@nextcloud/vue/components/NcButton'
import NcCheckboxRadioSwitch from '@nextcloud/vue/components/NcCheckboxRadioSwitch'
import NcDialog from '@nextcloud/vue/components/NcDialog'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import NcTextField from '@nextcloud/vue/components/NcTextField'
import { computed, ref } from 'vue'
import { dialogs, setTagOnRows } from '../store/actions.js'
import { messagesById } from '../store/mailbox.js'
import { createTag, hasTag, TAG_COLORS, tagsState } from '../store/tags.js'

const props = defineProps({
	/** { rows } from dialogs.tagPicker */
	request: { type: Object, required: true },
})
const emit = defineEmits(['close'])

const filter = ref('')
const creating = ref(false)

const shown = computed(() => {
	const term = filter.value.trim().toLowerCase()
	return term ? tagsState.list.filter((tag) => tag.name.toLowerCase().includes(term)) : tagsState.list
})
const canCreate = computed(() => {
	const name = filter.value.trim().toLowerCase()
	return name !== '' && !tagsState.list.some((tag) => tag.name.toLowerCase() === name)
})

const buttons = computed(() => [
	{
		label: t('sockless_mail', 'Manage tags…'),
		callback: () => {
			dialogs.tags = true
		},
	},
	{ label: t('sockless_mail', 'Done'), variant: 'primary' },
])

/** The row's message as the lists hold it now: a refresh while the dialog is open replaces the objects. */
function current(row) {
	return messagesById({ accountId: row.accountId, folderId: row.folderId }, [row.message.id])[0] ?? row.message
}

/** 'all', 'some' or 'none' of the messages carry the tag. */
function state(tag) {
	const count = props.request.rows.filter((row) => hasTag(current(row), tag)).length
	return count === 0 ? 'none' : count === props.request.rows.length ? 'all' : 'some'
}

function toggle(tag) {
	// A mixed selection gets the tag first, as a second click then clears it from all.
	setTagOnRows(props.request.rows, tag, state(tag) !== 'all')
}

async function createFromFilter() {
	if (!canCreate.value || creating.value) {
		return
	}
	creating.value = true
	try {
		const tag = await createTag({ name: filter.value.trim(), color: TAG_COLORS[tagsState.list.length % TAG_COLORS.length] })
		filter.value = ''
		await setTagOnRows(props.request.rows, tag, true)
	} catch (e) {
		showError(t('sockless_mail', 'Could not create the tag: {error}', { error: e.message }))
	} finally {
		creating.value = false
	}
}
</script>

<style scoped>
.tag-picker__list {
	margin-top: 8px;
	max-height: 50vh;
	overflow-y: auto;
}

.tag-picker__tag {
	display: inline-flex;
	align-items: center;
	gap: 8px;
}

.tag-picker__dot {
	flex: none;
	width: 12px;
	height: 12px;
	border-radius: 50%;
}

.tag-picker__empty {
	padding: 8px;
	color: var(--color-text-maxcontrast);
}
</style>
