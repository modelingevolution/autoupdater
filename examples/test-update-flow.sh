#!/bin/bash

# AutoUpdater End-to-End Test Flow Script
# This script simulates the complete version update process

set -e

# Colors for output
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
BLUE='\033[0;34m'
NC='\033[0m'

# Configuration
FROM_APP_VERSION="1.0.0"
TO_APP_VERSION="1.0.5"
FROM_COMPOSE_VERSION="v0.0.0"
TO_COMPOSE_VERSION="v0.0.5"
REGISTRY="local"  # Set to registry URL for remote testing
AUTOUPDATER_URL="http://localhost:8080"
VERSION_APP_URL="http://localhost:5000"

# Directories
SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
VERSION_APP_DIR="$SCRIPT_DIR/testproject"
COMPOSE_DIR="$SCRIPT_DIR/compose"

# Logging
log_step() {
    echo -e "${BLUE}[STEP $1]${NC} $2"
}

log_info() {
    echo -e "${GREEN}[INFO]${NC} $1"
}

log_warn() {
    echo -e "${YELLOW}[WARN]${NC} $1"
}

log_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

# Wait for service with timeout
wait_for_service() {
    local url=$1
    local timeout=${2:-60}
    local desc=${3:-"service"}
    
    log_info "Waiting for $desc at $url (timeout: ${timeout}s)"
    
    local count=0
    while [ $count -lt $timeout ]; do
        if curl -s -f "$url" >/dev/null 2>&1; then
            log_info "$desc is ready"
            return 0
        fi
        sleep 1
        count=$((count + 1))
    done
    
    log_error "$desc did not become ready within ${timeout}s"
    return 1
}

# Check current version
check_version() {
    local expected=$1
    local actual=$(curl -s "$VERSION_APP_URL/version" | jq -r '.version' 2>/dev/null || echo "unknown")
    
    if [ "$actual" = "$expected" ]; then
        log_info "✅ Version check passed: $actual"
        return 0
    else
        log_error "❌ Version check failed: expected $expected, got $actual"
        return 1
    fi
}

# Check AutoUpdater upgrade status
check_upgrade_status() {
    local package_name="version-app-compose"
    local expected_available=$1
    
    local response=$(curl -s "$AUTOUPDATER_URL/api/upgrades/$package_name" 2>/dev/null || echo "{}")
    local available=$(echo "$response" | jq -r '.availableVersion' 2>/dev/null || echo "unknown")
    local upgrade_available=$(echo "$response" | jq -r '.upgradeAvailable' 2>/dev/null || echo "false")
    
    log_info "Upgrade status: available=$available, upgradeAvailable=$upgrade_available"
    
    if [ "$available" = "$expected_available" ]; then
        log_info "✅ Upgrade status check passed"
        return 0
    else
        log_error "❌ Upgrade status check failed: expected $expected_available, got $available"
        return 1
    fi
}

# Trigger update via AutoUpdater API
trigger_update() {
    local package_name="version-app-compose"
    
    log_info "Triggering update via AutoUpdater API"
    local response=$(curl -s -X POST "$AUTOUPDATER_URL/api/update/$package_name" 2>/dev/null || echo "{}")
    local status=$(echo "$response" | jq -r '.status' 2>/dev/null || echo "unknown")
    
    if [ "$status" = "started" ]; then
        log_info "✅ Update triggered successfully"
        return 0
    else
        log_error "❌ Update trigger failed: $response"
        return 1
    fi
}

# Help function
show_help() {
    cat << EOF
AutoUpdater End-to-End Test Flow Script

USAGE:
    $0 [OPTIONS]

OPTIONS:
    --from-app-version VERSION      Source app version (default: $FROM_APP_VERSION)
    --to-app-version VERSION        Target app version (default: $TO_APP_VERSION)
    --from-compose-version VERSION  Source compose version (default: $FROM_COMPOSE_VERSION)
    --to-compose-version VERSION    Target compose version (default: $TO_COMPOSE_VERSION)
    --registry URL                  Docker registry URL (default: $REGISTRY)
    --autoupdater-url URL          AutoUpdater API URL (default: $AUTOUPDATER_URL)
    --version-app-url URL          VersionApp API URL (default: $VERSION_APP_URL)
    --skip-build                   Skip building new app version
    --skip-push                    Skip pushing to registry
    --manual-trigger               Trigger update manually via API
    --help                         Show this help

EXAMPLES:
    # Full automated test
    $0

    # Test with different versions
    $0 --to-app-version 1.1.0 --to-compose-version v0.1.0

    # Test manual trigger
    $0 --manual-trigger

PREREQUISITES:
    - Docker and docker-compose installed
    - AutoUpdater running at $AUTOUPDATER_URL
    - Git repositories accessible
    - Required permissions for Docker operations

EOF
}

# Parse command line arguments
SKIP_BUILD=false
SKIP_PUSH=false
MANUAL_TRIGGER=false

while [[ $# -gt 0 ]]; do
    case $1 in
        --from-app-version)
            FROM_APP_VERSION="$2"
            shift 2
            ;;
        --to-app-version)
            TO_APP_VERSION="$2"
            shift 2
            ;;
        --from-compose-version)
            FROM_COMPOSE_VERSION="$2"
            shift 2
            ;;
        --to-compose-version)
            TO_COMPOSE_VERSION="$2"
            shift 2
            ;;
        --registry)
            REGISTRY="$2"
            shift 2
            ;;
        --autoupdater-url)
            AUTOUPDATER_URL="$2"
            shift 2
            ;;
        --version-app-url)
            VERSION_APP_URL="$2"
            shift 2
            ;;
        --skip-build)
            SKIP_BUILD=true
            shift
            ;;
        --skip-push)
            SKIP_PUSH=true
            shift
            ;;
        --manual-trigger)
            MANUAL_TRIGGER=true
            shift
            ;;
        --help)
            show_help
            exit 0
            ;;
        *)
            log_error "Unknown option: $1"
            echo "Use --help for usage information"
            exit 1
            ;;
    esac
