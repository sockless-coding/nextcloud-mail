# Sockless Mail

A webmail client for Nextcloud, built as an **External App (ExApp)**: an ASP.NET Core (.NET 10)
service that Nextcloud manages through AppAPI and deploys with the HaRP deploy daemon
(Nextcloud 32+). Users reach it only through Nextcloud: a top-menu entry, a page rendered inside
the Nextcloud UI, authenticated by their Nextcloud session. Mail is handled with MailKit/MimeKit.

> Status: **phase 12b (Nextcloud Contacts)**. Each Nextcloud user manages any number of IMAP/SMTP accounts
> (credentials encrypted at rest), reads mail (folder tree, paged list, sanitized message view
> with remote images blocked by default, attachments), works with it (read/unread, flag, move,
> delete via Trash, folders) and writes it: new messages, reply, reply all, forward, attachments,
> with a copy saved to Sent. Message lists come from a local header cache kept in sync with
> QRESYNC/CONDSTORE and IMAP IDLE. Search covers the open folder on the server (message text
> included) or all accounts from the cache. With several accounts, "All accounts" shows unified
> folders (every account's Inbox, Drafts, Sent, Archive, Junk or Trash in one list) and the smart
> folders Unread and Flagged. Google and Microsoft accounts can be added by signing in (OAuth2),
> once an admin has registered the app with them. The interface follows desktop mail programs:
> an account rail with a colour and unread count per account, a folder pane, a command bar, a
> message list grouped by day with a line of each message's text, hover actions, multi-select
> and quick filters (Unread, Flagged, Attachments), a reading pane on the right, at the bottom
> or off, a composer window docked at the bottom with rich-text editing and drafts saved while
> writing, and Outlook's keyboard shortcuts. Files can be attached from Nextcloud Files or shared
> as links, attachments saved into Files, and files sent from the Files app ("Send by email").
> Recipients are suggested from the user's Nextcloud address books (contact groups add all
> their members), and a sender can be added to an address book from the message view.

## Layout

```
appinfo/info.xml            ExApp manifest: id sockless_mail, image, routes, env allow-list
companion/socklessmail/     optional PHP app: serves the mail page at /apps/socklessmail/
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
  Bimi/                     sender logos: DMARC verdicts, DNS lookups, SVG checks, logo cache
  Files/                    Nextcloud Files over WebDAV as the user: attach, share links, save attachments
  Contacts/                 Nextcloud address books over CardDAV: suggestions, groups, adding senders
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
node compose.mjs     # new message with attachment, validation (invalid recipient chip), reply, forward, copies in Sent
node search.mjs      # folder search (body text), search across accounts, opening a result
node unified.mjs     # unified folders: merged order, reading, deleting and new mail across accounts, smart folders
node oauth.mjs       # admin sign-in settings, Google popup with PKCE, callback through HaRP, non-admin user
node layout.mjs      # multi-select and actions on it, reading pane and density (kept on reload), folder pane
node filters.mjs     # previews, Unread/Flagged/Attachments in a folder and in the unified inbox, polling in a filter
node workspace.mjs   # sender details, attachment cards, composer window, recipient chips, keyboard shortcuts
node drafts.mjs      # rich text reaching the recipient, drafts saved while writing, Close, Continue editing, plain text
node push.mjs        # WebSocket to the hub (HaRP), no inbox polling while connected, new mail shown after a push
node companion.mjs   # /apps/socklessmail/: address kept, app menu entry, deep links, AppAPI's page still works
```

They use an installed Edge (`BROWSER_CHANNEL=chrome` for Chrome) and exit non-zero on console errors.
They open the app at `/apps/socklessmail/`. `APP_PAGE=embedded` uses AppAPI's page instead.

Database changes: `dotnet tool restore`, then
`dotnet ef migrations add <Name> --project src/SocklessMail.Api --output-dir Data/Migrations`.
Migrations run automatically at startup.

## Local development

`dev/dev.sh` (bash; Git Bash on Windows works) drives a complete local stack:
Nextcloud 35 on **http://localhost:8180** (admin/admin), HaRP behind the same nginx, and GreenMail.

```bash
dev/dev.sh up        # start the stack, register the deploy daemons harp_dev and manual_dev
```

