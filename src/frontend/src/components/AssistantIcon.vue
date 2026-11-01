<template>
	<!-- The Assistant's sparkles in its gradient, as Nextcloud shows them. Not NcAssistantIcon:
	     @nextcloud/vue 9.14 gives it gradientTransform="rotateX(285)", which browsers report as
	     an error on every render. -->
	<svg class="assistant-icon"
		:width="size"
		:height="size"
		viewBox="0 0 24 24"
		aria-hidden="true"
		xmlns="http://www.w3.org/2000/svg">
		<defs>
			<linearGradient :id="id" x1="0" y1="0" x2="1" y2="1">
				<stop v-for="stop in stops" :key="stop.offset" :offset="stop.offset" :stop-color="stop.color" />
			</linearGradient>
		</defs>
		<path :d="mdiCreation" :fill="`url(#${id})`" />
	</svg>
</template>

<script setup>
import { mdiCreation } from '@mdi/js'
import { useIsDarkTheme } from '@nextcloud/vue/composables/useIsDarkTheme'
import { computed } from 'vue'

defineProps({
	size: { type: Number, default: 20 },
})

/** Unique per icon: a gradient id shared by two icons breaks when one of them goes away. */
const id = `sockless-assistant-gradient-${Math.random().toString(36).slice(2, 10)}`
const dark = useIsDarkTheme()
const stops = computed(() => dark.value
	? [{ offset: '15%', color: '#CDACE7' }, { offset: '40%', color: '#008FDB' }, { offset: '82%', color: '#A180E0' }]
	: [{ offset: '15%', color: '#9669D3' }, { offset: '40%', color: '#00679E' }, { offset: '80%', color: '#492083' }])
</script>

<style scoped>
.assistant-icon {
	flex: none;
}
</style>
