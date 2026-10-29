<template>
	<!-- Closing goes through NcDialog's buttons (see AccountDialog.vue for why). -->
	<NcDialog :name="t('sockless_mail', 'Add to contacts')"
		:open="true"
		size="small"
		:buttons="buttons"
		@update:open="(open) => !open && emit('close')">
		<NcLoadingIcon v-if="loading" :size="32" />
		<template v-else>
			<NcNoteCard v-if="loadError" type="error" :text="loadError" />
			<NcNoteCard v-else-if="existing.length"
				type="info"
				:text="n('sockless_mail', '{address} is already in your contacts as {names}.', '{address} is already in your contacts as {names}.', existing.length, { address: request.address, names: existing.map((c) => `${c.name} (${c.addressBook})`).join(', ') })" />
			<NcNoteCard v-if="!loadError && !writableBooks.length"
				type="warning"
				:text="t('sockless_mail', 'You have no address book you can add contacts to. Create one in the Contacts app.')" />

			<template v-if="writableBooks.length">
				<div class="add-contact__modes" role="radiogroup" :aria-label="t('sockless_mail', 'Where to add the address')">
					<NcCheckboxRadioSwitch v-model="mode" type="radio" name="add-contact-mode" value="new">
						{{ t('sockless_mail', 'New contact') }}
					</NcCheckboxRadioSwitch>
					<NcCheckboxRadioSwitch v-model="mode" type="radio" name="add-contact-mode" value="existing">
						{{ t('sockless_mail', 'Add to an existing contact') }}
					</NcCheckboxRadioSwitch>
				</div>

				<template v-if="mode === 'new'">
					<NcTextField v-model="name"
						:label="t('sockless_mail', 'Name')"
						:error="!!fieldErrors.name"
						:helper-text="fieldErrors.name"
						autocomplete="off" />
					<p class="add-contact__address">
						{{ request.address }}
					</p>
					<NcSelect v-if="writableBooks.length > 1"
						v-model="book"
						:options="writableBooks"
						label="name"
						:clearable="false"
						:input-label="t('sockless_mail', 'Address book')" />
				</template>

				<template v-else>
					<NcTextField v-model="query"
						:label="t('sockless_mail', 'Find contact')"
						autocomplete="off" />
					<div class="add-contact__results" role="radiogroup" :aria-label="t('sockless_mail', 'Contact')">
						<NcCheckboxRadioSwitch v-for="contact in candidates"
							:key="contact.id"
							v-model="contactId"
							type="radio"
							name="add-contact-target"
							:value="contact.id">
							{{ contact.name }} <span class="add-contact__book">{{ contact.addressBook }}</span>
						</NcCheckboxRadioSwitch>
						<p v-if="query.trim().length >= 2 && !candidates.length" class="add-contact__empty">
							{{ t('sockless_mail', 'No matching contact') }}
						</p>
					</div>
				</template>
			</template>
			<NcNoteCard v-if="saveError" type="error" :text="saveError" />
		</template>
	</NcDialog>
</template>

<script setup>
import { showSuccess } from '@nextcloud/dialogs/toast'
import { n, t } from '@nextcloud/l10n'
import NcCheckboxRadioSwitch from '@nextcloud/vue/components/NcCheckboxRadioSwitch'
import NcDialog from '@nextcloud/vue/components/NcDialog'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import NcNoteCard from '@nextcloud/vue/components/NcNoteCard'
import NcSelect from '@nextcloud/vue/components/NcSelect'
import NcTextField from '@nextcloud/vue/components/NcTextField'
import { computed, onMounted, ref, watch } from 'vue'
import { addAddressToContact, addressBooks, contactsWithAddress, createContact, searchContacts } from '../store/contacts.js'
import { ActionError } from '../store/mailbox.js'

const props = defineProps({
	/** { name, address } from dialogs.addContact */
	request: { type: Object, required: true },
})
const emit = defineEmits(['close'])

const loading = ref(true)
const loadError = ref('')
const saveError = ref('')
const fieldErrors = ref({})
const books = ref([])
const existing = ref([])
const mode = ref('new')
const name = ref(props.request.name || '')
const book = ref(null)
const query = ref(props.request.name || '')
const found = ref([])
const contactId = ref(null)

const writableBooks = computed(() => books.value.filter((b) => b.writable))
const writableIds = computed(() => new Set(writableBooks.value.map((b) => b.id)))

/** One entry per contact (search answers per address), in books the user can change. */
const candidates = computed(() => {
	const seen = new Set()
	return found.value.filter((c) => writableIds.value.has(c.id.split('/')[0]) && !seen.has(c.id) && seen.add(c.id))
})

onMounted(async () => {
	try {
		;[books.value, existing.value] = await Promise.all([addressBooks(), contactsWithAddress(props.request.address)])
		book.value = writableBooks.value[0] ?? null
	} catch (e) {
		loadError.value = new ActionError(e).message
	} finally {
		loading.value = false
	}
})

let searchTimer
watch(query, (value) => {
	window.clearTimeout(searchTimer)
	searchTimer = window.setTimeout(async () => {
		found.value = (await searchContacts(value)).contacts
		if (!candidates.value.some((c) => c.id === contactId.value)) {
			contactId.value = candidates.value[0]?.id ?? null
		}
	}, 150)
}, { immediate: true })

const buttons = computed(() => [
	{ label: t('sockless_mail', 'Cancel') },
	{
		label: t('sockless_mail', 'Add'),
		variant: 'primary',
		disabled: loading.value || !writableBooks.value.length || (mode.value === 'existing' && !contactId.value),
		callback: async () => {
			saveError.value = ''
			fieldErrors.value = {}
			try {
				if (mode.value === 'new') {
					await createContact(book.value.id, name.value, props.request.address)
					showSuccess(t('sockless_mail', '{name} was added to {book}', { name: name.value || props.request.address, book: book.value.name }))
				} else {
					const contact = candidates.value.find((c) => c.id === contactId.value)
					await addAddressToContact(contactId.value, props.request.address)
					showSuccess(t('sockless_mail', '{address} was added to {name}', { address: props.request.address, name: contact?.name ?? '' }))
				}
			} catch (e) {
				const error = new ActionError(e)
				fieldErrors.value = error.fieldErrors
				saveError.value = error.fieldErrors.name ? '' : error.message
				return false
			}
		},
	},
])
</script>

<style scoped>
.add-contact__modes {
	display: flex;
	gap: 16px;
	margin-bottom: 8px;
}

.add-contact__address {
	margin: 4px 0 12px;
	color: var(--color-text-maxcontrast);
}

.add-contact__results {
	max-height: 240px;
	margin-top: 8px;
	overflow-y: auto;
}

.add-contact__book,
.add-contact__empty {
	color: var(--color-text-maxcontrast);
}
</style>
