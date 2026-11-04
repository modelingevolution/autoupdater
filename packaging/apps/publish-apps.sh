#!/usr/bin/env bash
# Publishes downloadable apps to stations as one container image, from a laptop: one command, no CI.
#
#   docker login registry.example.com               # once; this script never takes a credential
#   ./publish-apps.sh --release 1.2.0 --image registry.example.com/example/apps \
#       --app example-viewer --name "Example Viewer" --icon example/example-viewer.png \
#         --download --platform windows --arch x64 \
#                    --file dist/ExampleViewer-2.1.0.msi --version 2.1.0 --guide example/windows.md \
#         --download --platform android --arch arm64 --preview \
#                    --file dist/ExampleViewer-2.1.0-preview.apk --version 2.1.0-preview --guide example/android.md
#
# Each --app starts one app; each --download after it starts one of that app's downloads, and the options after it, up
# to the next --download or --app, describe it. Apps and downloads show in this order. A download with no --file shows
# as "Not available yet". It writes manifest.json (schemaVersion 2), copies each guide and icon next to it, builds a
# multi-arch image with one layer per file, pushes it, and prints the next step: pin the release in the AutoUpdater
# package that deploys the image.
# Runs in Git Bash, WSL and Linux. Needs docker with buildx, sha256sum, stat, iconv, od. It builds with its own buildx
# builder (docker-container driver, created on first use) so that a file's layer stays the same between releases.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
IMAGE_REPO=""
PLATFORMS="linux/amd64,linux/arm64"
RELEASE=""
LOCAL_TAG=""
OVERWRITE=false
DRY_RUN=false
# The docker driver ignores rewrite-timestamp with the classic image store; a docker-container builder applies it on
# every Docker setup, for --local-tag and for the push alike.
BUILDER="apps-publish"
# Each file's mtime comes from its SHA-256: a layer's digest covers mtimes, so an unchanged file keeps its layer (a
# station does not download it again), while a changed file gets another mtime, hence another ETag and Last-Modified
# from nginx (no stale installer from a browser cache, no resumed download joining two builds).
MTIME_BASE=946684800   # 2000-01-01T00:00:00Z; plus up to 16^7 s (about 8.5 years, so at most mid-2008) from the hash:
#                        never in the future, which RFC 9110 forbids for Last-Modified
MAX_APPS=16
MAX_DOWNLOADS=8
MAX_NAME=80
MAX_LABEL=40
MAX_GUIDE_BYTES=16384
MAX_ICON_BYTES=262144
PLATFORM_SET="windows linux macos android ios"
ARCH_SET="x64 arm64 x86 arm any"

IDS=()
declare -A NAME=() ICON=()
# One entry per --download, in order; D_APP says which app it belongs to.
D_APP=() D_PLATFORM=() D_ARCH=() D_FILE=() D_VERSION=() D_GUIDE=() D_LABEL=() D_PREVIEW=()

die() { echo "ERROR: $*" >&2; exit 1; }

usage() {
    sed -n '2,18p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
    cat <<EOF

Options:
  --release X.Y.Z             The image tag and compose package version (required)
  --image REGISTRY/REPOSITORY Where to push, e.g. registry.example.com/example/apps (required unless --local-tag or --dry-run)
  --app ID                    Starts an app: lower-case letters, digits, '-'; at most 32; at most $MAX_APPS apps
    --name TEXT               Its name on the page (required): one line, at most $MAX_NAME characters
    --icon PATH.png           Its icon: a PNG, at most 256 KiB (optional; the page shows a neutral icon without)
    --download                Starts one of its downloads; at most $MAX_DOWNLOADS per app
      --platform P            Required: $PLATFORM_SET
      --arch A                Required: $ARCH_SET
      --label TEXT            Optional short variant name, at most $MAX_LABEL characters (e.g. "Kiosk devices")
      --preview               Marks it as a preview build
      --file PATH --version V Its file (together, or neither)
      --guide PATH.md         Its install guide, markdown, UTF-8, at most 16 KiB
  --platforms LIST            Default: $PLATFORMS
  --overwrite                 Replace a release tag that already exists (default: refuse)
  --local-tag TAG             Build for this machine only and load as TAG. Nothing is pushed.
  --builder NAME              The buildx builder (default: $BUILDER; created with the docker-container driver if missing)
  --dry-run                   Validate, write the manifest, print it, and stop
EOF
}

