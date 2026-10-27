#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
#
# Fills the dev GreenMail mailbox of alice@example.test (or $1) over SMTP: 60 numbered
# messages and an HTML newsletter with a script, a remote tracking image, an inline image and
# a PDF attachment, to exercise paging, sanitizing, remote-image blocking and downloads.
set -euo pipefail

TO="${1:-alice@example.test}"
SMTP="smtp://127.0.0.1:3025"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

send() {
    # Via stdin: no file path for curl to misread on Windows (dev.sh disables path conversion).
    curl -s --fail --url "$SMTP" --mail-from "sender@example.test" --mail-rcpt "$TO" --upload-file - < "$1"
}

for i in $(seq -w 1 60); do
    cat > "$TMP/m.eml" <<EOF
From: Sender $i <sender$i@example.test>
To: $TO
Subject: Numbered message $i
Date: $(date -R)
Message-ID: <numbered-$i-$RANDOM@example.test>
MIME-Version: 1.0
Content-Type: text/plain; charset=utf-8

This is message $i. Visit https://example.com/$i for more.
EOF
    send "$TMP/m.eml"
done

PNG="iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAMElEQVR42mNk+M9Qz0AEYBxVSF+FjIyM/0kxgJGRkZGkGMDISKKCUYVDTSHRCgEAvp0QEZ9hWJQAAAAASUVORK5CYII="
PDF="$(printf '%%PDF-1.4 Sockless Mail test attachment\n' | base64)"
cat > "$TMP/news.eml" <<EOF
From: Sockless News <news@example.test>
To: $TO
Subject: Weekly newsletter with images
Date: $(date -R)
Message-ID: <newsletter-$RANDOM@example.test>
MIME-Version: 1.0
Content-Type: multipart/mixed; boundary="mixed"

--mixed
Content-Type: multipart/related; boundary="related"

--related
Content-Type: text/html; charset=utf-8

<html><head><style>.title{color:#0082c9}</style></head><body>
<h1 class="title">Weekly newsletter</h1>
<script>alert('this must never run')</script>
<p onclick="alert('nor this')">Inline logo: <img src="cid:logo@sockless" alt="logo" width="16" height="16"></p>
<p>Tracking pixel: <img src="https://picsum.photos/seed/sockless/200/80" alt="remote image"></p>
<p><a href="https://example.com/article">Read the article</a></p>
</body></html>
--related
Content-Type: image/png; name="logo.png"
Content-Transfer-Encoding: base64
Content-ID: <logo@sockless>
Content-Disposition: inline; filename="logo.png"

$PNG
--related--
--mixed
Content-Type: application/pdf; name="report.pdf"
Content-Transfer-Encoding: base64
Content-Disposition: attachment; filename="report.pdf"

$PDF
--mixed--
EOF
send "$TMP/news.eml"
echo "Sent 61 messages to $TO"
