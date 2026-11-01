<template>
	<iframe ref="frame"
		class="message-body"
		:class="{ 'message-body--pending': darkened && !shown }"
		:style="{ background: darkened ? colors.background : '#fff' }"
		:title="t('sockless_mail', 'Message content')"
		sandbox="allow-same-origin allow-popups allow-popups-to-escape-sandbox"
		:srcdoc="srcdoc"
		@load="prepare" />
</template>

<script setup>
import { t } from '@nextcloud/l10n'
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { adaptToDark, pageColors } from '../mailColors.js'
import { apiUrl } from '../store/mailbox.js'

/**
 * Shows server-sanitized message HTML; give it a new key to show other HTML or colours. The
 * iframe sandbox has no allow-scripts, so even markup the sanitizer missed cannot run code;
 * allow-same-origin only lets this component measure the content height and recolour it for a
 * dark theme. The document inherits Nextcloud's CSP, which also blocks remote loads.
 *
 * Links the server marked as showing another domain than they lead to get a marker and a tooltip;
 * clicking one emits deceptive-link ({ href, shows, leads }) instead of opening it.
 */
const props = defineProps({
	html: { type: String, required: true },
	/** Keep the colours as sent (a white page) even when the theme is dark. */
	original: { type: Boolean, default: false },
	/** Fold the quoted earlier message at the end away behind "Show quoted text". */
	foldQuotes: { type: Boolean, default: false },
})

const emit = defineEmits(['deceptive-link'])

const frame = ref(null)
const colors = pageColors()
const darkened = computed(() => colors.dark && !props.original)
/** Hidden until recoloured, so a dark theme never flashes a white page. */
const shown = ref(false)
let observer = null
let prepared = null
let polling = 0

const LIGHT_STYLE = `
	html, body { background: #fff; color: #222; }
	blockquote { border-left-color: #ccc; color: #555; }
	details.sockless-quote > summary { background: #eee; color: #555; }
`

const darkStyle = () => `
	html { color-scheme: dark; }
	html, body { background: ${colors.background}; color: ${colors.text}; }
	blockquote { border-left-color: #555; color: inherit; }
	details.sockless-quote > summary { background: #3a3a3a; color: #bbb; }
`

const BASE_STYLE = `
	html, body { margin: 0; padding: 0; }
	body { font: 14px/1.5 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; padding: 8px; overflow-wrap: anywhere; }
	img { max-width: 100%; height: auto; }
	a[data-sockless-link-leads]::after { content: " \\26A0\\FE0E"; color: #c9302c; text-decoration: none; display: inline-block; margin-inline-start: 2px; }
	pre { white-space: pre-wrap; }
	blockquote { margin: 0 0 0 8px; padding-left: 8px; border-left: 2px solid; }
	details.sockless-quote { margin-top: 8px; }
	details.sockless-quote > summary { display: inline-block; padding: 0 8px; border-radius: 8px; font-size: 12px; line-height: 18px; cursor: pointer; list-style: none; user-select: none; }
	details.sockless-quote > summary::-webkit-details-marker { display: none; }
	details.sockless-quote[open] > summary { margin-bottom: 8px; }
`

/** Where mail programs put the quoted earlier message: Gmail, Thunderbird, Outlook, Apple Mail and most others. */
const QUOTE_STARTS = '.gmail_quote_container, .gmail_quote, .moz-cite-prefix, #divRplyFwdMsg, blockquote'

/** The line before a quote that says who wrote it ("On Monday, Anna wrote:"), in some languages. */
const ATTRIBUTION = /\b(wrote|writes|schrieb|a écrit|skrev|escribió|scrisse|schreef|napisał|kirjoitti|escreveu)\s*:?\s*$/i

function meaningful(node) {
	return node.nodeType === 3 ? node.textContent.trim() !== '' : node.nodeType === 1 && (node.textContent.trim() !== '' || !!node.querySelector?.('img'))
}

/** Whether nothing but white space follows the node, up to the end of the body. */
function trailing(node, body) {
	for (let n = node; n && n !== body; n = n.parentNode) {
		for (let next = n.nextSibling; next; next = next.nextSibling) {
			if (meaningful(next)) {
				return false
			}
		}
	}
	return true
}

/**
 * Folds the quoted message at the end of the text (with the line that says who wrote it) into a
 * <details>, which opens without scripts. A quote with an answer below it is left alone: the
 * answer would be hidden too.
 */
