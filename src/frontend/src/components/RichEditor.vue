<template>
	<div class="rich-editor">
		<div class="rich-editor__toolbar" role="toolbar" :aria-label="t('sockless_mail', 'Formatting')">
			<select class="rich-editor__select"
				:aria-label="t('sockless_mail', 'Paragraph style')"
				:value="blockStyle"
				@change="setBlockStyle($event.target.value)">
				<option value="paragraph">
					{{ t('sockless_mail', 'Normal') }}
				</option>
				<option v-for="level in [1, 2, 3]" :key="level" :value="`h${level}`">
					{{ t('sockless_mail', 'Heading {level}', { level }) }}
				</option>
			</select>
			<select class="rich-editor__select"
				:aria-label="t('sockless_mail', 'Font')"
				:value="editor?.getAttributes('textStyle').fontFamily ?? ''"
				@change="setFont($event.target.value)">
				<option v-for="font in FONTS" :key="font.label" :value="font.value">
					{{ font.label }}
				</option>
			</select>
			<select class="rich-editor__select rich-editor__select--size"
				:aria-label="t('sockless_mail', 'Font size')"
				:value="editor?.getAttributes('textStyle').fontSize ?? ''"
				@change="setSize($event.target.value)">
				<option value="">
					{{ t('sockless_mail', 'Size') }}
				</option>
				<option v-for="size in SIZES" :key="size" :value="size">
					{{ parseInt(size) }}
				</option>
			</select>
			<span class="rich-editor__separator" />
			<button v-for="mark in MARKS"
				:key="mark.name"
				type="button"
				class="rich-editor__button"
				:aria-label="mark.label"
				:title="mark.label"
				:aria-pressed="String(!!editor?.isActive(mark.name))"
				@click="mark.toggle()">
				<NcIconSvgWrapper :path="mark.icon" :size="18" />
			</button>
			<span class="rich-editor__palette">
				<button type="button"
					class="rich-editor__button"
					:aria-label="t('sockless_mail', 'Text colour')"
					:title="t('sockless_mail', 'Text colour')"
					:aria-expanded="String(palette === 'color')"
					@click="palette = palette === 'color' ? null : 'color'">
					<NcIconSvgWrapper :path="mdiFormatColorText" :size="18" />
					<span class="rich-editor__swatch-bar" :style="{ background: editor?.getAttributes('textStyle').color || 'currentColor' }" />
				</button>
				<button type="button"
					class="rich-editor__button"
					:aria-label="t('sockless_mail', 'Highlight')"
					:title="t('sockless_mail', 'Highlight')"
					:aria-expanded="String(palette === 'highlight')"
					@click="palette = palette === 'highlight' ? null : 'highlight'">
					<NcIconSvgWrapper :path="mdiFormatColorHighlight" :size="18" />
				</button>
				<span v-if="palette" class="rich-editor__swatches" role="group" :aria-label="palette === 'color' ? t('sockless_mail', 'Text colour') : t('sockless_mail', 'Highlight')">
					<button v-for="swatch in (palette === 'color' ? COLORS : HIGHLIGHTS)"
						:key="swatch.value"
						type="button"
						class="rich-editor__swatch"
						:style="{ background: swatch.value }"
						:aria-label="swatch.label"
						:title="swatch.label"
						@click="applyColor(swatch.value)" />
					<button type="button" class="rich-editor__swatch rich-editor__swatch--none" :aria-label="t('sockless_mail', 'Automatic')" :title="t('sockless_mail', 'Automatic')" @click="applyColor(null)">
						<NcIconSvgWrapper :path="mdiCancel" :size="14" />
					</button>
				</span>
			</span>
			<span class="rich-editor__separator" />
			<button v-for="block in BLOCKS"
				:key="block.name"
				type="button"
				class="rich-editor__button"
				:aria-label="block.label"
				:title="block.label"
				:aria-pressed="String(!!block.active())"
				@click="block.toggle()">
				<NcIconSvgWrapper :path="block.icon" :size="18" />
			</button>
			<span class="rich-editor__separator" />
			<span class="rich-editor__palette">
				<button type="button"
					class="rich-editor__button"
					:aria-label="t('sockless_mail', 'Link')"
					:title="t('sockless_mail', 'Link')"
					:aria-pressed="String(!!editor?.isActive('link'))"
					:aria-expanded="String(linking)"
					@click="startLink">
					<NcIconSvgWrapper :path="mdiLinkVariant" :size="18" />
				</button>
				<form v-if="linking" class="rich-editor__link" @submit.prevent="applyLink">
					<input ref="linkInput"
						v-model="linkUrl"
						type="url"
						:aria-label="t('sockless_mail', 'Link address')"
						placeholder="https://"
						@keydown.esc.prevent.stop="linking = false">
					<button type="submit" class="rich-editor__link-apply">{{ t('sockless_mail', 'Apply') }}</button>
					<button v-if="editor?.isActive('link')" type="button" class="rich-editor__link-apply" @click="removeLink">{{ t('sockless_mail', 'Remove') }}</button>
				</form>
			</span>
			<NcActions class="rich-editor__actions"
				variant="tertiary"
				:menu-name="t('sockless_mail', 'Insert image')"
				:aria-label="t('sockless_mail', 'Insert image')">
				<template #icon>
					<NcIconSvgWrapper :path="mdiImagePlusOutline" :size="18" />
				</template>
				<NcActionButton close-after-click @click="imageInput.click()">
					<template #icon>
						<NcIconSvgWrapper :path="mdiUpload" />
					</template>
					{{ t('sockless_mail', 'From this device') }}
				</NcActionButton>
				<NcActionButton close-after-click @click="insertFromFiles">
					<template #icon>
						<NcIconSvgWrapper :path="mdiFolderOutline" />
					</template>
					{{ t('sockless_mail', 'From Files') }}
				</NcActionButton>
			</NcActions>
			<input ref="imageInput"
				type="file"
				:accept="INLINE_IMAGE_TYPES.join(',')"
				multiple
				hidden
				@change="onImageFiles">
			<button type="button"
				class="rich-editor__button"
				:aria-label="t('sockless_mail', 'Clear formatting')"
				:title="t('sockless_mail', 'Clear formatting')"
				@click="editor?.chain().focus().unsetAllMarks().clearNodes().run()">
				<NcIconSvgWrapper :path="mdiFormatClear" :size="18" />
			</button>
			<span class="rich-editor__separator" />
			<button type="button"
				class="rich-editor__button"
				:aria-label="t('sockless_mail', 'Undo')"
				:title="t('sockless_mail', 'Undo')"
				:disabled="!editor?.can().undo()"
				@click="editor?.chain().focus().undo().run()">
				<NcIconSvgWrapper :path="mdiUndo" :size="18" />
			</button>
			<button type="button"
				class="rich-editor__button"
				:aria-label="t('sockless_mail', 'Redo')"
				:title="t('sockless_mail', 'Redo')"
				:disabled="!editor?.can().redo()"
				@click="editor?.chain().focus().redo().run()">
				<NcIconSvgWrapper :path="mdiRedo" :size="18" />
			</button>
		</div>
		<EditorContent class="rich-editor__content" :editor="editor" />
		<BubbleMenu v-if="editor"
			:editor="editor"
			plugin-key="imageMenu"
			:should-show="({ editor: e }) => e.isEditable && e.isActive('image')"
			:options="{ placement: 'bottom' }">
			<div class="rich-editor__image-menu" role="toolbar" :aria-label="t('sockless_mail', 'Image')">
				<form v-if="editingAlt" class="rich-editor__alt" @submit.prevent="applyAlt">
					<input ref="altInput"
						v-model="altText"
						type="text"
						:aria-label="t('sockless_mail', 'Description for people who cannot see the image')"
						:placeholder="t('sockless_mail', 'Describe the image')"
						@keydown.esc.prevent.stop="editingAlt = false">
					<button type="submit" class="rich-editor__link-apply">{{ t('sockless_mail', 'Apply') }}</button>
				</form>
				<template v-else>
					<button v-for="size in IMAGE_SIZES"
						:key="size.label"
						type="button"
						class="rich-editor__size"
						:aria-pressed="String(imageWidth === size.width)"
						:title="size.title"
						@click="setImageWidth(size.width)">
						{{ size.label }}
					</button>
					<span class="rich-editor__separator" />
					<button v-if="originals.has(imageAttrs.cid)"
						type="button"
						class="rich-editor__size"
						:title="t('sockless_mail', 'The image was made smaller to keep the message small. Send it as it was instead.')"
						@click="useOriginal">
						{{ t('sockless_mail', 'Full quality') }}
					</button>
					<button type="button"
						class="rich-editor__button"
						:aria-label="t('sockless_mail', 'Alternative text')"
						:title="t('sockless_mail', 'Alternative text')"
						:aria-pressed="String(!!imageAttrs.alt)"
						@click="startAlt">
						<NcIconSvgWrapper :path="mdiImageText" :size="18" />
					</button>
					<button type="button"
						class="rich-editor__button"
						:aria-label="t('sockless_mail', 'Remove image')"
						:title="t('sockless_mail', 'Remove image')"
						@click="chain().deleteSelection().run()">
						<NcIconSvgWrapper :path="mdiDeleteOutline" :size="18" />
					</button>
				</template>
			</div>
		</BubbleMenu>
	</div>
