import { computed, reactive, watch } from 'vue'

/**
 * View preferences (reading pane, density, folder pane, pane sizes, rich or plain text, sender logos) and the
 * window width they depend on. Kept in the browser's localStorage, per Nextcloud user: several users can share a
 * browser, and the head element names the logged-in one. Storage may be unavailable (private
 * windows, blocked site data); the defaults apply then.
 */
const KEY = `sockless_mail.layout.${document.head?.dataset.user ?? ''}`

const DEFAULTS = {
	/** right | bottom | off */
	readingPane: 'right',
	/** comfortable | compact */
	density: 'comfortable',
	folderPane: true,
	/** Message list width in px (reading pane right). */
	listWidth: 400,
	/** Message list height in % of the panes (reading pane bottom). */
	listHeight: 45,
	/** How new messages are written: html (rich text) or text. */
	composeFormat: 'html',
	/** Show senders' brand logos (BIMI) in place of their initials. */
	senderLogos: true,
}

function load() {
	try {
		return { ...DEFAULTS, ...JSON.parse(localStorage.getItem(KEY) ?? '{}') }
	} catch {
		return { ...DEFAULTS }
	}
}

export const layout = reactive(load())

watch(layout, () => {
	try {
		localStorage.setItem(KEY, JSON.stringify(layout))
	} catch {
		// Not remembered; the session keeps working with the current values.
	}
})

/**
 * Below these widths the panes no longer fit side by side: the folder pane becomes a drawer
 * over the list, and the list and the message take turns.
 */
const narrowFolders = window.matchMedia('(max-width: 1024px)')
const narrowPanes = window.matchMedia('(max-width: 800px)')

export const viewport = reactive({
	narrowFolders: narrowFolders.matches,
	narrowPanes: narrowPanes.matches,
	/** The folder drawer, on narrow screens only. */
	drawerOpen: false,
})

narrowFolders.addEventListener('change', (e) => {
	viewport.narrowFolders = e.matches
	viewport.drawerOpen = false
})
narrowPanes.addEventListener('change', (e) => {
	viewport.narrowPanes = e.matches
})

/** Where the open message is shown: right, bottom, or off (instead of the list). */
export const readingPane = computed(() => (viewport.narrowPanes ? 'off' : layout.readingPane))

export const folderPaneShown = computed(() => (viewport.narrowFolders ? viewport.drawerOpen : layout.folderPane))

export function toggleFolderPane() {
	if (viewport.narrowFolders) {
		viewport.drawerOpen = !viewport.drawerOpen
	} else {
		layout.folderPane = !layout.folderPane
	}
}

/** After picking a folder in the drawer, get it out of the way. */
export function closeDrawer() {
	viewport.drawerOpen = false
}
