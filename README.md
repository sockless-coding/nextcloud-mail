# Sockless Mail

A webmail client for Nextcloud, built as an **External App (ExApp)**: an ASP.NET Core (.NET 10)
service that Nextcloud manages through AppAPI and deploys with the HaRP deploy daemon
(Nextcloud 32+). Users reach it only through Nextcloud: a top-menu entry, a page rendered inside
the Nextcloud UI, authenticated by their Nextcloud session. Mail is handled with MailKit/MimeKit.

> Status: **phase 6a (sync)**. Each Nextcloud user manages any number of IMAP/SMTP accounts
> (credentials encrypted at rest), reads mail (folder tree, paged list, sanitized message view
> with remote images blocked by default, attachments), works with it (read/unread, flag, move,
> delete via Trash, folders) and writes it: new messages, reply, reply all, forward, attachments,
> with a copy saved to Sent. Message lists come from a local header cache kept in sync with
> QRESYNC/CONDSTORE and IMAP IDLE. Still to come in phase 6: search (6b), a unified inbox (6c)
> and OAuth2 (6d).

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
  Accounts/                 account model, validation, service, encrypted credentials
  Data/                     EF Core DbContext (SQLite), per-user query filter, migrations
  Mail/                     MailKit connections: address policy, connector, connection pool, HTML sanitizer
  Mailbox/                  folders, message list, message view, parts, remote image proxy
  Sync/                     header cache, folder sync (QRESYNC/CONDSTORE/plain), IDLE watchers
  Security/                 Data Protection key ring wrapping
