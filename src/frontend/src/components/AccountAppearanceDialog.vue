<template>
	<NcDialog :name="t('sockless_mail', 'Customize {name}', { name: account.displayName })"
		:open="true"
		size="normal"
		:buttons="buttons"
		@update:open="(open) => !open && emit('close')">
		<div class="appearance">
			<AccountBadge class="appearance__preview"
				:account="account"
				:preview="choice"
				:size="64" />

			<fieldset class="appearance__group">
				<legend>{{ t('sockless_mail', 'Colour') }}</legend>
				<div class="appearance__swatches">
					<button type="button"
						class="appearance__swatch appearance__swatch--auto"
						:class="{ 'appearance__swatch--chosen': !choice.color }"
						:style="{ background: defaultColor(account.id) }"
						:aria-pressed="String(!choice.color)"
						:title="t('sockless_mail', 'Automatic')"
						:aria-label="t('sockless_mail', 'Automatic')"
						@click="choice.color = null">
						A
					</button>
					<button v-for="color in ACCOUNT_COLORS"
						:key="color"
						type="button"
						class="appearance__swatch"
						:class="{ 'appearance__swatch--chosen': choice.color === color }"
						:style="{ background: color }"
						:aria-pressed="String(choice.color === color)"
						:aria-label="color"
						@click="choice.color = color" />
					<NcColorPicker :model-value="choice.color || defaultColor(account.id)"
						advanced-fields
						@update:model-value="(color) => { choice.color = color.toLowerCase() }">
						<button type="button"
							class="appearance__swatch appearance__swatch--custom"
							:class="{ 'appearance__swatch--chosen': customColor }"
							:style="customColor ? { background: choice.color } : {}"
							:title="t('sockless_mail', 'Other colour…')"
							:aria-label="t('sockless_mail', 'Other colour…')">
							<NcIconSvgWrapper :path="mdiPaletteOutline" :size="18" />
						</button>
					</NcColorPicker>
				</div>
			</fieldset>

			<fieldset class="appearance__group">
				<legend>{{ t('sockless_mail', 'Show') }}</legend>
				<div class="appearance__kinds">
					<NcCheckboxRadioSwitch v-model="kind" type="radio" name="account-badge-kind" value="initials">
						{{ t('sockless_mail', 'Initials') }}
					</NcCheckboxRadioSwitch>
					<NcCheckboxRadioSwitch v-model="kind" type="radio" name="account-badge-kind" value="text">
						{{ t('sockless_mail', 'Text or emoji') }}
					</NcCheckboxRadioSwitch>
					<NcCheckboxRadioSwitch v-model="kind" type="radio" name="account-badge-kind" value="icon">
						{{ t('sockless_mail', 'Icon') }}
					</NcCheckboxRadioSwitch>
				</div>
				<NcTextField v-if="kind === 'text'"
					v-model="text"
					class="appearance__text"
					:label="t('sockless_mail', 'Up to three characters')"
					:error="Boolean(errors.badgeText)"
					:helper-text="errors.badgeText ?? ''"
					autocomplete="off" />
				<div v-else-if="kind === 'icon'" class="appearance__icons" role="radiogroup" :aria-label="t('sockless_mail', 'Icon')">
					<button v-for="(path, name) in ACCOUNT_ICONS"
						:key="name"
						type="button"
						role="radio"
						class="appearance__icon"
						:class="{ 'appearance__icon--chosen': icon === name }"
						:aria-checked="String(icon === name)"
						:aria-label="name"
						@click="icon = name">
						<NcIconSvgWrapper :path="path" :size="20" />
					</button>
				</div>
			</fieldset>

			<NcCheckboxRadioSwitch v-model="choice.includeInUnified" type="switch">
				{{ t('sockless_mail', 'Show in All accounts') }}
			</NcCheckboxRadioSwitch>
			<p class="appearance__hint">
				{{ t('sockless_mail', 'Left out, the account\'s mail is not in the unified folders and their counts.') }}
			</p>

			<NcNoteCard v-if="error" type="error" :text="error" />
		</div>
	</NcDialog>
</template>

