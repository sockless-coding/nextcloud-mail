import { getLanguage, register } from '@nextcloud/l10n'
import { apiBase, EX_APP_ID } from './api.js'

/* global __L10N_LANGUAGES__ */
// The languages in l10n/ at build time (vite.config.js), served from js/l10n/<lang>.json.
const available = new Set(__L10N_LANGUAGES__)

/**
 * Registers the user's translations before the app mounts. Nextcloud does not load them: AppAPI
 * adds the bundle as one of its own scripts, so the page only gets app_api's translations.
 * Strings stay English when there is no translation or it cannot be loaded.
 */
export async function loadTranslations() {
	const language = getLanguage()
	// "de_DE" (formal German) has its own file; "pt_PT" falls back to "pt".
	const file = [language, language.split(/[_-]/)[0]].find((l) => available.has(l))
	if (!file) {
		return
	}
	const response = await fetch(`${apiBase.replace(/\/api$/, '')}/js/l10n/${file}.json`)
	if (!response.ok) {
		throw new Error(`HTTP ${response.status}`)
	}
	const { translations } = await response.json()
	register(EX_APP_ID, translations)
}