The companion app is mounted read-only into `custom_apps/socklessmail` and enabled by `up`, so
PHP changes there apply on the next page load.

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
equal), then run `dev/dev.sh build` and `dev/dev.sh update-docker`. An update with an unchanged version does nothing.
`update-docker` disables and enables the app after updating; see "Known limitations" for why.
`register-docker` replaces any installed copy and also works without a bump. The `sockless.local`
registry in info.xml is fictional, and `dev.sh up` maps it to "local" so AppAPI uses the local image.

On Windows, `dev\update-docker.bat` does the build and the update from cmd or Explorer, through
Git Bash. `dev\update-docker.bat register` re-registers instead, for a rebuild without a bump.

## Deploying on a real Nextcloud with HaRP

1. **HaRP**: run it and register it as a deploy daemon, as described in the
   [HaRP README](https://github.com/nextcloud/HaRP) and the admin manual, section
   *AppAPI and External Apps*. Your reverse proxy must forward `/exapps/` to HaRP. AppAPI itself
   reaches the app through that path, so installation fails without it. For push (new mail
   without polling), the rule must also pass WebSocket upgrades (`Upgrade` and `Connection`
   headers in nginx). Without them, SignalR falls back to SSE or long polling. `dev/nginx.conf`
   has a working example.
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
5. **Web address (optional)**: AppAPI shows the app at
   `/apps/app_api/embedded/sockless_mail/sockless_mail`. For `/apps/socklessmail/`, copy
   `companion/socklessmail/` into a Nextcloud apps directory (usually `custom_apps/`), then run
   `occ app:enable socklessmail`. Keep its `<version>` in step with the ExApp's.
   - The app menu entry then points at the new address. AppAPI's address keeps working.
   - The page itself still comes from the ExApp, so the companion has no settings and stores
     nothing.
   - With the ExApp disabled, `/apps/socklessmail/` answers 404. If a future AppAPI no longer
     offers what the companion uses, it redirects to AppAPI's address instead.

## The companion app (`companion/socklessmail/`)

An ExApp has no PHP code, so it cannot add routes under `/apps/`. Only AppAPI's own
`/apps/app_api/embedded/<appid>/<menu>/` page is available. Collabora (`richdocuments`) and
ONLYOFFICE-style connectors get their address the same way: a PHP app in front of a backend
container. The companion stays small:

- `PageController` serves `/apps/socklessmail/` and every path below it. It renders the same
  empty `#content` element as AppAPI's page, with the same Content Security Policy.
- AppAPI adds the registered bundles in a middleware that only runs for its own controller. So
  `js/socklessmail-main.js`, a plain loader script, adds the ExApp's stylesheet and script from
  AppAPI's proxy (`/apps/app_api/proxy/sockless_mail/...`). The bundle paths are fixed in
  `lib/Service/ExApp.php` and must match `vite.config.js`.
- The initial state the ExApp registered (`UiRegistration.cs`) is read from AppAPI's
  `InitialStateService` and passed on under the `socklessmail` id. The frontend reads that id
  first, then `app_api`.
- `NavigationListener` adds a menu entry with AppAPI's id (`app_api_sockless_mail_sockless_mail`).
  Nextcloud keys entries by id and resolves plain entries after AppAPI's closures, so this entry
  replaces AppAPI's. The page also marks it active.
- AppAPI has no public PHP API for this, so `ExAppService` and `InitialStateService` are used
  directly, behind `class_exists` checks and try/catch.
- AppAPI loads an ExApp's translations only on its own pages. The app has no translations yet;
  once it has, the companion must load them as well.

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
| `SOCKLESS_MAIL_BIMI_AUTHSERV_IDS` | unset | Comma-separated authserv-ids (the host name that starts an `Authentication-Results` header) whose DMARC verdicts count for sender logos. Unset means the topmost header counts. See "Sender logos (BIMI)". |

Data lives in `APP_PERSISTENT_STORAGE` (the Docker volume `nc_app_sockless_mail_data`):
`sockless_mail.db` (SQLite, WAL mode) and `keys/` (the Data Protection key ring).

### Signing in with Google and Microsoft (OAuth2)

Users can add Gmail/Google Workspace and Outlook/Microsoft 365 accounts by signing in instead of
creating app passwords. A Nextcloud admin enables this once, in the app: **Sign-in providers** at
the bottom of the account rail (shown to members of the `admin` group only). The dialog shows the
callback URL to register, e.g. `https://cloud.example.com/exapps/sockless_mail/oauth/callback`
under HaRP (`…/index.php/apps/app_api/proxy/sockless_mail/oauth/callback` without it).

- **Google** (Google Cloud console → APIs & Services → Credentials): create an OAuth client of
  type *Web application* with the callback URL as an authorized redirect URI. The consent screen
  needs the scopes `https://mail.google.com/`, `openid`, `email` and `profile`. The Gmail scope is
  "restricted": until Google verifies the app, only test users listed on the consent screen can
  sign in (fine for one organization; publishing to everyone requires Google's review).
- **Microsoft** (Entra admin center → App registrations): register a *Web* platform with the
  callback URL, create a client secret, and add the delegated permissions `IMAP.AccessAsUser.All`
  and `SMTP.Send` (Office 365 Exchange Online) plus `offline_access`, `openid`, `email`,
  `profile`. Tenant: `common` (work and personal accounts), `organizations`, `consumers`, or your
  tenant id. SMTP AUTH must be enabled for the mailboxes in Exchange.

Enter the client id and secret and save. Saving an empty client id turns the provider off;
existing accounts then fail with `oauth_not_configured` until it is set up again.

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
- **The composer** is a window docked at the bottom right, not a modal dialog, so the mailbox
  stays usable while writing. It can be minimised to its title bar, or popped out to a large
  window. One message is written at a time: New message, reply or forward while a draft is open
  brings that draft back instead of replacing it.
  - Escape and clicking elsewhere do not close it, so a half-written message cannot vanish by
    accident. Send (also Ctrl+Enter) and Discard are the only ways out. Discard asks first when
    something was typed or attached.
  - **Rich text** (TipTap) is the default: paragraph styles, font and size, bold, italic,
    underline, strikethrough, text colour and highlight, lists, quotes, alignment, links. The
    message goes out as HTML with a plain-text alternative the server makes from it. The server
    cleans the HTML with an allowlist first (no scripts, event handlers, forms, frames or
    images), gives paragraphs no margins (one Enter is one line, as in desktop mail programs) and
    sets a default font. **Plain text** is one click away and is remembered for new messages.
    Replies and forwards quote the original's HTML (cleaned the same way, images left out) in a
    blockquote, or below a header block for a forward.
  - **Drafts** are saved to the account's Drafts folder (created when there is none) 4 seconds
    after the last change: `PUT /api/accounts/{id}/drafts` appends the message flagged `\Draft`
    and `\Seen`, then removes the version it replaces. Addresses still being typed are kept as
    written. Close (×) keeps the draft; Discard deletes it; sending removes it. A draft opened
    in Drafts has **Continue editing**, which loads it back (`…/draft?mode=edit`): fields, text,
    attachments and threading as saved. While a draft is continued, each saved copy becomes the
    source of its attachments, so uploads move into it. Leaving the page with unsaved changes
    asks first.
  - Recipients are chips. What is still being typed counts too, so sending without finishing an
    entry loses nothing; invalid addresses are marked. Suggestions come from the addresses of
    mail the app has loaded (lists, search results, unified folders); there is no address book
    lookup yet.
- **Keyboard shortcuts** follow Outlook, with single keys for the ones browsers keep for
  themselves (Ctrl+N opens a window): N or Ctrl+Shift+M new message; R or Ctrl+R reply; Shift+R
  or Ctrl+Shift+R reply all; F or Ctrl+Shift+F forward; Delete; Ctrl+Q read; Ctrl+U unread;
  Insert flag; / or Alt+Q search; Esc clears the selection or closes the message; ? lists them.
  In the list, the arrow keys move between messages and Ctrl+A selects all loaded ones. They are
  off while typing and while a dialog is open.
- **Message lists come from a header cache** in the database (`folders`, `messages` and
  `removed_messages` tables): subject, addresses, date, flags, size, has-attachments, and a
  preview of up to 200 characters of text. Bodies are never cached; opening a message still
  fetches it from the server.
  - **Previews** come with the list fields: MailKit uses the server's `PREVIEW` extension
    (RFC 8970) where there is one, and otherwise fetches the first bytes of the text part
    (`BODY.PEEK[n]<0.512>`, converted from HTML when needed). Whitespace is collapsed. Rows
    cached before previews existed are filled in by later syncs, 100 per sync, newest first.
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
- **New mail in the UI** is pushed with HaRP. The browser holds a SignalR connection to
  `/api/hub` (WebSockets, with SSE and long polling as fallbacks). It has the same route and
  authentication as the rest of the API, and the negotiate POST passes the same CSRF check.
  - Every cache write that a list or count would show (sync, flag change, removal) sends the
    owner's tabs `folderChanged { accountId, folderId }`. It is only a nudge: the tab asks the
    changes endpoint what changed and reloads that account's folder list (once per 2 s burst).
  - An open connection keeps the user active, so the IDLE watchers keep running while a tab is
    open, also in the background. The window of 10 minutes starts when the last tab closes.
  - While connected, inboxes and the unified views of inboxes only (Inbox, Unread) are polled
    every 2 minutes only, in case a watcher is down. Other folders and unified views have no
    watcher and are polled every 60 seconds. The folder
    lists are reloaded every 2 minutes.
  - Without a connection (AppAPI's PHP proxy, which buffers responses, or while reconnecting),
    the UI polls the open folder's changes endpoint every 15 seconds and reloads the folder lists
    (LIST + STATUS) every 60 seconds, for the unread counts in the account rail.
  - Everything is refreshed when the tab becomes visible again and after a reconnect.
- **Search** has two scopes, chosen next to the search box in the command bar:
  - **This folder** (`GET …/folders/{id}/search?q=…&before=<uid>`) asks the mail server:
    `UID SEARCH TEXT <term> TEXT <term> …`. It matches headers and message text, finds old mail
    the cache has never seen, and pages newest first with the same UID cursor as the list. The
    server decides how terms match: Dovecot with a full-text index matches words, others (such
    as GreenMail) substrings. The results are not cached.
  - **All accounts** (`GET /api/search?q=…`) only asks the database:
    - It matches subject, sender and recipient names and addresses of cached messages, without
      regard to case (also beyond ASCII). Message text is not included.
    - It covers only what the cache holds: folders shown before, as far back as they were paged.
    - It returns the 50 newest matches, saying when there are more.
  - Every word or "quoted phrase" must match. A search has at most 10 terms and 200 characters.
  - A quick filter (`&filter=unread|flagged|attachments`) narrows either scope, and can also
    be used without words.
  - Results from other folders show "Account › Folder". They open in place, and their actions
    (also the row's hover actions) go to the folder they are in. Clicking a folder or account
    leaves the search.
- **Quick filters** (the Unread, Flagged and Attachments chips above the list) ask the mail
  server, so they cover the whole folder, not only what the cache holds:
  - Unread and Flagged are `UID SEARCH UNSEEN` and `UID SEARCH FLAGGED`.
  - IMAP cannot search for attachments. The server finds candidates by their header
    (`HEADER Content-Type multipart/mixed` or `multipart/signed`) and keeps those whose
    BODYSTRUCTURE has an attachment, reading more candidates until the page is full (at most 5
    rounds per request). The total is left out when not every candidate was checked.
  - While a filter is shown without words, polling keeps it current: new mail that passes the
    filter is added, removed mail leaves. A message that stops passing (read while the Unread
    filter is shown) stays until the filter is shown again, as in Outlook.
- **All accounts** (shown with two or more accounts, and opened first then, on the unified
  Inbox) replaces the folder tree with views across every account. `{view}` is one of:
  - unified folders `inbox`, `drafts`, `sent`, `archive`, `junk`, `trash`: that special-use folder
    of every account, from the header cache. An account's folder is the one its folder list shows
    for the use (RFC 6154 attributes, else common names), picked like the folder pane does. Which
    folder has which use is remembered per account for 10 minutes and refreshed by every folder
    list the app loads, so a page does not LIST every account. Accounts without such a folder are
    left out; the pane hides a unified folder no account has.
  - smart folders `unread` (unread mail in every inbox) and `flagged` (flagged mail in every
    inbox, archive and sent folder). They are quick filters asked of each mail server, like
    `filter` below, merged by date; their total counts matches, and they take no further filter.
  - `GET /api/unified/{view}/messages?before=<cursor>` syncs the view's folder of every account
    at once, each account in its own service scope (an INBOX is taken as is while an IDLE
    watcher keeps it current), then merges the cached rows by date, account id and UID. The
    cursor is that triple, so pages neither skip nor repeat messages when dates are equal. Older
    pages read the cache without syncing.
  - The cache holds a UID window per folder, the list is ordered by date. A page is only
    served when every account has more than a page of cached messages below the cursor or its
    whole folder cached; a shorter window is extended on the server first. This assumes UIDs
    follow dates, as they do for delivered mail: messages appended with an old date (imports,
    mail filed into Archive) may appear later than their date suggests.
  - An account whose server fails (unreachable, wrong password) does not fail the list: its
    messages are shown as last cached, and `errors` names it, which the UI shows above the list.
  - `GET /api/unified/{view}/changes?since=<token>` answers like the folder changes endpoint. The
    token holds each folder's change token (`{account}.{folder}.{token}` joined by `~`); when a
    folder appeared or went away since (account added, deleted or first reached), the answer is
    `reset: true` and the UI reloads the list. A smart folder merges the changes that pass its
    filter; a message that stops passing stays until the folder is shown again.
  - `GET /api/unified/{view}/filter?filter=…&before=<cursor>` is a quick filter over a unified
    folder. It asks each mail server (like a folder filter) rather than the cache, which holds
    only part of each folder, and merges the matches by date. The cursor holds where each
    account's folder stands.
  - Every list entry carries its account and folder, so reading, flags, move and delete go to
    the message's own account (delete in the unified Trash deletes for good, as in an account's
    Trash); reply and forward send from it, and a draft opens in the composer. Search in these
    views covers all accounts and folders.
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
- **Nextcloud Files** (`Files/`): the app reads and writes the user's files over WebDAV
  (`remote.php/dav/files/<user>`) as that user. AppAPI's Sabre plugin accepts the same
  `AUTHORIZATION-APP-API` header there as on OCS, so no password is involved, and Nextcloud
  applies the user's own permissions, quota and sharing policy.
  - *Attach from Files*: the browser shows Nextcloud's file picker, and the server copies the
    chosen files into the same upload store as files from the device (25 MB each). A folder or
    an oversized file is reported on its chip, with "Share link instead" for the latter.
  - *Share link from Files*: a public read-only link through the OCS sharing API, inserted where
    the cursor is. Nextcloud's defaults apply (expiry). It refuses when it enforces passwords on
    links. The browser builds the link from the share token and its own address, because the
    URL Nextcloud returns uses the host the app reaches it by (often an internal one).
  - *Save to Files* (one attachment or all): never replaces a file. A taken name becomes
    "name (2).ext", and the upload uses `If-None-Match: *`, so a file created in the meantime
    moves it on to the next number.
  - *Send by email* in the Files app: registered on enable as a version 2 file action
    (`/api/v2/ui/files-actions-menu`, files only). Its handler `POST /files-action/send`
    does nothing but answer `redirect_handler`, so AppAPI opens our page with `?fileIds=…`. The
    page opens a new message and attaches the files, looked up by id with a WebDAV `SEARCH`. If
    AppAPI cannot register the action, enabling still succeeds.
- **Nextcloud Contacts** (`Contacts/`): CardDAV on `remote.php/dav/addressbooks/users/<user>/`,
  as the user in the same way as Files. That home lists the user's own address books, books
  shared with them, the system address book ("Accounts") and "Recently contacted". Nextcloud's
  privileges say which books are writable.
  - *Suggestions*: `ContactDirectory` keeps each user's contacts in memory: name, addresses,
    groups and organization, fetched with an `addressbook-query` REPORT that asks for those
    properties only (no photos) and only for cards with an address. The list of books is
    checked at most every 30 seconds, and a book is fetched again only when its ctag changed.
    Users who stop searching are dropped after an hour. Words that start with what is typed
    rank first; each address appears once, from the user's own books before shared, system and
    collected ones. The composer shows contacts first, then addresses from loaded mail, then
    groups.
  - *Groups* are the Contacts app's groups (vCard `CATEGORIES`), with each member's first
    address. "Recently contacted" never forms a group.
  - *Add sender to contacts*: a new vCard 3.0 in a writable book, or one more `EMAIL` line in
    an existing card. The line goes into the card's original text, so properties the app does
    not read (photos, addresses) stay as they were. The `PUT` carries the card's ETag
    (`If-Match`), so an edit made elsewhere in the meantime is not overwritten; the user is
    asked to try again.

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
- **Access levels**: `info.xml` declares `/api` as USER and `/api/admin` (listed first, since
  both proxies apply the first matching route) as ADMIN. HaRP and the PHP proxy reject other
  requests before they reach the app, and the app checks again: a user id for `/api`, and
  membership in Nextcloud's `admin` group (asked from Nextcloud, remembered for a minute) for
  `/api/admin`. `/oauth/callback` is PUBLIC, see below.
- **OAuth2 sign-in** uses the authorization code flow with PKCE (S256). A sign-in in progress is
  held in memory for 10 minutes and identified by two random 256-bit values: the `state` sent to
  the provider, and a flow id the user's browser polls. The provider's redirect is a cross-site
  navigation that may arrive without a Nextcloud session, so `/oauth/callback` needs only AppAPI's
  signature; it finds the flow by `state`, and refuses it when a signed-in user other than the one
  who started it is on the request. The code is exchanged there, once. The grant is then handed
  only to the user who started the flow, once, and signing in again for an existing account must
  name the same address. ID tokens are read without checking their signature: they come straight
  from the provider's token endpoint over TLS, in exchange for the client secret (OpenID Connect
  Core 3.1.3.7).
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

OAuth2 accounts store their refresh token the same way. Access tokens are kept in memory only
and refreshed shortly before they expire; when a provider rotates the refresh token, the new one is
saved. A refused refresh token (revoked, password changed) shows as `oauth_signin_required`, and
the account settings offer "Sign in again". The admins' client secrets are encrypted with the same
key ring (purpose bound to the provider) and never returned by the API.

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

### Sender logos (BIMI)

Messages from brands that publish a [BIMI](https://bimigroup.org/) record show the brand's logo
in place of the sender's initials. A logo is a claim that the mail really comes from that brand,
so it is shown only when every check passes:

- **The message passed DMARC.** The sync fetches `Authentication-Results` and `BIMI-Selector`
  with the list fields. Only the topmost `Authentication-Results` header counts, because it was
  added by the server closest to the mailbox. A sender's forged header sits further down and is
  never read. The verdict must be `dmarc=pass` for the From domain, and the message must have
  exactly one author. Some servers write no `dmarc=` result, only DKIM and SPF; amavis is one of
  them. Then a `dkim=pass` whose `header.d`, or an `spf=pass` whose `smtp.mailfrom` domain, is
  the From domain or a parent of it counts as the DMARC pass, because that is the check DMARC
  itself makes. A subdomain never counts. An explicit `dmarc=` result always wins.
- **That header comes from your server.** If your mail server adds no `Authentication-Results`
  of its own, the topmost header could be one the sender wrote. Set
  `SOCKLESS_MAIL_BIMI_AUTHSERV_IDS` to your server's authserv-id to close that gap. Only headers
  with those ids then count.
- **The domain enforces DMARC.** Its policy must be `quarantine` or `reject` at 100 %, and must
  not be in test mode. The policy is found with the DMARCbis DNS tree walk.
- **The BIMI record is checked.** It is read at `<selector>._bimi.<domain>`, or else at the
  organizational domain where the DMARC policy was found. It is never read at a public suffix
  (`psd=y`), so a registry's logo does not appear on its customers' mail. The logo URL must be
  HTTPS.
- **The logo is a static SVG of at most 32 KB.** It must have no scripts, event handlers, foreign
  content, animation, DTD or references outside the document. It is fetched through the same
  address policy as remote images.

Lookups are cached in the database and shared by all users, because they hold public DNS and
web content. One lookup per domain and selector is kept:

| Result | Kept for |
|---|---|
| A logo | 24 hours |
| No logo to show | 6 hours |
| DNS or the logo server failed | 1 hour; a logo already known stays meanwhile |

The browser loads logos from `/api/bimi/<domain>` and caches each answer: a logo for a day, a
404 for an hour. A long list therefore costs one request per sender domain. The endpoint
serves the logo with the sandbox CSP and is rate-limited to 300 requests per minute per user.
Users can turn logos off in the view menu ("Sender logos").

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
  no PATCH. It buffers responses, so without HaRP the app polls instead of using push.
- SQLite limits the app to a single instance.
- View settings (reading pane position, density, folder pane, pane sizes) are kept in the
  browser's local storage per Nextcloud user, so they do not follow the user to another browser.
- **AppAPI/HaRP route cache after `app:update`** (observed with Nextcloud 35.0.2 and the current
  HaRP release): after an update, HaRP answered every `/exapps/sockless_mail/...` request with
  404 ("No defined route"), although the routes were in Nextcloud's database. Disabling and
  enabling the app fixes it, because that pushes the app's metadata to HaRP again.
  `dev/dev.sh update-docker` does this automatically. This looks like an upstream issue.
- There is no signature yet.
- Changes made to contacts outside the app can take up to 30 seconds to show in suggestions.
  Contact groups are the Contacts app's groups (`CATEGORIES`); vCard 4 group cards
  (`KIND:group`) are not read.
- Some providers (Gmail, Outlook) store a copy of everything sent over their SMTP in Sent
  themselves, so the app's own copy duplicates it. This needs a per-account setting.
- Upload sizes through AppAPI's PHP proxy (manual-install) are also limited by PHP's
  `upload_max_filesize` and `post_max_size`.
- Actions work on one message at a time in the UI. The API takes up to 500 ids, so selecting
  several messages is a UI addition only. Folders cannot be deleted from the UI yet, and there is
  no undo after a move or delete (Trash is the safety net).
- The header cache covers what the user has paged through, not whole folders. The folder list
  and its counts still come from the server (LIST + STATUS) every time.
- Search across accounts only knows the subjects and addresses of cached messages, and shows at
  most 50 results. To find message text or older mail, search one folder; there is no search of
  all folders on the servers at once.
- The unified folders cover special-use folders only (no unified view of a custom folder), the
  Flagged smart folder looks in inboxes, archives and sent folders only, and searching any of
  them searches the cache of all folders.
- Only the INBOX is watched with IDLE. Other folders are synced when they are shown or polled.
- The UI still polls: IDLE makes those polls cheap and current, but the browser learns about new
  mail up to 15 seconds late.
- Share links from the composer are public and read-only, with Nextcloud's default expiry. A
  password or a custom expiry has to be set in the Files app. Where Nextcloud enforces link
  passwords, only "Attach from Files" works.
- "Send by email" in the Files app always opens AppAPI's page (`/apps/app_api/embedded/...`),
  even when the companion app is installed: AppAPI builds that address itself.
- Message parts are held in memory while they are served, so very large attachments cost
  memory in proportion to their size.
- The frontend bundle is about 1.2 MB (355 kB gzipped), mostly the Nextcloud component library.
- One login per account, used for both IMAP and SMTP: a username and password, or an OAuth2
  sign-in with Google or Microsoft. Other OAuth2 providers (Yahoo, Fastmail, AOL) are not
  supported. OAuth2 accounts use the provider's standard servers.
- Sign-ins in progress live in memory: a restart during a sign-in means starting it again. The
  callback page carries no script, so the popup is closed by the app's window when it sees the
  result.
- Sender logos (BIMI) do not check Verified Mark Certificates (VMC/CMC). Any domain that
  enforces DMARC can show a logo, including look-alike domains. Gmail and Apple Mail require a
  certificate. The logo appears next to the sender's address, not in place of it.
- Mail servers with self-signed certificates cannot be used. There is no "trust this certificate"
  option yet.
- Nextcloud installs translations only from app store release archives, so the UI is English
  until the app is published there.

## License

MIT. See [LICENSE](LICENSE).
