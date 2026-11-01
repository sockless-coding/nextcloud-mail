<template>
	<nav class="account-rail" :aria-label="t('sockless_mail', 'Accounts')">
		<button v-if="unifiedAvailable"
			class="account-rail__button account-rail__button--unified"
			:class="{ 'account-rail__button--active': unifiedPaneShown() }"
			:aria-label="t('sockless_mail', 'All accounts')"
			:aria-current="unifiedPaneShown() ? 'page' : undefined"
			:title="t('sockless_mail', 'All accounts')"
			@click="openUnified()">
			<NcIconSvgWrapper :path="mdiInboxMultipleOutline" />
			<span v-if="viewUnread('inbox')" class="account-rail__badge">{{ badge(viewUnread('inbox')) }}</span>
		</button>
		<!-- Drag an account to reorder; right-click to customize it. -->
		<button v-for="account in accountsState.accounts"
			:key="account.id"
			class="account-rail__button account-rail__account"
			:class="{ 'account-rail__button--active': isCurrent(account), ...dragClasses(account, 'account-rail__account') }"
			:style="{ '--account-color': accountColor(account.id) }"
			:aria-label="account.displayName"
			:aria-current="isCurrent(account) ? 'page' : undefined"
			:title="`${account.displayName} · ${account.emailAddress}`"
			draggable="true"
			@click="selectAccount(account)"
			@contextmenu.prevent="dialogs.accountAppearance = account"
			@dragstart="dragStart(account, $event)"
			@dragover="dragOver(account, $event)"
			@drop="drop"
			@dragend="dragEnd">
			<AccountBadge :account="account" />
			<span v-if="inboxUnread(account.id)" class="account-rail__badge">{{ badge(inboxUnread(account.id)) }}</span>
		</button>
		<button class="account-rail__button account-rail__tool"
			:aria-label="t('sockless_mail', 'Add account')"
			:title="t('sockless_mail', 'Add account')"
			@click="dialogs.accountForm = { account: null }">
			<NcIconSvgWrapper :path="mdiPlus" />
		</button>
		<span class="account-rail__spacer" />
		<button v-if="oauthState.isAdmin"
			class="account-rail__button account-rail__tool"
			:aria-label="t('sockless_mail', 'Administration')"
			:title="t('sockless_mail', 'Administration')"
			@click="dialogs.oauthSettings = true">
			<NcIconSvgWrapper :path="mdiShieldAccountOutline" />
		</button>
		<button class="account-rail__button account-rail__tool"
			:aria-label="t('sockless_mail', 'Settings')"
			:title="t('sockless_mail', 'Settings')"
			@click="dialogs.settings = true">
			<NcIconSvgWrapper :path="mdiCogOutline" />
		</button>
	</nav>
</template>

<script setup>
import { mdiCogOutline, mdiInboxMultipleOutline, mdiPlus, mdiShieldAccountOutline } from '@mdi/js'
import { t } from '@nextcloud/l10n'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import { accountColor, accountsState, unifiedAvailable } from '../store/accounts.js'
import { dialogs } from '../store/actions.js'
import { mailboxState } from '../store/mailbox.js'
import { inboxUnread, openUnified, selectAccount } from '../store/navigation.js'
import { oauthState } from '../store/oauth.js'
import { unifiedPaneShown, viewUnread } from '../store/unified.js'
import AccountBadge from './AccountBadge.vue'
import { useAccountDrag } from './accountDrag.js'

const { dragStart, dragOver, drop, dragEnd, dragClasses } = useAccountDrag()

function isCurrent(account) {
	return mailboxState.selected.accountId === account.id
}

function badge(count) {
	return count > 99 ? '99+' : String(count)
}
</script>

<style scoped>
.account-rail {
	display: flex;
	flex-direction: column;
	align-items: center;
	gap: 8px;
	width: 60px;
	flex: none;
	padding: 12px 0;
	overflow-y: auto;
	background: var(--sm-pane-background);
	border-inline-end: 1px solid var(--color-border);
}

.account-rail__button {
	position: relative;
	display: grid;
	place-items: center;
	width: 40px;
	height: 40px;
	min-height: 40px;
	margin: 0;
	padding: 0;
	border: 0;
	border-radius: 12px;
	font-weight: 600;
	font-size: 14px;
	cursor: pointer;
	color: var(--color-main-text);
	background: transparent;
	transition: border-radius 0.15s;
}

.account-rail__button:hover {
	--sm-button-background: var(--color-background-hover);
	background: var(--sm-button-background);
}

.account-rail__button:focus-visible {
	outline: 2px solid var(--color-primary-element);
	outline-offset: 2px;
}

.account-rail__account,
.account-rail__account:hover {
	--sm-button-background: var(--account-color);
	--sm-button-color: #fff;
	color: #fff;
	background: var(--account-color);
}

.account-rail__account:hover,
.account-rail__button--active {
	border-radius: 10px;
}

.account-rail__account--dragged {
	opacity: 0.4;
}

/* Where a dragged account would go: a line in the gap above or below. */
.account-rail__account--drop-before::after,
.account-rail__account--drop-after::after {
	content: '';
	position: absolute;
	inset-inline: 2px;
	height: 3px;
	border-radius: 2px;
	background: var(--color-primary-element);
}

.account-rail__account--drop-before::after {
	top: -6px;
}

.account-rail__account--drop-after::after {
	bottom: -6px;
}

.account-rail__button--unified {
	--sm-button-background: var(--color-background-hover);
	background: var(--color-background-hover);
}

/* The current account: a bar at the rail's edge, like the active app in a dock. */
.account-rail__button--active::before {
	content: '';
	position: absolute;
	inset-inline-start: -10px;
	top: 8px;
	bottom: 8px;
	width: 4px;
	border-radius: 2px;
	background: var(--color-primary-element);
}

.account-rail__button--unified.account-rail__button--active {
	--sm-button-background: var(--color-primary-element-light);
	--sm-button-color: var(--color-primary-element-light-text);
	color: var(--color-primary-element-light-text);
	background: var(--color-primary-element-light);
}

.account-rail__tool {
	color: var(--color-text-maxcontrast);
}

.account-rail__badge {
	position: absolute;
	top: -4px;
	inset-inline-end: -6px;
	min-width: 18px;
	height: 18px;
	padding: 0 4px;
	border: 2px solid var(--sm-pane-background);
	border-radius: 9px;
	box-sizing: content-box;
	font-size: 10px;
	line-height: 18px;
	font-weight: 600;
	font-variant-numeric: tabular-nums;
	color: #fff;
	background: var(--color-element-error, var(--color-error));
}

.account-rail__spacer {
	flex: 1;
}

@media (max-width: 800px) {
	.account-rail {
		width: 52px;
	}

	.account-rail__button {
		width: 36px;
		height: 36px;
		min-height: 36px;
	}
}
</style>
