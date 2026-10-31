# Sockless Mail

A webmail client for Nextcloud with any number of IMAP/SMTP accounts per user. It runs as a
Nextcloud **External App (ExApp)**: an ASP.NET Core (.NET 10) service that Nextcloud manages
through AppAPI, with a Vue 3 interface rendered inside Nextcloud. Users reach it only through
Nextcloud and are signed in by their Nextcloud session. Mail is handled with MailKit/MimeKit.

- [Features](#features)
- [Installation](#installation)
- [Configuration](#configuration)
- [Security](#security)
- [Known limitations](#known-limitations)
- [Development](#development)
- Internals: [docs/architecture.md](docs/architecture.md) and [docs/security.md](docs/security.md)

## Features

**Accounts**
- Any number of IMAP/SMTP accounts per Nextcloud user, with credentials encrypted at rest.
- Google and Microsoft accounts can be added by signing in (OAuth2), once an admin has set it up.

**Reading**
- A layout like desktop mail programs: an account rail with a colour and unread count per
  account, a folder pane, a command bar, and a message list grouped by day with a line of each
  message's text. The reading pane sits on the right, at the bottom, or is off.
- Hover actions, a right-click menu, multi-select and quick filters (Unread, Flagged,
  Attachments). Outlook's keyboard shortcuts.
- Sanitized message view with remote images blocked by default, attachments, raw headers,
  message source and `.eml` download.
- Message lists come from a local header cache kept in sync with QRESYNC/CONDSTORE. New mail
  arrives through IMAP IDLE and is pushed to the browser.
- With several accounts, **All accounts** shows unified folders (every account's Inbox, Drafts,
  Sent, Archive, Junk or Trash in one list) and the smart folders Unread and Flagged.
- Search in the open folder on the server (message text included), or across all accounts from
  the cache.
- The app remembers expanded folders and the last open folder per account, on the server or in
  the browser.

**Writing**
- A composer window docked at the bottom, with rich-text editing or plain text, and drafts saved
  while writing.
- Reply, reply all, forward, attachments, and a copy saved to Sent.
- **Send later**: at a preset ("Tomorrow morning") or a chosen time. The app's server sends the
  message, so the browser can be closed meanwhile.

**Organizing**
- Move, delete through Trash, flag, read/unread. Folders can be created, renamed and deleted.
- **Coloured tags** across all accounts: stored as IMAP keywords where the server keeps them, and
  as Outlook categories for Microsoft accounts. Each tag has a folder (all accounts or one), and
  the list can be filtered by tag.
- **Rules** for new mail, run by the app: move, copy, mark as read, flag, move to Trash, tag.

**Nextcloud integration**
- Files: attach from Files or share as a link, save attachments into Files, and "Send by email"
  in the Files app.
- Contacts: recipient suggestions from the user's address books (contact groups add all their
  members), and "Add sender to contacts".
- Calendar: invitations show as a card with Accept, Maybe and Decline. The event goes into a
  Nextcloud calendar and the answer goes to the organizer from the mail account. Cancellations,
  attendees' replies and `.ics` files update the calendar the same way.
- Tasks: "Create task…" turns a message into a task in the Tasks app.

**Safety**
- Senders whose domain imitates a known one, or was registered only recently, get a warning.
- Links whose text shows another domain than they lead to ask before opening.
- Brand logos (BIMI) appear only for mail that passed DMARC.

## Installation

### Requirements

- Nextcloud 32 or later with **AppAPI** enabled. AppAPI ships with current Nextcloud releases.
- A deploy daemon. **HaRP** is recommended. A *manual-install* daemon works too, with
  limitations (see below).
- The ability to run a Docker image from a registry your Docker engine can pull from.

### With HaRP

1. **HaRP**: run it and register it as a deploy daemon, as described in the
   [HaRP README](https://github.com/nextcloud/HaRP) and the Nextcloud admin manual, section
   *AppAPI and External Apps*.
   - Your reverse proxy must forward `/exapps/` to HaRP. AppAPI itself reaches the app through
     that path, so installation fails without it.
   - For push (new mail without polling), the proxy must also pass WebSocket upgrades (the
     `Upgrade` and `Connection` headers in nginx). Without them, the browser falls back to SSE
     or long polling.
   - [dev/nginx.conf](dev/nginx.conf) has a working example.
2. **Image**: releases are published as `ghcr.io/sockless-coding/sockless_mail:<version>` for
   amd64 and arm64 (see [Releasing](#releasing)). For your own build, push it to a registry
   your Docker engine can pull from, and set `<registry>`, `<image>` and `<image-tag>` in
   [appinfo/info.xml](appinfo/info.xml) to match.
3. **Register**. `--info-xml` takes a local path or a URL, such as the release's own manifest:
   ```bash
   occ app_api:app:register sockless_mail <daemon> \
       --info-xml https://raw.githubusercontent.com/sockless-coding/nextcloud-mail/v<version>/appinfo/info.xml \
       --env SOCKLESS_MAIL_ENCRYPTION_KEY='<long random value>' --wait-finish
   ```
   Generate the key with, for example, `openssl rand -base64 48`, and keep a copy somewhere
   safe (see [Stored credentials](docs/security.md#stored-credentials)). Add further `--env`
   options for the other [settings](#environment-variables).
4. **Web address (optional)**: AppAPI shows the app at
   `/apps/app_api/embedded/sockless_mail/sockless_mail`. For a cleaner `/apps/socklessmail/`,
   install the companion app:
   ```bash
   cp -r companion/socklessmail /path/to/nextcloud/custom_apps/
   occ app:enable socklessmail
   ```
   The app menu entry then points at the new address, and AppAPI's address keeps working. The
   companion only serves the page; AppAPI and the ExApp are still required. Each release has
   it as `socklessmail.tar.gz`.

### Nextcloud AIO

Enable HaRP in the AIO interface first. AIO then registers the deploy daemon and routes
`/exapps/` for you. Run `occ` inside the Nextcloud container:

```bash
occ() { docker exec --user www-data -i nextcloud-aio-nextcloud php occ "$@"; }
VERSION=0.32.2                    # the release to install
KEY="$(openssl rand -base64 48)"; echo "$KEY"   # store this in your password manager

occ app_api:daemon:list   # AIO's HaRP daemon is harp_aio
occ app_api:app:register sockless_mail harp_aio \
    --info-xml "https://raw.githubusercontent.com/sockless-coding/nextcloud-mail/v$VERSION/appinfo/info.xml" \
    --env SOCKLESS_MAIL_ENCRYPTION_KEY="$KEY" --wait-finish
occ app_api:app:list      # check that sockless_mail is enabled

# Optional: the companion app, for /apps/socklessmail/
curl -fsSL "https://github.com/sockless-coding/nextcloud-mail/releases/download/v$VERSION/socklessmail.tar.gz" \
    | docker exec --user www-data -i nextcloud-aio-nextcloud tar -xz -C /var/www/html/custom_apps
occ app:enable socklessmail
```

AIO's backup covers AIO's own volumes. Check whether it also includes the app's volume
`nc_app_sockless_mail_data`, and back that volume up yourself if it does not.

### Updating

1. Run:
   ```bash
   occ app_api:app:update sockless_mail \
       --info-xml https://raw.githubusercontent.com/sockless-coding/nextcloud-mail/v<version>/appinfo/info.xml \
       --wait-finish
   ```
   The app secret, port, settings and data volume are kept. An update to the version already
   installed does nothing.
2. If every `/exapps/sockless_mail/...` request answers 404 after the update, run
   `occ app_api:app:disable sockless_mail` and then `occ app_api:app:enable sockless_mail` (see
   [Known limitations](#known-limitations)).
3. For the companion app, unpack the new `socklessmail.tar.gz` over the old copy. Nextcloud
   updates it on the next page load, or with `occ upgrade`.

Unregistering keeps the data volume unless you pass `--rm-data`. Registering again creates a
new app secret, which matters if no encryption key is set.

### Without HaRP (manual-install)

You can run the container yourself and register it with a `manual-install` daemon, as
`dev/dev.sh register-manual` does. Requests then go through AppAPI's PHP proxy, which means:

- no push: the browser polls the open folder every 15 seconds;
- upload sizes are also limited by PHP's `upload_max_filesize` and `post_max_size`;
- the OAuth2 callback URL is `…/index.php/apps/app_api/proxy/sockless_mail/oauth/callback`.

## Configuration

### Environment variables

AppAPI provides what the app needs to run (`APP_ID`, `APP_SECRET`, `APP_PERSISTENT_STORAGE`,
`NEXTCLOUD_URL` and, under HaRP, `HP_SHARED_KEY` and the FRP address). Your own settings are
passed with `--env` at registration. AppAPI drops values for names that are not declared in
`<environment-variables>`. Values are fixed at registration, so changing one means registering
again.

| Variable | Default | Purpose |
|---|---|---|
| `SOCKLESS_MAIL_ENCRYPTION_KEY` | unset | Wraps the key ring that encrypts stored mail credentials. At least 32 characters. Strongly recommended; see [Stored credentials](docs/security.md#stored-credentials). |
| `SOCKLESS_MAIL_ALLOW_LOCAL_SERVERS` | `false` | `true` allows mail servers on private, loopback and carrier-grade NAT addresses, e.g. an internal mail server. |
| `SOCKLESS_MAIL_BIMI_AUTHSERV_IDS` | unset | Comma-separated authserv-ids (the host name that starts an `Authentication-Results` header) whose DMARC verdicts count for sender logos. Unset means the topmost header counts. See [Sender logos](docs/security.md#sender-logos-bimi). |

### Data

Everything lives in `APP_PERSISTENT_STORAGE`, the Docker volume `nc_app_sockless_mail_data`:

- `sockless_mail.db`: SQLite in WAL mode
- `keys/`: the encrypted Data Protection key ring
- `uploads/`: attachments waiting to be sent, for 24 hours at most
- `scheduled/`: encrypted messages waiting to be sent later

Back up the volume, but keep `SOCKLESS_MAIL_ENCRYPTION_KEY` out of the same backup.

### Admin settings

Members of the Nextcloud `admin` group see **Administration** at the bottom of the account rail.
It has two sections:

- **Sign-in providers**: set up Google and Microsoft sign-in (see below).
- **Suspicious senders**: turn domain-age lookups over RDAP on or off.

### Signing in with Google and Microsoft (OAuth2)

Users can add Gmail/Google Workspace and Outlook/Microsoft 365 accounts by signing in instead of
creating app passwords. The **Sign-in providers** dialog shows the callback URL to register with
the provider, e.g. `https://cloud.example.com/exapps/sockless_mail/oauth/callback`.

**Google** (Google Cloud console → APIs & Services → Credentials):
- Create an OAuth client of type *Web application*, with the callback URL as an authorized
  redirect URI.
- The consent screen needs the scopes `https://mail.google.com/`, `openid`, `email` and
  `profile`.
- The Gmail scope is "restricted". Until Google verifies the app, only test users listed on the
  consent screen can sign in. That is fine for one organization; opening it to everyone needs
  Google's review.

**Microsoft** (Entra admin center → App registrations):
- Register a *Web* platform with the callback URL, and create a client secret.
- Add the delegated permissions `IMAP.AccessAsUser.All` and `SMTP.Send` (Office 365 Exchange
  Online), plus `offline_access`, `openid`, `email` and `profile`.
- **Also add the delegated Microsoft Graph permissions `Mail.ReadWrite` and
  `MailboxSettings.ReadWrite`.** Exchange keeps no IMAP keywords, so tags on Microsoft accounts
  are Outlook categories, read and set through Graph.
  - Users consent to them together with the mail permissions. Where the tenant does not let
    users consent, grant admin consent for the organization in the app registration.
  - If consent is refused, the whole sign-in fails, mail included.
  - Accounts signed in before these permissions were asked for keep working, with their tags
    kept in Sockless Mail only. Once the user signs in again in the account settings, their tags
    are copied to Outlook.
- Tenant: `common` (work and personal accounts), `organizations`, `consumers`, or your tenant id.
- SMTP AUTH must be enabled for the mailboxes in Exchange.

Enter the client id and secret and save. Saving an empty client id turns the provider off;
existing accounts then fail with `oauth_not_configured` until it is set up again.

## Security

In short (details in [docs/security.md](docs/security.md)):

- Every request is authenticated by AppAPI or HaRP with the app secret. The app takes the user id
  only from that verified header and scopes every query by it, so users cannot see each other's
  accounts.
- Mail passwords, OAuth2 tokens and scheduled messages are encrypted with a key ring that is
  itself encrypted with `SOCKLESS_MAIL_ENCRYPTION_KEY`, which is not stored on the volume.
- Message HTML is sanitized on the server, shown in a sandboxed iframe without scripts, and
  covered by Nextcloud's Content Security Policy. Remote images load only on request, through
  the app's image proxy.
- Users cannot use the app to reach hosts on internal networks, unless
  `SOCKLESS_MAIL_ALLOW_LOCAL_SERVERS` is set.

## Known limitations

**Deployment**
- **Route cache after `app:update`** (seen with Nextcloud 35.0.2 and the current HaRP release):
  after an update, HaRP answered every `/exapps/sockless_mail/...` request with 404 ("No defined
  route"), although the routes were in Nextcloud's database. Disabling and enabling the app
  fixes it, because that sends the app's metadata to HaRP again. `dev/dev.sh update-docker` does
  this automatically. This looks like an upstream issue.
- SQLite limits the app to a single instance.
- Send later needs the app to be running. While it is disabled or its container is down, nothing
  is sent, and due messages go out as soon as it is back.
- Without HaRP: no push, and AppAPI's PHP proxy forwards only GET, HEAD, POST, PUT and DELETE
  (the API uses no PATCH for that reason).
- Nextcloud installs translations only from app store release archives, so the UI is English
  until the app is published there.

**Accounts and servers**
- One login per account, used for both IMAP and SMTP: a username and password, or an OAuth2
  sign-in with Google or Microsoft. Other OAuth2 providers (Yahoo, Fastmail, AOL) are not
  supported, and OAuth2 accounts use the provider's standard servers.
- Mail servers with self-signed certificates cannot be used. There is no "trust this
  certificate" option yet.
- Some providers (Gmail, Outlook) save everything sent over their SMTP in Sent themselves, so
  the app's own copy is a duplicate. This needs a per-account setting.
- Sign-ins in progress are kept in memory, so a restart during a sign-in means starting again.

**Mail**
- There are no signatures yet.
- There is no undo after a move or delete. Trash is the safety net.
- The header cache covers what the user has paged through, not whole folders. Search across
  accounts only knows the subjects and addresses of cached messages and shows at most 50
  results. To find message text or older mail, search one folder.
- Only the INBOX is watched with IDLE. Other folders are synced when they are shown or polled.
- The unified folders cover special-use folders only (no unified view of a custom folder). The
  Flagged smart folder looks in inboxes, archives and sent folders only.
- Message parts are held in memory while they are served, so very large attachments cost
  memory in proportion to their size.
- Scheduled messages are not on the mail server, so other mail programs do not see them.

**Nextcloud integration**
- Share links from the composer are public and read-only, with Nextcloud's default expiry. A
  password or custom expiry has to be set in the Files app. Where Nextcloud enforces link
  passwords, only "Attach from Files" works.
- "Send by email" in the Files app always opens AppAPI's page (`/apps/app_api/embedded/...`),
  even with the companion app installed, because AppAPI builds that address itself.
- Changes to contacts made outside the app can take up to 30 seconds to show in suggestions.
  vCard 4 group cards (`KIND:group`) are not read; groups are the Contacts app's `CATEGORIES`.
- Invitations: counter-proposals (COUNTER), delegation and REFRESH are not supported, and the
  card has no comment field (the API takes one). Recurrence is described from FREQ and INTERVAL
  only ("Repeats weekly"). Free/busy conflicts are not checked. Reply mails are in English,
  whatever the user's language.

**Tags**
- A copy made by a rule (Copy to folder) carries no tag on accounts whose tags the app keeps.
- Messages without a Message-ID cannot be followed when another program moves them, and cannot
  carry Outlook categories.
- Tagged messages deleted elsewhere drop out of tag folders with the next scan (a week later for
  tags the app keeps).
- Outlook categories are matched by name, so a category renamed in Outlook is a different tag.

**Interface**
- Layout settings (reading pane position, density, folder pane, pane sizes) are kept in the
  browser, so they do not follow the user to another browser.
- Sender logos (BIMI) do not check Verified Mark Certificates (VMC/CMC). Any domain that enforces
  DMARC can show a logo, including look-alike domains (which the suspicious-sender check
  flags). The logo appears next to the sender's address, not in place of it.
- The frontend bundle is about 1.2 MB (355 kB gzipped), mostly the Nextcloud component library.

## Development

### Layout

```
appinfo/info.xml            ExApp manifest: id sockless_mail, image, routes, env allow-list
companion/socklessmail/     optional PHP app: serves the mail page at /apps/socklessmail/
Dockerfile                  frontend build → dotnet publish → aspnet:10.0-alpine + frpc
docker/entrypoint.sh        runs as root, fixes ownership, drops to the "sockless" user
docker/start.sh             opens the FRP tunnel to HaRP, then starts the app
docs/                       architecture and security internals
src/SocklessMail.Api/       ASP.NET Core minimal APIs
  AppApi/                   AppAPI contract: env, auth headers, lifecycle, OCS client, UI registration
  Hosting/                  listener (unix socket / TCP), CSRF guard, request logging
  Api/                      the app's own /api endpoints
  Accounts/                 account model, validation, service, encrypted credentials
  OAuth/                    Google and Microsoft sign-in, token refresh
  Data/                     EF Core DbContext (SQLite), per-user query filter, migrations
  Mail/                     MailKit connections: address policy, connector, connection pool, HTML sanitizer
  Mailbox/                  folders, message list, message view, parts, source, remote image proxy
  Compose/                  replies and forwards, outgoing HTML, attachment uploads
  Sync/                     header cache, folder sync (QRESYNC/CONDSTORE/plain), IDLE watchers
  Tags/                     tags, tag index and scan, Outlook categories over Graph
  Rules/                    rule engine, matcher, background runner
  Scheduling/               send later: store and sender
  Bimi/                     sender logos: DMARC verdicts, DNS lookups, SVG checks, logo cache
  Senders/                  suspicious-sender checks: look-alike domains, brand list, trust, RDAP domain age
  Files/                    Nextcloud Files over WebDAV as the user: attach, share links, save attachments
  Contacts/                 Nextcloud address books over CardDAV: suggestions, groups, adding senders
  Calendar/                 invitations (iMIP) and .ics files against Nextcloud calendars over CalDAV
  Tasks/                    tasks from messages in the Tasks app over CalDAV
  Security/                 Data Protection key ring wrapping
src/frontend/               Vue 3 + @nextcloud/vue 9, built into wwwroot/js and wwwroot/css
tests/SocklessMail.Tests/   xUnit v3: auth, CSRF, accounts, user isolation, key ring, pool, sanitizer
tests/SocklessMail.IntegrationTests/  API and IMAP against GreenMail and Dovecot in Docker (Testcontainers)
tests/e2e/                  browser checks against the dev stack (Playwright with installed Edge/Chrome)
dev/                        local Nextcloud + HaRP + GreenMail stack, the dev.sh helper, unicode-data.sh
```

### Build and test

Requirements: .NET SDK 10, Node 22, Docker (Engine 25+ for the healthcheck start interval).

```bash
dotnet test --project tests/SocklessMail.Tests              # unit + endpoint tests
dotnet test --project tests/SocklessMail.IntegrationTests   # needs Docker: starts GreenMail and Dovecot
(cd src/frontend && npm ci && npm run build)
dev/dev.sh build                                            # docker build ghcr.io/sockless-coding/sockless_mail:<image-tag>
```

Database changes: run `dotnet tool restore`, then
`dotnet ef migrations add <Name> --project src/SocklessMail.Api --output-dir Data/Migrations`.
Migrations run automatically at startup.

### Local stack

`dev/dev.sh` (bash; Git Bash on Windows works) drives a complete local stack: Nextcloud 35 on
**http://localhost:8180** (admin/admin), HaRP behind the same nginx, and GreenMail.

```bash
dev/dev.sh up            # start the stack, register the deploy daemons harp_dev and manual_dev
dev/dev.sh seed-mail     # send 61 test messages (optionally to a given address)
```

- The companion app is mounted read-only into `custom_apps/socklessmail` and enabled by `up`, so
  PHP changes there apply on the next page load.
- GreenMail has two mailboxes, `alice@example.test` (password `alice`) and `bob@example.test`
  (password `bob`), without encryption: IMAP on port 3143, SMTP on 3025. Use the server name
  `greenmail` for the container deployed by HaRP, and `127.0.0.1` for the app running on your
  machine. The dev registrations set `SOCKLESS_MAIL_ALLOW_LOCAL_SERVERS=true`, because both
  addresses are local.
- The seeded mail has numbered plain-text messages and an HTML newsletter with a script, a
  remote image, an inline image and a PDF attachment.

**Fast loop: the app on your machine (manual-install daemon)**

```bash
dev/dev.sh run               # terminal 1: builds the frontend, runs the app on :9080
dev/dev.sh register-manual   # terminal 2: registers it with Nextcloud
```

Open http://localhost:8180 and click **Sockless Mail** in the top menu. After code changes,
restart `dev/dev.sh run`; register again only when routes or the version change. The
`manual-install` launch profile in `src/SocklessMail.Api/Properties/launchSettings.json` has the
same dev-only secret and port, so you can start the app from your IDE instead.

**Production-like loop: the container deployed by HaRP**

```bash
dev/dev.sh build             # docker build ghcr.io/sockless-coding/sockless_mail:<image-tag>
dev/dev.sh register-docker   # deploy through the harp_dev daemon
dev/dev.sh logs              # follow the container's logs
```

- After code changes, bump **both** `<version>` and `<image-tag>` in `appinfo/info.xml`, then
  run `dev/dev.sh build` and `dev/dev.sh update-docker`. `update-docker` also disables and
  enables the app (see [Known limitations](#known-limitations)).
- `register-docker` replaces any installed copy and works without a bump.
- `dev.sh up` maps the registry in `info.xml` (`ghcr.io`) to "local" on the dev daemon, so
  AppAPI deploys the locally built image instead of pulling the published one. Run `up` once on
  an existing stack to add the mapping.
- On Windows, `dev\update-docker.bat` builds and updates from cmd or Explorer, through Git Bash.
  `dev\update-docker.bat register` registers again instead, for a rebuild without a bump.

### Browser checks

They need the local stack, the app deployed and `dev/dev.sh seed-mail`:

```bash
cd tests/e2e && npm ci
node accounts.mjs    # one script per area; see the list below
```

| Script | Covers |
|---|---|
| `accounts.mjs` | add, test, validate, reorder and delete accounts |
| `mail.mjs` | paging, sanitized view, remote images, attachments, new and removed mail |
| `actions.mjs` | read/unread, flag, delete (with and without Trash), move, create/rename folders |
| `menu.mjs` | the message row menu |
| `compose.mjs` | new message with attachment, invalid recipients, reply, forward, copies in Sent |
| `drafts.mjs` | rich text, drafts saved while writing, Close, Continue editing, plain text |
| `workspace.mjs` | sender details, attachment cards, composer window, recipient chips, shortcuts |
| `layout.mjs` | multi-select, reading pane and density, folder pane |
| `viewstate.mjs` | remembered folders and last view |
| `filters.mjs` | previews and quick filters, in a folder and in the unified inbox |
| `search.mjs` | folder search, search across accounts, opening a result |
| `unified.mjs` | unified folders and smart folders |
| `push.mjs` | WebSocket push through HaRP |
| `oauth.mjs` | admin sign-in settings, Google popup with PKCE, callback through HaRP |
| `files.mjs`, `contacts.mjs`, `calendar.mjs`, `tasks.mjs` | Nextcloud integration |
| `senders.mjs` | suspicious-sender warnings |
| `tags.mjs`, `rules.mjs` | tags and rules |
| `sendlater.mjs`, `scheduled.mjs`, `presets.mjs` | send later, the Scheduled view, presets |
| `companion.mjs` | `/apps/socklessmail/`, menu entry, deep links |

They use an installed Edge (`BROWSER_CHANNEL=chrome` for Chrome) and exit non-zero on console
errors. They open the app at `/apps/socklessmail/`; `APP_PAGE=embedded` uses AppAPI's page
instead.

### Releasing

1. Bump `<version>` and `<image-tag>` in `appinfo/info.xml` (keep them equal), and `<version>`
   in `companion/socklessmail/appinfo/info.xml`. Commit.
2. Tag and push:
   ```bash
   git tag v0.32.0 && git push origin v0.32.0
   ```
3. [.github/workflows/release.yml](.github/workflows/release.yml) checks that the tag, the
   version and the image tag agree, runs the unit tests, and pushes
   `ghcr.io/sockless-coding/sockless_mail:<version>` and `:latest` for amd64 and arm64. It then
   creates a GitHub release with `info.xml` and `socklessmail.tar.gz`.
4. The first time only: on GitHub, open the package's settings (your profile or organization →
   Packages → `sockless_mail`) and make the package **public**, so Docker can pull it without
   logging in.

## License

MIT. See [LICENSE](LICENSE).
