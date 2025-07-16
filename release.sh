#!/bin/bash

# AutoUpdater Release Script
# Automates version tagging and triggering CI/CD stable releases

set -e

# Check if running in interactive terminal
check_interactive_terminal() {
    if [[ ! -t 0 ]] || [[ ! -t 1 ]]; then
        echo "# This script requires an interactive terminal for user prompts and confirmations."
        echo "# Please run this script from a terminal session, not from CI/CD or automated context."
        echo "# For automated releases, use the GitHub Actions workflow or implement a non-interactive version."
        return 1
    fi
    return 0
}

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Functions
print_usage() {
    echo -e "${BLUE}AutoUpdater Release Script${NC}"
    echo
    echo "Usage: ./release.sh [VERSION] [OPTIONS]"
    echo
    echo "Arguments:"
    echo "  VERSION     Semantic version (e.g., 1.2.3, 2.0.0)"
    echo "              If not provided, script will auto-increment patch version"
    echo
    echo "Options:"
    echo "  -m, --message TEXT    Release notes/message (optional)"
    echo "  -p, --patch           Auto-increment patch version (default)"
    echo "  -n, --minor           Auto-increment minor version"
    echo "  -M, --major           Auto-increment major version"
    echo "  -y, --yes             Auto-confirm release without prompts"
    echo "  --no-wait             Skip waiting for Docker images to be built"
    echo "  --dry-run             Show what would be done without executing"
    echo "  -h, --help            Show this help message"
    echo
    echo "Examples:"
    echo "  ./release.sh 1.2.3                           # Release specific version"
    echo "  ./release.sh 1.2.3 -m \"Added new features\"   # With release notes"
    echo "  ./release.sh --minor -m \"New UI components\"  # Auto-increment minor"
    echo "  ./release.sh --patch                         # Auto-increment patch"
    echo "  ./release.sh --minor -y -m \"New feature\"     # Auto-confirm release"
    echo "  ./release.sh --dry-run                       # Preview next patch release"
    echo "  ./release.sh --minor --no-wait               # Skip Docker image wait"
}

print_error() {
    echo -e "${RED}Error: $1${NC}" >&2
}

print_warning() {
    echo -e "${YELLOW}Warning: $1${NC}"
}

print_success() {
    echo -e "${GREEN}$1${NC}"
}

print_info() {
    echo -e "${BLUE}$1${NC}"
}

# Validate semantic version format
validate_version() {
    if [[ ! $1 =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
        print_error "Invalid version format: $1. Expected format: X.Y.Z (e.g., 1.2.3)"
        return 1
    fi
}

# Get the latest version tag
get_latest_version() {
    git tag --sort=-version:refname | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+$' | head -1 | sed 's/^v//' || echo "1.0.22"
}

# Increment version
increment_version() {
    local version=$1
    local part=$2
    
    IFS='.' read -ra VERSION_PARTS <<< "$version"
    local major=${VERSION_PARTS[0]}
    local minor=${VERSION_PARTS[1]}
    local patch=${VERSION_PARTS[2]}
    
    case $part in
        "major")
            major=$((major + 1))
            minor=0
            patch=0
            ;;
        "minor")
            minor=$((minor + 1))
            patch=0
            ;;
        "patch"|*)
            patch=$((patch + 1))
            ;;
    esac
    
    echo "$major.$minor.$patch"
}

# Check if working directory is clean
check_working_directory() {
    # Initialize git config for cross-platform compatibility
    init_git_config
    
    if [[ -n $(git status --porcelain) ]]; then
        print_error "Working directory is not clean. Please commit or stash your changes."
        echo "Uncommitted changes:"
        git status --short
        return 1
    fi
}

# Initialize git configuration for cross-platform compatibility
init_git_config() {
    # Set core.autocrlf to false to prevent line ending issues between Windows/Linux
    git config --local core.autocrlf false 2>/dev/null || true
    
    # Set core.filemode to false to ignore file permission changes (Windows/WSL compatibility)
    git config --local core.filemode false 2>/dev/null || true
    
    # Ensure consistent behavior for git status and diff operations
    git config --local status.submodulesummary false 2>/dev/null || true
    
    # Set safe directory (helps with WSL/Windows shared folders)
    git config --global --add safe.directory "$(git rev-parse --show-toplevel)" 2>/dev/null || true
}

