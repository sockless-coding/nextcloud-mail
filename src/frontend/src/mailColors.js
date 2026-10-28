/**
 * Dark mode for message bodies, the way Outlook and Gmail do it: mail is written for a white page,
 * so light backgrounds are darkened and text that would then lack contrast is lightened. Hues
 * stay, and images are left alone. Works on the computed styles, so <style> rules, bgcolor and
 * inline styles are all covered.
 */

/** Below this contrast (WCAG AA for body text) text is lightened. */
const MIN_CONTRAST = 4.5
/** Backgrounds lighter than this relative luminance are darkened. */
const LIGHT_BACKGROUND = 0.4

/** "rgb(…)" / "rgba(…)" → [r, g, b, a] with r, g, b in 0–255; null for anything else. */
function parse(value) {
	const match = /^rgba?\(\s*([\d.]+)[,\s]+([\d.]+)[,\s]+([\d.]+)(?:[,\s/]+([\d.]+%?))?\s*\)$/.exec(value?.trim() ?? '')
	if (!match) {
		return null
	}
	const alpha = match[4] === undefined ? 1 : match[4].endsWith('%') ? parseFloat(match[4]) / 100 : parseFloat(match[4])
	return [+match[1], +match[2], +match[3], alpha]
}

function css([r, g, b]) {
	return `rgb(${Math.round(r)}, ${Math.round(g)}, ${Math.round(b)})`
}

function luminance([r, g, b]) {
	const channel = (c) => {
		c /= 255
		return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4
	}
	return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b)
}

function contrast(a, b) {
	const [light, dark] = [luminance(a), luminance(b)].sort((x, y) => y - x)
	return (light + 0.05) / (dark + 0.05)
}

/** A colour with alpha over the background behind it. */
function blend([r, g, b, a], [br, bg, bb]) {
	return [r * a + br * (1 - a), g * a + bg * (1 - a), b * a + bb * (1 - a)]
}

function toHsl([r, g, b]) {
	r /= 255
	g /= 255
	b /= 255
	const max = Math.max(r, g, b)
	const min = Math.min(r, g, b)
	const l = (max + min) / 2
	if (max === min) {
		return [0, 0, l]
	}
	const d = max - min
	const s = l > 0.5 ? d / (2 - max - min) : d / (max + min)
	const h = max === r ? (g - b) / d + (g < b ? 6 : 0) : max === g ? (b - r) / d + 2 : (r - g) / d + 4
	return [h / 6, s, l]
}

function fromHsl([h, s, l]) {
	if (s === 0) {
		return [l * 255, l * 255, l * 255]
	}
	const q = l < 0.5 ? l * (1 + s) : l + s - l * s
	const p = 2 * l - q
	const channel = (t) => {
		t = (t + 1) % 1
		if (t < 1 / 6) {
			return p + (q - p) * 6 * t
		}
		if (t < 1 / 2) {
			return q
		}
		return t < 2 / 3 ? p + (q - p) * (2 / 3 - t) * 6 : p
	}
	return [channel(h + 1 / 3) * 255, channel(h) * 255, channel(h - 1 / 3) * 255]
}

/** White and near-white become the page itself; other light colours a dark shade of their hue. */
function darken(color, page) {
	const [h, s, l] = toHsl(color)
	if (l > 0.94 && s < 0.3) {
		return page
	}
	return fromHsl([h, s * 0.45, 0.09 + (1 - l) * 0.35])
}

/** Mirrors the lightness, then lightens further until the text is readable on its background. */
function lighten(color, background) {
	const [h, s, l] = toHsl(color)
	let lightness = Math.min(Math.max(1 - l, 0.6), 0.88)
	let result = fromHsl([h, s, lightness])
	while (contrast(result, background) < MIN_CONTRAST && lightness < 0.97) {
		lightness = Math.min(lightness + 0.05, 0.97)
		result = fromHsl([h, s, lightness])
	}
	return result
}

const SKIPPED = new Set(['IMG', 'PICTURE', 'VIDEO', 'svg', 'CANVAS', 'STYLE', 'BR'])

/**
 * Recolours a (same-origin) message document for a dark page. All styles are read before any
 * is written, so the browser computes the layout once rather than per element.
 *
 * @param {Document} doc the message document
 * @param {number[]} page the page background, [r, g, b]
 */
export function adaptToDark(doc, page) {
	const view = doc.defaultView
	const changes = []
	// null background: an image is behind the text, whose colours were chosen for it.
	const visit = (element, background) => {
		const style = view.getComputedStyle(element)
		let behind = background
		const own = parse(style.backgroundColor)
		if (own && own[3] > 0 && background) {
			let color = blend(own, background)
			if (luminance(color) > LIGHT_BACKGROUND) {
				color = darken(color, page)
				changes.push([element, 'background-color', css(color)])
			}
			behind = color
		}
		if (style.backgroundImage.includes('url(')) {
			behind = null
		}
		const text = parse(style.color)
		if (behind && text && text[3] > 0) {
			const color = blend(text, behind)
			if (contrast(color, behind) < MIN_CONTRAST) {
				changes.push([element, 'color', css(lighten(color, behind))])
			}
		}
		for (const child of element.children) {
			if (!SKIPPED.has(child.tagName)) {
				visit(child, behind)
			}
		}
	}
	visit(doc.body, page)
	for (const [element, property, value] of changes) {
		element.style.setProperty(property, value, 'important')
	}
}

/**
 * The page's own colours, and whether they are dark (Nextcloud's dark theme, or the default
 * theme following a dark system setting).
 */
export function pageColors() {
	const probe = document.createElement('span')
	document.body.append(probe)
	const resolve = (variable, fallback) => {
		probe.style.color = `var(${variable}, ${fallback})`
		return parse(getComputedStyle(probe).color)
	}
	const background = resolve('--color-main-background', '#fff') ?? [255, 255, 255, 1]
	const text = resolve('--color-main-text', '#222') ?? [34, 34, 34, 1]
	probe.remove()
	return { dark: luminance(background) < LIGHT_BACKGROUND / 2, background: css(background), text: css(text), rgb: background.slice(0, 3) }
}
