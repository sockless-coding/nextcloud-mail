import vue from '@vitejs/plugin-vue'
import { copyFileSync, existsSync, mkdirSync, readdirSync, readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { defineConfig } from 'vite'

// One version for the whole app: appinfo/info.xml.
const infoXml = readFileSync(new URL('../../appinfo/info.xml', import.meta.url), 'utf8')
const appVersion = infoXml.match(/<version>([^<]+)<\/version>/)[1]

// Translations: l10n/<lang>.json (tools/l10n.mjs compile, or the Transifex bot) are served as
// js/l10n/<lang>.json and fetched for the user's language before the app mounts (src/l10n.js).
const l10nDir = fileURLToPath(new URL('../../l10n/', import.meta.url))
const l10nFiles = existsSync(l10nDir) ? readdirSync(l10nDir).filter((f) => f.endsWith('.json')).sort() : []
const copyTranslations = {
	name: 'sockless-mail-l10n',
	writeBundle(options) {
		const target = `${options.dir}/js/l10n`
		mkdirSync(target, { recursive: true })
		for (const file of l10nFiles) {
			copyFileSync(l10nDir + file, `${target}/${file}`)
		}
	},
}

// AppAPI injects registered scripts as classic <script> tags with a fixed path
// (js/sockless_mail-main.js, served by the ExApp through the PHP proxy), so the
// build must produce one self-contained IIFE bundle and one stylesheet.
export default defineConfig(({ mode }) => ({
	plugins: [vue(), copyTranslations],
	define: {
		__L10N_LANGUAGES__: JSON.stringify(l10nFiles.map((f) => f.slice(0, -'.json'.length))),
		'process.env.NODE_ENV': JSON.stringify(mode === 'development' ? 'development' : 'production'),
		// Read by @nextcloud/vue (logging, translations); @nextcloud/vite-config would set these.
		appName: JSON.stringify('sockless_mail'),
		appVersion: JSON.stringify(appVersion),
		__VUE_OPTIONS_API__: 'true',
		__VUE_PROD_DEVTOOLS__: 'false',
		__VUE_PROD_HYDRATION_MISMATCH_DETAILS__: 'false',
	},
	build: {
		outDir: '../SocklessMail.Api/wwwroot',
		// wwwroot also holds committed files (img/); only js/ and css/ are build output.
		emptyOutDir: false,
		sourcemap: mode === 'development',
		lib: {
			entry: 'src/main.js',
			name: 'SocklessMail',
			formats: ['iife'],
			fileName: () => 'js/sockless_mail-main.js',
			cssFileName: 'css/sockless_mail-main',
		},
	},
}))
