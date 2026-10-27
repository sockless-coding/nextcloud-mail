<template>
	<NcContent app-name="sockless_mail">
		<NcAppNavigation :aria-label="t('sockless_mail', 'Mail accounts')">
			<template #list>
				<NcAppNavigationNew :text="t('sockless_mail', 'Add account')" @click="openDialog(null)">
					<template #icon>
						<NcIconSvgWrapper :path="mdiPlus" />
					</template>
				</NcAppNavigationNew>
				<NcAppNavigationItem v-for="(account, index) in accountsState.accounts"
					:key="account.id"
					:name="account.displayName"
					:active="account.id === accountsState.selectedId"
					@click="accountsState.selectedId = account.id">
					<template #icon>
						<NcIconSvgWrapper :path="mdiEmailOutline" />
					</template>
					<template #actions>
						<NcActionButton close-after-click @click="openDialog(account)">
							<template #icon>
								<NcIconSvgWrapper :path="mdiPencil" />
							</template>
							{{ t('sockless_mail', 'Edit') }}
						</NcActionButton>
						<NcActionButton :disabled="index === 0" @click="move(account, -1)">
							<template #icon>
								<NcIconSvgWrapper :path="mdiArrowUp" />
							</template>
							{{ t('sockless_mail', 'Move up') }}
						</NcActionButton>
						<NcActionButton :disabled="index === accountsState.accounts.length - 1" @click="move(account, 1)">
							<template #icon>
								<NcIconSvgWrapper :path="mdiArrowDown" />
							</template>
							{{ t('sockless_mail', 'Move down') }}
						</NcActionButton>
						<NcActionButton close-after-click @click="accountToDelete = account">
							<template #icon>
								<NcIconSvgWrapper :path="mdiDelete" />
							</template>
							{{ t('sockless_mail', 'Delete') }}
						</NcActionButton>
					</template>
				</NcAppNavigationItem>
			</template>
		</NcAppNavigation>

		<NcAppContent>
			<div v-if="accountsState.loading" class="centered">
				<NcLoadingIcon :size="64" />
			</div>
			<NcEmptyContent v-else-if="accountsState.loadError"
				:name="t('sockless_mail', 'Could not load your accounts')"
				:description="accountsState.loadError" />
			<NcEmptyContent v-else-if="!selectedAccount"
				:name="t('sockless_mail', 'No mail accounts yet')"
				:description="t('sockless_mail', 'Connect an IMAP/SMTP account to read and send mail.')">
				<template #icon>
					<NcIconSvgWrapper :path="mdiEmailOutline" />
				</template>
				<template #action>
					<NcButton variant="primary" @click="openDialog(null)">
						{{ t('sockless_mail', 'Add account') }}
					</NcButton>
				</template>
			</NcEmptyContent>
			<AccountDetails v-else :account="selectedAccount" @edit="openDialog(selectedAccount)" />
		</NcAppContent>

		<AccountDialog v-if="dialog.open"
			:account="dialog.account"
			@close="dialog.open = false" />

		<NcDialog v-if="accountToDelete"
			:name="t('sockless_mail', 'Delete account')"
			:message="t('sockless_mail', 'Remove {name} from Sockless Mail? Mail on the server is not affected.', { name: accountToDelete.displayName })"
			:buttons="deleteButtons"
			:open="true"
			@update:open="(open) => !open && (accountToDelete = null)" />
	</NcContent>
</template>

<script setup>
import { mdiArrowDown, mdiArrowUp, mdiDelete, mdiEmailOutline, mdiPencil, mdiPlus } from '@mdi/js'
import { showError } from '@nextcloud/dialogs/toast'
import { t } from '@nextcloud/l10n'
import NcActionButton from '@nextcloud/vue/components/NcActionButton'
import NcAppContent from '@nextcloud/vue/components/NcAppContent'
import NcAppNavigation from '@nextcloud/vue/components/NcAppNavigation'
import NcAppNavigationItem from '@nextcloud/vue/components/NcAppNavigationItem'
import NcAppNavigationNew from '@nextcloud/vue/components/NcAppNavigationNew'
import NcButton from '@nextcloud/vue/components/NcButton'
import NcContent from '@nextcloud/vue/components/NcContent'
import NcDialog from '@nextcloud/vue/components/NcDialog'
import NcEmptyContent from '@nextcloud/vue/components/NcEmptyContent'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import { computed, onMounted, reactive, ref } from 'vue'
import AccountDetails from './components/AccountDetails.vue'
import AccountDialog from './components/AccountDialog.vue'
import { accountsState, deleteAccount, loadAccounts, moveAccount } from './store/accounts.js'

const dialog = reactive({ open: false, account: null })
const accountToDelete = ref(null)

const selectedAccount = computed(() => accountsState.accounts.find((a) => a.id === accountsState.selectedId) ?? null)

const deleteButtons = computed(() => [
	{ label: t('sockless_mail', 'Cancel') },
	{
		label: t('sockless_mail', 'Delete'),
		variant: 'error',
		callback: async () => {
			try {
				await deleteAccount(accountToDelete.value.id)
			} catch (e) {
				showError(t('sockless_mail', 'Could not delete the account: {error}', { error: e.message }))
			}
		},
	},
])

function openDialog(account) {
	dialog.account = account
	dialog.open = true
}

async function move(account, delta) {
	try {
		await moveAccount(account.id, delta)
	} catch (e) {
		showError(t('sockless_mail', 'Could not reorder the accounts: {error}', { error: e.message }))
	}
}

onMounted(loadAccounts)
</script>

<style scoped>
.centered {
	display: flex;
	align-items: center;
	justify-content: center;
	height: 100%;
}
</style>
