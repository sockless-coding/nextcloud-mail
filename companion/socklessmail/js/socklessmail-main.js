// SPDX-License-Identifier: MIT
//
// Adds the External App's stylesheet and script (served through AppAPI's proxy) to the page,
// as AppAPI's own page does for its registered bundles. Plain script, no build step.
(function () {
	const input = document.getElementById('initial-state-socklessmail-bundles')
	if (!input) {
		return
	}
	const bundles = JSON.parse(atob(input.value))

	const style = document.createElement('link')
	style.rel = 'stylesheet'
	style.href = bundles.style
	document.head.appendChild(style)

	const script = document.createElement('script')
	script.src = bundles.script
	document.head.appendChild(script)
})()
