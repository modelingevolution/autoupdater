#!/usr/bin/env bash
# Publishes the RocketWelder Player builds to stations, from a laptop: one command, no CI. This is the upload path for the
# Windows installer (a WiX .msi), which is built on the owner's laptop, and for the Android preview APK and the Linux .deb.
#
#   docker login docker.modelingevolution.com        # once; this script never takes a credential
#   ./publish-apps.sh --release 1.2.0 \
#       --windows dist/RocketWelderPlayer-2.1.0.msi --windows-version 2.1.0 \
#       --android dist/RocketWelderPlayer-2.1.0-arm64-v8a.apk --android-version 2.1.0 \
#       --linux   dist/native-player_2.1.0_arm64.deb --linux-version 2.1.0
#
# It writes manifest.json (version, size, SHA-256) and SHA256SUMS, builds a multi-arch image with one
# layer per file, pushes it, and prints the next step: pin the release in the compose package.
# Runs in Git Bash, WSL and Linux. Needs docker with buildx, sha256sum, stat. Pushing more than one platform needs a
# builder that can: Docker Desktop's containerd image store, or `docker buildx create --use --driver docker-container`.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REGISTRY="docker.modelingevolution.com"
REPOSITORY="rocketwelder/rocketwelder-apps"
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

declare -A FILE=() VERSION=()
PLATFORM_NAMES=(windows linux android)

die() { echo "ERROR: $*" >&2; exit 1; }

usage() {
    sed -n '2,14p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
    cat <<EOF

Options:
  --release X.Y.Z             The image tag and compose package version (required)
  --windows FILE              --windows-version V
  --linux FILE                --linux-version V
  --android FILE              --android-version V
                              Give at least one app. An app left out shows as "Not available yet".
  --registry HOST[:PORT]      Default: $REGISTRY
  --platforms LIST            Default: $PLATFORMS
  --overwrite                 Replace a release tag that already exists (default: refuse)
  --local-tag TAG             Build for this machine only and load as TAG. Nothing is pushed.
  --dry-run                   Validate, write the manifest, print it, and stop
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --release) RELEASE="${2:?}"; shift 2 ;;
        --windows|--linux|--android) FILE["${1#--}"]="${2:?}"; shift 2 ;;
        --windows-version|--linux-version|--android-version)
            key="${1#--}"; VERSION["${key%-version}"]="${2:?}"; shift 2 ;;
        --registry) REGISTRY="${2:?}"; shift 2 ;;
        --platforms) PLATFORMS="${2:?}"; shift 2 ;;
        --local-tag) LOCAL_TAG="${2:?}"; shift 2 ;;
        --overwrite) OVERWRITE=true; shift ;;
        --dry-run) DRY_RUN=true; shift ;;
        -h|--help) usage; exit 0 ;;
        *) usage >&2; die "unknown option: $1" ;;
    esac
done

[[ "$RELEASE" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "--release must be X.Y.Z (got '$RELEASE')"
[ "${#FILE[@]}" -gt 0 ] || die "give at least one of --windows, --linux, --android"

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
mkdir -p "$STAGE/files"
cp "$SCRIPT_DIR/image/Dockerfile" "$SCRIPT_DIR/image/nginx.conf" "$SCRIPT_DIR/image/verify-files.sh" "$STAGE/"
# Windows checkouts must not put a carriage return into a script the container runs.
sed -i 's/\r$//' "$STAGE/verify-files.sh" "$STAGE/nginx.conf"

entries=""
: > "$STAGE/SHA256SUMS"
for platform in "${PLATFORM_NAMES[@]}"; do
    path="${FILE[$platform]:-}"
    if [ -z "$path" ]; then
        [ -z "${VERSION[$platform]:-}" ] || die "--$platform-version given without --$platform"
        continue
    fi
    [ -f "$path" ] || die "$platform: no such file: $path"
    version="${VERSION[$platform]:-}"
    [[ "$version" =~ ^[0-9A-Za-z][0-9A-Za-z.+~_-]{0,63}$ ]] || die "$platform: --$platform-version is missing or invalid: '$version'"
    name="$(basename "$path")"
    # The same rule as the nginx location and the AutoUpdater's manifest reader.
    [[ "$name" =~ ^[A-Za-z0-9][A-Za-z0-9._+~-]{0,127}$ ]] || die "$platform: file name not allowed (letters, digits, ._+~-, at most 128): '$name'"
    [ ! -e "$STAGE/files/$name" ] || die "two apps use the same file name: $name"

    cp "$path" "$STAGE/files/$name"
    chmod 0644 "$STAGE/files/$name"

    size="$(stat -c %s "$STAGE/files/$name")"
    [ "$size" -gt 0 ] || die "$platform: empty file: $path"
    sha="$(sha256sum "$STAGE/files/$name" | cut -d' ' -f1)"
    touch -d "@$((MTIME_BASE + 16#${sha:0:7}))" "$STAGE/files/$name"

    printf '%s  %s\n' "$sha" "$name" >> "$STAGE/SHA256SUMS"
    entries+="${entries:+,}"$'\n'"    {\"platform\":\"$platform\",\"version\":\"$version\",\"file\":\"$name\",\"size\":$size,\"sha256\":\"$sha\"}"
    printf 'COPY --link --chmod=0444 files/%s /srv/apps/files/%s\n' "$name" "$name" >> "$STAGE/Dockerfile"
done

printf '{\n  "schemaVersion": 1,\n  "release": "%s",\n  "apps": [%s\n  ]\n}\n' "$RELEASE" "$entries" > "$STAGE/manifest.json"
printf 'COPY --link --chmod=0444 manifest.json SHA256SUMS /srv/apps/\n' >> "$STAGE/Dockerfile"

cat "$STAGE/manifest.json"
[ "$DRY_RUN" = false ] || exit 0

cd "$STAGE"
if [ -n "$LOCAL_TAG" ]; then
    docker buildx build --quiet --load --tag "$LOCAL_TAG" . > /dev/null
    echo "Built $LOCAL_TAG for this machine. Nothing was pushed."
    exit 0
fi

IMAGE="$REGISTRY/$REPOSITORY:$RELEASE"
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

docker buildx build --platform "$PLATFORMS" --tag "$IMAGE" --push .

cat <<EOF

Pushed $IMAGE
Next, in the rocketwelder-apps-compose repo:  ./release.sh $RELEASE
Stations then see the new version on their Packages page.
EOF