</template>

<script setup>
import { mdiCancel, mdiDeleteOutline, mdiFolderOutline, mdiImagePlusOutline, mdiImageText, mdiUpload, mdiFormatAlignCenter, mdiFormatAlignLeft, mdiFormatAlignRight, mdiFormatBold, mdiFormatClear, mdiFormatColorHighlight, mdiFormatColorText, mdiFormatItalic, mdiFormatListBulleted, mdiFormatListNumbered, mdiFormatQuoteClose, mdiFormatStrikethrough, mdiFormatUnderline, mdiLinkVariant, mdiRedo, mdiUndo } from '@mdi/js'
import { showError, showWarning } from '@nextcloud/dialogs/toast'
import { n, t } from '@nextcloud/l10n'
import NcActionButton from '@nextcloud/vue/components/NcActionButton'
import NcActions from '@nextcloud/vue/components/NcActions'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import Highlight from '@tiptap/extension-highlight'
import Image from '@tiptap/extension-image'
import TextAlign from '@tiptap/extension-text-align'
import { Color, FontFamily, FontSize, TextStyle } from '@tiptap/extension-text-style'
import { Placeholder } from '@tiptap/extensions'
import StarterKit from '@tiptap/starter-kit'
import { EditorContent, useEditor } from '@tiptap/vue-3'
import { BubbleMenu } from '@tiptap/vue-3/menus'
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { apiBase, apiGetBlob } from '../api.js'
import { dataUrlFile, INLINE_IMAGE_TYPES, placeholderUrl, prepareImage } from '../images.js'
import { adoptImage, discardImage, imageJob, importImage, objectUrl, uploadContentUrl, uploadImage } from '../store/compose.js'
import { copyFromFiles, pickImages } from '../store/files.js'

