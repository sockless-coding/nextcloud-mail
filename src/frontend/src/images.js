/** Images a message can show inside its text (the server's OutgoingHtml.InlineImageTypes); other files are attachments. */
export const INLINE_IMAGE_TYPES = ['image/png', 'image/jpeg', 'image/gif', 'image/webp']

/** Longest side of an image put into a message; larger ones are scaled down before uploading. */
const MAX_SIDE = 2000

/**
 * The image as it goes into a message: at most MAX_SIDE pixels on its longest side, and WebP
 * (which many mail programs cannot show) as JPEG. GIFs stay as they are, they may be animated.
 *
 * @param {File} file a file of one of the INLINE_IMAGE_TYPES
 * @return {Promise<{file: File, scaled: boolean}>} what to upload; scaled when it is smaller than the original
 */
export async function prepareImage(file) {
	if (file.type === 'image/gif') {
		return { file, scaled: false }
	}
	let bitmap
	try {
		bitmap = await createImageBitmap(file)
	} catch {
		return { file, scaled: false }
	}
	const scale = Math.min(1, MAX_SIDE / Math.max(bitmap.width, bitmap.height))
	if (scale === 1 && file.type !== 'image/webp') {
		bitmap.close()
		return { file, scaled: false }
	}

	const canvas = document.createElement('canvas')
	canvas.width = Math.max(1, Math.round(bitmap.width * scale))
	canvas.height = Math.max(1, Math.round(bitmap.height * scale))
	const context = canvas.getContext('2d')
	const type = file.type === 'image/png' ? 'image/png' : 'image/jpeg'
	if (type === 'image/jpeg') {
		// JPEG has no transparency; without this, transparent areas turn black.
		context.fillStyle = '#ffffff'
		context.fillRect(0, 0, canvas.width, canvas.height)
	}
	context.imageSmoothingQuality = 'high'
	context.drawImage(bitmap, 0, 0, canvas.width, canvas.height)
	bitmap.close()

	const blob = await new Promise((resolve) => canvas.toBlob(resolve, type, 0.9))
	if (!blob) {
		return { file, scaled: false }
	}
	const name = `${file.name.replace(/\.[^.]*$/, '') || 'image'}.${type === 'image/png' ? 'png' : 'jpg'}`
	return { file: new File([blob], name, { type }), scaled: scale < 1 }
}

let placeholder = null

/** What an image shows while it is being fetched: a grey box (one object URL, kept). */
export function placeholderUrl() {
	placeholder ??= URL.createObjectURL(new Blob(
		['<svg xmlns="http://www.w3.org/2000/svg" width="160" height="120"><rect width="160" height="120" fill="#e0e0e0"/></svg>'],
		{ type: 'image/svg+xml' }))
	return placeholder
}

/**
 * The image in a data: URL, decoded here (fetching it would be refused by Nextcloud's CSP).
 *
 * @param {string} url data:image/…
 * @return {File} the image
 */
export function dataUrlFile(url) {
	const match = /^data:([^;,]+)((?:;[^;,]*)*),(.*)$/s.exec(url)
	if (!match) {
		throw new Error('not a data URL')
	}
	const type = match[1].toLowerCase()
	const bytes = /;base64/i.test(match[2])
		? Uint8Array.from(atob(match[3].replace(/\s/g, '')), (c) => c.charCodeAt(0))
		: new TextEncoder().encode(decodeURIComponent(match[3]))
	const extension = { 'image/png': 'png', 'image/gif': 'gif', 'image/webp': 'webp' }[type] ?? 'jpg'
	return new File([bytes], `image.${extension}`, { type })
}
