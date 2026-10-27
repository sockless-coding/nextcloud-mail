<template>
	<iframe ref="frame"
		class="message-body"
		:title="t('sockless_mail', 'Message content')"
		sandbox="allow-same-origin allow-popups allow-popups-to-escape-sandbox"
		:srcdoc="srcdoc"
		@load="watchHeight" />
</template>

<script setup>
import { t } from '@nextcloud/l10n'
import { computed, onBeforeUnmount, ref } from 'vue'
import { apiUrl } from '../store/mailbox.js'

/**
 * Shows server-sanitized message HTML. The iframe sandbox has no allow-scripts, so even markup
 * the sanitizer missed cannot run code; allow-same-origin only lets this component measure the
 * content height. The document inherits Nextcloud's CSP, which also blocks remote loads.
 */
const props = defineProps({
	html: { type: String, required: true },
})

const frame = ref(null)
let observer = null

const BASE_STYLE = `
	html, body { margin: 0; padding: 0; background: #fff; color: #222; }
	body { font: 14px/1.5 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; padding: 8px; overflow-wrap: anywhere; }
	img { max-width: 100%; height: auto; }
	pre { white-space: pre-wrap; }
	blockquote { margin: 0 0 0 8px; padding-left: 8px; border-left: 2px solid #ccc; color: #555; }
`

const srcdoc = computed(() => {
	// DOMParser documents are inert: nothing runs or loads while we rewrite them.
	const doc = new DOMParser().parseFromString(props.html, 'text/html')
	for (const element of doc.querySelectorAll('[data-sockless-src]')) {
		element.setAttribute('src', apiUrl(element.getAttribute('data-sockless-src')))
		element.removeAttribute('data-sockless-src')
	}
	const base = doc.createElement('base')
	base.setAttribute('target', '_blank')
	const style = doc.createElement('style')
	style.textContent = BASE_STYLE
	doc.head.prepend(base, style)
	return '<!DOCTYPE html>' + doc.documentElement.outerHTML
})

function watchHeight() {
	observer?.disconnect()
	const doc = frame.value?.contentDocument
	if (!doc?.body) {
		return
	}
	const resize = () => {
		frame.value.style.height = `${doc.documentElement.scrollHeight}px`
	}
	resize()
	// Images arriving later change the height.
	observer = new ResizeObserver(resize)
	observer.observe(doc.body)
}

onBeforeUnmount(() => observer?.disconnect())
</script>

<style scoped>
.message-body {
	width: 100%;
	min-height: 120px;
	border: none;
	border-radius: var(--border-radius-large);
	background: #fff;
}
</style>