/**
 * The rich-text editor of the composer (TipTap). The model is HTML; what the toolbar offers is
 * what the server lets through to recipients (see OutgoingHtml on the server).
 */
const props = defineProps({
	modelValue: { type: String, default: '' },
	label: { type: String, required: true },
	placeholder: { type: String, default: '' },
})
/**
 * normalized: the loaded content as the editor writes it (see Composer.vue).
 * attach: files dropped or pasted that are not images, for the composer to attach.
 */
const emit = defineEmits(['update:modelValue', 'normalized', 'attach'])

const FONTS = [
	{ label: t('sockless_mail', 'Default font'), value: '' },
	{ label: 'Arial', value: 'Arial, Helvetica, sans-serif' },
	{ label: 'Georgia', value: 'Georgia, serif' },
	{ label: 'Times New Roman', value: '"Times New Roman", Times, serif' },
	{ label: 'Verdana', value: 'Verdana, Geneva, sans-serif' },
	{ label: 'Courier New', value: '"Courier New", Courier, monospace' },
]
const SIZES = ['10px', '12px', '14px', '16px', '18px', '24px', '32px']
const COLORS = [
	{ label: t('sockless_mail', 'Black'), value: '#000000' },
	{ label: t('sockless_mail', 'Grey'), value: '#5f6368' },
	{ label: t('sockless_mail', 'Red'), value: '#c5221f' },
	{ label: t('sockless_mail', 'Orange'), value: '#e37400' },
	{ label: t('sockless_mail', 'Green'), value: '#188038' },
	{ label: t('sockless_mail', 'Teal'), value: '#0e7584' },
	{ label: t('sockless_mail', 'Blue'), value: '#1a62d6' },
	{ label: t('sockless_mail', 'Purple'), value: '#7346c2' },
]
const HIGHLIGHTS = [
	{ label: t('sockless_mail', 'Yellow'), value: '#fff59d' },
	{ label: t('sockless_mail', 'Green'), value: '#c8e6c9' },
	{ label: t('sockless_mail', 'Blue'), value: '#bbdefb' },
	{ label: t('sockless_mail', 'Pink'), value: '#f8bbd0' },
	{ label: t('sockless_mail', 'Grey'), value: '#e0e0e0' },
]

/**
 * Images are parts of the message, marked with their Content-ID (data-cid, see OutgoingHtml on
 * the server); src only shows them here. pending marks one still being added; source is where
 * a pasted one is fetched from (data-import, see transformPastedHTML).
 */
const MailImage = Image.extend({
	addAttributes() {
		return {
			...this.parent?.(),
			cid: {
				default: null,
				parseHTML: (element) => element.getAttribute('data-cid'),
				renderHTML: (attributes) => (attributes.cid ? { 'data-cid': attributes.cid } : {}),
			},
			pending: {
				default: null,
				parseHTML: () => null,
				renderHTML: (attributes) => (attributes.pending ? { 'data-pending': '' } : {}),
			},
			source: {
				default: null,
				parseHTML: (element) => element.getAttribute('data-import'),
				renderHTML: (attributes) => (attributes.source ? { 'data-import': attributes.source } : {}),
			},
		}
	},
}).configure({ inline: true })

