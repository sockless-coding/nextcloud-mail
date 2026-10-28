// OAuth2 sign-in as far as it runs without a real Google account: an admin sets up Google, users
// get the sign-in button, the popup goes to Google with PKCE, and Google's redirect back reaches
// the app through HaRP without a session. Google itself is not signed in to: the callback is
// called with a made-up code, which Google refuses, and the refusal must reach the dialog.
// A second, non-admin user (created with occ) must not see or reach the admin settings.
// Needs: dev/dev.sh up and the app deployed. Turns Google off again at the end.
import { execSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { appPage, base, check, start } from './lib.mjs'

const callback = `${base}/exapps/sockless_mail/oauth/callback`

/** Polls an async condition, up to the given number of seconds. */
async function until(page, condition, seconds = 10) {
	for (let i = 0; i < seconds * 4; i++) {
		if (await condition()) {
			return true
		}
		await page.waitForTimeout(250)
	}
	return false
}

const admin = await start()
const { page, step } = admin

step('admin settings')
await page.getByRole('button', { name: 'Sign-in providers' }).click()
const settings = page.getByRole('dialog', { name: 'Sign-in providers' })
await settings.waitFor()
check(await settings.getByLabel('Callback URL to register').inputValue() === callback, `the dialog shows the callback URL (${callback})`)
const google = settings.locator('form').filter({ hasText: 'Google' })
await google.getByLabel('Client id').fill('e2e-client.apps.googleusercontent.com')
await google.getByLabel('Client secret').fill('e2e-secret')
await google.getByRole('button', { name: 'Save Google' }).click()
check(await until(page, async () => (await google.innerText()).includes('enabled')), 'saving enables Google')
check(await until(page, async () => (await google.getByLabel('Client secret').inputValue()) === ''), 'the secret is not shown again')
await settings.getByRole('button', { name: 'Close' }).first().click()
await settings.waitFor({ state: 'detached' })

step('sign in with Google')
// Load the page again so the provider list includes Google.
await page.goto(appPage)
await page.getByRole('button', { name: 'Add account' }).first().click()
const dialog = page.getByRole('dialog', { name: 'Add mail account' })
const signIn = dialog.getByRole('button', { name: 'Sign in with Google' })
check(await until(page, async () => (await signIn.count()) > 0), 'the account dialog offers "Sign in with Google"')
// Google redirects at once (and refuses the made-up client), so read the first request it got.
const authorizeRequest = page.context().waitForEvent('request', { predicate: (r) => r.url().startsWith('https://accounts.google.com/o/oauth2/v2/auth'), timeout: 20000 })
const [popup] = await Promise.all([page.waitForEvent('popup'), signIn.click()])
const authorize = new URL((await authorizeRequest).url())
check(authorize.searchParams.get('client_id') === 'e2e-client.apps.googleusercontent.com', 'the popup goes to Google with the client id')
check(authorize.searchParams.get('code_challenge_method') === 'S256' && !!authorize.searchParams.get('code_challenge'), 'with a PKCE challenge')
check(authorize.searchParams.get('redirect_uri') === callback, 'and the registered callback URL')
// What Google would do after the user agreed, with a code Google never issued.
await popup.goto(`${callback}?state=${encodeURIComponent(authorize.searchParams.get('state'))}&code=made-up`)
check((await popup.locator('body').innerText().catch(() => '')).includes('Sockless Mail'), 'the callback page is served through HaRP without a session')
const error = dialog.locator('.notecard--error, [class*="error"]').filter({ hasText: /Google/ })
check(await until(page, async () => (await error.count()) > 0, 15), 'Google\'s refusal of the code is shown in the dialog')
check(await until(page, async () => popup.isClosed(), 5), 'the popup is closed')
await dialog.getByRole('button', { name: 'Cancel' }).click()

step('non-admin user')
execSync('docker compose -f dev/docker-compose.yml exec -T -e OC_PASS=e2e-bob-password -u www-data nextcloud php occ user:add --password-from-env e2ebob || true',
	{ cwd: fileURLToPath(new URL('../..', import.meta.url)), stdio: 'ignore' })
process.env.NC_USER = 'e2ebob'
process.env.NC_PASSWORD = 'e2e-bob-password'
const bob = await start()
check(await bob.page.getByRole('button', { name: 'Sign-in providers' }).count() === 0, 'a non-admin user has no admin settings')
const forbidden = (await bob.page.request.get(`${base}/exapps/sockless_mail/api/admin/oauth`, { headers: { 'X-Sockless-Request': '1' } })).status()
check(forbidden === 403, `the admin API refuses them (${forbidden})`)
await bob.page.getByRole('button', { name: 'Add account' }).first().click()
check(await until(bob.page, async () => (await bob.page.getByRole('button', { name: 'Sign in with Google' }).count()) > 0), 'but they can sign in with Google')
await bob.finish()

step('clean up')
const headers = { 'X-Sockless-Request': '1', requesttoken: await page.evaluate(() => OC.requestToken) }
const off = await page.request.put(`${base}/exapps/sockless_mail/api/admin/oauth/google`, { headers, data: { clientId: '' } })
check(off.ok(), 'Google is turned off again')
await admin.finish()
