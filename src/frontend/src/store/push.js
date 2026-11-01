import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { reactive } from 'vue'
import { apiBase, apiMode } from '../api.js'

/**
 * The push channel (SignalR, MailPush.cs). Only with HaRP: AppAPI's PHP proxy buffers responses,
 * so in proxy mode the app keeps polling. The server sends nudges ({ accountId, folderId } of a
 * folder whose cache changed); what changed is still asked through the changes endpoints.
 */
export const push = reactive({ connected: false })

const MAX_RETRY_MS = 60_000

/** 1 s, 2 s, 4 s … up to a minute, and never give up. */
const retryDelay = (attempt) => Math.min(MAX_RETRY_MS, 1000 * 2 ** attempt)

/**
 * @param {object} handlers callbacks
 * @param {(change: {accountId: string, folderId: string}) => void} handlers.folderChanged a folder changed
 * @param {() => void} handlers.scheduledChanged a scheduled message was sent, failed or changed
 * @param {(task: {id: number}) => void} handlers.assistantTaskChanged an Assistant task ended
 * @param {() => void} handlers.reconnected nudges may have been missed: refresh everything
 * @return {() => void} stops the connection
 */
export function startPush({ folderChanged, scheduledChanged, assistantTaskChanged, reconnected }) {
	if (apiMode !== 'harp') {
		return () => {}
	}
	const connection = new HubConnectionBuilder()
		// The CSRF guard (ApiRequestGuard.cs) also covers the negotiate POST.
		.withUrl(`${apiBase}/hub`, { headers: { 'X-Sockless-Request': '1' } })
		.withAutomaticReconnect({ nextRetryDelayInMilliseconds: ({ previousRetryCount }) => retryDelay(previousRetryCount) })
		.configureLogging(LogLevel.None)
		.build()
	connection.on('folderChanged', folderChanged)
	connection.on('scheduledChanged', scheduledChanged)
	connection.on('assistantTaskChanged', assistantTaskChanged)
	connection.onreconnecting(() => {
		push.connected = false
	})
	connection.onreconnected(() => {
		push.connected = true
		reconnected()
	})

	let stopped = false
	let timer = null
	// Automatic reconnects only follow a connection that was up once; the first start retries here.
	const start = async (attempt) => {
		try {
			await connection.start()
			push.connected = !stopped
			if (attempt > 0) {
				reconnected()
			}
		} catch {
			if (!stopped) {
				timer = window.setTimeout(() => start(attempt + 1), retryDelay(attempt))
			}
		}
	}
	start(0)

	return () => {
		stopped = true
		window.clearTimeout(timer)
		push.connected = false
		connection.stop()
	}
}
