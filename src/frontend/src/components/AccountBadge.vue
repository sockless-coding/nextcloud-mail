<template>
	<!-- The account's square: its colour with its icon, its own text, or its initials. -->
	<span class="account-badge"
		:style="{ background: color, color: textColorOn(color), width: box, height: box, fontSize: `${Math.round(pixels * (text.length > 2 ? 0.3 : 0.36))}px` }"
		aria-hidden="true">
		<NcIconSvgWrapper v-if="icon" :path="icon" :size="Math.round(pixels * 0.55)" />
		<template v-else>{{ text }}</template>
	</span>
</template>

<script setup>
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import { computed } from 'vue'
import { ACCOUNT_ICONS } from '../accountIcons.js'
import { accountInitials, defaultColor, textColorOn } from '../store/accounts.js'

/**
 * Shows an account as it is set up, or as it would look (the Customize dialog passes the
 * values being chosen).
 */
const props = defineProps({
	account: { type: Object, required: true },
	/** Values that are not saved yet: { color, badgeText, badgeIcon }. */
	preview: { type: Object, default: null },
	/** In px; without it the badge fills its box (sized like 40 px for its text and icon). */
	size: { type: Number, default: null },
})

const pixels = computed(() => props.size ?? 40)
const box = computed(() => (props.size ? `${props.size}px` : '100%'))

const shown = computed(() => props.preview ?? props.account)
const color = computed(() => shown.value.color || defaultColor(props.account.id))
const icon = computed(() => ACCOUNT_ICONS[shown.value.badgeIcon] ?? null)
const text = computed(() => shown.value.badgeText || accountInitials(props.account))
</script>

<style scoped>
.account-badge {
	display: inline-grid;
	place-items: center;
	flex: none;
	border-radius: inherit;
	font-weight: 600;
	line-height: 1;
	overflow: hidden;
	white-space: nowrap;
}

/* NcIconSvgWrapper is a whole click target (34–44px) by default; small badges are less. */
.account-badge :deep(.icon-vue) {
	min-width: 0;
	min-height: 0;
}
</style>