const editor = useEditor({
	content: props.modelValue,
	extensions: [
		StarterKit.configure({
			heading: { levels: [1, 2, 3] },
			code: false,
			codeBlock: false,
			horizontalRule: false,
			link: { openOnClick: false, autolink: true, protocols: ['mailto'], defaultProtocol: 'https' },
		}),
		TextStyle,
		Color,
		FontFamily,
		FontSize,
		Highlight.configure({ multicolor: true }),
		TextAlign.configure({ types: ['heading', 'paragraph'] }),
		Placeholder.configure({ placeholder: () => props.placeholder }),
		MailImage,
	],
	editorProps: {
		attributes: { 'aria-label': props.label, role: 'textbox', 'aria-multiline': 'true', spellcheck: 'true' },
		handlePaste: (view, event) => takeFiles(event.clipboardData, event, null),
		handleDrop: (view, event, slice, moved) => !moved
			&& takeFiles(event.dataTransfer, event, view.posAtCoords({ left: event.clientX, top: event.clientY })?.pos ?? null),
		// Images of pasted content are fetched and become parts of the message (startImports);
		// until then a placeholder shows, so the browser does not load them from elsewhere.
		transformPastedHTML: (html) => {
			const document = new DOMParser().parseFromString(html, 'text/html')
			for (const img of document.querySelectorAll('img:not([data-cid])')) {
				const source = importable(img.getAttribute('src') ?? '')
				if (source) {
					img.setAttribute('data-import', source)
					img.setAttribute('src', placeholderUrl())
				} else {
					img.remove()
				}
			}
			return document.body.innerHTML
		},
	},
	onUpdate: ({ editor: e, transaction }) => {
		emit('update:modelValue', e.isEmpty ? '' : e.getHTML())
		if (['paste', 'drop'].includes(transaction.getMeta('uiEvent'))) {
			window.setTimeout(startImports)
		}
	},
	// After the first tick, so plugins that adjust the loaded document (a trailing paragraph) are done.
	onCreate: ({ editor: e }) => window.setTimeout(() => emit('normalized', e.isEmpty ? '' : e.getHTML())),
})

// Changes from outside (switching from plain text) replace the content.
watch(() => props.modelValue, (value) => {
	const current = editor.value?.isEmpty ? '' : editor.value?.getHTML()
	if (editor.value && value !== current) {
		editor.value.commands.setContent(value, { emitUpdate: false })
	}
})

onBeforeUnmount(() => editor.value?.destroy())

const chain = () => editor.value.chain().focus()

const MARKS = [
	{ name: 'bold', icon: mdiFormatBold, label: t('sockless_mail', 'Bold'), toggle: () => chain().toggleBold().run() },
	{ name: 'italic', icon: mdiFormatItalic, label: t('sockless_mail', 'Italic'), toggle: () => chain().toggleItalic().run() },
	{ name: 'underline', icon: mdiFormatUnderline, label: t('sockless_mail', 'Underline'), toggle: () => chain().toggleUnderline().run() },
	{ name: 'strike', icon: mdiFormatStrikethrough, label: t('sockless_mail', 'Strikethrough'), toggle: () => chain().toggleStrike().run() },
]

const BLOCKS = [
	{ name: 'bulletList', icon: mdiFormatListBulleted, label: t('sockless_mail', 'Bulleted list'), active: () => editor.value?.isActive('bulletList'), toggle: () => chain().toggleBulletList().run() },
	{ name: 'orderedList', icon: mdiFormatListNumbered, label: t('sockless_mail', 'Numbered list'), active: () => editor.value?.isActive('orderedList'), toggle: () => chain().toggleOrderedList().run() },
	{ name: 'blockquote', icon: mdiFormatQuoteClose, label: t('sockless_mail', 'Quote'), active: () => editor.value?.isActive('blockquote'), toggle: () => chain().toggleBlockquote().run() },
	{ name: 'left', icon: mdiFormatAlignLeft, label: t('sockless_mail', 'Align left'), active: () => editor.value?.isActive({ textAlign: 'left' }), toggle: () => chain().setTextAlign('left').run() },
	{ name: 'center', icon: mdiFormatAlignCenter, label: t('sockless_mail', 'Center'), active: () => editor.value?.isActive({ textAlign: 'center' }), toggle: () => chain().setTextAlign('center').run() },
	{ name: 'right', icon: mdiFormatAlignRight, label: t('sockless_mail', 'Align right'), active: () => editor.value?.isActive({ textAlign: 'right' }), toggle: () => chain().setTextAlign('right').run() },
]

const blockStyle = computed(() => {
	for (const level of [1, 2, 3]) {
		if (editor.value?.isActive('heading', { level })) {
			return `h${level}`
		}
	}
	return 'paragraph'
})

function setBlockStyle(value) {
	if (value === 'paragraph') {
		chain().setParagraph().run()
	} else {
		chain().setHeading({ level: Number(value.slice(1)) }).run()
	}
}

function setFont(value) {
	value ? chain().setFontFamily(value).run() : chain().unsetFontFamily().run()
}

function setSize(value) {
	value ? chain().setFontSize(value).run() : chain().unsetFontSize().run()
}