done

# Main execution
main() {
    echo -e "${GREEN}AutoUpdater End-to-End Test Flow${NC}"
    echo "=================================="
    echo "From: app=$FROM_APP_VERSION, compose=$FROM_COMPOSE_VERSION"
    echo "To: app=$TO_APP_VERSION, compose=$TO_COMPOSE_VERSION"
    echo "Registry: $REGISTRY"
    echo ""

    # Step 1: Verify initial state
    log_step "1" "Verify initial system state"
    wait_for_service "$VERSION_APP_URL/health" 30 "VersionApp"
    wait_for_service "$AUTOUPDATER_URL/api/packages" 30 "AutoUpdater"
    check_version "$FROM_APP_VERSION"

    # Step 2: Build new application version
    if [ "$SKIP_BUILD" = false ]; then
        log_step "2" "Build new application version $TO_APP_VERSION"
        cd "$VERSION_APP_DIR"
        
        if [ "$SKIP_PUSH" = false ] && [ "$REGISTRY" != "local" ]; then
            ./build.sh -v "$TO_APP_VERSION" --push --registry "$REGISTRY"
        else
            ./build.sh -v "$TO_APP_VERSION"
        fi
        
        cd "$SCRIPT_DIR"
        log_info "✅ Application build completed"
    else
        log_info "Skipping application build"
    fi

    # Step 3: Update compose configuration
    log_step "3" "Update compose configuration to reference $TO_APP_VERSION"
    cd "$COMPOSE_DIR"
    
    # Update docker-compose.yml
    local image_name="versionapp:$TO_APP_VERSION"
    if [ "$REGISTRY" != "local" ]; then
        image_name="$REGISTRY/versionapp:$TO_APP_VERSION"
    fi
    
    sed -i.bak "s|image: versionapp:.*|image: $image_name|g" docker-compose.yml
    
    # Commit and tag
    git add docker-compose.yml
    git commit -m "Update to version-app $TO_APP_VERSION"
    git tag "$TO_COMPOSE_VERSION"
    
    # Push changes
    git push origin master
    git push origin "$TO_COMPOSE_VERSION"
    
    cd "$SCRIPT_DIR"
    log_info "✅ Compose configuration updated and pushed"

    # Step 4: Wait for AutoUpdater to detect change
    log_step "4" "Wait for AutoUpdater to detect new version (max 120s)"
    local detected=false
    for i in {1..24}; do  # 24 * 5s = 120s
        if check_upgrade_status "$TO_COMPOSE_VERSION" 2>/dev/null; then
            detected=true
            break
        fi
        log_info "Waiting for detection... (attempt $i/24)"
        sleep 5
    done
    
    if [ "$detected" = false ]; then
        log_error "AutoUpdater did not detect new version within 120s"
        exit 1
    fi
    
    log_info "✅ AutoUpdater detected new version"

    # Step 5: Trigger update
    log_step "5" "Trigger update process"
    if [ "$MANUAL_TRIGGER" = true ]; then
        trigger_update
    else
        log_info "Waiting for automatic update..."
    fi

    # Step 6: Wait for update completion
    log_step "6" "Wait for update to complete (max 180s)"
    local updated=false
    for i in {1..36}; do  # 36 * 5s = 180s
        if check_version "$TO_APP_VERSION" 2>/dev/null; then
            updated=true
            break
        fi
        log_info "Waiting for update completion... (attempt $i/36)"
        sleep 5
    done
    
    if [ "$updated" = false ]; then
        log_error "Update did not complete within 180s"
        exit 1
    fi
    
    log_info "✅ Update completed successfully"

    # Step 7: Final verification
    log_step "7" "Final system verification"
    check_version "$TO_APP_VERSION"
    check_upgrade_status "$TO_COMPOSE_VERSION"
    
    # Check container is running correct image
    local container_image=$(docker ps --filter "name=version-app" --format "{{.Image}}" | head -1)
    local expected_image="versionapp:$TO_APP_VERSION"
    if [ "$REGISTRY" != "local" ]; then
        expected_image="$REGISTRY/versionapp:$TO_APP_VERSION"
    fi
    
    if [[ "$container_image" == *"$TO_APP_VERSION"* ]]; then
        log_info "✅ Container is running correct image: $container_image"
    else
        log_error "❌ Container image mismatch: expected $expected_image, got $container_image"
        exit 1
    fi

    echo ""
    echo -e "${GREEN}🎉 End-to-End Test Completed Successfully!${NC}"
    echo ""
    echo "Summary:"
    echo "- Application updated from $FROM_APP_VERSION to $TO_APP_VERSION"
    echo "- Compose updated from $FROM_COMPOSE_VERSION to $TO_COMPOSE_VERSION"
    echo "- AutoUpdater successfully detected and applied the update"
    echo "- All services are healthy and running the new version"
}

# Error handling
trap 'log_error "Test failed at step $?"; exit 1' ERR

# Run main function
main "$@"