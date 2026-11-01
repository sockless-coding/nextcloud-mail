import { showError } from '@nextcloud/dialogs/toast'
import { t } from '@nextcloud/l10n'
import { ref } from 'vue'
import { accountsState, moveAccountTo } from '../store/accounts.js'

/**
 * Reordering accounts by dragging them (the account rail, Settings → Accounts). Bind the
 * handlers to each account's element; dropTarget says where a line should show: before or
 * after the account under the pointer. Move up / Move down in the account menu do the same
 * from the keyboard. Lists run top to bottom.
 */
export function useAccountDrag() {
	const dragged = ref(null)
	/** { id, after } while an account is dragged over another one. */
	const dropTarget = ref(null)

	function dragStart(account, event) {
		dragged.value = account.id
		event.dataTransfer.effectAllowed = 'move'
		event.dataTransfer.setData('text/plain', account.displayName)
	}

	function dragOver(account, event) {
		if (!dragged.value) {
			return
		}
		event.preventDefault()
		event.dataTransfer.dropEffect = 'move'
		const box = event.currentTarget.getBoundingClientRect()
		dropTarget.value = account.id === dragged.value ? null : { id: account.id, after: event.clientY > box.top + box.height / 2 }
	}

	async function drop(event) {
		event.preventDefault()
		const id = dragged.value
		const target = dropTarget.value
		dragEnd()
		if (!id || !target) {
			return
		}
		const others = accountsState.accounts.map((a) => a.id).filter((other) => other !== id)
		try {
			await moveAccountTo(id, others.indexOf(target.id) + (target.after ? 1 : 0))
		} catch (e) {
			showError(t('sockless_mail', 'Could not change the order: {error}', { error: e.message }))
		}
	}

	function dragEnd() {
		dragged.value = null
		dropTarget.value = null
	}

	/** Classes for an account's element: being dragged, or a drop line before/after it. */
	function dragClasses(account, prefix) {
		return {
			[`${prefix}--dragged`]: dragged.value === account.id,
			[`${prefix}--drop-before`]: dropTarget.value?.id === account.id && !dropTarget.value.after,
			[`${prefix}--drop-after`]: dropTarget.value?.id === account.id && dropTarget.value.after,
		}
	}

	return { dragStart, dragOver, drop, dragEnd, dragClasses }
}
