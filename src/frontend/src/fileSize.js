import { getCanonicalLocale } from '@nextcloud/l10n'

const UNITS = ['byte', 'kilobyte', 'megabyte', 'gigabyte']

/**
 * @param {number} bytes a size in bytes
 * @return {string} e.g. "1.2 MB", in the user's locale
 */
export function formatFileSize(bytes) {
	const exponent = Math.min(Math.floor(Math.log(Math.max(bytes, 1)) / Math.log(1024)), UNITS.length - 1)
	return new Intl.NumberFormat(getCanonicalLocale(), { style: 'unit', unit: UNITS[exponent], unitDisplay: 'short', maximumFractionDigits: 1 })
		.format(bytes / 1024 ** exponent)
}
