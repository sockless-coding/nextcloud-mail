import { reactive } from 'vue'
import { apiGet, apiSend } from '../api.js'

/**
 * Nextcloud Tasks: whether the user has the app and a task list to write to, and tasks made
 * from messages. The server writes them as VTODOs over CalDAV.
 */

const LAST_LIST = 'sockless_mail.lastTaskList'

export const tasksState = reactive({
	/** The Tasks app is enabled for the user and they have a writable task list. */
	available: false,
	/** The Tasks app's path on this Nextcloud ("/apps/tasks/"). */
	appUrl: null,
	/** Task lists ({ id, name, color, writable }). */
	lists: [],
})

/** Asks the server again; "Create task" shows only when this says so. Failures leave it hidden. */
export async function loadTasks() {
	try {
		Object.assign(tasksState, await apiGet('/tasks'))
	} catch {
		Object.assign(tasksState, { available: false, appUrl: null, lists: [] })
	}
	return tasksState
}

/** The task list used last time, if it is still there and writable. */
export function preferredList(lists) {
	const writable = lists.filter((l) => l.writable)
	return writable.find((l) => l.id === localStorage.getItem(LAST_LIST)) ?? writable[0] ?? null
}

/**
 * @param {{accountId: string, folderId: string}} where the message's folder
 * @param {string} messageId the message
 * @param {{calendarId: string, title: string, due: string|null, includeText: boolean}} input the task
 * @return {Promise<{calendarId: string, name: string, uid: string}>} where it was stored
 */
export async function createTask(where, messageId, input) {
	const created = await apiSend('POST', `/accounts/${where.accountId}/folders/${where.folderId}/messages/${messageId}/task`, {
		...input,
		timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
	})
	localStorage.setItem(LAST_LIST, input.calendarId)
	return created
}

/** The task in the Tasks app. */
export function taskUrl({ calendarId, name }) {
	return `${window.location.origin}${tasksState.appUrl ?? '/apps/tasks/'}#/calendars/${encodeURIComponent(calendarId)}/tasks/${encodeURIComponent(name)}`
}
