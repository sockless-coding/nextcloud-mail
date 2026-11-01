<template>
	<!-- An Assistant answer as text: the little Markdown models like to answer with (bullets,
	     numbered items, bold, headings) becomes lists and paragraphs, never HTML from the model. -->
	<div class="assistant-text">
		<template v-for="(block, index) in blocks" :key="index">
			<component :is="block.list" v-if="block.list">
				<li v-for="(item, i) in block.items" :key="i">
					<template v-for="(part, j) in item" :key="j">
						<strong v-if="part.bold">{{ part.text }}</strong>
						<template v-else>
							{{ part.text }}
						</template>
					</template>
				</li>
			</component>
			<p v-else>
				<template v-for="(part, j) in block.parts" :key="j">
					<strong v-if="part.bold">{{ part.text }}</strong>
					<template v-else>
						{{ part.text }}
					</template>
				</template>
			</p>
		</template>
	</div>
</template>

<script setup>
import { computed } from 'vue'

const props = defineProps({
	text: { type: String, default: '' },
})

/** "**bold**" as strong text, the rest as it is. */
function inline(text) {
	return text.split(/\*\*(.+?)\*\*/g).map((part, i) => ({ text: part, bold: i % 2 === 1 })).filter((p) => p.text)
}

const blocks = computed(() => {
	const result = []
	for (const raw of props.text.split('\n')) {
		const line = raw.trim()
		const bullet = line.match(/^[-*•]\s+(.*)$/)
		const numbered = line.match(/^\d+[.)]\s+(.*)$/)
		const item = bullet?.[1] ?? numbered?.[1]
		const list = bullet ? 'ul' : numbered ? 'ol' : null
		const last = result[result.length - 1]
		if (item !== undefined) {
			if (last?.list === list) {
				last.items.push(inline(item))
			} else {
				result.push({ list, items: [inline(item)] })
			}
		} else if (line) {
			result.push({ parts: inline(line.replace(/^#+\s*/, '')) })
		}
	}
	return result
})
</script>

<style scoped>
.assistant-text {
	overflow-wrap: anywhere;
}

.assistant-text p,
.assistant-text ul,
.assistant-text ol {
	margin: 6px 0 0;
}

.assistant-text ul,
.assistant-text ol {
	padding-inline-start: 20px;
}

.assistant-text ul {
	list-style: disc;
}

.assistant-text ol {
	list-style: decimal;
}
</style>
