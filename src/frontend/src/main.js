import '@nextcloud/dialogs/style.css'
import { createApp } from 'vue'
import App from './App.vue'
import { loadTranslations } from './l10n.js'

// Translations first: components translate as they render. Module code must not call t() while
// loading (use a getter instead), as that runs before this.
loadTranslations()
	.catch((e) => console.warn('Sockless Mail: translations could not be loaded, showing English.', e))
	// AppAPI's embedded page template is an empty <div id="content">.
	.finally(() => createApp(App).mount('#content'))