const palette = ref(null)

function applyColor(value) {
	if (palette.value === 'color') {
		value ? chain().setColor(value).run() : chain().unsetColor().run()
	} else {
		value ? chain().setHighlight({ color: value }).run() : chain().unsetHighlight().run()
	}
	palette.value = null
}

const linking = ref(false)
const linkUrl = ref('')
const linkInput = ref(null)

async function startLink() {
	if (linking.value) {
		linking.value = false
		return
	}
	linkUrl.value = editor.value?.getAttributes('link').href ?? ''
	linking.value = true
	await nextTick()
	linkInput.value?.focus()
}

function applyLink() {
	const url = linkUrl.value.trim()
	if (url) {
		chain().extendMarkRange('link').setLink({ href: /^(https?:|mailto:)/i.test(url) ? url : `https://${url}` }).run()
	}
	linking.value = false
}

function removeLink() {
	chain().extendMarkRange('link').unsetLink().run()
	linking.value = false
}

/** The text without formatting, for switching to plain text. */
function getText() {
	return editor.value?.getText({ blockSeparator: '\n' }) ?? ''
}

function focus(position = 'start') {
	editor.value?.commands.focus(position)
}

/**
 * Inserts links where the cursor was, one per line, each showing its name (share links).
 *
 * @param {{name: string, url: string}[]} links what to insert
 */
function insertLinks(links) {
	const content = links.flatMap((link, i) => [
		...(i > 0 ? [{ type: 'hardBreak' }] : []),
		{ type: 'text', text: link.name, marks: [{ type: 'link', attrs: { href: link.url } }] },
	])
	editor.value?.chain().focus().insertContent(content).unsetMark('link').insertContent(' ').run()
}

/**
 * Files pasted or dropped: images go into the text, other files are attached. Pasted content
 * with text of its own (from a document, often with a picture of itself as a file) is pasted
 * as text instead.
 *
 * @param {DataTransfer | null} data what was pasted or dropped
 * @param {Event} event the paste or drop
 * @param {number | null} position where to insert; the cursor when null
 * @return {boolean} whether the files were taken (and the event handled)
 */
function takeFiles(data, event, position) {
	const files = [...(data?.files ?? [])]
	if (!files.length) {
		return false
	}
	if (event.type === 'paste') {
		const html = data.getData('text/html')
		const text = html ? new DOMParser().parseFromString(html, 'text/html').body.textContent.trim() : data.getData('text/plain').trim()
		if (text) {
			return false
		}
	}
	event.preventDefault()
	const images = files.filter((f) => INLINE_IMAGE_TYPES.includes(f.type))
	const others = files.filter((f) => !images.includes(f))
	if (others.length) {
		emit('attach', others)
	}
	insertImages(images, position)
	return true
}

/** Originals of images that were made smaller, by Content-ID, while the composer is open. */
const originals = ref(new Map())

/** A key for an image being added, to find it again (its position changes as the user types). */
const pendingKey = () => `${Date.now()}-${Math.random()}`

/**
 * Makes a file an image of the message: smaller when needed, uploaded (or the upload it already
 * is, kept), and marked on the images that match.
 *
 * @param {File} file the image
 * @param {(attrs: object) => boolean} matches which images it is
 * @param {{id: string} | null} upload the upload the file came from, if any
 * @param {boolean} show whether to show it from the file (it shows a placeholder so far); a
 *   smaller one is always shown
 */
async function finishImage(file, matches, upload = null, show = false) {
	const prepared = await prepareImage(file)
	let cid
	if (upload && prepared.file === file) {
		cid = adoptImage(upload.id)
	} else {
		if (upload) {
			discardImage(upload.id)
		}
		cid = await uploadImage(prepared.file)
	}
	if (prepared.scaled) {
		originals.value.set(cid, file)
	}
	// What is shown is what is sent: the smaller one, once there is one.
	const shown = show || prepared.file !== file
	updateImage(matches, { cid, pending: null, source: null, ...(shown ? { src: objectUrl(prepared.file) } : {}) })
}

/**
 * Puts images into the text, shown at once and uploaded meanwhile.
 *
 * @param {File[]} files images
 * @param {number | null} position where; the cursor when null
 */
function insertImages(files, position) {
	let at = position
	for (const file of files) {
		const pending = pendingKey()
		const node = { type: 'image', attrs: { src: objectUrl(file), pending } }
		if (at === null) {
			chain().insertContent(node).run()
		} else {
			editor.value?.chain().focus().insertContentAt(at, node).run()
			at = null
		}
		imageJob(() => finishImage(file, (attrs) => attrs.pending === pending))
			.catch((e) => {
				removeImage((attrs) => attrs.pending === pending)
				showError(t('sockless_mail', 'Could not upload {name}: {error}', { name: file.name, error: e.message }))
			})
	}
}

