#!/usr/bin/env bash
# Publishes downloadable apps to stations as one container image, from a laptop: one command, no CI.
#
#   docker login registry.example.com               # once; this script never takes a credential
#   ./publish-apps.sh --release 1.2.0 --image registry.example.com/example/apps \
#       --app windows --title "Example Viewer for Windows" \
#             --file dist/ExampleViewer-2.1.0.msi --version 2.1.0 --guide guides/windows.md \
#       --app android --title "Example Viewer for Android" --guide guides/android.md
#
# Each --app starts one app; the options after it, up to the next --app, describe it. The apps show in this order.
# An app with no --file shows as "Not available yet". It writes manifest.json (schemaVersion 2), copies each guide next
# to it, builds a multi-arch image with one layer per file, pushes it, and prints the next step: pin the release in the
# AutoUpdater package that deploys the image.
# Runs in Git Bash, WSL and Linux. Needs docker with buildx, sha256sum, stat, iconv. Pushing more than one platform needs
# a builder that can: Docker Desktop's containerd image store, or `docker buildx create --use --driver docker-container`.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
IMAGE_REPO=""
PLATFORMS="linux/amd64,linux/arm64"
RELEASE=""
LOCAL_TAG=""
OVERWRITE=false
DRY_RUN=false
# Each file's mtime comes from its SHA-256: a layer's digest covers mtimes, so an unchanged file keeps its layer (a
# station does not download it again), while a changed file gets another mtime, hence another ETag and Last-Modified
# from nginx (no stale installer from a browser cache, no resumed download joining two builds).
MTIME_BASE=946684800   # 2000-01-01T00:00:00Z; plus up to 16^7 s (about 8.5 years, so at most mid-2008) from the hash:
#                        never in the future, which RFC 9110 forbids for Last-Modified
MAX_APPS=16
MAX_TITLE=80
MAX_GUIDE_BYTES=65536

IDS=()
declare -A TITLE=() FILE=() VERSION=() GUIDE=()

die() { echo "ERROR: $*" >&2; exit 1; }

usage() {
    sed -n '2,15p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
    cat <<EOF

Options:
  --release X.Y.Z             The image tag and compose package version (required)
  --image REGISTRY/REPOSITORY Where to push, e.g. registry.example.com/example/apps (required unless --local-tag or --dry-run)
  --app ID                    Starts an app: lower-case letters, digits, '-'; at most 32; at most $MAX_APPS apps
    --title TEXT              Its title on the page (required): one line, at most $MAX_TITLE characters
    --file PATH --version V   Its download (together, or neither)
    --guide PATH.md           Its install guide, markdown, UTF-8, at most 64 KiB
  --platforms LIST            Default: $PLATFORMS
  --overwrite                 Replace a release tag that already exists (default: refuse)
  --local-tag TAG             Build for this machine only and load as TAG. Nothing is pushed.
  --dry-run                   Validate, write the manifest, print it, and stop
EOF
}

current() { [ "${#IDS[@]}" -gt 0 ] || die "$1 must follow an --app"; echo "${IDS[-1]}"; }

while [ $# -gt 0 ]; do
    case "$1" in
        --release) RELEASE="${2:?}"; shift 2 ;;
        --image) IMAGE_REPO="${2:?}"; shift 2 ;;
        --app)
            id="${2:?}"
            [[ "$id" =~ ^[a-z0-9][a-z0-9-]{0,31}$ ]] || die "--app: id not allowed (a-z, 0-9, '-', at most 32): '$id'"
            [ -z "${TITLE[$id]+x}" ] || die "--app $id given twice"
            IDS+=("$id"); TITLE[$id]=""; shift 2 ;;
        --title) app="$(current --title)"; TITLE[$app]="${2?}"; shift 2 ;;
        --file) app="$(current --file)"; FILE[$app]="${2:?}"; shift 2 ;;
        --version) app="$(current --version)"; VERSION[$app]="${2:?}"; shift 2 ;;
        --guide) app="$(current --guide)"; GUIDE[$app]="${2:?}"; shift 2 ;;
        --platforms) PLATFORMS="${2:?}"; shift 2 ;;
        --local-tag) LOCAL_TAG="${2:?}"; shift 2 ;;
        --overwrite) OVERWRITE=true; shift ;;
        --dry-run) DRY_RUN=true; shift ;;
        -h|--help) usage; exit 0 ;;
        *) usage >&2; die "unknown option: $1" ;;
    esac
