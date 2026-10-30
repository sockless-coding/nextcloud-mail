<template>
	<span v-if="shown.length" class="tag-chips" :class="{ 'tag-chips--small': small }">
		<span v-for="tag in shown"
			:key="tag.id"
			class="tag-chips__chip"
			:style="{ background: tag.color, color: textOn(tag.color) }"
			:title="tag.name">
			{{ tag.name }}
		</span>
		<span v-if="hidden" class="tag-chips__more" :title="tags.slice(max).map((t) => t.name).join(', ')">
			+{{ hidden }}
		</span>
	</span>
</template>

<script setup>
import { computed } from 'vue'
import { textOn } from '../store/tags.js'

const props = defineProps({
	/** Tags ({ id, name, color }) in display order. */
	tags: { type: Array, required: true },
	/** At most this many chips; the rest as "+n". */
	max: { type: Number, default: Infinity },
	small: { type: Boolean, default: false },
})

const shown = computed(() => props.tags.slice(0, props.max))
const hidden = computed(() => Math.max(0, props.tags.length - props.max))
</script>

<style scoped>
.tag-chips {
	display: inline-flex;
	flex-wrap: wrap;
	align-items: center;
	gap: 4px;
	min-width: 0;
}

.tag-chips--small {
	flex-wrap: nowrap;
	overflow: hidden;
}

.tag-chips__chip {
	flex: none;
	max-width: 12em;
	padding: 0 8px;
	border-radius: var(--border-radius-pill);
	font-size: 0.85em;
	font-weight: normal;
	line-height: 1.6;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.tag-chips--small .tag-chips__chip {
	max-width: 8em;
	padding: 0 6px;
	font-size: 0.75em;
	line-height: 1.5;
}

.tag-chips__more {
	flex: none;
	font-size: 0.75em;
	color: var(--color-text-maxcontrast);
}
</style>