current_app() { [ "${#IDS[@]}" -gt 0 ] || die "$1 must follow an --app"; echo "${IDS[-1]}"; }
# The index of the download being described; it must belong to the app being described.
current_download() {
    local app; app="$(current_app "$1")"
    [ "${#D_APP[@]}" -gt 0 ] && [ "${D_APP[-1]}" = "$app" ] || die "$1 must follow a --download of --app $app"
    echo $(( ${#D_APP[@]} - 1 ))
}
in_set() { local x; for x in $2; do [ "$x" = "$1" ] && return 0; done; return 1; }

while [ $# -gt 0 ]; do
    case "$1" in
        --release) RELEASE="${2:?}"; shift 2 ;;
        --image) IMAGE_REPO="${2:?}"; shift 2 ;;
        --app)
            id="${2:?}"
            [[ "$id" =~ ^[a-z0-9][a-z0-9-]{0,31}$ ]] || die "--app: id not allowed (a-z, 0-9, '-', at most 32): '$id'"
            [ -z "${NAME[$id]+x}" ] || die "--app $id given twice"
            IDS+=("$id"); NAME[$id]=""; shift 2 ;;
        --name) app="$(current_app --name)"; NAME[$app]="${2?}"; shift 2 ;;
        --icon) app="$(current_app --icon)"; ICON[$app]="${2:?}"; shift 2 ;;
        --download)
            app="$(current_app --download)"
            D_APP+=("$app"); D_PLATFORM+=(""); D_ARCH+=(""); D_FILE+=(""); D_VERSION+=(""); D_GUIDE+=(""); D_LABEL+=(""); D_PREVIEW+=(false)
            shift ;;
        --platform) i="$(current_download --platform)"; D_PLATFORM[$i]="${2:?}"; shift 2 ;;
        --arch) i="$(current_download --arch)"; D_ARCH[$i]="${2:?}"; shift 2 ;;
        --label) i="$(current_download --label)"; D_LABEL[$i]="${2?}"; shift 2 ;;
        --preview) i="$(current_download --preview)"; D_PREVIEW[$i]=true; shift ;;
        --file) i="$(current_download --file)"; D_FILE[$i]="${2:?}"; shift 2 ;;
        --version) i="$(current_download --version)"; D_VERSION[$i]="${2:?}"; shift 2 ;;
        --guide) i="$(current_download --guide)"; D_GUIDE[$i]="${2:?}"; shift 2 ;;
        --platforms) PLATFORMS="${2:?}"; shift 2 ;;
        --local-tag) LOCAL_TAG="${2:?}"; shift 2 ;;
        --builder) BUILDER="${2:?}"; shift 2 ;;
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
is_png() { [ "$(head -c 8 "$1" | od -An -tx1 | tr -d ' \n')" = "89504e470d0a1a0a" ]; }

# The AutoUpdater's plain-text rule: valid UTF-8, not blank, at most $3 UTF-16 code units, no control characters
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
mkdir -p "$STAGE/files" "$STAGE/guides" "$STAGE/icons"
cp "$SCRIPT_DIR/image/Dockerfile" "$SCRIPT_DIR/image/nginx.conf" "$STAGE/"
# Windows checkouts must not put a carriage return into the nginx config.
sed -i 's/\r$//' "$STAGE/nginx.conf"

# The same mtime rule for files, guides and icons.
stamp() { touch -d "@$((MTIME_BASE + 16#$(sha256sum "$1" | cut -c1-7)))" "$1"; }

