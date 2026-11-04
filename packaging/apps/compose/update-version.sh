#!/bin/bash

# RocketWelder Apps Compose — version pin script.
# `set X.Y.Z` writes rocketwelder-apps.version and seds the pinned image tag across the base +
# arch overrides. `list` queries the Harbor API for the available :X.Y.Z release tags.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

# Harbor registry (the multi-arch image the device pulls).
REGISTRY="docker.modelingevolution.com"
IMAGE_NAME="rocketwelder/rocketwelder-apps"   # <project>/<repository> on Harbor
HARBOR_PROJECT="rocketwelder"
HARBOR_REPO="rocketwelder-apps"
HARBOR_API="https://$REGISTRY/api/v2.0"
# Optional Harbor API credentials (a pull robot) for the private project. Export before
# running `list`:
#   HARBOR_USERNAME='robot$rocketwelder+deploy-pull' HARBOR_PASSWORD='<secret>'

VERSION_FILE="$SCRIPT_DIR/rocketwelder-apps.version"
COMPOSE_FILE="$SCRIPT_DIR/docker-compose.yml"
ARCH_FILES=("$SCRIPT_DIR/docker-compose.x64.yml" "$SCRIPT_DIR/docker-compose.arm64.yml")
FORMAT="text"

log_info()  { if [ "$FORMAT" = "json" ]; then echo -e "${GREEN}[INFO]${NC} $1" >&2;  else echo -e "${GREEN}[INFO]${NC} $1";  fi }
log_error() { if [ "$FORMAT" = "json" ]; then echo -e "${RED}[ERROR]${NC} $1" >&2; else echo -e "${RED}[ERROR]${NC} $1"; fi }
log_warn()  { if [ "$FORMAT" = "json" ]; then echo -e "${YELLOW}[WARN]${NC} $1" >&2; else echo -e "${YELLOW}[WARN]${NC} $1"; fi }

show_help() {
    cat << EOF
RocketWelder Apps Compose — version pin script

USAGE:
    ./update-version.sh <command> [options]

COMMANDS:
    check                   Show the currently pinned version
    set <version>           Pin a specific version (e.g., 0.2.0): writes the version
                            file and seds the image tag across base + arch overrides
    list                    List available :X.Y.Z release tags from Harbor
    help                    Show this help message

OPTIONS:
    --format=json           Output the result as JSON (for scripting)

ENVIRONMENT (for 'list' on the private project):
    HARBOR_USERNAME         Harbor robot/user (e.g. 'robot$rocketwelder+deploy-pull')
    HARBOR_PASSWORD         Harbor robot/user secret

JSON OUTPUT FORMAT:
    {"success": true, "version": "0.2.0"}
    {"success": false, "error": "error message"}
EOF
}

get_current_version() {
    if [ -f "$VERSION_FILE" ]; then
        tr -d '[:space:]' < "$VERSION_FILE"
    else
        { grep -E "image:.*$IMAGE_NAME:" "$COMPOSE_FILE" || true; } | head -1 | sed "s|.*$IMAGE_NAME:\([^[:space:]]*\).*|\1|"
    fi
}

# Fetch the semver release tags for the image from the Harbor API.
fetch_versions() {
    # The credentials go to curl as a config on stdin, never on its command line, where any user's ps sees them.
    # (stdin rather than <(...): a Windows curl cannot open a /dev/fd path.)
    local url="$HARBOR_API/projects/$HARBOR_PROJECT/repositories/$HARBOR_REPO/artifacts?page_size=100&with_tag=true"
    local body
    if [ -n "${HARBOR_USERNAME:-}" ] && [ -n "${HARBOR_PASSWORD:-}" ]; then
        body=$(printf 'user = "%s:%s"\n' "$HARBOR_USERNAME" "$HARBOR_PASSWORD" | curl -fsSL -K - "$url")
    else
        body=$(curl -fsSL "$url")
    fi
    # No release tag yet is an empty list, not a failure (grep exits 1 on no match, which pipefail would turn into one).
    jq -r '.[].tags[]?.name' <<< "$body" | { grep -E '^[0-9]+\.[0-9]+\.[0-9]+$' || true; }
}

check_registry() {
    if ! command -v jq >/dev/null 2>&1; then
        log_error "jq is required. Please install jq."
        return 1
    fi
    if ! fetch_versions >/dev/null 2>&1; then
        log_error "Cannot reach the Harbor registry API at $HARBOR_API"
        log_error "If the project is private, export HARBOR_USERNAME and HARBOR_PASSWORD (a Harbor pull robot)."
        return 1
    fi
}

list_versions() {
    log_info "Fetching available release tags from Harbor..."
    check_registry || return 1
    local versions current
    versions=$(fetch_versions | sort -V -r | head -20)
    if [ -z "$versions" ]; then
        log_error "No release tags found"
        return 1
    fi
    current=$(get_current_version)
    echo -e "${BLUE}Available versions:${NC}"
    echo "$versions" | while read -r version; do
        if [ "$version" = "$current" ]; then
            echo -e "  ${GREEN}→ $version (pinned)${NC}"
        else
            echo "    $version"
        fi
    done
}

set_version() {
    local new_version="$1"
    if ! [[ "$new_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
        log_error "Invalid version format: $new_version. Expected X.Y.Z (e.g., 0.2.0)"
        return 1
    fi

    echo "$new_version" > "$VERSION_FILE"
    log_info "✓ Pinned rocketwelder-apps.version to $new_version"

    sed -i "s|image: $REGISTRY/$IMAGE_NAME:[^[:space:]]*|image: $REGISTRY/$IMAGE_NAME:$new_version|g" "$COMPOSE_FILE"
    log_info "✓ Updated $(basename "$COMPOSE_FILE") image tag"

    for arch_file in "${ARCH_FILES[@]}"; do
        if [ -f "$arch_file" ]; then
            sed -i "s|image: $REGISTRY/$IMAGE_NAME:[^[:space:]]*|image: $REGISTRY/$IMAGE_NAME:$new_version|g" "$arch_file"
            log_info "✓ Updated $(basename "$arch_file")"
        fi
    done
}

COMMAND=""
VERSION_ARG=""
for arg in "$@"; do
    case "$arg" in
        --format=json) FORMAT="json" ;;
        --*) log_error "Unknown option: $arg"; show_help; exit 1 ;;
        *)
            if [ -z "$COMMAND" ]; then COMMAND="$arg"
            elif [ -z "$VERSION_ARG" ]; then VERSION_ARG="$arg"
            fi
            ;;
    esac
done

if [ -z "$COMMAND" ]; then
    log_error "No command provided"
    show_help
    exit 1
fi

case "$COMMAND" in
    check)
        current_version=$(get_current_version)
        if [ "$FORMAT" = "json" ]; then
            echo "{\"success\": true, \"version\": \"$current_version\"}"
        else
            log_info "Pinned version: $current_version"
        fi
        ;;
    set)
        if [ -z "$VERSION_ARG" ]; then
            log_error "Please provide a version number"
            [ "$FORMAT" = "json" ] && echo "{\"success\": false, \"error\": \"Version number required\"}"
            exit 1
        fi
        if set_version "$VERSION_ARG"; then
            [ "$FORMAT" = "json" ] && echo "{\"success\": true, \"version\": \"$VERSION_ARG\"}"
            exit 0
        else
            [ "$FORMAT" = "json" ] && echo "{\"success\": false, \"error\": \"Failed to set version\"}"
            exit 1
        fi
        ;;
    list) list_versions ;;
    help) show_help ;;
    *) log_error "Unknown command: $COMMAND"; show_help; exit 1 ;;
esac
