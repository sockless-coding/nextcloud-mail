<template>
	<!-- A link whose text shows another domain than it leads to (marked by the server, see
	     MessageHtmlSanitizer.cs): say where it really goes before opening it. -->
	<NcDialog :name="t('sockless_mail', 'This link leads somewhere else')"
		size="small"
		:open="true"
		:buttons="buttons"
		@update:open="(open) => !open && emit('close', link.id)">
		<p>
			{{ t('sockless_mail', 'The link shows {shown}, but it leads to {host}.', { shown: link.shows, host: link.leads }) }}
		</p>
		<p class="deceptive-link__url">
			{{ link.href }}
		</p>
		<p>
			{{ t('sockless_mail', 'Only open it if you expected this address. Phishing messages hide where their links go this way.') }}
		</p>
	</NcDialog>
</template>

<script setup>
import { t } from '@nextcloud/l10n'
import NcDialog from '@nextcloud/vue/components/NcDialog'

const props = defineProps({
	/** { id, href, shows, leads }; close is emitted with the id */
	link: { type: Object, required: true },
})
const emit = defineEmits(['close'])

// The dialog closes itself after a button (update:open), which emits close.
const buttons = [
	{ label: t('sockless_mail', 'Cancel'), variant: 'primary' },
	{
		label: t('sockless_mail', 'Open {host}', { host: props.link.leads }),
		callback: () => window.open(props.link.href, '_blank', 'noopener,noreferrer'),
	},
]
</script>

<style scoped>
.deceptive-link__url {
	margin: 8px 0;
	padding: 8px;
	border-radius: var(--border-radius);
	background: var(--color-background-dark);
	font-family: var(--font-face-monospace, monospace);
	overflow-wrap: anywhere;
	max-height: 8em;
	overflow-y: auto;
}
</style>
