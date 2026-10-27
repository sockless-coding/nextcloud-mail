<template>
	<NcContent app-name="sockless_mail">
		<NcAppContent>
			<div class="sockless-hello">
				<NcLoadingIcon v-if="loading" :size="64" />
				<NcEmptyContent v-else-if="error" :name="t('sockless_mail', 'Could not reach Sockless Mail')" :description="error" />
				<NcEmptyContent v-else
					:name="t('sockless_mail', 'Hello {name}', { name: me.displayName || me.userId })"
					:description="t('sockless_mail', 'Signed in as {userId}. API via {mode}.', { userId: me.userId, mode: apiMode })">
					<template #icon>
						<svg viewBox="0 0 24 24" aria-hidden="true">
							<path fill="currentColor" d="M20 4H4a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V6a2 2 0 0 0-2-2Zm0 4-8 5-8-5V6l8 5 8-5v2Z" />
						</svg>
					</template>
				</NcEmptyContent>
			</div>
		</NcAppContent>
	</NcContent>
</template>

<script setup>
import { t } from '@nextcloud/l10n'
import NcAppContent from '@nextcloud/vue/components/NcAppContent'
import NcContent from '@nextcloud/vue/components/NcContent'
import NcEmptyContent from '@nextcloud/vue/components/NcEmptyContent'
import NcLoadingIcon from '@nextcloud/vue/components/NcLoadingIcon'
import { onMounted, ref } from 'vue'
import { apiGet, apiMode } from './api.js'

const loading = ref(true)
const error = ref('')
const me = ref({ userId: '', displayName: null })

onMounted(async () => {
	try {
		me.value = await apiGet('/me')
	} catch (e) {
		error.value = e?.response ? `HTTP ${e.response.status}` : String(e)
	} finally {
		loading.value = false
	}
})
</script>

<style scoped>
.sockless-hello {
	display: flex;
	align-items: center;
	justify-content: center;
	height: 100%;
}
</style>
