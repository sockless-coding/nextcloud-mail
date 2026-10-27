# Sockless Mail

A webmail client for Nextcloud, built as an **External App (ExApp)**: an ASP.NET Core (.NET 10)
service that Nextcloud manages through AppAPI and deploys with the HaRP deploy daemon
(Nextcloud 32+). Users reach it only through Nextcloud: a top-menu entry, a page rendered inside
the Nextcloud UI, authenticated by their Nextcloud session. Mail is handled with MailKit/MimeKit.

> Status: **phase 1 (skeleton)**. The AppAPI lifecycle, request authentication, packaging and a
> "Hello &lt;user&gt;" page work end to end. Mail accounts come in phase 2.

## Layout

```
appinfo/info.xml            ExApp manifest: id sockless_mail, image, routes, env allow-list
Dockerfile                  frontend build → dotnet publish → aspnet:10.0-alpine + frpc
docker/entrypoint.sh        runs as root, fixes ownership, drops to the "sockless" user
docker/start.sh             opens the FRP tunnel to HaRP, then starts the app
src/SocklessMail.Api/       ASP.NET Core minimal APIs
  AppApi/                   AppAPI contract: env, auth headers, lifecycle, OCS client, UI registration
  Hosting/                  listener (unix socket / TCP), CSRF guard, request logging
  Api/                      the app's own /api endpoints
src/frontend/               Vue 3 + @nextcloud/vue 9, built into wwwroot/js and wwwroot/css
tests/SocklessMail.Tests/   xUnit v3: auth validation, lifecycle, scoping, CSRF
dev/                        local Nextcloud + HaRP + GreenMail stack and the dev.sh helper
```

## Build and test

Requirements: .NET SDK 10, Node 22, Docker (Engine 25+ for the healthcheck start interval).

```bash
dotnet test                                 # unit + endpoint tests
(cd src/frontend && npm ci && npm run build)
docker build -t sockless.local/sockless_mail:0.1.0 .
```

## Local development

`dev/dev.sh` (bash; Git Bash on Windows works) drives a complete local stack:
Nextcloud 35 on **http://localhost:8180** (admin/admin), HaRP behind the same nginx, and GreenMail.

```bash
dev/dev.sh up        # start the stack, register the deploy daemons harp_dev and manual_dev
```

### Fast loop: run the app on your machine (manual-install daemon)

```bash
dev/dev.sh run               # terminal 1: builds the frontend, runs the app on :9080
dev/dev.sh register-manual   # terminal 2: registers it with Nextcloud
```

Open http://localhost:8180 and click **Sockless Mail** in the top menu. Edit code and restart
`dev/dev.sh run`; re-register only when routes or the version change. The `manual-install`
launch profile in `src/SocklessMail.Api/Properties/launchSettings.json` carries the same
dev-only secret and port, so you can also start the app from your IDE instead of `dev.sh run`.
In this mode the browser reaches the API through AppAPI's PHP proxy
(`/index.php/apps/app_api/proxy/sockless_mail/api/...`).

### Production-like loop: container deployed by HaRP

```bash
dev/dev.sh build             # docker build sockless.local/sockless_mail:<image-tag>
dev/dev.sh register-docker   # deploy through the harp_dev daemon
dev/dev.sh logs              # follow the container's logs
```

After code changes, bump **both** `<version>` and `<image-tag>` in `appinfo/info.xml` (keep them
equal), then `dev/dev.sh build update-docker`. An update with an unchanged version does nothing.
`register-docker` replaces any installed copy and also works without a bump. The `sockless.local`
registry in info.xml is fictional, and `dev.sh up` maps it to "local" so AppAPI uses the local image.

## Deploying on a real Nextcloud with HaRP