src/frontend/               Vue 3 + @nextcloud/vue 9, built into wwwroot/js and wwwroot/css
tests/SocklessMail.Tests/   xUnit v3: auth, CSRF, accounts, user isolation, key ring, pool, sanitizer
tests/SocklessMail.IntegrationTests/  API and IMAP against GreenMail and Dovecot in Docker (Testcontainers)
tests/e2e/                  browser checks against the dev stack (Playwright with installed Edge/Chrome)
dev/                        local Nextcloud + HaRP + GreenMail stack and the dev.sh helper
```

## Build and test

Requirements: .NET SDK 10, Node 22, Docker (Engine 25+ for the healthcheck start interval).

```bash
dotnet test --project tests/SocklessMail.Tests              # unit + endpoint tests
dotnet test --project tests/SocklessMail.IntegrationTests   # needs Docker: starts GreenMail and Dovecot
(cd src/frontend && npm ci && npm run build)
docker build -t sockless.local/sockless_mail:0.3.2 .
```

Browser checks (need the dev stack below, the app deployed and `dev/dev.sh seed-mail`):

```bash
cd tests/e2e && npm ci
node accounts.mjs    # add/test/validate/reorder/delete accounts
node mail.mjs        # paging, sanitized view, remote images, attachments, polling (new and removed mail)
node actions.mjs     # read/unread, flag, delete (with and without Trash), move, create/rename folders
node compose.mjs     # new message with attachment, validation, reply, forward, copies in Sent
```

They use an installed Edge (`BROWSER_CHANNEL=chrome` for Chrome) and exit non-zero on console errors.

Database changes: `dotnet tool restore`, then
`dotnet ef migrations add <Name> --project src/SocklessMail.Api --output-dir Data/Migrations`.
Migrations run automatically at startup.

## Local development

`dev/dev.sh` (bash; Git Bash on Windows works) drives a complete local stack:
Nextcloud 35 on **http://localhost:8180** (admin/admin), HaRP behind the same nginx, and GreenMail.

```bash
dev/dev.sh up        # start the stack, register the deploy daemons harp_dev and manual_dev
```

GreenMail has two mailboxes, `alice@example.test` (password `alice`) and `bob@example.test`
(password `bob`), with no encryption: IMAP on port 3143, SMTP on 3025. Use the server name
`greenmail` for the container deployed by HaRP, and `127.0.0.1` for the app running on your machine.
The dev registrations set `SOCKLESS_MAIL_ALLOW_LOCAL_SERVERS=true`, because both addresses are local.
`dev/dev.sh seed-mail [address]` sends 61 test messages there: numbered plain-text mails and an HTML
newsletter with a script, a remote image, an inline image and a PDF attachment.

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
`update-docker` disables and enables the app after updating; see "Known limitations" for why.
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
   unless you pass `--rm-data`. If every `/exapps/sockless_mail/...` request answers 404 after the
   update, run `occ app_api:app:disable sockless_mail` and `occ app_api:app:enable sockless_mail`
   (see "Known limitations").

## Configuration

AppAPI injects everything the app needs: `APP_ID`, `APP_VERSION`, `APP_SECRET`, `APP_HOST`,
`APP_PORT`, `APP_PERSISTENT_STORAGE`, `NEXTCLOUD_URL`, and under HaRP `HP_SHARED_KEY`,
`HP_FRP_ADDRESS` and `HP_FRP_PORT`. Your own settings must be declared in
`<environment-variables>`; AppAPI drops `--env` values for undeclared names. Values are fixed at
registration, so changing one means registering again.

| Variable | Default | Purpose |
|---|---|---|
| `SOCKLESS_MAIL_ENCRYPTION_KEY` | unset | Wraps the key ring that encrypts stored mail credentials. At least 32 characters. Strongly recommended; see "Stored credentials". |
| `SOCKLESS_MAIL_ALLOW_LOCAL_SERVERS` | `false` | `true` allows mail servers on private, loopback and carrier-grade NAT addresses, e.g. an internal mail server. |

Data lives in `APP_PERSISTENT_STORAGE` (the Docker volume `nc_app_sockless_mail_data`):
`sockless_mail.db` (SQLite, WAL mode) and `keys/` (the Data Protection key ring).

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
- **IMAP connections** for requests are pooled per account (`ImapConnections` over `ConnectionPool`):
  - At most 3 connections per account and 100 in total. When the total is reached, the oldest idle
    connection of another account is closed.
  - A connection is leased exclusively for one operation, because MailKit clients are not
    thread-safe.
  - Before reuse, a connection idle for more than 30 seconds is checked with `NOOP`. A
    background job closes connections idle for more than 5 minutes.
  - If a connection turns out to be dead, the operation runs once more on a fresh one (read
    operations only).
  - Changing or deleting an account closes its pooled connections.
  - SMTP connects per use.
  - IDLE watchers hold one more connection per watched account, outside the pool (see below).
- **Reading** never changes the mailbox. Folders are opened read-only (`EXAMINE`) and bodies are
  fetched with `BODY.PEEK`. Marking a message as read when it is opened is a separate,
  explicit request from the UI.
- **Changes** go through POST/PUT endpoints with JSON bodies, because the PHP proxy forwards no
  PATCH:
  - `PUT …/messages/flags` (`seen`, `flagged`)
  - `POST …/messages/move`
  - `POST …/messages/delete`
  - `POST …/folders` (create) and `PUT …/folders/{id}` (rename)

  The flag, move and delete endpoints take up to 500 message ids.
  - Changes are not retried automatically, unlike reads. A move repeated after a dropped
    connection could act twice.
  - Ids from before the server renumbered the folder (a different UIDVALIDITY) are refused with
    409 `stale_messages`, rather than acting on whatever now has those UIDs.
  - **Delete** moves to the Trash folder (RFC 6154 special-use, or a common name such as
    "Trash" or "Deleted Items"). In Trash, or with `permanent: true`, it flags the messages
    `\Deleted` and expunges exactly those UIDs (`UID EXPUNGE`). On servers without UIDPLUS this
    falls back to `EXPUNGE`, which also removes other messages another client had marked
    `\Deleted` in that folder.
  - An account without a Trash folder answers 409 `no_trash`, and the UI asks before deleting
    permanently.
  - Folder names must not contain the server's hierarchy separator, `*` or `%`. INBOX cannot
    be renamed.
- **In the UI** changes show at once (optimistic) and are undone with an error toast if the
  server refuses:
  - Changes are sent one at a time in the order made. Requests run on several pooled IMAP
    connections, so "read" (when a message opens) followed quickly by "unread" could otherwise
    reach the server in the wrong order.
  - While changes are in flight, and for snapshots requested before them, polling results are
    discarded so they cannot overwrite the newer local state.
  - After a move or delete, the counts and the target folder (if shown) are reloaded.
- **Writing** happens in four steps:
  1. **Attachments** are uploaded first (`POST /api/uploads`, at most 25 MB each and in total).
     They wait in `uploads/` on the data volume, in a directory per user named by a hash of the
     user id, until the message is sent or discarded (then deleted), or for 24 hours at most.
     Another user's upload id is simply not found.
  2. **Replies and forwards** start from a draft the server builds
     (`GET …/messages/{id}/draft?mode=reply|replyAll|forward`):
     - Recipients follow Reply-To. Reply all copies everyone, and the account's own address is
       never added.
     - The subject gets one `Re:`/`Fwd:` prefix.
     - The original is quoted as plain text; HTML-only mail is converted.
     - A forward lists the original's attachments, and the user can drop any of them.
  3. **Sending** (`POST /api/accounts/{id}/send`): MimeKit builds the message.
     - Headers: `From` (the account's sender name and address), a new `Message-ID`, and for
       replies `In-Reply-To` and `References`, so mail clients thread it.
     - The body is plain text for now.
     - It goes out over SMTP through the same address-checked connector as the connection test.
     - The `Bcc` header is explicitly left out of what the SMTP server receives. Bcc recipients
       get the message, nobody else sees them, and only the copy in Sent keeps the header.
     - Limits: 100 recipients, 25 attachments, 30 sends per 10 minutes per user.
  4. **After sending** the message is appended to Sent with `\Seen` (RFC 6154 or a common name).
     The original is flagged `\Answered` (reply) or `$Forwarded` (forward). These steps are best
     effort: if they fail, the answer says the message *was* sent and what did not work, so the
     user does not send it twice. If the connection breaks during the SMTP transfer, the error
     says to check Sent first.
- **In the composer**, Escape and clicking outside do not close it, so a half-written message
  cannot vanish by accident. Discard and Send are the only ways out.
- **Message lists come from a header cache** in the database (`folders`, `messages` and
  `removed_messages` tables): subject, addresses, date, flags, size, has-attachments. Bodies are
  never cached; opening a message still fetches it from the server.
  - Per folder, the cache holds a contiguous window of UIDs. It starts with the newest page (50)
    the first time the folder is shown, grows downwards when older pages are requested, and new
    mail extends it upwards. More than 500 new messages since the last sync rebuild the window
    from the newest page instead.
  - Every list request first runs an **incremental sync** of the folder, then answers from the
    cache. How the sync finds changes depends on the server:
    - **QRESYNC** (enabled on every connection that offers it):
      `UID FETCH <window> (FLAGS) (CHANGEDSINCE <modseq> VANISHED)`. If HIGHESTMODSEQ has not
      moved, the sync ends after the `EXAMINE`.
    - **CONDSTORE** alone: `CHANGEDSINCE` for flags. A `UID SEARCH` over the window finds
      removed messages, and only runs when the message count or UIDNEXT moved.
    - **Neither** (GreenMail, for example): the flags of the whole window are fetched and
      compared, so the cost grows with how far the user has scrolled.
    - New messages are fetched with `UID SEARCH <lastuid+1>:*` plus a `FETCH` of the new ones.
    - A changed UIDVALIDITY drops the folder's cache and rebuilds it.
  - A sync reads everything from the server first and then writes the database in one
    transaction. A connection that breaks halfway therefore leaves the cache unchanged, and the
    sync (a read) is retried like other reads. A lock per folder keeps a sync and the app's own
    changes from interleaving.
  - The app's own changes (flags, move, delete) update the cache directly after the server has
    applied them.
  - Folders that disappear from the folder list (deleted or renamed elsewhere) lose their cache
    the next time the list is loaded. Deleting an account deletes its cache.
- **Paging**: newest first, 50 per page. The cursor is a UID (`?before=<uid>`), so mail arriving
  between page loads neither shifts nor duplicates entries. Message ids are `<UIDVALIDITY>-<UID>`,
  so an id becomes 404 when the server renumbers the folder.
- **Change tokens**: each folder has a change counter. Every row records the change that last
  touched it, and removals are kept for 24 hours. A page answers with a `token`, and
  `GET …/folders/{id}/changes?since=<token>` answers with:
  - the messages added or changed since then
  - the ids removed since then
  - the folder's total and unread counts
  - a new token

  The answer says `reset: true` instead when the token is older than the kept removals, comes
  from before a rebuilt window, or more than 500 messages changed. The UI then reloads the list.
- **New mail in the UI** is found by polling the changes endpoint every 15 seconds, and when the
  tab becomes visible again. The folder list (LIST + STATUS) is reloaded every 60 seconds.
  There is no WebSocket or SSE channel to the browser (see "Known limitations").
- **IDLE watchers** keep the INBOX cache current without any request:
  - Only accounts of users who used the app in the last 10 minutes are watched, at most 50
    accounts in total. A user's first request starts their watchers at once.
  - Each watcher has its own connection, separate from the pool. It syncs the INBOX, then idles
    (re-issued every 9 minutes). A change the server reports ends IDLE, and the INBOX is synced
    again.
  - While a watcher idles with nothing pending, INBOX list and changes requests are answered
    from the cache without contacting the server.
  - Servers without IDLE are synced every 2 minutes instead, and requests keep syncing
    themselves.
  - Failures reconnect after 30 seconds, doubling up to 10 minutes. Changing an account restarts
    its watcher, and deleting the account stops it. An account whose password can no longer be
    decrypted is not watched until the password is entered again.

## Security model

- **Request authentication**: every request except `/heartbeat` must carry `EX-APP-ID`,
  `EX-APP-VERSION` and `AUTHORIZATION-APP-API = base64(userId:APP_SECRET)`. HaRP or AppAPI add
  these headers; browsers never see the secret. The app compares the secret in constant time and
  takes the Nextcloud user id from the same header. `AA-VERSION` is not checked, because HaRP
  rewrites it.
- **User scoping**: every `/api` endpoint requires a non-empty user, and the user id comes only
  from the verified header. It is never taken from the request body. Account queries are scoped by
  it twice: explicitly in `AccountService`, and by an EF Core query filter that hides other users'
  rows from any query that forgets. Another user's account answers 404, exactly like a missing one.
  `UserIsolationTests` tries every endpoint across users.
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

### Stored credentials

Mail passwords are encrypted with ASP.NET Core Data Protection (AES-256-CBC + HMAC-SHA256). The
protector purpose includes the account id, so a ciphertext copied into another account's row does
not decrypt. The API never returns credentials.

The Data Protection **key ring** lives in `keys/` on the same volume as the database. Unprotected,
anyone holding a copy of the volume, such as a backup or snapshot, could decrypt every password.
So the key ring itself is encrypted (AES-256-GCM) with a key derived from a secret kept elsewhere:

- **`SOCKLESS_MAIL_ENCRYPTION_KEY`** when set. It is stored in Nextcloud's database (the deploy
  options) and in the container's environment (visible in `docker inspect`), but not on the volume.
  Keep a copy somewhere safe: without it the stored passwords cannot be recovered.
- **`APP_SECRET`** otherwise, with a warning at startup. It is also kept in Nextcloud's database,
  but AppAPI generates a new one whenever the app is **registered again**. `app:update` keeps it.
  After a re-registration every user has to enter their passwords again.

Each wrapped key records which secret protects it. Setting the variable later therefore keeps
older keys readable, and only new keys use it. Removing or changing the variable makes the stored
credentials unreadable: the app keeps running, and connection tests report
`credential_unavailable`. Users then re-enter their password when editing the account.

### Displaying mail

Message HTML is attacker-controlled, and it is displayed inside the Nextcloud origin. There are
three layers of defence:

1. **Server-side sanitizing** (`MessageHtmlSanitizer`, based on HtmlSanitizer) removes:
   - scripts, event handlers, frames, objects and embeds
   - forms and inputs, so a mail cannot imitate a Nextcloud login inside Nextcloud
   - `base`, `meta` and `link` elements
   - `javascript:` and other unsafe URLs, and `data:` URLs except raster images

   It keeps `<style>` blocks, which are sanitized as CSS, so newsletters keep their layout. Links
   open in a new tab with `rel="noopener noreferrer"`. Plain-text mail is converted to escaped
   HTML with clickable links.
2. **A sandboxed iframe** without `allow-scripts`, so even markup the sanitizer missed cannot run
   code. `allow-same-origin` is set only so the page can size the frame to its content.
3. **Nextcloud's Content-Security-Policy**, which the iframe inherits. It blocks remote loads and
   inline scripts as well.

**Remote content** is blocked by default, so opening a message does not tell the sender anything
(tracking pixels):
- Remote `img` sources, `background` attributes and CSS `url()` are removed.
- The message is flagged, and the UI offers **Show images**.
- When the user chooses that, image URLs are rewritten to the app's **image proxy**
  (`/api/image-proxy?token=...`). The browser never contacts the sender's server, and the CSP
  needs no exception.
- Proxy tokens are signed, user-bound and expire after 12 hours, so the proxy only fetches URLs it
  issued itself.
- Fetches go through the same address policy as mail connections, also after redirects.
- Only PNG, JPEG, GIF, WebP, AVIF, BMP and ICO up to 5 MB are passed on. SVG is refused, because
  it can carry script.
- Remote CSS `url()` stays blocked even then.

**Attachments and inline parts** are served with:
- `Content-Disposition: attachment`, except raster images referenced inline (`cid:`)
- `Content-Security-Policy: default-src 'none'; sandbox`
- `X-Content-Type-Options: nosniff`
- `Cache-Control: no-store`

An HTML or SVG attachment therefore cannot run in the Nextcloud origin, even if opened directly.

### Connections to mail servers

Any user can make the app connect to a host and port of their choice. Without limits that would
be a way to probe the network the app runs in (HaRP, Nextcloud, cloud metadata endpoints). So:

- The app resolves DNS itself and checks every address before connecting. It then hands the
  already connected socket to MailKit, so DNS cannot change between the check and the connection
  (DNS rebinding), and TLS still verifies the certificate against the host name.
- Link-local (incl. `169.254.169.254`), multicast and unspecified addresses are always refused.
  Private, loopback, unique-local and carrier-grade NAT addresses are refused unless
  `SOCKLESS_MAIL_ALLOW_LOCAL_SERVERS=true`.
- Connection tests are rate-limited to 10 per minute per user, and every connection has a
  20-second timeout.
- TLS certificates are validated strictly. Self-signed certificates are rejected with an
  explanation (name mismatch, untrusted chain).

## Known limitations

- The PHP proxy (manual-install) forwards only GET, HEAD, POST, PUT and DELETE, so the API uses
  no PATCH. It buffers responses, so the app polls instead of using WebSockets or SSE.
- SQLite limits the app to a single instance.
- **AppAPI/HaRP route cache after `app:update`** (observed with Nextcloud 35.0.2 and the current
  HaRP release): after an update, HaRP answered every `/exapps/sockless_mail/...` request with
  404 ("No defined route"), although the routes were in Nextcloud's database. Disabling and
  enabling the app fixes it, because that pushes the app's metadata to HaRP again.
  `dev/dev.sh update-docker` does this automatically. This looks like an upstream issue.
- Writing is plain text only, with no drafts saved to the server, no address-book
  autocompletion and no signature yet.
- Some providers (Gmail, Outlook) store a copy of everything sent over their SMTP in Sent
  themselves, so the app's own copy duplicates it. This needs a per-account setting.
- Upload sizes through AppAPI's PHP proxy (manual-install) are also limited by PHP's
  `upload_max_filesize` and `post_max_size`.
- Actions work on one message at a time in the UI. The API takes up to 500 ids, so selecting
  several messages is a UI addition only. Folders cannot be deleted from the UI yet, and there is
  no undo after a move or delete (Trash is the safety net).
- The header cache covers what the user has paged through, not whole folders. The folder list
  and its counts still come from the server (LIST + STATUS) every time.
- Only the INBOX is watched with IDLE. Other folders are synced when they are shown or polled.
- The UI still polls: IDLE makes those polls cheap and current, but the browser learns about new
  mail up to 15 seconds late.
- Message parts are held in memory while they are served, so very large attachments cost
  memory in proportion to their size.
- The frontend bundle is about 1.2 MB (355 kB gzipped), mostly the Nextcloud component library.
- One username and password per account, used for both IMAP and SMTP. OAuth2 (Gmail, Outlook)
  is prepared in the data model (`AuthMethod` plus a typed credential) but not implemented yet.
- Mail servers with self-signed certificates cannot be used. There is no "trust this certificate"
  option yet.
- Nextcloud installs translations only from app store release archives, so the UI is English
  until the app is published there.

## License

MIT. See [LICENSE](LICENSE).
