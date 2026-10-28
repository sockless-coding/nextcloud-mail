<template>
	<NcDialog :name="t('sockless_mail', 'Keyboard shortcuts')"
		:open="true"
		size="normal"
		@update:open="(open) => !open && emit('close')">
		<table class="shortcuts">
			<tbody>
				<tr v-for="shortcut in [...SHORTCUTS, ...LOCAL_SHORTCUTS]" :key="shortcut.label">
					<td class="shortcuts__keys">
						<template v-for="(combo, index) in shortcut.keys" :key="combo">
							<span v-if="index > 0" class="shortcuts__or">{{ t('sockless_mail', 'or') }}</span>
							<kbd v-for="key in combo.split('+')" :key="key">{{ key }}</kbd>
						</template>
					</td>
					<td>{{ shortcut.label }}</td>
				</tr>
			</tbody>
		</table>
	</NcDialog>
</template>

<script setup>
import { t } from '@nextcloud/l10n'
import NcDialog from '@nextcloud/vue/components/NcDialog'
import { LOCAL_SHORTCUTS, SHORTCUTS } from '../store/shortcuts.js'

const emit = defineEmits(['close'])
</script>

<style scoped>
.shortcuts {
	width: 100%;
	border-collapse: collapse;
}

.shortcuts td {
	padding: 6px 8px;
	border-bottom: 1px solid var(--color-border);
	vertical-align: middle;
}

.shortcuts__keys {
	white-space: nowrap;
}

.shortcuts kbd {
	display: inline-block;
	min-width: 22px;
	margin-inline-end: 3px;
	padding: 1px 6px;
	border: 1px solid var(--color-border-dark, var(--color-border));
	border-bottom-width: 2px;
	border-radius: 4px;
	background: var(--color-background-hover);
	font: inherit;
	font-size: 12px;
	text-align: center;
}

.shortcuts__or {
	margin: 0 6px 0 3px;
	font-size: 12px;
	color: var(--color-text-maxcontrast);
}
</style>