# Stages a guide or icon under $STAGE/$1, once per name: two downloads may share one guide only if it is the same file.
stage_extra() {
    local dir="$1" source="$2" name="$3" what="$4"
    if [ -e "$STAGE/$dir/$name" ]; then
        cmp -s "$source" "$STAGE/$dir/$name" || die "$what: two different files are both named $name"
        return 1
    fi
    cp "$source" "$STAGE/$dir/$name"
    chmod 0644 "$STAGE/$dir/$name"
    stamp "$STAGE/$dir/$name"
}

apps_json=""
guide_names=() icon_names=()
for id in "${IDS[@]}"; do
    check_text "--app $id: --name" "${NAME[$id]}" "$MAX_NAME"
    app_json="{\"id\":\"$id\",\"name\":$(json_string "${NAME[$id]}")"

    icon="${ICON[$id]:-}"
    if [ -n "$icon" ]; then
        [ -f "$icon" ] || die "--app $id: no such icon: $icon"
        iname="$(basename "$icon")"
        [[ "$iname" =~ ^[A-Za-z0-9][A-Za-z0-9._+~-]{0,123}\.png$ ]] || die "--app $id: icon name not allowed (a plain name ending in .png, at most 128): '$iname'"
        isize="$(stat -c %s "$icon")"
        [ "$isize" -gt 0 ] && [ "$isize" -le "$MAX_ICON_BYTES" ] || die "--app $id: icon must be 1 to $MAX_ICON_BYTES bytes: $icon"
        is_png "$icon" || die "--app $id: icon is not a PNG (only PNG is accepted): $icon"
        if stage_extra icons "$icon" "$iname" "--app $id: icon"; then icon_names+=("$iname"); fi
        app_json+=",\"icon\":\"$iname\""
    fi

    downloads_json="" count=0 keys=" "
    for i in "${!D_APP[@]}"; do
        [ "${D_APP[$i]}" = "$id" ] || continue
        count=$((count + 1))
        platform="${D_PLATFORM[$i]}" arch="${D_ARCH[$i]}" label="${D_LABEL[$i]}"
        what="--app $id: download $count"
        in_set "$platform" "$PLATFORM_SET" || die "$what: --platform must be one of: $PLATFORM_SET (got '$platform')"
        in_set "$arch" "$ARCH_SET" || die "$what: --arch must be one of: $ARCH_SET (got '$arch')"
        d="{\"platform\":\"$platform\",\"architecture\":\"$arch\""
        if [ -n "$label" ]; then
            check_text "$what: --label" "$label" "$MAX_LABEL"
            d+=",\"label\":$(json_string "$label")"
        fi
        key="$platform/$arch/$label"
        case "$keys" in *" $key "*) die "$what: another download of this app has the same platform, architecture and label" ;; esac
        keys+="$key "
        [ "${D_PREVIEW[$i]}" = false ] || d+=",\"preview\":true"

        path="${D_FILE[$i]}" version="${D_VERSION[$i]}"
        if [ -n "$path" ] || [ -n "$version" ]; then
            [ -n "$path" ] || die "$what: --version given without --file"
            [ -f "$path" ] || die "$what: no such file: $path"
            [[ "$version" =~ ^[0-9A-Za-z][0-9A-Za-z.+~_-]{0,63}$ ]] || die "$what: --version is missing or invalid: '$version'"
            name="$(basename "$path")"
            # The same rule as the nginx location and the AutoUpdater's manifest reader.
            [[ "$name" =~ ^[A-Za-z0-9][A-Za-z0-9._+~-]{0,127}$ ]] || die "$what: file name not allowed (letters, digits, ._+~-, at most 128): '$name'"
            [ ! -e "$STAGE/files/$name" ] || die "two downloads use the same file name: $name"

            cp "$path" "$STAGE/files/$name"
            chmod 0644 "$STAGE/files/$name"
            size="$(stat -c %s "$STAGE/files/$name")"
            [ "$size" -gt 0 ] || die "$what: empty file: $path"
            stamp "$STAGE/files/$name"

            d+=",\"version\":\"$version\",\"file\":\"$name\",\"size\":$size"
            printf 'COPY --link --chmod=0444 files/%s /srv/apps/files/%s\n' "$name" "$name" >> "$STAGE/Dockerfile"
        fi

        guide="${D_GUIDE[$i]}"
        if [ -n "$guide" ]; then
            [ -f "$guide" ] || die "$what: no such guide: $guide"
            gname="$(basename "$guide")"
            [[ "$gname" =~ ^[A-Za-z0-9][A-Za-z0-9._+~-]{0,124}\.md$ ]] || die "$what: guide name not allowed (a plain name ending in .md, at most 128): '$gname'"
            gsize="$(stat -c %s "$guide")"
            [ "$gsize" -gt 0 ] && [ "$gsize" -le "$MAX_GUIDE_BYTES" ] || die "$what: guide must be 1 to $MAX_GUIDE_BYTES bytes: $guide"
            is_utf8 "$guide" || die "$what: guide is not valid UTF-8: $guide"
            if stage_extra guides "$guide" "$gname" "$what: guide"; then guide_names+=("$gname"); fi
            d+=",\"guide\":\"$gname\""
        fi

        downloads_json+="${downloads_json:+,}"$'\n'"        $d}"
    done
    [ "$count" -le "$MAX_DOWNLOADS" ] || die "--app $id: at most $MAX_DOWNLOADS downloads (got $count)"

    apps_json+="${apps_json:+,}"$'\n'"    $app_json,\"downloads\":[$downloads_json"$'\n'"    ]}"