function foldQuote(doc) {
	const body = doc.body
	for (const candidate of body.querySelectorAll(QUOTE_STARTS)) {
		// Its own content may follow it at its level (Gmail's and Outlook's markers start the quote).
		const fromHere = trailing(candidate.matches('blockquote') ? candidate : candidate.parentNode, body)
		if (!fromHere || (candidate.matches('blockquote') && candidate.parentNode.closest('blockquote'))) {
			continue
		}
		let start = candidate
		let previous = start.previousSibling
		while (previous && !meaningful(previous) && previous.nodeName !== 'IMG') {
			previous = previous.previousSibling
		}
		if (previous && previous.textContent.length < 400 && ATTRIBUTION.test(previous.textContent.trim())) {
			start = previous
		}
		const details = doc.createElement('details')
		details.className = 'sockless-quote'
		const summary = doc.createElement('summary')
		summary.textContent = '···'
		summary.title = t('sockless_mail', 'Show quoted text')
		details.append(summary)
		start.before(details)
		while (details.nextSibling) {
			details.append(details.nextSibling)
		}
		// Only keep the fold when it hides something worth folding.
		if (!meaningful(details)) {
			details.replaceWith(...[...details.childNodes].slice(1))
			continue
		}
		return
	}
}

const srcdoc = computed(() => {
	// DOMParser documents are inert: nothing runs or loads while we rewrite them.
	const doc = new DOMParser().parseFromString(props.html, 'text/html')
	for (const element of doc.querySelectorAll('[data-sockless-src]')) {
		element.setAttribute('src', apiUrl(element.getAttribute('data-sockless-src')))
		element.removeAttribute('data-sockless-src')
	}
	for (const link of doc.querySelectorAll('[data-sockless-link-leads]')) {
		link.setAttribute('title', t('sockless_mail', 'Shows {shown} but leads to {host}', {
			shown: link.getAttribute('data-sockless-link-shows'),
			host: link.getAttribute('data-sockless-link-leads'),
		}))
	}
	if (props.foldQuotes) {
		foldQuote(doc)
	}
	const base = doc.createElement('base')
	base.setAttribute('target', '_blank')
	const style = doc.createElement('style')
	style.textContent = BASE_STYLE + (darkened.value ? darkStyle() : LIGHT_STYLE)
	doc.head.prepend(base, style)
	return '<!DOCTYPE html>' + doc.documentElement.outerHTML
})

/**
 * Recolours and sizes the document once it is parsed. The load event waits for every image, so
 * this also polls for the parsed document to show text as soon as it can.
 */
function prepare() {
	const doc = frame.value?.contentDocument
	if (!doc?.body || doc.location.href !== 'about:srcdoc' || doc.readyState === 'loading') {
		return false
	}
	if (prepared !== doc) {
		prepared = doc
		if (darkened.value) {
			adaptToDark(doc, colors.rgb)
		}
		doc.addEventListener('click', interceptDeceptive)
		// Middle click opens a tab without a click event.
		doc.addEventListener('auxclick', interceptDeceptive)
		watchHeight(doc)
		shown.value = true
	}
	return true
}

onMounted(() => {
	const check = () => {
		if (!prepare()) {
			polling = requestAnimationFrame(check)
		}
	}
	check()
})

function interceptDeceptive(event) {
	// Not instanceof Element: the frame's nodes come from its own window.
	const link = typeof event.target?.closest === 'function' ? event.target.closest('a[data-sockless-link-leads]') : null
	if (!link || (event.type === 'auxclick' && event.button !== 1)) {
		return
	}
	event.preventDefault()
	emit('deceptive-link', {
		href: link.href,
		shows: link.getAttribute('data-sockless-link-shows'),
		leads: link.getAttribute('data-sockless-link-leads'),
	})
}

function watchHeight(doc) {
	observer?.disconnect()
	const resize = () => {
		frame.value.style.height = `${doc.documentElement.scrollHeight}px`
	}
	resize()
	// Images arriving later change the height.
	observer = new ResizeObserver(resize)
	observer.observe(doc.body)
}

onBeforeUnmount(() => {
	observer?.disconnect()
	cancelAnimationFrame(polling)
})
</script>

<style scoped>
.message-body {
	width: 100%;
	min-height: 64px;
	border: none;
	border-radius: var(--border-radius-large);
}

.message-body--pending {
	visibility: hidden;
}
</style>