<script setup>
import { mdiPaletteOutline } from '@mdi/js'
import { t } from '@nextcloud/l10n'
import NcCheckboxRadioSwitch from '@nextcloud/vue/components/NcCheckboxRadioSwitch'
import NcColorPicker from '@nextcloud/vue/components/NcColorPicker'
import NcDialog from '@nextcloud/vue/components/NcDialog'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import NcNoteCard from '@nextcloud/vue/components/NcNoteCard'
import NcTextField from '@nextcloud/vue/components/NcTextField'
import { computed, reactive, ref } from 'vue'
import { ACCOUNT_ICONS } from '../accountIcons.js'
import { ACCOUNT_COLORS, defaultColor, setAppearance, ValidationError } from '../store/accounts.js'
import { unifiedAccountsChanged } from '../store/navigation.js'
import AccountBadge from './AccountBadge.vue'

const props = defineProps({
	account: { type: Object, required: true },
})

const emit = defineEmits(['close'])

const kind = ref(props.account.badgeIcon ? 'icon' : props.account.badgeText ? 'text' : 'initials')
const text = ref(props.account.badgeText ?? '')
const icon = ref(props.account.badgeIcon ?? Object.keys(ACCOUNT_ICONS)[0])

/** What is being chosen, as the account would store it. */
const choice = reactive({
	color: props.account.color ?? null,
	includeInUnified: props.account.includeInUnified !== false,
	badgeText: computed(() => (kind.value === 'text' && text.value.trim()) || null),
	badgeIcon: computed(() => (kind.value === 'icon' ? icon.value : null)),
})

const customColor = computed(() => !!choice.color && !ACCOUNT_COLORS.includes(choice.color))

const errors = ref({})
const error = ref('')
const saving = ref(false)

/** Whether it was saved. */
async function save() {
	saving.value = true
	errors.value = {}
	error.value = ''
	try {
		const { color, badgeText, badgeIcon, includeInUnified } = choice
		await setAppearance(props.account.id, { color, badgeText, badgeIcon, includeInUnified })
		if (includeInUnified !== (props.account.includeInUnified !== false)) {
			unifiedAccountsChanged()
		}
		return true
	} catch (e) {
		if (e instanceof ValidationError) {
			errors.value = e.fieldErrors
			error.value = e.fieldErrors.badgeText ? '' : Object.values(e.fieldErrors).join(' ')
		} else {
			error.value = e.message
		}
		return false
	} finally {
		saving.value = false
	}
}

// Once saved, NcDialog closes as for any button (and reports it, see update:open); returning
// false keeps it open to show the errors. Unmounting it from here would break its focus trap.
const buttons = computed(() => [
	{ label: t('sockless_mail', 'Cancel') },
	{
		label: t('sockless_mail', 'Save'),
		variant: 'primary',
		disabled: saving.value,
		callback: async () => ((await save()) ? undefined : false),
	},
])
</script>

<style scoped>
.appearance {
	display: flex;
	flex-direction: column;
	gap: 12px;
	padding-bottom: 8px;
}

.appearance__preview {
	align-self: center;
	border-radius: 16px;
}

.appearance__group legend {
	font-weight: bold;
	margin-bottom: 6px;
}

.appearance__swatches,
.appearance__icons {
	display: flex;
	flex-wrap: wrap;
	gap: 8px;
}

.appearance__swatch,
.appearance__icon {
	display: grid;
	place-items: center;
	width: 32px;
	height: 32px;
	min-height: 32px;
	margin: 0;
	padding: 0;
	border: 2px solid transparent;
	border-radius: 8px;
	cursor: pointer;
}

.appearance__swatch {
	outline: 2px solid transparent;
	outline-offset: 1px;
	color: #fff;
	font-weight: 600;
}

.appearance__swatch--chosen {
	outline-color: var(--color-main-text);
}

.appearance__swatch--custom {
	background: var(--color-background-dark);
	color: var(--color-main-text);
}

.appearance__icon {
	background: var(--color-background-hover);
	color: var(--color-main-text);
}

.appearance__icon--chosen {
	border-color: var(--color-primary-element);
	background: var(--color-primary-element-light);
}

.appearance__kinds {
	display: flex;
	flex-wrap: wrap;
	gap: 0 16px;
}

.appearance__text {
	max-width: 240px;
}

.appearance__hint {
	margin-top: -8px;
	color: var(--color-text-maxcontrast);
}
</style>
