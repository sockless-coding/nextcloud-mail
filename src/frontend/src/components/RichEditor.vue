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
	</div>
</template>

<script setup>
import { mdiCancel, mdiFormatAlignCenter, mdiFormatAlignLeft, mdiFormatAlignRight, mdiFormatBold, mdiFormatClear, mdiFormatColorHighlight, mdiFormatColorText, mdiFormatItalic, mdiFormatListBulleted, mdiFormatListNumbered, mdiFormatQuoteClose, mdiFormatStrikethrough, mdiFormatUnderline, mdiLinkVariant, mdiRedo, mdiUndo } from '@mdi/js'
import { t } from '@nextcloud/l10n'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import Highlight from '@tiptap/extension-highlight'
import TextAlign from '@tiptap/extension-text-align'
import { Color, FontFamily, FontSize, TextStyle } from '@tiptap/extension-text-style'
import { Placeholder } from '@tiptap/extensions'
import StarterKit from '@tiptap/starter-kit'
import { EditorContent, useEditor } from '@tiptap/vue-3'
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'

/**
 * The rich-text editor of the composer (TipTap). The model is HTML; what the toolbar offers is
 * what the server lets through to recipients (see OutgoingHtml on the server).
 */
const props = defineProps({
	modelValue: { type: String, default: '' },
	label: { type: String, required: true },
	placeholder: { type: String, default: '' },
})
/** normalized: the loaded content as the editor writes it (see Composer.vue). */
const emit = defineEmits(['update:modelValue', 'normalized'])

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
	],
	editorProps: {
		attributes: { 'aria-label': props.label, role: 'textbox', 'aria-multiline': 'true', spellcheck: 'true' },
	},
	onUpdate: ({ editor: e }) => emit('update:modelValue', e.isEmpty ? '' : e.getHTML()),
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

defineExpose({ getText, focus })
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

.rich-editor__content :deep(.ProseMirror p.is-editor-empty:first-child::before) {
	content: attr(data-placeholder);
	float: inline-start;
	height: 0;
	color: var(--color-text-maxcontrast);
	pointer-events: none;
}
</style>
