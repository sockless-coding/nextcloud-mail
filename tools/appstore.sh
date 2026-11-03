#!/usr/bin/env bash
# Nextcloud App Store packaging for both apps: the External App (sockless_mail) and the
# companion (socklessmail). Used by .github/workflows/release.yml; runs locally too.
#
#   tools/appstore.sh check [tag]          versions, CHANGELOG entry, store schema validation
#   tools/appstore.sh package [out-dir]    build sockless_mail.tar.gz and socklessmail.tar.gz
#   tools/appstore.sh sign <archive> <key> print the store signature of an archive
#   tools/appstore.sh upload <url> <signature>
#                                          publish a release; needs APPSTORE_TOKEN
#
# check needs xsltproc and xmllint (Debian/Ubuntu: xsltproc libxml2-utils).
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
exapp_xml="$root/appinfo/info.xml"
companion_dir="$root/companion/socklessmail"
companion_xml="$companion_dir/appinfo/info.xml"

# The store validates info.xml after this transformation, which drops elements it does not know
# (such as <routes>) and puts the rest in schema order.
store_xslt="https://raw.githubusercontent.com/nextcloud/appstore/master/nextcloudappstore/api/v1/release/pre-info.xslt"
store_xsd="https://apps.nextcloud.com/schema/apps/info.xsd"

die() { echo "appstore.sh: $*" >&2; exit 1; }

# First value of a simple element, e.g. xml_value version file.xml.
xml_value() { sed -n "s:.*<$1>\(.*\)</$1>.*:\1:p" "$2" | head -1; }

check() {
	local tag="${1:-}"
	local version image_tag companion_version
	version="$(xml_value version "$exapp_xml")"
	image_tag="$(xml_value image-tag "$exapp_xml")"
	companion_version="$(xml_value version "$companion_xml")"

	[ -n "$version" ] || die "no <version> in appinfo/info.xml"
	[ "$version" = "$image_tag" ] || die "<version> $version and <image-tag> $image_tag differ"
	[ "$version" = "$companion_version" ] \
		|| die "companion version $companion_version differs from the External App's $version"
	if [ -n "$tag" ]; then
		[ "${tag#v}" = "$version" ] || die "tag $tag does not match <version> $version"
	fi
	grep -q "^## \[$version\]" "$root/CHANGELOG.md" \
		|| die "CHANGELOG.md has no '## [$version]' section"

	command -v xsltproc >/dev/null || die "xsltproc is not installed"
	command -v xmllint >/dev/null || die "xmllint is not installed"
	local tmp
	tmp="$(mktemp -d)"
	trap 'rm -rf "$tmp"' RETURN
	curl -fsSL "$store_xslt" -o "$tmp/pre-info.xslt"
	curl -fsSL "$store_xsd" -o "$tmp/info.xsd"
	local xml
	for xml in "$exapp_xml" "$companion_xml"; do
		xsltproc "$tmp/pre-info.xslt" "$xml" > "$tmp/info.xml"
		if ! xmllint --noout --schema "$tmp/info.xsd" "$tmp/info.xml" 2> "$tmp/errors"; then
			cat "$tmp/errors" >&2
			die "${xml#"$root"/} does not pass the App Store schema"
		fi
	done
	echo "Version $version: info.xml files, image tag and CHANGELOG agree, store schema passes."
}

# Packs $1/stage/<app id> as $1/<app id>.tar.gz, without dot files.
pack() {
	local out="$1" id="$2"
	tar -czf "$out/$id.tar.gz" --owner=0 --group=0 --exclude='.*' -C "$out/stage" "$id"
	echo "$out/$id.tar.gz"
}

package() {
	local out="${1:-$root/dist}"
	mkdir -p "$out"
	out="$(cd "$out" && pwd)"
	rm -rf "$out/stage"
	mkdir -p "$out/stage/sockless_mail/appinfo"

	# The External App: AppAPI reads info.xml (and later l10n/) from this archive and pulls
	# the image it names. The code itself is in the image.
	cp "$exapp_xml" "$out/stage/sockless_mail/appinfo/info.xml"
	cp "$root/CHANGELOG.md" "$root/LICENSE" "$out/stage/sockless_mail/"
	if [ -d "$root/l10n" ]; then cp -r "$root/l10n" "$out/stage/sockless_mail/"; fi

	# The companion: a regular PHP app.
	cp -r "$companion_dir" "$out/stage/socklessmail"
	cp "$root/CHANGELOG.md" "$root/LICENSE" "$out/stage/socklessmail/"

	pack "$out" sockless_mail
	pack "$out" socklessmail
	rm -rf "$out/stage"
}

sign() {
	local archive="${1:?archive}" key="${2:?private key file}"
	openssl dgst -sha512 -sign "$key" "$archive" | openssl base64 -A
	echo
}

upload() {
	local url="${1:?download url}" signature="${2:?signature}"
	[ -n "${APPSTORE_TOKEN:-}" ] || die "APPSTORE_TOKEN is not set"
	local body status response
	body="$(printf '{"download":"%s","signature":"%s","nightly":false}' "$url" "$signature")"
	response="$(mktemp)"
	status="$(curl -sS -o "$response" -w '%{http_code}' \
		-X POST https://apps.nextcloud.com/api/v1/apps/releases \
		-H "Authorization: Token $APPSTORE_TOKEN" -H 'Content-Type: application/json' \
		-d "$body")" || status=000
	cat "$response"; echo
	rm -f "$response"
	case "$status" in
		200) echo "Release updated in the App Store: $url" ;;
		201) echo "Release published in the App Store: $url" ;;
		*) die "App Store answered HTTP $status for $url" ;;
	esac
}

cmd="${1:-}"
shift || true
case "$cmd" in
	check) check "$@" ;;
	package) package "$@" ;;
	sign) sign "$@" ;;
	upload) upload "$@" ;;
	*) sed -n '2,11p' "$0" | sed 's/^# \{0,1\}//'; exit 1 ;;
esac