done

[[ "$RELEASE" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "--release must be X.Y.Z (got '$RELEASE')"
[ "${#IDS[@]}" -gt 0 ] || die "give at least one --app"
[ "${#IDS[@]}" -le "$MAX_APPS" ] || die "at most $MAX_APPS apps (got ${#IDS[@]})"
if [ -z "$LOCAL_TAG" ] && [ "$DRY_RUN" = false ]; then
    [[ "$IMAGE_REPO" =~ ^[a-z0-9.:-]+(/[a-z0-9._-]+)+$ ]] || die "--image REGISTRY/REPOSITORY is required, without a tag (got '$IMAGE_REPO')"
fi

is_utf8() { iconv -f UTF-8 -t UTF-8 < "$1" > /dev/null 2>&1; }

# The AutoUpdater's plain-text rule: valid UTF-8, not blank, at most $2 UTF-16 code units, no control characters
# (C0, DEL, C1), no U+2028/2029, no bidirectional embeddings, overrides (U+202A-202E) or isolates (U+2066-2069).
check_text() {
    local what="$1" text="$2" max="$3" units
    is_utf8 <(printf '%s' "$text") || die "$what is not valid UTF-8"
    [[ "$text" =~ [^[:space:]] ]] || die "$what is blank"
    local LC_ALL=C
    case "$text" in
        *[$'\001'-$'\037'$'\177']*|*$'\xc2'[$'\x80'-$'\x9f']*|*$'\xe2\x80'[$'\xa8'-$'\xae']*|*$'\xe2\x81'[$'\xa6'-$'\xa9']*)
            die "$what holds a control or layout character" ;;
    esac
    # Code points, plus one for each that needs a surrogate pair in UTF-16 (4-byte sequences).
    units=$(( $(printf '%s' "$text" | tr -d '\200-\277' | wc -c) + $(printf '%s' "$text" | tr -cd '\360-\367' | wc -c) ))
    [ "$units" -le "$max" ] || die "$what is longer than $max characters"
}

json_string() {
    local s="${1//\\/\\\\}"
    printf '"%s"' "${s//\"/\\\"}"
}

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
mkdir -p "$STAGE/files" "$STAGE/guides"
cp "$SCRIPT_DIR/image/Dockerfile" "$SCRIPT_DIR/image/nginx.conf" "$STAGE/"
# Windows checkouts must not put a carriage return into the nginx config.
sed -i 's/\r$//' "$STAGE/nginx.conf"

# The same mtime rule for files and guides.
stamp() { touch -d "@$((MTIME_BASE + 16#$(sha256sum "$1" | cut -c1-7)))" "$1"; }

entries=""
guide_names=()
for id in "${IDS[@]}"; do
    check_text "--app $id: --title" "${TITLE[$id]}" "$MAX_TITLE"
    entry="{\"id\":\"$id\",\"title\":$(json_string "${TITLE[$id]}")"

    path="${FILE[$id]:-}"
    version="${VERSION[$id]:-}"
    if [ -n "$path" ] || [ -n "$version" ]; then
        [ -n "$path" ] || die "--app $id: --version given without --file"
        [ -f "$path" ] || die "--app $id: no such file: $path"
        [[ "$version" =~ ^[0-9A-Za-z][0-9A-Za-z.+~_-]{0,63}$ ]] || die "--app $id: --version is missing or invalid: '$version'"
        name="$(basename "$path")"
        # The same rule as the nginx location and the AutoUpdater's manifest reader.
        [[ "$name" =~ ^[A-Za-z0-9][A-Za-z0-9._+~-]{0,127}$ ]] || die "--app $id: file name not allowed (letters, digits, ._+~-, at most 128): '$name'"
        [ ! -e "$STAGE/files/$name" ] || die "two apps use the same file name: $name"

        cp "$path" "$STAGE/files/$name"
        chmod 0644 "$STAGE/files/$name"
        size="$(stat -c %s "$STAGE/files/$name")"
        [ "$size" -gt 0 ] || die "--app $id: empty file: $path"
        stamp "$STAGE/files/$name"

        entry+=",\"version\":\"$version\",\"file\":\"$name\",\"size\":$size"
        printf 'COPY --link --chmod=0444 files/%s /srv/apps/files/%s\n' "$name" "$name" >> "$STAGE/Dockerfile"
    fi

    guide="${GUIDE[$id]:-}"
    if [ -n "$guide" ]; then
        [ -f "$guide" ] || die "--app $id: no such guide: $guide"
        gname="$(basename "$guide")"
        [[ "$gname" =~ ^[A-Za-z0-9][A-Za-z0-9._+~-]{0,124}\.md$ ]] || die "--app $id: guide name not allowed (a plain name ending in .md, at most 128): '$gname'"
        [ ! -e "$STAGE/guides/$gname" ] || die "two apps use the same guide name: $gname"
        gsize="$(stat -c %s "$guide")"
        [ "$gsize" -gt 0 ] && [ "$gsize" -le "$MAX_GUIDE_BYTES" ] || die "--app $id: guide must be 1 to $MAX_GUIDE_BYTES bytes: $guide"
        is_utf8 "$guide" || die "--app $id: guide is not valid UTF-8: $guide"

        cp "$guide" "$STAGE/guides/$gname"
        chmod 0644 "$STAGE/guides/$gname"
        stamp "$STAGE/guides/$gname"
        guide_names+=("$gname")
        entry+=",\"guide\":\"$gname\""
    fi

    entries+="${entries:+,}"$'\n'"    $entry}"
done

printf '{\n  "schemaVersion": 2,\n  "release": "%s",\n  "apps": [%s\n  ]\n}\n' "$RELEASE" "$entries" > "$STAGE/manifest.json"
# One COPY per guide, file to file like the apps' files: copying into a directory would give the layer that directory's
# build-time mtime, so an unchanged guide would not keep its layer.
for gname in ${guide_names[@]+"${guide_names[@]}"}; do
    printf 'COPY --link --chmod=0444 guides/%s /srv/apps/guides/%s\n' "$gname" "$gname" >> "$STAGE/Dockerfile"
done
printf 'COPY --link --chmod=0444 manifest.json /srv/apps/\n' >> "$STAGE/Dockerfile"

cat "$STAGE/manifest.json"
[ "$DRY_RUN" = false ] || exit 0

cd "$STAGE"
# Every timestamp later than the latest derived mtime is clamped to it: a COPY --link layer also holds its parent
# directories, stamped with the build time, so without this a file's layer would change whenever a layer before it is
# rebuilt, and stations would download unchanged apps again. Derived mtimes are never later, so they stay as they are.
export SOURCE_DATE_EPOCH=$((MTIME_BASE + 16#FFFFFFF))
if [ -n "$LOCAL_TAG" ]; then
    docker buildx build --quiet --output "type=docker,name=$LOCAL_TAG,rewrite-timestamp=true" . > /dev/null
    echo "Built $LOCAL_TAG for this machine. Nothing was pushed."
    exit 0
fi

IMAGE="$IMAGE_REPO:$RELEASE"
if [ "$OVERWRITE" = false ]; then
    # Only a clear "not found" lets the push go ahead. Any other failure (login, network, an old CLI) stops here, because
    # treating it as "absent" would overwrite a release that stations may already have.
    if inspect="$(docker buildx imagetools inspect "$IMAGE" 2>&1)"; then
        die "$IMAGE already exists. Use a new --release (a station that has it would not see the change), or --overwrite."
    fi
    # A refused login wins over "not found": some registries answer an unauthorised lookup with both.
    if grep -qiE 'unauthori|denied|forbidden|authenticat' <<< "$inspect" \
        || ! grep -qiE 'not found|manifest unknown|name unknown' <<< "$inspect"; then
        echo "$inspect" >&2
        die "could not check whether $IMAGE exists (above). Fix that and run again; nothing was pushed."
    fi
fi

docker buildx build --platform "$PLATFORMS" --output "type=image,name=$IMAGE,push=true,rewrite-timestamp=true" .

cat <<EOF

Pushed $IMAGE
Next, pin $RELEASE in the AutoUpdater package that deploys this image (from packaging/apps/compose: ./release.sh $RELEASE).
Stations then see the new version on their Packages page.
EOF
