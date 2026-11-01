<template>
	<NcAppSettingsDialog v-model:open="open"
		:name="t('sockless_mail', 'Settings')"
		show-navigation>
		<NcAppSettingsSection id="sockless-settings-writing" :name="t('sockless_mail', 'Writing')">
			<fieldset class="settings__group">
				<legend>{{ t('sockless_mail', 'Open new messages, replies and forwards') }}</legend>
				<NcCheckboxRadioSwitch v-for="option in SIZES"
					:key="option.value"
					v-model="layout.composerSize"
					type="radio"
					name="sockless-composer-size"
					:value="option.value">
					{{ option.label }}
				</NcCheckboxRadioSwitch>
				<p v-if="layout.composerSize === 'window'" class="settings__hint">
					{{ t('sockless_mail', 'If the browser blocks the window, allow pop-ups for this site. Meanwhile messages open large.') }}
				</p>
			</fieldset>

			<fieldset class="settings__group">
				<legend>{{ t('sockless_mail', 'When closing a message you are writing') }}</legend>
				<NcCheckboxRadioSwitch v-for="option in CLOSE_ACTIONS"
					:key="option.value"
					v-model="layout.composerClose"
					type="radio"
					name="sockless-composer-close"
					:value="option.value"
					:description="option.description">
					{{ option.label }}
				</NcCheckboxRadioSwitch>
				<p class="settings__hint">
					{{ t('sockless_mail', 'A message with nothing written or attached always just closes.') }}
				</p>
			</fieldset>

			<fieldset class="settings__group">
				<legend>{{ t('sockless_mail', 'Write new messages in') }}</legend>
				<NcCheckboxRadioSwitch v-for="option in FORMATS"
					:key="option.value"
					v-model="layout.composeFormat"
					type="radio"
					name="sockless-compose-format"
					:value="option.value">
					{{ option.label }}
				</NcCheckboxRadioSwitch>
			</fieldset>
		</NcAppSettingsSection>

		<NcAppSettingsSection v-if="accountsState.accounts.length"
			id="sockless-settings-accounts"
			:name="t('sockless_mail', 'Accounts')">
			<p class="settings__hint">
				{{ t('sockless_mail', 'Drag accounts to change their order. Accounts left out of All accounts keep their own folders. Conversations group each message with its replies, wherever they are kept.') }}
			</p>
			<ul class="settings__accounts">
				<li v-for="(account, index) in accountsState.accounts"
					:key="account.id"
					class="settings__account"
					:class="dragClasses(account, 'settings__account')"
					draggable="true"
					@dragstart="dragStart(account, $event)"
					@dragover="dragOver(account, $event)"
					@drop="drop"
					@dragend="dragEnd">
					<NcIconSvgWrapper class="settings__handle" :path="mdiDragVertical" :size="20" />
					<AccountBadge class="settings__badge" :account="account" :size="32" />
					<span class="settings__account-text">
						<span class="settings__account-name">{{ account.displayName }}</span>
						<span class="settings__account-address">{{ account.emailAddress }}</span>
					</span>
					<span class="settings__switches">
						<NcCheckboxRadioSwitch type="switch"
							:model-value="account.includeInUnified !== false"
							:loading="saving === `${account.id}:unified`"
							:aria-label="t('sockless_mail', 'Show {name} in All accounts', { name: account.displayName })"
							@update:model-value="(include) => setUnified(account, include)">
							{{ t('sockless_mail', 'In All accounts') }}
						</NcCheckboxRadioSwitch>
						<NcCheckboxRadioSwitch type="switch"
							:model-value="account.conversations === true"
							:loading="saving === `${account.id}:conversations`"
							:aria-label="t('sockless_mail', 'Show the mail of {name} as conversations', { name: account.displayName })"
							@update:model-value="(enabled) => setGrouping(account, enabled)">
							{{ t('sockless_mail', 'Conversations') }}
						</NcCheckboxRadioSwitch>
					</span>
					<NcActions :aria-label="t('sockless_mail', 'Actions for {name}', { name: account.displayName })">
						<NcActionButton close-after-click @click="dialogs.accountAppearance = account">
							<template #icon>
								<NcIconSvgWrapper :path="mdiPaletteOutline" />
							</template>
							{{ t('sockless_mail', 'Customize…') }}
						</NcActionButton>
						<NcActionButton :disabled="index === 0" close-after-click @click="moveAccountBy(account, -1)">
							<template #icon>
								<NcIconSvgWrapper :path="mdiArrowUp" />
							</template>
							{{ t('sockless_mail', 'Move up') }}
						</NcActionButton>
						<NcActionButton :disabled="index === accountsState.accounts.length - 1" close-after-click @click="moveAccountBy(account, 1)">
							<template #icon>
								<NcIconSvgWrapper :path="mdiArrowDown" />
							</template>
							{{ t('sockless_mail', 'Move down') }}
						</NcActionButton>
					</NcActions>
				</li>
			</ul>
		</NcAppSettingsSection>
	</NcAppSettingsDialog>
