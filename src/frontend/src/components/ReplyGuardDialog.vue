<template>
	<!-- Before replying in a conversation: newer messages the reply may miss, and people a reply
	     all would leave out (store/conversation.js, guardFor). -->
	<NcDialog :name="newer.length ? t('sockless_mail', 'There are newer messages') : t('sockless_mail', 'Some people are not in this reply')"
		size="normal"
		:open="true"
		:buttons="buttons"
		@update:open="(open) => !open && close()">
		<p v-if="newer.length">
			{{ n('sockless_mail',
				'{count} message in this conversation came after the one you are answering, from {people}.',
				'{count} messages in this conversation came after the one you are answering, from {people}.',
				newer.length,
				{ count: newer.length, people: names(newer.map((e) => e.message.from[0]).filter(Boolean)) }) }}
		</p>
		<template v-if="guard.dropped.length">
			<p>
				{{ t('sockless_mail', 'Earlier in the conversation, {people} took part, but they would not get this reply.', { people: names(guard.dropped) }) }}
			</p>
			<NcCheckboxRadioSwitch v-model="addDropped">
				{{ t('sockless_mail', 'Add them back to Cc') }}
			</NcCheckboxRadioSwitch>
		</template>
	</NcDialog>
</template>

<script setup>
import { showError } from '@nextcloud/dialogs/toast'
import { n, t } from '@nextcloud/l10n'
import NcCheckboxRadioSwitch from '@nextcloud/vue/components/NcCheckboxRadioSwitch'
import NcDialog from '@nextcloud/vue/components/NcDialog'
import { computed, ref } from 'vue'
import { answerGuard, conversationState, entryByKey, showNewer } from '../store/conversation.js'

const guard = conversationState.guard
const newer = computed(() => guard.newer.map(entryByKey).filter(Boolean))
const addDropped = ref(true)

function names(addresses) {
	const unique = [...new Map(addresses.map((a) => [a.address.toLowerCase(), a.name || a.address])).values()]
	return unique.length > 4 ? `${unique.slice(0, 3).join(', ')} +${unique.length - 3}` : unique.join(', ')
}

async function answer(target) {
	try {
		await answerGuard(guard, target, addDropped.value)
	} catch (e) {
		showError(t('sockless_mail', 'Could not prepare the message: {error}', { error: e.message }))
	}
}

/** NcDialog closes itself after any button and reports it; only then may the dialog go (unmounting it earlier trips its focus trap). */
function close() {
	if (conversationState.guard === guard) {
		conversationState.guard = null
	}
}

const buttons = computed(() => [
	newer.value.length && {
		label: t('sockless_mail', 'Show them'),
		callback: () => showNewer(guard),
	},
	{
		label: newer.value.length ? t('sockless_mail', 'Reply anyway') : t('sockless_mail', 'Reply'),
		variant: guard.latestKey ? 'secondary' : 'primary',
		callback: () => answer('this'),
	},
	guard.latestKey && {
		label: t('sockless_mail', 'Reply to the latest'),
		variant: 'primary',
		callback: () => answer('latest'),
	},
].filter(Boolean))
</script>
