# How Sockless Mail works

Internals for people working on the app. Installing and configuring it is in the [README](../README.md); the security design is in [security.md](security.md).

## Runtime

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

## Reading and changing mail

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
  - IDLE watchers hold one more connection per watched account, outside the pool (see "IDLE watchers").
- **Reading** never changes the mailbox. Folders are opened read-only (`EXAMINE`) and bodies are
  fetched with `BODY.PEEK`. Marking a message as read when it is opened is a separate,
  explicit request from the UI.
- **Headers and source**: `GET …/messages/{id}/headers` returns the raw header block, and
  `GET …/messages/{id}/source` the whole message as text, cut at 1 MiB (`truncated` says so).
  `?download=true` returns the complete message as a `.eml` file. Text is read as UTF-8, with
  Latin-1 as the fallback.
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

## Writing

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
     - The body is HTML with a plain-text alternative, or plain text only (see "The composer").
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
  stays usable while writing. It can be minimised to its title bar, or enlarged; Settings
  choose the size it opens in (`layout.composerSize`, kept in the browser like the View menu).
- **Separate windows** (`store/windows.js`) load the same page with a fragment saying what to
  show: `#window=message&account=…&folder=…&message=…` or `#window=compose&mode=new|reply|replyAll|forward|edit|handover&…`.
  App.vue then shows only the message or the composer, and a body class hides Nextcloud's
  header; there is no polling or push in such a window. A message window closes when its
  message is deleted or moved, a compose window when the message is sent, kept or discarded.
  With the composer setting "In a separate window", New message, Reply, Forward and Continue
  editing open one (straight from the click, so browsers allow it; when blocked, the composer
  opens large in the page). **Open in a new window** in the composer moves the message being
  written: it is saved, then copied into a slot on the window it was in
  (`window.socklessMailHandovers`, read through `window.opener`, same origin), together with
  the blobs of the images shown in the text, since the page's CSP keeps `fetch()` from reading
  `blob:` URLs and the old window's object URLs end with it. That composer closes without
  deleting uploads or the saved draft (`closeComposer('handedOver')`). Several messages can be
  written at once this way, one per window. One message is written at a time: New message, reply or forward while a draft is open
  brings that draft back instead of replacing it.
  - Escape and clicking elsewhere do not close it, so a half-written message cannot vanish by
    accident. Send (also Ctrl+Enter) and Discard are the only ways out. Discard asks first when
    something was typed or attached.
  - **Rich text** (TipTap) is the default: paragraph styles, font and size, bold, italic,
    underline, strikethrough, text colour and highlight, lists, quotes, alignment, links. The
    message goes out as HTML with a plain-text alternative the server makes from it. The server
    cleans the HTML with an allowlist first (no scripts, event handlers, forms, frames, and no
    images except its own, see below), gives paragraphs no margins (one Enter is one line, as in
    desktop mail programs) and sets a default font. **Plain text** is one click away and is
    remembered for new messages; it asks first when the text has images. Replies and forwards
    quote the original's HTML (cleaned the same way) in a blockquote, or below a header block
    for a forward. The quote keeps the original's own inline images (its `cid:` parts, listed in
    `inlineImages` like a continued draft's and sent along from the original), never remote ones.
  - **Images in the text** are pasted, dropped where they should go, or inserted from the
    toolbar, from this device or from Files (PNG, JPEG, GIF, WebP; other dropped or pasted files
    are attached instead). Pasted content with text of its own (from a document or a web page) is
    pasted as HTML, and its images are added one by one, a grey placeholder until then, so the
    browser never loads them from elsewhere: `data:` images are decoded in the browser, images of
    a message shown in the app are fetched as the user, and anything else over `http(s)` is fetched
    by the server (`POST /api/uploads/import`, through the image proxy's connector: no internal
    addresses, raster images up to 5 MB, 200 per 10 minutes per user). Images that cannot be
    fetched (`file:` paths from a word processor, say) are left out, with a warning. The
    browser scales images down to at most 2000 pixels on the longest side and turns WebP into
    JPEG; "Full quality" in the image menu sends the original instead. The menu also sets the
    width (Small 200, Medium 400, Large 640 pixels, or Fit) and alternative text.
    - Each image is an upload (`POST /api/uploads`), shown from an object URL and marked
      `data-cid="<upload id>@inline.sockless-mail"`; the HTML's src is never sent. The server
      keeps `<img>` only with a Content-ID (plus alt, width, height) and builds a
      `multipart/related` with each image as an inline part under that Content-ID, so the
      HTML refers to it as `cid:`. Images count toward the 25 MB total.
    - A continued draft lists its images' parts (`inlineImages` of `…/draft?mode=edit`); the
      composer fetches them once and shows them from object URLs. On saving or sending, a
      Content-ID is looked up in the source draft first and then among the uploads, so an image
      keeps its Content-ID through every saved copy. An image whose upload is gone is left out
      of a draft and refused when sending. Uploads the server made (imports, Files) are shown via
      `GET /api/uploads/{id}/content` (images only, sandboxed). At most 100 images per message,
      apart from the 25 attachments.
  - **Drafts** are saved to the account's Drafts folder (created when there is none) 4 seconds
    after the last change: `PUT /api/accounts/{id}/drafts` appends the message flagged `\Draft`
    and `\Seen`, then removes the version it replaces. Addresses still being typed are kept as
    written. Close (×) keeps the draft, asks, or deletes it, as set in Settings
    (`layout.composerClose`; a message with nothing in it just closes); Discard deletes it after
    asking; sending removes it. A draft opened
    in Drafts has **Continue editing**, which loads it back (`…/draft?mode=edit`): fields, text,
    attachments and threading as saved. While a draft is continued, each saved copy becomes the
    source of its attachments, so uploads move into it. Leaving the page with unsaved changes
    asks first.
  - Recipients are chips. What is still being typed counts too, so sending without finishing an
    entry loses nothing; invalid addresses are marked. Suggestions come from the user's
    Nextcloud address books and contact groups (see "Nextcloud integration"), then from the
    addresses of mail the app has loaded (lists, search results, unified folders).

