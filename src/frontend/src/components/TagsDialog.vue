<template>
	<NcDialog :name="t('sockless_mail', 'Tags')"
		:open="true"
		size="normal"
		:buttons="[{ label: t('sockless_mail', 'Done'), variant: 'primary' }]"
		@update:open="(open) => !open && emit('close')">
		<p class="tags__hint">
			{{ t('sockless_mail', 'Tags work across all accounts. Most mail servers keep them, so other mail programs see them too.') }}
		</p>
		<p v-if="appKept.length" class="tags__hint">
			{{ n('sockless_mail',
				'The mail server of {accounts} keeps no tags; Sockless Mail keeps them for it, so other mail programs do not see them.',
				'The mail servers of {accounts} keep no tags; Sockless Mail keeps them for them, so other mail programs do not see them.',
				appKept.length, { accounts: appKept.join(', ') }) }}
		</p>
		<p v-if="outlook.length" class="tags__hint">
			{{ n('sockless_mail',
				'Tags of {accounts} are Outlook categories, so Outlook shows them too.',
				'Tags of {accounts} are Outlook categories, so Outlook shows them too.',
				outlook.length, { accounts: outlook.join(', ') }) }}
		</p>
		<p v-if="needSignIn.length" class="tags__hint">
			{{ n('sockless_mail',
				'Sign in to {accounts} again in the account settings to show its tags in Outlook as categories.',
				'Sign in to {accounts} again in the account settings to show their tags in Outlook as categories.',
				needSignIn.length, { accounts: needSignIn.join(', ') }) }}
		</p>

		<form class="tags__new" @submit.prevent="add">
			<NcColorPicker v-model="newColor" :palette="TAG_COLORS">
				<button type="button" class="tags__swatch" :style="{ background: newColor }" :aria-label="t('sockless_mail', 'Color of the new tag')" />
			</NcColorPicker>
			<NcTextField v-model="newName"
				:label="t('sockless_mail', 'New tag')"
				:error="Boolean(newError)"
				:helper-text="newError"
				autocomplete="off" />
			<NcButton type="submit" :disabled="!newName.trim() || busy">
				{{ t('sockless_mail', 'Add') }}
			</NcButton>
		</form>

		<ul v-if="tagsState.list.length" class="tags__list">
			<li v-for="(tag, i) in tagsState.list" :key="tag.id" class="tags__row">
				<NcColorPicker :model-value="tag.color" :palette="TAG_COLORS" @update:model-value="(color) => save(tag, { color })">
					<button type="button" class="tags__swatch" :style="{ background: tag.color }" :aria-label="t('sockless_mail', 'Color of {name}', { name: tag.name })" />
				</NcColorPicker>
				<NcTextField v-if="editing === tag.id"
					v-model="editName"
					class="tags__name"
					:label="t('sockless_mail', 'Name')"
					:error="Boolean(editError)"
					:helper-text="editError"
					autocomplete="off"
					@keydown.enter.prevent="rename(tag)"
					@keydown.esc.stop.prevent="editing = null" />
				<span v-else class="tags__name">
					<TagChips :tags="[tag]" />
					<span class="tags__keyword" :title="t('sockless_mail', 'The keyword the mail server stores')">{{ tag.keyword }}</span>
				</span>
				<NcActions :inline="1">
					<NcActionButton v-if="editing === tag.id" @click="rename(tag)">
						<template #icon>
							<NcIconSvgWrapper :path="mdiCheck" />
						</template>
						{{ t('sockless_mail', 'Save name') }}
					</NcActionButton>
					<NcActionButton v-else @click="startEdit(tag)">
						<template #icon>
							<NcIconSvgWrapper :path="mdiPencilOutline" />
						</template>
						{{ t('sockless_mail', 'Rename') }}
					</NcActionButton>
					<NcActionButton :disabled="i === 0" close-after-click @click="move(i, -1)">
						<template #icon>
							<NcIconSvgWrapper :path="mdiArrowUp" />
						</template>
						{{ t('sockless_mail', 'Move up') }}
					</NcActionButton>
					<NcActionButton :disabled="i === tagsState.list.length - 1" close-after-click @click="move(i, 1)">
						<template #icon>
							<NcIconSvgWrapper :path="mdiArrowDown" />
						</template>
						{{ t('sockless_mail', 'Move down') }}
					</NcActionButton>
					<NcActionButton close-after-click @click="toDelete = tag; fromMessages = true">
						<template #icon>
							<NcIconSvgWrapper :path="mdiDeleteOutline" />
						</template>
						{{ t('sockless_mail', 'Delete') }}
					</NcActionButton>
				</NcActions>
			</li>
		</ul>
		<p v-else class="tags__hint">
			{{ t('sockless_mail', 'No tags yet.') }}
		</p>

		<template v-if="suggestions.length">
			<h3 class="tags__heading">
				{{ t('sockless_mail', 'Found on your messages') }}
			</h3>
			<p class="tags__hint">
				{{ t('sockless_mail', 'Other mail programs (or Outlook) tagged messages with these. Use one as a tag to show it here as well.') }}
			</p>
			<ul class="tags__list">
				<li v-for="suggestion in suggestions" :key="suggestion.keyword" class="tags__row">
					<span class="tags__swatch tags__swatch--static" :style="{ background: suggestion.color }" aria-hidden="true" />
					<span class="tags__name">
						{{ suggestion.name }}
						<span v-if="suggestion.outlook" class="tags__keyword">{{ t('sockless_mail', 'Outlook category') }}</span>
						<span v-else class="tags__keyword">{{ suggestion.keyword }} · {{ n('sockless_mail', '{count} message', '{count} messages', suggestion.count, { count: suggestion.count }) }}</span>
					</span>
					<NcButton variant="secondary" :disabled="busy" @click="adopt(suggestion)">
						{{ t('sockless_mail', 'Use as tag') }}
					</NcButton>
				</li>
			</ul>
		</template>

		<NcDialog v-if="toDelete"
			:name="t('sockless_mail', 'Delete {name}?', { name: toDelete.name })"
			:open="true"
			:buttons="deleteButtons"
			@update:open="(open) => !open && (toDelete = null)">
			<NcCheckboxRadioSwitch v-model="fromMessages">
				{{ t('sockless_mail', 'Also remove it from all messages') }}
			</NcCheckboxRadioSwitch>
			<p class="tags__hint">
				{{ fromMessages
					? t('sockless_mail', 'Other mail programs will no longer see the tag either.')
					: t('sockless_mail', 'Messages keep the keyword on the server; it shows up again under "Found on your messages".') }}
			</p>
		</NcDialog>
	</NcDialog>
