<template>
	<fieldset class="server-fields">
		<legend>{{ legend }}</legend>
		<div class="server-fields__row">
			<NcTextField v-model="server.host"
				class="server-fields__host"
				:label="t('sockless_mail', 'Server')"
				:error="!!errors[`${prefix}.host`]"
				:helper-text="errors[`${prefix}.host`] ?? ''"
				autocomplete="off"
				spellcheck="false" />
			<NcTextField v-model.number="server.port"
				class="server-fields__port"
				type="number"
				:label="t('sockless_mail', 'Port')"
				:error="!!errors[`${prefix}.port`]"
				:helper-text="errors[`${prefix}.port`] ?? ''" />
		</div>
		<label class="server-fields__security">
			<span>{{ t('sockless_mail', 'Security') }}</span>
			<select :value="server.security" @change="changeSecurity($event.target.value)">
				<option value="sslTls">SSL/TLS</option>
				<option value="startTls">STARTTLS</option>
				<option value="none">{{ t('sockless_mail', 'None (unencrypted)') }}</option>
			</select>
		</label>
	</fieldset>
</template>

<script setup>
import { t } from '@nextcloud/l10n'
import NcTextField from '@nextcloud/vue/components/NcTextField'

const props = defineProps({
	legend: { type: String, required: true },
	/** "imap" or "smtp"; also the prefix of the server's field errors */
	prefix: { type: String, required: true },
	/** { host, port, security }, edited in place */
	server: { type: Object, required: true },
	errors: { type: Object, default: () => ({}) },
})

const DEFAULT_PORTS = {
	imap: { sslTls: 993, startTls: 143, none: 143 },
	smtp: { sslTls: 465, startTls: 587, none: 25 },
}

/**
 * Switching security also switches the port, unless the user typed a non-standard one.
 *
 * @param {string} security new security value
 */
function changeSecurity(security) {
	const defaults = DEFAULT_PORTS[props.prefix]
	if (Object.values(defaults).includes(props.server.port)) {
		props.server.port = defaults[security]
	}
	props.server.security = security
}
</script>

<style scoped>
.server-fields {
	margin-top: 16px;
}

.server-fields legend {
	font-weight: bold;
	margin-bottom: 4px;
}

.server-fields__row {
	display: flex;
	gap: 8px;
}

.server-fields__host {
	flex: 3;
}

.server-fields__port {
	flex: 1;
}

.server-fields__security {
	display: flex;
	align-items: center;
	gap: 8px;
	margin-top: 8px;
}

.server-fields__security select {
	min-width: 200px;
}
</style>