/** The app's own API (images copied from a message shown here), fetched as the user. */
const ownApi = new URL(`${apiBase}/`, window.location.origin).href

/** Where a pasted image can be fetched from, or null for one that cannot (file:, relative). */
function importable(src) {
	const value = src.trim()
	if (/^data:image\//i.test(value)) {
		return value
	}
	try {
		const url = new URL(value.startsWith('//') ? `https:${value}` : value)
		return ['http:', 'https:'].includes(url.protocol) ? url.href : null
	} catch {
		return null
	}
}

/**
 * Fetches a pasted image: data: URLs here, the app's own images as the user, anything else
 * by the server (which also keeps it as an upload).
 *
 * @param {string} source where it is
 * @return {Promise<{file: File, upload: object | null}>} the image, and the upload it already is
 */
async function fetchPasted(source) {
	if (source.startsWith('data:')) {
		return { file: dataUrlFile(source), upload: null }
	}
	if (source.startsWith(ownApi)) {
		const blob = await apiGetBlob(source)
		return { file: new File([blob], 'image', { type: blob.type }), upload: null }
	}
	const upload = await importImage(source)
	try {
		const blob = await apiGetBlob(uploadContentUrl(upload.id))
		return { file: new File([blob], upload.fileName, { type: upload.contentType }), upload }
	} catch (e) {
		discardImage(upload.id)
		throw e
	}
}

/** Fetches the images of content just pasted or dropped; those that cannot be fetched are removed. */
async function startImports() {
	const found = findImages((attrs) => attrs.source && !attrs.pending)
	if (!found.length) {
		return
	}
	const tr = editor.value.state.tr
	const jobs = found.map(({ node, pos }) => {
		const pending = pendingKey()
		tr.setNodeMarkup(pos, undefined, { ...node.attrs, pending })
		return { pending, source: node.attrs.source }
	})
	editor.value.view.dispatch(tr.setMeta('addToHistory', false))

	const results = await Promise.allSettled(jobs.map(({ pending, source }) => imageJob(async () => {
		const matches = (attrs) => attrs.pending === pending
		try {
			const { file, upload } = await fetchPasted(source)
			if (!INLINE_IMAGE_TYPES.includes(file.type)) {
				if (upload) {
					discardImage(upload.id)
				}
				throw new Error('not an image')
			}
			await finishImage(file, matches, upload, true)
		} catch (e) {
			removeImage(matches)
			throw e
		}
	})))
	const failed = results.filter((r) => r.status === 'rejected').length
	if (failed) {
		showWarning(n('sockless_mail', '{count} image of the pasted content could not be added.', '{count} images of the pasted content could not be added.', failed, { count: failed }))
	}
}

/** Images chosen in Files, copied by the server, put where the cursor is. */
async function insertFromFiles() {
	let paths
	try {
		paths = await pickImages()
	} catch (e) {
		showError(t('sockless_mail', 'Could not open Files: {error}', { error: e.message }))
		return
	}
	if (!paths?.length) {
		return
	}
	const pendings = paths.map(() => pendingKey())
	for (const pending of pendings) {
		chain().insertContent({ type: 'image', attrs: { src: placeholderUrl(), pending } }).run()
	}
	let files
	try {
		files = await imageJob(() => copyFromFiles(paths))
	} catch (e) {
		removeImage((attrs) => pendings.includes(attrs.pending))
		showError(e.message)
		return
	}
	files.forEach((copied, i) => {
		const matches = (attrs) => attrs.pending === pendings[i]
		const upload = copied.upload
		if (!upload || !INLINE_IMAGE_TYPES.includes(upload.contentType)) {
			if (upload) {
				discardImage(upload.id)
			}
			removeImage(matches)
			showError(t('sockless_mail', 'Could not add {name}: {error}', {
				name: copied.name,
				error: upload ? t('sockless_mail', 'it cannot be shown in a message') : copied.message,
			}))
			return
		}
		imageJob(async () => {
			const blob = await apiGetBlob(uploadContentUrl(upload.id))
			await finishImage(new File([blob], upload.fileName, { type: upload.contentType }), matches, upload, true)
		}).catch((e) => {
			discardImage(upload.id)
			removeImage(matches)
			showError(t('sockless_mail', 'Could not add {name}: {error}', { name: copied.name, error: e.message }))
		})
	})
}

/** Positions of the images whose attributes match. */
function findImages(matches) {
	const found = []
	editor.value?.state.doc.descendants((node, pos) => {
		if (node.type.name === 'image' && matches(node.attrs)) {
			found.push({ node, pos })
		}
	})
	return found
}

/** Changes attributes of the images that match, without an undo step (an upload finishing). */
function updateImage(matches, changes) {
	const found = findImages(matches)
	if (found.length) {
		const tr = editor.value.state.tr
		for (const { node, pos } of found) {
			tr.setNodeMarkup(pos, undefined, { ...node.attrs, ...changes })
		}
		editor.value.view.dispatch(tr.setMeta('addToHistory', false))
	}
}

