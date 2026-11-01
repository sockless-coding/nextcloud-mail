// SPDX-License-Identifier: MIT
//
// A stand-in for an OpenAI-compatible API, for the dev stack only: Nextcloud's integration_openai
// app points at it (dev/dev.sh ai), so Assistant tasks finish without a real model. Answers are
// predictable for e2e tests: they name the task's subject line and what was asked.
import { createServer } from 'node:http'

const DELAY_MS = Number(process.env.STUB_DELAY_MS ?? 1500)

const send = (res, status, body) => {
	res.writeHead(status, { 'Content-Type': 'application/json' })
	res.end(JSON.stringify(body))
}

/** Every "Subject: …" line of the request, in order. */
const subjects = (text) => [...text.matchAll(/^Subject: (.*)$/gm)].map((m) => m[1].trim())

function answer(messages) {
	const system = messages.filter((m) => m.role === 'system').map((m) => m.content).join('\n')
	const user = messages.filter((m) => m.role === 'user').map((m) => typeof m.content === 'string' ? m.content : JSON.stringify(m.content)).join('\n')
	const all = `${system}\n${user}`
	const found = subjects(all)
	const words = user.split(/\s+/).filter(Boolean).length
	const between = (start, end) => {
		const from = all.indexOf(start)
		if (from < 0) {
			return null
		}
		const to = end ? all.indexOf(end, from + start.length) : -1
		return all.slice(from + start.length, to < 0 ? undefined : to).trim()
	}
	// integration_openai's translation: JSON back.
	if (/translations expert/.test(system)) {
		const [, language, text] = user.match(/^Translate the following text (?:from \S+ )?to (.+?):\s*\n\n([\s\S]*)$/) ?? []
		return JSON.stringify({ translation: `[${language}] ${text?.trim()}` })
	}
	// Change tone, improve, and the app's own prompts (AssistantPrompts.cs).
	const tone = user.match(/^Reformulate the following text in a (.+?) tone/)
	if (tone) {
		return `[${tone[1]}] ${between('Here is the text:\n\n', '\n\nDo not mention')}`
	}
	if (user.startsWith('Improve the following text')) {
		return `[improved] ${between('*TEXT*:\n\n', '\n\nDo not mention')}`
	}
	if (user.startsWith('You help the user write an email.')) {
		return `Stub message: ${between('What the user wants to say: ', '\n')}\n\nBest regards`
	}
	// The scam check (ScamEvidence.cs): a scam when the app's own checks found anything.
	if (user.startsWith('You check emails for phishing and scams')) {
		const found = between("What the mail app's own checks found:\n", '\n\nHeaders:') ?? ''
		const language = user.match(/in (\w+)\. Give at most/)?.[1] ?? 'English'
		return found.startsWith('- Nothing')
			? `{"verdict": "safe", "summary": "Stub: nothing suspicious (${language}).", "reasons": ["The domains match"], "advice": "No action needed."}`
			: `\`\`\`json\n{"verdict": "scam", "summary": "Stub: this imitates someone (${language}).", "reasons": ${JSON.stringify(found.split('\n').map((l) => l.replace(/^- /, '')))}, "advice": "Delete it."}\n\`\`\``
	}
	if (user.includes('\n\nThe text:\n\n')) {
		return `[rewritten] ${between('\n\nThe text:\n\n')}`
	}
	if (/summar/i.test(all)) {
		return `Stub summary of ${found.length} message(s): ${found[0] ?? '(no subject)'}\n- ${words} words read`
	}
	return `Stub answer (${words} words): ${user.slice(0, 200)}`
}

createServer((req, res) => {
	let raw = ''
	req.on('data', (chunk) => { raw += chunk })
	req.on('end', () => {
		const path = req.url.replace(/\?.*$/, '')
		if (req.method === 'GET' && path.endsWith('/models')) {
			return send(res, 200, { object: 'list', data: [{ id: 'stub-model', object: 'model', owned_by: 'dev' }] })
		}
		if (req.method === 'POST' && path.endsWith('/chat/completions')) {
			const body = JSON.parse(raw || '{}')
			const content = answer(body.messages ?? [])
			return setTimeout(() => send(res, 200, {
				id: `stub-${Date.now()}`,
				object: 'chat.completion',
				created: Math.floor(Date.now() / 1000),
				model: body.model ?? 'stub-model',
				choices: [{ index: 0, message: { role: 'assistant', content }, finish_reason: 'stop' }],
				usage: { prompt_tokens: 1, completion_tokens: 1, total_tokens: 2 },
			}), DELAY_MS)
		}
		send(res, 404, { error: { message: `No stub for ${req.method} ${path}` } })
	})
}).listen(8000, () => console.log('AI stub listening on :8000'))
