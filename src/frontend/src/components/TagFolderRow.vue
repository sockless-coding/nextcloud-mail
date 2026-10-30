<template>
	<!-- Looks like the unified rows (FolderPane.vue), with the tag's color for an icon. -->
	<div class="tag-folder" :class="{ 'tag-folder--active': active, 'tag-folder--unread': unread > 0 }">
		<button class="tag-folder__button"
			:data-tag="tag.id"
			:aria-current="active ? 'page' : undefined"
			@click="openUnified(viewId)">
			<span class="tag-folder__dot" :style="{ background: tag.color }" aria-hidden="true" />
			<span class="tag-folder__name">{{ tag.name }}</span>
			<span v-if="unread" class="tag-folder__count">{{ unread }}</span>
		</button>
	</div>
</template>

<script setup>
import { computed } from 'vue'
import { mailboxState } from '../store/mailbox.js'
import { openUnified } from '../store/navigation.js'
import { tagUnread } from '../store/tags.js'
import { tagViewId } from '../store/unified.js'

const props = defineProps({
	tag: { type: Object, required: true },
	/** The account whose tag folder this is; null for all accounts. */
	accountId: { type: String, default: null },
})

const viewId = computed(() => tagViewId(props.tag.id, props.accountId))
const active = computed(() => mailboxState.selected.unified === viewId.value)
const unread = computed(() => tagUnread(props.tag.id, props.accountId))
</script>

<style scoped>
.tag-folder {
	display: flex;
	border-radius: var(--border-radius-element, 8px);
}

.tag-folder:hover {
	background: var(--color-background-hover);
}

.tag-folder--active,
.tag-folder--active:hover {
	background: var(--color-primary-element-light);
	color: var(--color-primary-element-light-text);
}

.tag-folder__button {
	flex: 1;
	display: flex;
	align-items: center;
	gap: 10px;
	min-height: 34px;
	margin: 0;
	padding: 0 8px 0 24px;
	border: 0;
	background: none;
	color: inherit;
	font: inherit;
	cursor: pointer;
}

.tag-folder__button:focus-visible {
	outline: 2px solid var(--color-primary-element);
	outline-offset: -2px;
	border-radius: var(--border-radius-element, 8px);
}

.tag-folder__dot {
	flex: none;
	width: 12px;
	height: 12px;
	margin-inline: 4px;
	border-radius: 50%;
}

.tag-folder__name {
	flex: 1;
	min-width: 0;
	overflow: hidden;
	text-align: start;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.tag-folder--active .tag-folder__name,
.tag-folder--unread .tag-folder__name {
	font-weight: 600;
}

.tag-folder__count {
	font-size: 12px;
	font-weight: 600;
	font-variant-numeric: tabular-nums;
	color: var(--color-primary-element);
}

.tag-folder--active .tag-folder__count {
	color: inherit;
}
</style>
