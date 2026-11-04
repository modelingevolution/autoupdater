#!/bin/sh
# Hashes every file against SHA256SUMS before nginx starts. A file that does not match, or that the
# manifest does not list, is blocked (nginx answers 503 for it); the others stay available.
# The result is published as /verification.json, which the AutoUpdater's Apps page reads.
set -eu

ROOT=/srv/apps
SUMS="$ROOT/SHA256SUMS"
STATE=/tmp/apps-state

mkdir -p "$STATE/blocked"

blocked=""
block() {
    : > "$STATE/blocked/$1"
    blocked="$blocked${blocked:+,}\"$1\""
    echo "verify-files: BLOCKED $1: $2" >&2
}

expected_sum() {
    [ -f "$SUMS" ] || return 0
    awk -v name="$1" '$2 == name { print $1 }' "$SUMS"
}

cd "$ROOT/files"
for name in *; do
    [ -f "$name" ] || continue
    expected="$(expected_sum "$name")"
    if [ -z "$expected" ]; then
        block "$name" "not listed in the manifest"
        continue
    fi
    actual="$(sha256sum "$name" | cut -d' ' -f1)"
    if [ "$actual" = "$expected" ]; then
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

exec nginx -g 'daemon off;'