</template>

<script setup>
import { mdiArrowDown, mdiArrowUp, mdiDragVertical, mdiPaletteOutline } from '@mdi/js'
import { showError } from '@nextcloud/dialogs/toast'
import { t } from '@nextcloud/l10n'
import NcActionButton from '@nextcloud/vue/components/NcActionButton'
import NcActions from '@nextcloud/vue/components/NcActions'
import NcAppSettingsDialog from '@nextcloud/vue/components/NcAppSettingsDialog'
import NcAppSettingsSection from '@nextcloud/vue/components/NcAppSettingsSection'
import NcCheckboxRadioSwitch from '@nextcloud/vue/components/NcCheckboxRadioSwitch'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import { ref } from 'vue'
import { accountsState, setAppearance, setConversations } from '../store/accounts.js'
import { dialogs } from '../store/actions.js'
import { layout } from '../store/layout.js'
import { moveAccountBy, unifiedAccountsChanged } from '../store/navigation.js'
import AccountBadge from './AccountBadge.vue'
import { useAccountDrag } from './accountDrag.js'

/**
 * The user's preferences that do not fit the View menu. The writing ones are kept with the view
 * settings in this browser (store/layout.js): which composer size suits depends on the screen.
 * The accounts' order and appearance are kept on the server, for every device.
 */
const open = defineModel('open', { type: Boolean, default: false })

const { dragStart, dragOver, drop, dragEnd, dragClasses } = useAccountDrag()

/** The switch being saved: "<account id>:unified" or "<account id>:conversations". */
const saving = ref(null)

async function setUnified(account, includeInUnified) {
	saving.value = `${account.id}:unified`
	try {
		const { color, badgeText, badgeIcon } = account
		await setAppearance(account.id, { color, badgeText, badgeIcon, includeInUnified })
		unifiedAccountsChanged()
	} catch (e) {
		showError(t('sockless_mail', 'Could not save the change: {error}', { error: e.message }))
	} finally {
		saving.value = null
	}
}

async function setGrouping(account, enabled) {
	saving.value = `${account.id}:conversations`
	try {
		await setConversations(account.id, enabled)
	} catch (e) {
		showError(t('sockless_mail', 'Could not save the change: {error}', { error: e.message }))
	} finally {
		saving.value = null
	}
}

const SIZES = [
	{ value: 'small', label: t('sockless_mail', 'Small, docked at the bottom') },
	{ value: 'large', label: t('sockless_mail', 'Large, over the mailbox') },
	{ value: 'window', label: t('sockless_mail', 'In a separate window') },
]

const CLOSE_ACTIONS = [
	{ value: 'keep', label: t('sockless_mail', 'Keep it as a draft'), description: t('sockless_mail', 'Saved in Drafts, to continue later') },
	{ value: 'ask', label: t('sockless_mail', 'Ask') },
	{ value: 'discard', label: t('sockless_mail', 'Discard it'), description: t('sockless_mail', 'The saved draft is deleted too') },
]

const FORMATS = [
	{ value: 'html', label: t('sockless_mail', 'Rich text') },
	{ value: 'text', label: t('sockless_mail', 'Plain text') },
]
</script>

<style scoped>
.settings__group {
	margin-block-end: 16px;
}

.settings__group legend {
	font-weight: bold;
	margin-block-end: 4px;
}

.settings__hint {
	color: var(--color-text-maxcontrast);
	margin-block-start: 4px;
}

.settings__accounts {
	margin-block-start: 8px;
}

.settings__account {
	position: relative;
	display: flex;
	align-items: center;
	gap: 10px;
	padding: 6px 4px;
	border-radius: var(--border-radius-element, 8px);
}

.settings__switches {
	display: flex;
	flex-direction: column;
	align-items: flex-start;
}

.settings__account:hover {
	background: var(--color-background-hover);
}

.settings__handle {
	cursor: grab;
	color: var(--color-text-maxcontrast);
}

.settings__badge {
	border-radius: 8px;
}

.settings__account-text {
	display: flex;
	flex: 1;
	flex-direction: column;
	min-width: 0;
}

.settings__account-name,
.settings__account-address {
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.settings__account-address {
	color: var(--color-text-maxcontrast);
}

.settings__account--dragged {
	opacity: 0.4;
}

.settings__account--drop-before::after,
.settings__account--drop-after::after {
	content: '';
	position: absolute;
	inset-inline: 0;
	height: 3px;
	border-radius: 2px;
	background: var(--color-primary-element);
}

.settings__account--drop-before::after {
	top: -2px;
}

.settings__account--drop-after::after {
	bottom: -2px;
}
</style>