1. **HaRP**: run it and register it as a deploy daemon, as described in the
   [HaRP README](https://github.com/nextcloud/HaRP) and the admin manual, section
   *AppAPI and External Apps*. Your reverse proxy must forward `/exapps/` to HaRP. AppAPI itself
   reaches the app through that path, so installation fails without it. `dev/nginx.conf` has a
   working example.
2. **Image**: build and push to a registry Nextcloud's Docker engine can pull from, and set
   `<registry>`, `<image>` and `<image-tag>` in `appinfo/info.xml` accordingly.
3. **Register**:
   ```bash
   occ app_api:app:register sockless_mail <daemon> --info-xml /path/to/info.xml \
       --env SOCKLESS_MAIL_ENCRYPTION_KEY='<long random value>' --wait-finish
   ```
4. **Update**: bump `<version>` and `<image-tag>`, push, then
   `occ app_api:app:update sockless_mail --info-xml /path/to/info.xml --wait-finish`.
   The app secret, port, settings and data volume are kept. Unregistering keeps the data volume
   unless you pass `--rm-data`.

## Configuration

AppAPI injects everything the app needs: `APP_ID`, `APP_VERSION`, `APP_SECRET`, `APP_HOST`,
`APP_PORT`, `APP_PERSISTENT_STORAGE`, `NEXTCLOUD_URL`, and under HaRP `HP_SHARED_KEY`,
`HP_FRP_ADDRESS` and `HP_FRP_PORT`. Your own settings must be declared in
`<environment-variables>`; AppAPI drops `--env` values for undeclared names. Values are fixed at
registration, so changing one means registering again.

| Variable | Purpose |
|---|---|
| `SOCKLESS_MAIL_ENCRYPTION_KEY` | Protects the key ring that encrypts stored mail credentials (used from phase 2). |

## How it runs

- **Listening**: under HaRP (`HP_SHARED_KEY` set) the app serves on the unix socket
  `/tmp/exapp.sock`, and `start.sh` runs `frpc`, which tunnels HaRP to it. Otherwise it listens on
  `APP_HOST:APP_PORT`.
- **Lifecycle**: `GET /heartbeat` (no auth) and `PUT /enabled?enabled=1|0`. Enabling registers
  the top-menu entry, script, style and initial state through AppAPI's OCS API. There is no
  `/init`, because there is nothing to prepare; AppAPI treats the 404 as "no init needed".
- **UI**: AppAPI renders the page at `/index.php/apps/app_api/embedded/sockless_mail/sockless_mail`
  and loads our bundle through its PHP proxy. The bundle calls the API at
  `/exapps/sockless_mail/api` under HaRP, otherwise through the PHP proxy.
- **Container user**: the image's default user stays root, because AppAPI copies certificates in
  and runs `update-ca-certificates` as root. The entrypoint drops to the unprivileged user
  `sockless` before starting `frpc` and the app.

## Security model

- **Request authentication**: every request except `/heartbeat` must carry `EX-APP-ID`,
  `EX-APP-VERSION` and `AUTHORIZATION-APP-API = base64(userId:APP_SECRET)`. HaRP or AppAPI add
  these headers; browsers never see the secret. The app compares the secret in constant time and
  takes the Nextcloud user id from the same header. `AA-VERSION` is not checked, because HaRP
  rewrites it.
- **User scoping**: every `/api` endpoint requires a non-empty user, and the user id comes only
  from the verified header. From phase 2, every account, folder and message query is scoped by it.
- **Access levels**: `info.xml` declares `/api` as USER. HaRP and the PHP proxy reject
  anonymous requests before they reach the app, and the app checks again.
- **CSRF**: neither proxy checks Nextcloud's request token. The app therefore rejects
  `POST`/`PUT`/`DELETE` on `/api` without `X-Sockless-Request: 1`, and any `/api` request marked
  `Sec-Fetch-Site: cross-site`.
- **Caching**: `/api` responses send `Cache-Control: no-store`. Without it, the PHP proxy would
  let shared caches keep non-JSON responses, such as attachments, for an hour.
- **Logging**: one structured line per request (method, path, status, duration). Headers, query
  strings, credentials and message bodies are never logged.
- **Calls to Nextcloud** carry the same headers. With a user id the app acts as that user, and
  Nextcloud records this in `exapp_impersonation.log`.

## Known limitations

- The PHP proxy (manual-install) forwards only GET, HEAD, POST, PUT and DELETE, so the API uses
  no PATCH. It buffers responses, so the app polls instead of using WebSockets or SSE.
- SQLite (from phase 2) limits the app to a single instance.
- Nextcloud installs translations only from app store release archives, so the UI is English
  until the app is published there.

## License

MIT. See [LICENSE](LICENSE).