</template>

<script setup>
import { mdiArrowDown, mdiArrowUp, mdiCheck, mdiDeleteOutline, mdiPencilOutline } from '@mdi/js'
import { showError } from '@nextcloud/dialogs/toast'
import { n, t } from '@nextcloud/l10n'
import NcActionButton from '@nextcloud/vue/components/NcActionButton'
import NcActions from '@nextcloud/vue/components/NcActions'
import NcButton from '@nextcloud/vue/components/NcButton'
import NcCheckboxRadioSwitch from '@nextcloud/vue/components/NcCheckboxRadioSwitch'
import NcColorPicker from '@nextcloud/vue/components/NcColorPicker'
import NcDialog from '@nextcloud/vue/components/NcDialog'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import NcTextField from '@nextcloud/vue/components/NcTextField'
import { computed, onMounted, ref } from 'vue'
import { accountsState } from '../store/accounts.js'
import { mailboxState } from '../store/mailbox.js'
import { openFolder, openUnified } from '../store/navigation.js'
import { searchState, setFilter } from '../store/search.js'
import { createTag, deleteTag, loadSuggestions, loadTagCounts, reorderTags, TAG_COLORS, tagsState, updateTag } from '../store/tags.js'
import { folderOfUse } from '../store/unified.js'
import TagChips from './TagChips.vue'

const emit = defineEmits(['close'])

const newName = ref('')
const newColor = ref(TAG_COLORS[tagsState.list.length % TAG_COLORS.length])
const newError = ref('')
const busy = ref(false)
const editing = ref(null)
const editName = ref('')
const editError = ref('')
const suggestions = ref([])
const toDelete = ref(null)
const fromMessages = ref(true)

/** Signed in with Microsoft: their tags can be Outlook categories (see the server's OutlookCategories). */
function isMicrosoft(account) {
	return account.authMethod === 'oAuth2' && account.imap.host === 'outlook.office365.com'
}

const appKept = computed(() => accountsState.accounts.filter((a) => a.tagStorage === 'local' && !isMicrosoft(a)).map((a) => a.displayName))
const outlook = computed(() => accountsState.accounts.filter((a) => a.tagStorage === 'graph').map((a) => a.displayName))
// Kept by the app until a sign-in with the Graph consent; the scan then copies them to Outlook.
const needSignIn = computed(() => accountsState.accounts.filter((a) => a.tagStorage === 'local' && isMicrosoft(a)).map((a) => a.displayName))

