#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
#
# Regenerates the Unicode tables behind the suspicious-sender checks (src/SocklessMail.Api/Senders/Data)
# from unicode.org: confusables (UTS #39) and canonical decompositions (UnicodeData.txt). The app runs
# with invariant globalization, where string.Normalize does not decompose, so it carries its own table.
# Usage: dev/unicode-data.sh [version], e.g. 18.0.0 (default: latest).
set -euo pipefail

VERSION="${1:-latest}"
OUT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/src/SocklessMail.Api/Senders/Data"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
mkdir -p "$OUT"

if [[ "$VERSION" == latest ]]; then
	SECURITY=https://www.unicode.org/Public/security/latest
	UCD=https://www.unicode.org/Public/UCD/latest/ucd
else
	SECURITY="https://www.unicode.org/Public/security/$VERSION"
	UCD="https://www.unicode.org/Public/$VERSION/ucd"
fi

curl -sSfL "$SECURITY/confusables.txt" -o "$TMP/confusables.txt"
curl -sSfL "$UCD/UnicodeData.txt" -o "$TMP/UnicodeData.txt"

# Header comments (copyright and terms, which must travel with the data), then "source;target" in hex.
{
	sed -n '1,/^$/p' "$TMP/confusables.txt" | tr -d '\r' | sed 's/^\xEF\xBB\xBF//' | grep '^#'
	tr -d '\r' < "$TMP/confusables.txt" | awk -F';' '/^[0-9A-F]/ { gsub(/^[ \t]+|[ \t]+$/, "", $1); gsub(/^[ \t]+|[ \t]+$/, "", $2); print $1 ";" $2 }'
} > "$OUT/confusables.txt"

# Canonical decompositions only (field 6 without a <tag>), "code;decomposition" in hex.
{
	echo "# Canonical decompositions from UnicodeData.txt ($UCD)."
	echo "# © Unicode®, Inc. For terms of use and license, see https://www.unicode.org/terms_of_use.html"
	tr -d '\r' < "$TMP/UnicodeData.txt" | awk -F';' '$6 != "" && $6 !~ /^</ { print $1 ";" $6 }'
} > "$OUT/decompositions.txt"

wc -l "$OUT/confusables.txt" "$OUT/decompositions.txt"