# Check if we're on the correct branch
check_branch() {
    local auto_confirm="$1"
    local current_branch=$(git branch --show-current)
    if [[ "$current_branch" != "master" ]]; then
        print_warning "You are on branch '$current_branch', not 'master'"
        
        if [[ "$auto_confirm" != "true" ]]; then
            read -p "Continue anyway? (y/N): " -n 1 -r
            echo
            if [[ ! $REPLY =~ ^[Yy]$ ]]; then
                print_error "Release cancelled"
                return 1
            fi
        else
            print_info "Auto-confirming: continuing on $current_branch branch"
        fi
    fi
    
    # Check for unpushed commits
    git fetch origin "$current_branch" 2>/dev/null || true
    local unpushed_commits=$(git rev-list --count origin/"$current_branch"..HEAD 2>/dev/null || echo "0")
    if [[ "$unpushed_commits" -gt 0 ]]; then
        print_error "There are $unpushed_commits unpushed commit(s) on branch $current_branch"
        echo
        echo "Unpushed commits:"
        git log --oneline origin/"$current_branch"..HEAD
        echo
        print_info "Please push your commits first:"
        print_info "  git push origin $current_branch"
        return 1
    fi
}

# Check if Docker images exist for current commit
check_docker_images() {
    local no_wait="$1"
    local current_commit=$(git rev-parse --short HEAD)
    local registry="modelingevolution"
    local image_name="autoupdater"
    
    # Expected image tags based on current commit (for Docker Hub)
    local expected_images=(
    "${registry}/${image_name}:master-${current_commit}"
    )
    
    print_info "Checking if Docker images exist for commit $current_commit..."
    
    # Function to check if image exists
    check_image_exists() {
        local image="$1"
        if command -v docker >/dev/null 2>&1; then
            # Try with docker (if available and logged in)
            docker manifest inspect "$image" >/dev/null 2>&1
        else
            # Fallback: assume images exist if docker not available
            return 0
        fi
    }
    
    local max_wait_minutes=5
    local wait_interval=30
    local max_iterations=$((max_wait_minutes * 60 / wait_interval))
    local iteration=0
    
    while [ $iteration -lt $max_iterations ]; do
        local missing_images=()
        local found_images=()
        
        # Check each expected image
        for image in "${expected_images[@]}"; do
            if check_image_exists "$image"; then
                found_images+=("$image")
            else
                missing_images+=("$image")
            fi
        done
        
        # If we found the master-commit image, that's sufficient
        if [[ " ${found_images[*]} " =~ " ${registry}/${image_name}:master-${current_commit} " ]]; then
            print_success "✓ Found required Docker images for commit $current_commit"
            return 0
        fi
        
        # If no wait requested, exit immediately
        if [[ "$no_wait" == "true" ]]; then
            if [ ${#missing_images[@]} -gt 0 ]; then
                print_warning "Docker images not found for commit $current_commit"
                echo "Missing images:"
                printf '  - %s\n' "${missing_images[@]}"
                echo
                print_info "The promotion workflow may fail without these images."
                print_info "Proceeding anyway due to --no-wait flag."
                return 0
            fi
            return 0
        fi
        
        # Show status and wait
        if [ $iteration -eq 0 ]; then
            print_info "Docker images not yet available. Waiting for build workflow to complete..."
            echo "Looking for images:"
            printf '  - %s\n' "${expected_images[@]}"
            echo
        fi
        
        local remaining_time=$(( (max_iterations - iteration) * wait_interval ))
        print_info "Waiting... (${remaining_time}s remaining, checking every ${wait_interval}s)"
        
        sleep $wait_interval
        iteration=$((iteration + 1))
    done
    
    # Timeout reached
    print_error "Timeout: Docker images still not available after ${max_wait_minutes} minutes"
    echo "Missing images:"
    printf '  - %s\n' "${missing_images[@]}"
    echo
    print_info "Possible solutions:"
    print_info "1. Wait longer for the build workflow to complete"
    print_info "2. Check GitHub Actions: https://github.com/modelingevolution/autoupdater/actions"
    print_info "3. Use --no-wait flag to proceed without checking"
    
    return 1
}

# Create and push tag
create_tag() {
    local version=$1
    local message=$2
    local tag="v$version"
    
    if git tag -l | grep -q "^$tag$"; then
        print_error "Tag $tag already exists"
        return 1
    fi
    
    print_info "Creating tag: $tag"
    
    if [[ -n "$message" ]]; then
        git tag -a "$tag" -m "$message"
    else
        git tag -a "$tag" -m "Release $version"
    fi
    
    print_info "Pushing tag to origin..."
    git push origin "$tag"
    
    print_success "✅ Tag $tag created and pushed successfully"
    print_info "CI/CD will now promote preview images to production with tags:"
    print_info "  - modelingevolution/autoupdater:latest"
    print_info "  - modelingevolution/autoupdater:$version"
    local major_minor=$(echo "$version" | cut -d. -f1,2)
    local major=$(echo "$version" | cut -d. -f1)
    print_info "  - modelingevolution/autoupdater:$major_minor"
    print_info "  - modelingevolution/autoupdater:$major"
}

# Main script logic
main() {
    local version=""
    local increment_type="patch"
    local message=""
    local dry_run=false
    local auto_confirm=false
    local needs_interactive=true
    local no_wait=false
    
    # Parse arguments first to check for help, dry-run, or auto-confirm
    while [[ $# -gt 0 ]]; do
        case $1 in
            -h|--help)
                print_usage
                exit 0
                ;;
            --dry-run)
                dry_run=true
                needs_interactive=false
                shift
                ;;
            -y|--yes)
                auto_confirm=true
                needs_interactive=false
                shift
                ;;
            --no-wait)
                no_wait=true
                shift
                ;;
            -m|--message)
                message="$2"
                shift 2
                ;;
            -p|--patch)
                increment_type="patch"
                shift
                ;;
            -n|--minor)
                increment_type="minor"
                shift
                ;;
            -M|--major)
                increment_type="major"
                shift
                ;;
            -*)
                print_error "Unknown option: $1"
                print_usage
                exit 1
                ;;
            *)
                if [[ -z "$version" ]]; then
                    version="$1"
                else
                    print_error "Too many arguments"
                    print_usage
                    exit 1
                fi
                shift
                ;;
        esac
    done
    
    # If no arguments provided, show helpful guidance
    if [[ $# -eq 0 ]] && [[ -z "$version" ]] && [[ "$increment_type" == "patch" ]] && [[ -z "$message" ]] && [[ "$dry_run" == false ]] && [[ "$auto_confirm" == false ]]; then
        echo "No arguments provided. Here are some examples:"
        echo
        print_usage
        echo
        echo "For non-interactive environments:"
        echo "  ./release.sh --dry-run                       # Preview release"
        echo "  ./release.sh --minor -y -m \"New feature\"    # Auto-confirm release"
        exit 1
    fi
    
    # Check if running in interactive terminal (only when interactive input is needed)
    if [[ "$needs_interactive" == true ]] && ! check_interactive_terminal; then
        echo
        echo "To run this script in non-interactive mode:"
        echo "  ./release.sh --dry-run                       # Preview release"
        echo "  ./release.sh --minor -y -m \"New feature\"    # Auto-confirm release"
        exit 1
    fi
    
    # If no version specified, auto-increment
    if [[ -z "$version" ]]; then
        local latest_version=$(get_latest_version)
        version=$(increment_version "$latest_version" "$increment_type")
        print_info "Auto-incrementing $increment_type version: $latest_version → $version"
    fi
    
    # Validate version format
    validate_version "$version" || exit 1
    
    # Check if tag already exists
    if git tag -l | grep -q "^v$version$"; then
        print_error "Tag v$version already exists"
        exit 1
    fi
    
    if [[ "$dry_run" == true ]]; then
        print_info "🔍 DRY RUN - Would create tag: v$version"
        if [[ -n "$message" ]]; then
            print_info "Release message: $message"
        fi
        print_info "This would trigger CI/CD to promote preview to production tags"
        exit 0
    fi
    
    print_info "🚀 Starting AutoUpdater release process..."
    print_info "Version: $version"
    if [[ -n "$message" ]]; then
        print_info "Message: $message"
    fi
    
    # Pre-flight checks (skip for dry run)
    if [[ "$dry_run" != true ]]; then
        check_working_directory || exit 1
        check_branch "$auto_confirm" || exit 1
        
        # Check if Docker images exist (skip for dry run)
        if ! check_docker_images "$no_wait"; then
            print_error "Docker images not available. Release cancelled."
            print_info "Use --no-wait to skip this check, or wait for build workflow to complete."
            exit 1
        fi
    fi
    
    # Ensure we have the latest changes
    print_info "Fetching latest changes..."
    git fetch origin
    
    # Create and push tag
    create_tag "$version" "$message" || exit 1
    
    print_success "🎉 Release $version completed successfully!"
    print_info ""
    print_info "Next steps:"
    print_info "1. Monitor the GitHub Actions workflow: https://github.com/modelingevolution/autoupdater/actions"
    print_info "2. Verify image promotion completed successfully"
    print_info "3. Test the new production images:"
    print_info "   docker pull modelingevolution/autoupdater:latest"
    print_info "   docker pull modelingevolution/autoupdater:$version"
    print_info ""
    print_info "🔗 View release: https://github.com/modelingevolution/autoupdater/releases/tag/v$version"
}

# Run main function
main "$@"