function removeImage(matches) {
	const found = findImages(matches)
	if (found.length) {
		const tr = editor.value.state.tr
		for (const { pos } of found.reverse()) {
			tr.delete(pos, pos + 1)
		}
		editor.value.view.dispatch(tr.setMeta('addToHistory', false))
	}
}

const imageInput = ref(null)

function onImageFiles(event) {
	insertImages([...event.target.files].filter((f) => INLINE_IMAGE_TYPES.includes(f.type)), null)
	event.target.value = ''
}

/** Widths offered for an image; null fits it to the window it is read in. */
const IMAGE_SIZES = [
	{ label: t('sockless_mail', 'Small'), width: 200, title: t('sockless_mail', '200 pixels wide') },
	{ label: t('sockless_mail', 'Medium'), width: 400, title: t('sockless_mail', '400 pixels wide') },
	{ label: t('sockless_mail', 'Large'), width: 640, title: t('sockless_mail', '640 pixels wide') },
	{ label: t('sockless_mail', 'Fit'), width: null, title: t('sockless_mail', 'As large as it is, at most as wide as the message') },
]

const imageAttrs = computed(() => (editor.value?.isActive('image') ? editor.value.getAttributes('image') : {}))
const imageWidth = computed(() => (imageAttrs.value.width ? Number(imageAttrs.value.width) : null))

function setImageWidth(width) {
	chain().updateAttributes('image', { width, height: null }).run()
}

/** Sends the image as it was chosen, not made smaller. */
async function useOriginal() {
	const { cid } = imageAttrs.value
	const file = originals.value.get(cid)
	if (!file) {
		return
	}
	try {
		const original = await imageJob(() => uploadImage(file))
		originals.value.delete(cid)
		updateImage((attrs) => attrs.cid === cid, { cid: original, src: objectUrl(file) })
	} catch (e) {
		showError(t('sockless_mail', 'Could not upload {name}: {error}', { name: file.name, error: e.message }))
	}
}

const editingAlt = ref(false)
const altText = ref('')
const altInput = ref(null)

async function startAlt() {
	altText.value = imageAttrs.value.alt ?? ''
	editingAlt.value = true
	await nextTick()
	altInput.value?.focus()
}

function applyAlt() {
	chain().updateAttributes('image', { alt: altText.value.trim() || null }).run()
	editingAlt.value = false
}

// The alt text form belongs to the image it was opened for.
watch(() => imageAttrs.value.cid, () => { editingAlt.value = false })

/** Whether the text has images (lost when switching to plain text). */
function hasImages() {
	return findImages(() => true).length > 0
}

defineExpose({ getText, focus, insertLinks, insertImages, hasImages })
</script>

<style scoped>
.rich-editor {
	display: flex;
	flex-direction: column;
	flex: 1;
	min-height: 0;
}

.rich-editor__toolbar {
	display: flex;
	flex-wrap: wrap;
	align-items: center;
	gap: 2px;
	padding: 4px 8px;
	border-bottom: 1px solid var(--color-border);
}

.rich-editor__select {
	width: auto;
	max-width: 140px;
	height: 30px;
	min-height: 0;
	margin: 0;
	padding: 0 4px;
	border: 1px solid transparent;
	border-radius: 6px;
	background: none;
	color: var(--color-main-text);
	font: inherit;
	font-size: 13px;
}

.rich-editor__select:hover {
	border-color: var(--color-border);
}

.rich-editor__select--size {
	max-width: 70px;
}

.rich-editor__button {
	--sm-button-background: transparent;
	position: relative;
	display: grid;
	place-items: center;
	width: 30px;
	height: 30px;
	min-height: 0;
	margin: 0;
	padding: 0;
	border: 0;
	border-radius: 6px;
	background: none;
	color: var(--color-main-text);
	cursor: pointer;
}

.rich-editor__button:hover:not(:disabled) {
	--sm-button-background: var(--color-background-hover);
	background: var(--color-background-hover);
}

.rich-editor__button[aria-pressed='true'] {
	--sm-button-background: var(--color-primary-element-light);
	--sm-button-color: var(--color-primary-element-light-text);
	background: var(--color-primary-element-light);
	color: var(--color-primary-element-light-text);
}

.rich-editor__button:disabled {
	opacity: 0.4;
	cursor: default;
}

.rich-editor__button:focus-visible,
.rich-editor__swatch:focus-visible {
	outline: 2px solid var(--color-primary-element);
	outline-offset: -2px;
}

.rich-editor__separator {
	width: 1px;
	height: 20px;
	margin: 0 4px;
	background: var(--color-border);
}

.rich-editor__palette {
	position: relative;
	display: inline-flex;
}

.rich-editor__swatch-bar {
	position: absolute;
	inset-inline: 8px;
	bottom: 4px;
	height: 3px;
	border-radius: 2px;
}

