#!/bin/sh
# Hashes every file against SHA256SUMS before nginx starts. Only a file whose bytes match its manifest entry gets an
# ok marker, and nginx serves nothing else (it answers 503), so the check fails closed. Files that fail are listed in
# /verification.json, which the AutoUpdater's Apps page reads.
set -eu

ROOT=/srv/apps
SUMS="$ROOT/SHA256SUMS"
STATE=/tmp/apps-state

# Start from nothing: without the compose tmpfs, /tmp survives a restart, and a marker from the last start must not vouch
# for a file that has changed since.
rm -rf "$STATE"
mkdir -p "$STATE/ok"

# The names come from the image and match the publish script's rule ([A-Za-z0-9._+~-], no quote or backslash), so
# joining them into JSON as strings is safe. A malformed document would also fail closed: the Apps page then treats
# no file as verified.
blocked=""
block() {
    blocked="$blocked${blocked:+,}\"$1\""
    echo "verify-files: BLOCKED $1: $2" >&2
}

expected_sum() {
    [ -f "$SUMS" ] || return 0
    awk -v name="$1" '$2 == name { print $1 }' "$SUMS"
}

cd "$ROOT/files"
for name in * .[!.]* ..?*; do
    [ -e "$name" ] || [ -L "$name" ] || continue
    if [ -L "$name" ] || [ ! -f "$name" ]; then
        block "$name" "not a regular file"
        continue
    fi
    expected="$(expected_sum "$name")"
    if [ -z "$expected" ]; then
        block "$name" "not listed in the manifest"
        continue
    fi
    actual="$(sha256sum "$name" | cut -d' ' -f1)"
    if [ "$actual" = "$expected" ]; then
        : > "$STATE/ok/$name"
        echo "verify-files: ok $name"
    else
        block "$name" "SHA-256 mismatch"
    fi
done

# A file the manifest promises but the image does not hold.
if [ -f "$SUMS" ]; then
    while read -r _ name; do
        [ -n "$name" ] || continue
        [ -f "$name" ] || block "$name" "listed in the manifest but missing"
    done < "$SUMS"
else
    echo "verify-files: $SUMS is missing, so no file is trusted" >&2
fi

if [ -f "$SUMS" ] && [ -z "$blocked" ]; then state=verified; else state=failed; fi
printf '{"state":"%s","blocked":[%s]}\n' "$state" "$blocked" > "$STATE/verification.json"

# -e: the compiled-in error log path is not writable on a read-only root; this avoids nginx's start-up alert about it.
exec nginx -e /dev/stderr -g 'daemon off;'
