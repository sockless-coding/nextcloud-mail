# Security model

How Sockless Mail authenticates requests, keeps users apart, stores credentials and shows untrusted mail. See the [README](../README.md) for installation and [architecture.md](architecture.md) for the rest of the internals.

## Requests and users

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

## Stored credentials

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

Messages scheduled to be sent later are encrypted with the same key ring (purpose bound to the
scheduled message), as files under `scheduled/`. One that can no longer be decrypted is not sent:
it shows as failed.

## Displaying mail

Message HTML is attacker-controlled, and it is displayed inside the Nextcloud origin. There are
three layers of defence:

1. **Server-side sanitizing** (`MessageHtmlSanitizer`, based on HtmlSanitizer) removes:
   - scripts, event handlers, frames, objects and embeds
   - forms and inputs, so a mail cannot imitate a Nextcloud login inside Nextcloud
   - `base`, `meta` and `link` elements
   - `javascript:` and other unsafe URLs, and `data:` URLs except raster images

   It keeps `<style>` blocks, which are sanitized as CSS, so newsletters keep their layout. Links
   open in a new tab with `rel="noopener noreferrer"`; a link whose text shows another domain than
   it leads to is marked (see [Suspicious senders](#suspicious-senders)). Plain-text mail is converted to escaped
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

## Sender logos (BIMI)

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
204 (no logo) for an hour. A long list therefore costs one request per sender domain. The endpoint
serves the logo with the sandbox CSP and is rate-limited to 300 requests per minute per user.
Users can turn logos off in the view menu ("Sender logos").

## Suspicious senders

Senders whose domain imitates one the user knows, or was registered only days ago, get a shield
icon in the message list. The open message shows a banner that explains why. The look-alike
checks run in the app (`Senders/`) and send nothing anywhere; only the domain age is asked of
the domain's registry (see below). A domain is compared with:

- a bundled list of the brands phishing imitates most (`Senders/Data/brands.txt`: banks,
  payment services, carriers, authorities, mail and cloud providers, with Nordic ones first);
- the user's own account domains, the domains in their Nextcloud address books, and the people
  they wrote to (Sent folders in the cache) or answered.

These are the checks:

| Found | Example | Level |
|---|---|---|
| Letters from another alphabet that look the same (UTS #39 confusables) | `pаypal.com` (Cyrillic а) | danger |
| Look-alike characters within ASCII | `paypa1.com`, `rnicrosoft.com`, `vvise.com` | danger |
| One letter off (insertion, deletion, substitution, swap); names of 5 letters or more | `micorsoft.com` | warning |
| A brand's name under another ending (brands only) | `dropbox.net` | warning |
| A brand's name as a word of the domain or a subdomain | `apple-support.com`, `paypal.com.login.top` | warning |
| Letters from several alphabets in one label | `exаmple.org` | warning |
| The sender's name shows another domain | `"service@paypal.com" <x@example.net>` | warning |
| Your mail server says the message failed DMARC (open message only) | `dmarc=fail` | danger |
| Replies go to a look-alike (open message only) | Reply-To `billing@paypa1.com` | warning |
| The domain was registered less than 30 days ago | (RDAP registration date) | warning |
| … less than 7 days ago | | danger |
| A link shows a domain the user knows but leads elsewhere (open message only) | `https://www.paypal.com` → `paypal.com.evil.top` | warning |
| … to a look-alike of it | `https://www.paypal.com` → `paypa1.com` | danger |

A different Reply-To domain is mentioned only along with another reason, because newsletters
and ticket systems use one all the time. A domain the user knows, or any of its subdomains,
is never a look-alike. Brand names that are also common words (`~orange.*` in the list) only
count as look-alikes. A domain that merely contains them, such as `orange-county.org`, does not.
The app runs with invariant globalization, where `string.Normalize` does not decompose.
Unicode's confusables and canonical decompositions therefore ship as data files in
`Senders/Data`. To update them, run `dev/unicode-data.sh`.

"Trust this domain" on a warning stops warnings for that registered domain and its subdomains.
It also adds the domain to the domains look-alikes are compared with. The view menu lists the
trusted domains ("Trusted domains") and turns warnings off ("Warn about suspicious senders").
Each user's known domains are kept in memory for 5 minutes (30 seconds when the address
books could not be read). The list asks `POST /api/sender-checks` once per sender for each
session.

**Domain age.** For a sender whose registered domain is none of the known or trusted ones, the app
asks the domain's registry over RDAP when it was registered. The registry is found through IANA's
bootstrap file (`https://data.iana.org/rdap/dns.json`, fetched once a week). Lookups run in the
background, at most 4 at a time and 500 queued, so a check never waits for a registry. The answer
of `POST /api/sender-checks` lists the senders whose age is still being looked up under `pending`,
and the browser asks about those again after 3, 10 and 30 seconds. The open message uses what is
known when it is opened. Answers are shared by all users in the `domain_ages` table: a date for
30 days, "unknown" (no RDAP server for the ending, domain not found, no registration date) for a
day, a failure (including HTTP 429) for an hour. An unknown age never causes a warning. The
registries see which sender domains the instance receives mail from (not who receives it). An
admin can turn the lookups off under **Administration** → **Suspicious senders** ("Warn about
newly registered sender domains", on by default; `GET`/`PUT /api/admin/settings`). Requests go
through the same address policy as BIMI and image proxy fetches.

**Deceptive links.** While sanitizing, the app compares each web link's visible text with its
target (`Senders/LinkText.cs`). Only text that is itself an address counts: a web address, a
domain name with `www.`, a usual ending (`.com`, `.net`, ..., any country) or an e-mail address.
"Log in here" or a sentence that mentions a domain do not count. A link that stays on the shown
registered domain or one of its subdomains is fine. Any other target is a mismatch, including a
redirect service, an IP address, or `https://paypal.com@evil.top`. Mismatched links get
`data-sockless-link-shows`/`-leads` attributes (the sanitizer strips every `data-*` attribute of
the message itself, so a message cannot set them). The reader marks such links with ⚠ and a
tooltip, and clicking one, middle click included, asks first and shows the whole address. The
banner mentions a mismatch only when the shown domain is one the user knows (brand list, own
accounts, contacts, correspondents) and the target is not known or trusted, at most 3 per message.
Newsletters routinely send "shop.example" through a click tracker, so other mismatches only get
the question on click.

## Connections to mail servers

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