.rich-editor__swatches,
.rich-editor__link {
	position: absolute;
	top: calc(100% + 4px);
	inset-inline-start: 0;
	z-index: 3;
	display: flex;
	gap: 4px;
	padding: 6px;
	border: 1px solid var(--color-border);
	border-radius: var(--border-radius-element, 8px);
	background: var(--color-main-background);
	box-shadow: 0 4px 16px rgb(0 0 0 / 15%);
}

.rich-editor__swatch {
	--sm-button-background: inherit;
	display: grid;
	place-items: center;
	width: 22px;
	height: 22px;
	min-height: 0;
	margin: 0;
	padding: 0;
	border: 1px solid var(--color-border-dark, var(--color-border));
	border-radius: 50%;
	cursor: pointer;
}

.rich-editor__swatch--none {
	--sm-button-background: var(--color-main-background);
	background: var(--color-main-background);
	color: var(--color-text-maxcontrast);
}

.rich-editor__link input {
	width: 240px;
	height: 30px;
	min-height: 0;
	margin: 0;
}

.rich-editor__link-apply {
	height: 30px;
	min-height: 0;
	margin: 0;
	padding: 0 10px;
}

.rich-editor__content {
	flex: 1;
	min-height: 0;
	overflow-y: auto;
	cursor: text;
}

/* The page as recipients will see it: the font and spacing of the sent HTML (OutgoingHtml).
   Nextcloud styles every div[contenteditable] as a small input box; this one is the page. */
.rich-editor__content :deep(.ProseMirror) {
	box-sizing: border-box;
	width: 100%;
	height: auto;
	min-height: 100%;
	margin: 0;
	padding: 12px 14px;
	border: 0;
	border-radius: 0;
	background: none;
	box-shadow: none;
	outline: none;
	font-family: Calibri, Arial, Helvetica, sans-serif;
	font-size: 11pt;
	line-height: 1.4;
	overflow-wrap: anywhere;
}

.rich-editor__content :deep(.ProseMirror p) {
	margin: 0;
	min-height: 1.4em;
}

.rich-editor__content :deep(.ProseMirror h1),
.rich-editor__content :deep(.ProseMirror h2),
.rich-editor__content :deep(.ProseMirror h3) {
	margin: 0.4em 0 0.2em;
	font-weight: 600;
}

.rich-editor__content :deep(.ProseMirror ul),
.rich-editor__content :deep(.ProseMirror ol) {
	margin: 0;
	padding-inline-start: 1.6em;
}

.rich-editor__content :deep(.ProseMirror ul) {
	list-style: disc;
}

.rich-editor__content :deep(.ProseMirror ol) {
	list-style: decimal;
}

.rich-editor__content :deep(.ProseMirror blockquote) {
	margin: 0 0 0 0.8ex;
	padding-inline-start: 1ex;
	border-inline-start: 2px solid var(--color-border-dark, #ccc);
	color: var(--color-text-maxcontrast);
}

.rich-editor__content :deep(.ProseMirror a) {
	color: var(--color-primary-element);
	text-decoration: underline;
}

.rich-editor__content :deep(.ProseMirror img) {
	max-width: 100%;
	height: auto;
	vertical-align: bottom;
}

.rich-editor__content :deep(.ProseMirror img[data-pending]) {
	opacity: 0.5;
}

.rich-editor__actions :deep(.button-vue) {
	width: 30px;
	min-width: 30px;
	height: 30px;
	min-height: 30px;
}

.rich-editor__content :deep(.ProseMirror img.ProseMirror-selectednode) {
	outline: 2px solid var(--color-primary-element);
	outline-offset: 1px;
}

.rich-editor__image-menu {
	display: flex;
	align-items: center;
	gap: 2px;
	padding: 4px;
	border: 1px solid var(--color-border);
	border-radius: var(--border-radius-element, 8px);
	background: var(--color-main-background);
	box-shadow: 0 4px 16px rgb(0 0 0 / 15%);
}

.rich-editor__size {
	height: 30px;
	min-height: 0;
	margin: 0;
	padding: 0 8px;
	border: 0;
	border-radius: 6px;
	background: none;
	color: var(--color-main-text);
	font-size: 13px;
	cursor: pointer;
}

.rich-editor__size:hover {
	background: var(--color-background-hover);
}

.rich-editor__size[aria-pressed='true'] {
	background: var(--color-primary-element-light);
	color: var(--color-primary-element-light-text);
}

.rich-editor__alt {
	display: flex;
	gap: 4px;
}

.rich-editor__alt input {
	width: 260px;
	height: 30px;
	min-height: 0;
	margin: 0;
}

.rich-editor__content :deep(.ProseMirror p.is-editor-empty:first-child::before) {
	content: attr(data-placeholder);
	float: inline-start;
	height: 0;
	color: var(--color-text-maxcontrast);
	pointer-events: none;
}
</style>