## Keyboard shortcuts

- **Keyboard shortcuts** follow Outlook, with single keys for the ones browsers keep for
  themselves (Ctrl+N opens a window): N or Ctrl+Shift+M new message; R or Ctrl+R reply; Shift+R
  or Ctrl+Shift+R reply all; F or Ctrl+Shift+F forward; Delete; Ctrl+Q read; Ctrl+U unread;
  Insert flag; / or Alt+Q search; Esc clears the selection or closes the message; ? lists them.
  In the list, the arrow keys move between messages and Ctrl+A selects all loaded ones. They are
  off while typing and while a dialog is open.

## Header cache and sync

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
  - UIDs need not follow dates: a Gmail label numbers messages in the order they were labelled,
    and mail moved or imported into a folder gets new UIDs. Whenever the window is built or
    grows, `UID SEARCH <below the window> SENTSINCE <oldest date just cached, less a day>` finds
    older UIDs with newer mail, and the window is extended down to the lowest of them (by at
    most 500 messages). So the window holds every message at least as new as the ones it
    caches, and lists can be ordered by date.
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
- **Paging**: newest first by date (then UID), 50 per page. The cursor names the last message's
  position (`?before=<utc ticks>.<account id>.<uid>`, as in the unified folders), so mail
  arriving between page loads neither shifts nor duplicates entries. Message ids are `<UIDVALIDITY>-<UID>`,
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

## Search, quick filters and unified views

- **Search** has two scopes, chosen next to the search box in the command bar:
  - **This folder** (`GET …/folders/{id}/search?q=…&before=<uid>`) asks the mail server:
    `UID SEARCH TEXT <term> TEXT <term> …`. It matches headers and message text, finds old mail
    the cache has never seen, and pages newest first by UID (`?before=<uid>`). The
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
- **Account appearance** (`PUT /api/accounts/{id}/appearance`, apart from the account's
  settings so connections stay and `UpdatedAt` is left alone): a colour (`#rrggbb`, else one
  derived from the id), a badge of up to three characters or one of a fixed set of icon names
  (`AppearanceInput.Icons`, matched by `accountIcons.js`), and `includeInUnified`. Set from the
  Customize dialog (account menu, or right-click in the rail) and Settings → Accounts; the
  order is changed by dragging in the rail or Settings (`PUT /api/accounts/order`).
- **All accounts** (shown with two or more accounts in it, and opened first then, on the unified
  Inbox) replaces the folder tree with views across every account the user has not left out
  (`includeInUnified`; the server skips the others, the client leaves them out of the counts). `{view}` is one of:
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

## Tags

