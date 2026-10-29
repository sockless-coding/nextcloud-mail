// The companion app: the mail page at /apps/socklessmail/, the app menu pointing there, and
// AppAPI's own page still working. Needs: dev/dev.sh up (enables the companion) and the app
// deployed through HaRP.
import { base, check, start } from './lib.mjs'

const { page, step, finish } = await start()
const sockets = []
page.on('websocket', (ws) => sockets.push(ws))

step('the page stays at /apps/socklessmail/')
await page.goto(`${base}/apps/socklessmail/`)
await page.getByRole('button', { name: 'Add account' }).first().waitFor({ timeout: 30000 })
check(new URL(page.url()).pathname === '/apps/socklessmail/', `address: ${new URL(page.url()).pathname}`)

step('the app menu entry points here and is active')
// The app menu is rendered from this initial state; the markup differs between versions.
const apps = await page.evaluate(() => JSON.parse(atob(document.getElementById('initial-state-core-apps').value)))
const entries = apps.filter((a) => a.name === 'Sockless Mail')
check(entries.length === 1, `menu entries named Sockless Mail: ${entries.length}`)
check(entries[0]?.href === '/apps/socklessmail/', `menu link: ${entries[0]?.href}`)
check(entries[0]?.active === true, 'menu entry active')

step('push connects from this page')
await page.waitForTimeout(3000)
check(sockets.some((ws) => ws.url().includes('/exapps/sockless_mail/api/hub') && !ws.isClosed()), 'WebSocket to the hub open')

step('paths below the page show it too')
await page.goto(`${base}/apps/socklessmail/some/deep/link`)
await page.getByRole('button', { name: 'Add account' }).first().waitFor({ timeout: 30000 })
check(true, 'deep link renders the app')

step("AppAPI's page still works")
await page.goto(`${base}/index.php/apps/app_api/embedded/sockless_mail/sockless_mail`)
await page.getByRole('button', { name: 'Add account' }).first().waitFor({ timeout: 30000 })
check(true, 'embedded page renders the app')

await finish()