onMounted(refreshSuggestions)

async function refreshSuggestions() {
	try {
		suggestions.value = await loadSuggestions()
	} catch {
		// Suggestions are a convenience; without them the dialog works as well.
	}
}

async function add() {
	busy.value = true
	newError.value = ''
	try {
		await createTag({ name: newName.value.trim(), color: newColor.value })
		newName.value = ''
		newColor.value = TAG_COLORS[tagsState.list.length % TAG_COLORS.length]
	} catch (e) {
		newError.value = e.fieldErrors?.name ?? e.message
	} finally {
		busy.value = false
	}
}

function startEdit(tag) {
	editing.value = tag.id
	editName.value = tag.name
	editError.value = ''
}

async function rename(tag) {
	if (editName.value.trim() === tag.name) {
		editing.value = null
		return
	}
	try {
		await updateTag(tag.id, { name: editName.value.trim() })
		editing.value = null
	} catch (e) {
		editError.value = e.fieldErrors?.name ?? e.message
	}
}

async function save(tag, change) {
	try {
		await updateTag(tag.id, change)
	} catch (e) {
		showError(t('sockless_mail', 'Could not change {name}: {error}', { name: tag.name, error: e.message }))
	}
}

async function move(index, by) {
	const ids = tagsState.list.map((tag) => tag.id)
	const [moved] = ids.splice(index, 1)
	ids.splice(index + by, 0, moved)
	try {
		await reorderTags(ids)
	} catch (e) {
		showError(t('sockless_mail', 'Could not reorder the tags: {error}', { error: e.message }))
	}
}

async function adopt(suggestion) {
	busy.value = true
	try {
		await createTag({ name: suggestion.name, color: suggestion.color, keyword: suggestion.keyword })
		suggestions.value = suggestions.value.filter((s) => s.keyword !== suggestion.keyword)
	} catch (e) {
		showError(t('sockless_mail', 'Could not add the tag: {error}', { error: e.fieldErrors?.name ?? e.message }))
	} finally {
		busy.value = false
	}
}

/** The deleted tag's folder or filter may be shown: show the inbox, or the folder without the filter. */
function leaveTag(id) {
	const { accountId, unified } = mailboxState.selected
	if (unified?.startsWith(`tag:${id}`)) {
		if (accountId) {
			openFolder(accountId, folderOfUse(accountId, 'inbox'))
		} else {
			openUnified('inbox')
		}
	} else if (searchState.filter === `tag:${id}`) {
		setFilter('')
	}
	loadTagCounts()
}

const deleteButtons = computed(() => [
	{ label: t('sockless_mail', 'Cancel') },
	{
		label: t('sockless_mail', 'Delete'),
		variant: 'error',
		callback: async () => {
			const tag = toDelete.value
			try {
				await deleteTag(tag.id, fromMessages.value)
				leaveTag(tag.id)
				if (!fromMessages.value) {
					refreshSuggestions()
				}
			} catch (e) {
				showError(t('sockless_mail', 'Could not delete {name}: {error}', { name: tag.name, error: e.message }))
			}
		},
	},
])
</script>

<style scoped>
.tags__hint {
	margin-block: 8px;
	color: var(--color-text-maxcontrast);
}

.tags__heading {
	margin-block: 16px 0;
	font-size: 1.1em;
}

.tags__new {
	display: flex;
	align-items: flex-start;
	gap: 8px;
	margin-block: 12px;
}

.tags__new :deep(.input-field) {
	flex: 1;
}

.tags__swatch {
	flex: none;
	width: 28px;
	height: 28px;
	min-height: 0;
	margin: 0;
	margin-top: 6px;
	padding: 0;
	border: 2px solid var(--color-border-dark);
	border-radius: 50%;
	cursor: pointer;
}

.tags__swatch--static {
	display: inline-block;
	width: 16px;
	height: 16px;
	margin: 0;
	cursor: default;
}

.tags__list {
	margin-block-end: 12px;
}

.tags__row {
	display: flex;
	align-items: center;
	gap: 8px;
	min-height: 44px;
	padding-inline-start: 4px;
	border-bottom: 1px solid var(--color-border);
}

.tags__row .tags__swatch {
	margin-top: 0;
}

.tags__name {
	display: flex;
	flex: 1;
	align-items: center;
	gap: 8px;
	min-width: 0;
}

.tags__keyword {
	overflow: hidden;
	color: var(--color-text-maxcontrast);
	font-size: 0.85em;
	text-overflow: ellipsis;
	white-space: nowrap;
}
</style>