- **Tags** (`Tags/`) are the user's, shared by all accounts: a name, a color and the IMAP keyword
  they are stored as (`$` and the name, kept when the tag is renamed, so tagged messages stay
  tagged). Where an account keeps its tags is decided when it is first tagged:
  - IMAP keywords on the server (most servers: PERMANENTFLAGS allows `\*`), which Thunderbird,
    Nextcloud Mail and other programs see too. Keywords already on messages (Thunderbird's
    `$label1`…`$label5`) are offered as tags to adopt.
  - Outlook categories for Microsoft accounts signed in with the Graph permissions (see
    [Signing in with Google and Microsoft](../README.md#signing-in-with-google-and-microsoft-oauth2)): a tag is the category of the same name, with the
    nearest of Outlook's 25 colors. Changes go to Graph first; categories set in Outlook come in
    with the tag scan. Renaming a tag replaces the category (Outlook cannot rename one).
  - Kept by the app alone, for servers without keywords and Microsoft accounts without the
    Graph consent. Messages are followed by Message-ID when they move.

  The **tag index** (`tagged_messages`) holds every tagged message the app knows of, with the
  fields a list shows, so tag folders and the tag filter are read from the database and open at
  once. It is kept current by the app's own changes, folder syncs and a **tag scan** (every 10
  minutes for users with tags, soon after a tag is created, and when a tag folder is opened more
  than 5 minutes after the last one): it searches each folder for the tag keywords (skipping
  folders whose `HIGHESTMODSEQ` did not move), reads Outlook's categories, and looks for moved
  messages by Message-ID. Tag folders leave out Trash, Junk and Drafts and show a message that
  is in several folders (Gmail labels) once. Rules can add tags; the tag is set before a rule's
  move, so it travels with the message.

## Send later

- **Send later** (`Scheduling/`): `POST /api/accounts/{id}/schedule` takes what `send` takes
  plus `sendAt` (at least a minute ahead, at most a year). The message is built and checked
  right then, so its attachments are inside it (uploads expire after 24 hours, a forwarded
  original may be moved before the time comes), and the saved draft is removed as after sending.
  It waits encrypted on the volume (`scheduled/`, see [Stored credentials](security.md#stored-credentials)), with a row in
  `scheduled_messages` for the list.
  - `ScheduledSender` sends due messages, at most 4 at a time, whether or not anybody has the
    app open. It wakes when the next one is due and looks at least every 30 seconds. Sending
    sets the date to the moment it goes out and keeps the Message-ID; the copy goes to Sent and
    a reply or forward marks its original, as with `send`.
  - Each message is claimed (status `sending`, one UPDATE) before it is sent or cancelled, so
    the timer, "send now" and cancel never act on the same message twice.
  - Failures that may pass (server unreachable, login refused) are tried again after 1, 5 and
    15 minutes, then the message is `failed`. A refused recipient, sender or message fails at
    once. A connection that broke during the transfer, or a restart while sending, fails
    without retrying, since the message may have gone out. A failure is also reported in
    Nextcloud's notifications (when the notifications app is enabled).
  - `GET /api/scheduled[?accountId=]` lists them; `GET /api/scheduled/{id}` adds the
    recipients, the HTML (sanitized like received mail, nothing fetched from elsewhere) and the
    attachments' names; `PUT /api/scheduled/{id}` (`sendAt`) reschedules, a failed one too;
    `POST /api/scheduled/{id}/send-now`; `DELETE /api/scheduled/{id}` cancels and puts the
    message back in Drafts. Every change is pushed to the user's tabs (`scheduledChanged`).
  - The **Scheduled** view sits in the folder pane, across accounts in the "All accounts" pane
    and per account beside its folders, while something is scheduled (with a mark when one
    failed). The reader offers Send now (Try again now when failed), Reschedule, Edit (back to
    Drafts and into the composer) and Cancel sending (back to Drafts, after asking).
  - The UI offers presets worked out in the browser's time zone and a date and time picker;
    the server only stores the moment. Built in: tomorrow morning, after lunch, Monday morning
    (offered Friday to Sunday) and in an hour. Users can replace them with their own ("Send later
    presets" in the settings menu, or "Manage presets…" in the Send later menu): today, tomorrow
    or the next given weekday at a time, or a number of hours ahead, each optionally offered on
    some weekdays only. `GET/PUT/DELETE /api/send-later/presets` keeps them per user
    (`send_later_presets`, up to 20; `null` means the built-in ones, whose names stay translated).

## Rules

- **Rules** (`Rules/`) are kept per account and run by the app over IMAP, not as Sieve on the
  server, so they also work for Gmail and Microsoft accounts. They apply to new INBOX mail.
  - A rule has a name, conditions (all or any), actions and "stop processing more rules". The
    conditions test From, To, Cc, any recipient, Subject, a named header, size or attachments.
    Text conditions ignore case. A regular expression gets 100 ms at most.
  - The actions are move, copy, mark as read, flag, move to Trash and tag. Flags run first,
    then copies, then moves. There is no forward or auto-reply.
  - A cursor per account (UIDVALIDITY and last UID) records how far rules have run. It starts
    at UIDNEXT when the first rule is enabled. A changed UIDVALIDITY moves it to UIDNEXT again.
  - Rules run inside the INBOX sync (the IDLE watcher's or a request's), before the cache is
    updated, so mail moved away never shows in the Inbox. `RuleRunner` checks other accounts
    with enabled rules every 5 minutes.
  - An action that fails (missing folder, refused by the server) is stored on the rule and shown
    in the editor until the rule next succeeds or is edited. Each rule also counts its matches.
  - `GET`/`PUT /api/accounts/{id}/rules` reads and replaces the whole ordered list (100 rules at
    most). `POST …/rules/test` is a dry run over the newest 200 INBOX messages.
    `POST …/rules/{id}/preview` and `…/run` apply one rule to the newest 1000 messages of a
    folder, after the user has confirmed how many matched.
  - "Create rule…" in the row menu and the reader starts from the message's `List-Id`, or else
    its sender.

## IDLE watchers

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

## View state

- **Open folders**: per account, the app remembers which folders are expanded and which folder
  was open last. Switching to an account opens that folder (Inbox if it is gone), and the last
  view is restored at start-up. The user chooses where this is kept, under "Remember open
  folders":
  - *On all devices* (the default): `GET`/`PUT /api/view-state`, in the `view_settings` and
    `account_view_states` tables. Rows go with their account. Writes are debounced by about a
    second, with a local copy as the fallback.
  - *In this browser only*: local storage. Choosing this deletes the rows on the server.
- **Layout settings** (reading pane position, density, folder pane, pane sizes) are kept in the
  browser's local storage, per Nextcloud user.

## Nextcloud integration

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
- **Nextcloud Calendar** (`Calendar/`): CalDAV on `remote.php/dav/calendars/<user>/`, as the
  user. Event calendars only: subscriptions, task lists, the scheduling inbox/outbox and the
  trash bin are left out.
  - *Calendar parts*: a message lists its `text/calendar`, `application/ics` and `*.ics` parts
    with their iTIP method. The reader shows the iMIP one (with a method) before plain files.
    The card resolves the event's times with NodaTime, which knows IANA and Windows zone names.
    Outlook-style names with an offset ("(UTC+01:00) …"), a VTIMEZONE's `X-LIC-LOCATION`, or a
    VTIMEZONE without daylight saving also work. Anything else is shown as written, with the
    zone's name.
  - *Text, not a model*: calendar objects are changed as iCalendar text (`ICalText.cs`); only
    the lines the app changes are rewritten. A library round trip (Ical.Net) was tried and
    dropped: it lost DTSTART for a zone name it did not know.
  - *Answering* (Accept, Maybe, Decline): the invitation's events go into the chosen calendar
    without METHOD, with the user's PARTSTAT, merged into the stored object when it is there
    already. An updated occurrence replaces only its own copy. A declined invitation that is
    not in a calendar is not added. The REPLY (RFC 6047) goes to the organizer from the mail
    account, with a copy in Sent.
  - *Who sends the reply*: stored as is, an accepted invitation makes Nextcloud mail the
    organizer itself, "via Nextcloud" from the instance's address (seen on Nextcloud 35). So the
    object is first written with `SCHEDULE-AGENT=CLIENT` on ORGANIZER, the app sends the reply,
    and then the object is written once more without the parameter. That second write changes
    no answer, so Nextcloud sends nothing for it, and later changes made in the Calendar app
    are scheduled by Nextcloud as usual.
  - *Cancellations* delete the event, or add an EXDATE for a cancelled occurrence. The object
    is first written with `SCHEDULE-AGENT=CLIENT`, because an attendee deleting an event makes
    Nextcloud decline it to the organizer.
  - *Replies* to the user's own meetings update that attendee's PARTSTAT in the stored event.
    Nextcloud does not mail the attendees about that change.
  - *.ics files* (no method or PUBLISH): "Add to calendar" adds every event, updating events
    that are already there (by UID).
- **Nextcloud Tasks** (`Tasks/`): "Create task…" in the row menu and the reader turns a message
  into a VTODO in one of the user's task lists, over the same CalDAV home.
  - Offered only when the Tasks app is enabled for the user (OCS navigation apps, cached for 5
    minutes) and a writable task list exists (`GET /api/tasks`).
  - `POST …/messages/{id}/task` reads the message itself. The task's notes are "From:" and
    "Date:" plus up to 2000 characters of the text, and an `X-` property keeps the Message-ID.
    A due time is sent as a UTC instant, a due day as a date.
  - The toast links to the task in the Tasks app.

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
