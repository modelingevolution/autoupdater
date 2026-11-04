#!/bin/sh
# Keeps the two newest release images of the apps package on this station and removes older ones, so
# they cannot pile up: docker pull never removes an image. Run from each release's up-X.Y.Z.sh, after the
# new image is pulled and before the old container stops, so "two" means the new one and the running one.
#
# Housekeeping only: it must never fail an update, so it always exits 0, and it never forces a removal.
# The repository is read from the package's docker-compose.yml next to this script; APPS_IMAGE_REPOSITORY overrides it.
REPOSITORY="${APPS_IMAGE_REPOSITORY:-$(sed -n 's/^[[:space:]]*image:[[:space:]]*\([^[:space:]]*\):[^:/[:space:]]*$/\1/p' "$(dirname "$0")/docker-compose.yml" 2>/dev/null | head -1)}"
if [ -z "$REPOSITORY" ]; then
    echo "prune-old-images: no image found in docker-compose.yml, nothing pruned"
    exit 0
fi
KEEP="${APPS_KEEP_IMAGES:-2}"

docker image ls "$REPOSITORY" --format '{{.Tag}}' 2>/dev/null \
    | grep -E '^[0-9]+\.[0-9]+\.[0-9]+$' \
    | sort -t. -k1,1nr -k2,2nr -k3,3nr \
    | tail -n "+$((KEEP + 1))" \
    | while read -r tag; do
        echo "prune-old-images: removing $REPOSITORY:$tag"
        docker image rm "$REPOSITORY:$tag" >/dev/null 2>&1 \
            || echo "prune-old-images: could not remove $REPOSITORY:$tag, keeping it"
    done

exit 0