done

printf '{\n  "schemaVersion": 2,\n  "release": "%s",\n  "apps": [%s\n  ]\n}\n' "$RELEASE" "$apps_json" > "$STAGE/manifest.json"
# One COPY per guide and icon, file to file like the apps' files: copying into a directory would give the layer that
# directory's build-time mtime.
for gname in ${guide_names[@]+"${guide_names[@]}"}; do
    printf 'COPY --link --chmod=0444 guides/%s /srv/apps/guides/%s\n' "$gname" "$gname" >> "$STAGE/Dockerfile"
done
for iname in ${icon_names[@]+"${icon_names[@]}"}; do
    printf 'COPY --link --chmod=0444 icons/%s /srv/apps/icons/%s\n' "$iname" "$iname" >> "$STAGE/Dockerfile"
done
printf 'COPY --link --chmod=0444 manifest.json /srv/apps/\n' >> "$STAGE/Dockerfile"

cat "$STAGE/manifest.json"
[ "$DRY_RUN" = false ] || exit 0

cd "$STAGE"
if ! docker buildx inspect "$BUILDER" > /dev/null 2>&1; then
    docker buildx create --name "$BUILDER" --driver docker-container > /dev/null
fi
# A COPY --link layer also holds its parent directories, stamped with the build time. With rewrite-timestamp every mtime
# later than SOURCE_DATE_EPOCH is clamped to it, so a file's layer depends only on the file: when one app changes, the
# layers of the others stay the same and stations do not download them again. The files' own mtimes (from their hash,
# above) are earlier, so they stay as they are.
export SOURCE_DATE_EPOCH=$((MTIME_BASE + 16#FFFFFFF))
if [ -n "$LOCAL_TAG" ]; then
    docker buildx build --builder "$BUILDER" --quiet --output "type=docker,name=$LOCAL_TAG,rewrite-timestamp=true" . > /dev/null
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

docker buildx build --builder "$BUILDER" --platform "$PLATFORMS" --output "type=image,name=$IMAGE,push=true,rewrite-timestamp=true" .

cat <<EOF

Pushed $IMAGE
Next, pin $RELEASE in the AutoUpdater package that deploys this image (from packaging/apps/compose: ./release.sh $RELEASE).
Stations then see the new version on their Packages page.
EOF
