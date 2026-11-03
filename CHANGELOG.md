# Changelog

All notable changes to Sockless Mail are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/). The External App (`sockless_mail`) and the companion
app (`socklessmail`) share version numbers, so this file covers both.

## [Unreleased]

### Added
- Packaging for the Nextcloud App Store: release archives for both apps, signed and uploaded by
  the release workflow, and store metadata in `info.xml`.

## [0.50.0] - 2026-11-02

### Added
- Encrypted mail with OpenPGP and S/MIME: read, decrypt and verify signed mail, and sign or
  encrypt from the composer. Keys can be imported, created, saved into Contacts and published to
  keys.openpgp.org. Keys sent with mail (Autocrypt or attachments) can be imported with one click.
- Desktop notifications for new mail, which can be turned off per account and per device.
- A new-mail or unread count on the app icon and in the tab title.
- Conversation view: messages grouped into threads, turned on per account.

### Changed
- The companion app is called "Sockless Mail" and can be installed as a browser app with its
  own icon.

## [0.40.0] - 2026-11-01

### Added
- Nextcloud Assistant: summaries, scam checks, translation and writing help in the composer.
- Per-account appearance: colour, badge and whether the account appears in the unified views.
- Images in the message text: paste, drop or insert them, sent as inline parts.

### Fixed
- Icon button styling in the composer, dialogs and badges.

## [0.32.2] - 2026-10-31

### Fixed
- Folder lists are ordered by date instead of UID.

## [0.32.1] - 2026-10-31

### Fixed
- The app menu entry pointing at `/apps/socklessmail/` on Nextcloud 32 to 34.

## [0.32.0] - 2026-10-31

First published release. Container images are on `ghcr.io/sockless-coding/sockless_mail`.

### Added
- Multiple IMAP/SMTP accounts per user, with password or OAuth2 sign-in (Google, Microsoft).
- Reading, searching and organising mail with a local sync cache, IMAP IDLE and push to the browser.
- Unified inbox, unified folders by role, and Unread and Flagged smart folders.
- Composer with rich text, drafts, attachments, recipient suggestions and send later.
- Tags, including Outlook categories, and server-side mail rules.
- Nextcloud Files, Contacts, Calendar and Tasks integration.
- Sender logos (BIMI), warnings about suspicious senders and deceptive links, the message
  source and `.eml` download.
- Companion app serving the mail page at `/apps/socklessmail/`.

[Unreleased]: https://github.com/sockless-coding/nextcloud-mail/compare/v0.50.0...HEAD
[0.50.0]: https://github.com/sockless-coding/nextcloud-mail/compare/v0.40.0...v0.50.0
[0.40.0]: https://github.com/sockless-coding/nextcloud-mail/compare/v0.32.2...v0.40.0
[0.32.2]: https://github.com/sockless-coding/nextcloud-mail/compare/v0.32.1...v0.32.2
[0.32.1]: https://github.com/sockless-coding/nextcloud-mail/compare/v0.32.0...v0.32.1
[0.32.0]: https://github.com/sockless-coding/nextcloud-mail/releases/tag/v0.32.0
