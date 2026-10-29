<template>
	<NcDialog :name="t('sockless_mail', 'Trusted domains')"
		:open="true"
		size="normal"
		@update:open="(open) => !open && emit('close')">
		<p class="trusted__hint">
			{{ t('sockless_mail', 'Mail from these domains and their subdomains is never marked as suspicious. Domains imitating them still are.') }}
		</p>
		<div v-if="loading" class="trusted__center">
			<NcLoadingIcon :size="32" />
		</div>
		<p v-else-if="error" class="trusted__hint">
			{{ error }}
		</p>
		<p v-else-if="domains.length === 0" class="trusted__hint">
			{{ t('sockless_mail', 'No trusted domains yet. Use "Trust this domain" on a warning to add one.') }}
		</p>
		<ul v-else class="trusted__list">
			<li v-for="domain in domains" :key="domain">
				<span>{{ domain }}</span>
				<NcButton variant="tertiary"
					:aria-label="t('sockless_mail', 'Stop trusting {domain}', { domain })"
					:title="t('sockless_mail', 'Stop trusting {domain}', { domain })"
					@click="remove(domain)">
					<template #icon>
						<NcIconSvgWrapper :path="mdiDeleteOutline" :size="18" />
					</template>
				</NcButton>
			</li>
		</ul>
	</NcDialog>
</template>

<script setup>
import { mdiDeleteOutline } from '@mdi/js'
import { showError } from '@nextcloud/dialogs/toast'
import { t } from '@nextcloud/l10n'
import NcButton from '@nextcloud/vue/components/NcButton'
import NcDialog from '@nextcloud/vue/components/NcDialog'
import NcIconSvgWrapper from '@nextcloud/vue/components/NcIconSvgWrapper'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import { onMounted, ref } from 'vue'
import { distrustDomain, listTrusted } from '../store/senders.js'

const emit = defineEmits(['close'])

const domains = ref([])
const loading = ref(true)
const error = ref(null)

onMounted(async () => {
	try {
		domains.value = await listTrusted()
	} catch (e) {
		error.value = t('sockless_mail', 'Could not load the trusted domains: {error}', { error: e.message })
	} finally {
		loading.value = false
	}
})

async function remove(domain) {
	try {
		await distrustDomain(domain)
		domains.value = domains.value.filter((d) => d !== domain)
	} catch (e) {
		showError(t('sockless_mail', 'Could not stop trusting {domain}: {error}', { domain, error: e.message }))
	}
}
</script>

<style scoped>
.trusted__hint {
	margin-block: 8px;
	color: var(--color-text-maxcontrast);
}

.trusted__center {
	display: flex;
	justify-content: center;
	padding: 16px;
}

.trusted__list {
	margin-block-end: 12px;
}

.trusted__list li {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 8px;
	padding-inline-start: 4px;
	border-bottom: 1px solid var(--color-border);
}
</style>
