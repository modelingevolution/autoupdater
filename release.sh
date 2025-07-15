#!/bin/bash

# AutoUpdater Release Script
# Automates version tagging and triggering CI/CD stable releases

set -e

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
    echo "  --dry-run             Show what would be done without executing"
    echo "  -h, --help            Show this help message"
    echo
    echo "Examples:"
    echo "  ./release.sh 1.2.3                           # Release specific version"
    echo "  ./release.sh 1.2.3 -m \"Added new features\"   # With release notes"
    echo "  ./release.sh --minor -m \"New UI components\"  # Auto-increment minor"
    echo "  ./release.sh --patch                         # Auto-increment patch"
    echo "  ./release.sh --dry-run                       # Preview next patch release"
    echo ""
    echo "Non-interactive mode:"
    echo "  echo 'y' | ./release.sh [options]            # For CI/CD or automated use"
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
    if [[ -n $(git status --porcelain) ]]; then
        print_error "Working directory is not clean. Please commit or stash your changes."
        echo "Uncommitted changes:"
        git status --short
        return 1
    fi
}

# Check if we're on the correct branch
check_branch() {
    local current_branch=$(git branch --show-current)
    if [[ "$current_branch" != "master" ]]; then
        print_warning "You are on branch '$current_branch', not 'master'"
        
        # Check if running in non-interactive mode
        if [[ ! -t 0 ]]; then
            print_error "Non-interactive mode detected. Cannot proceed from non-master branch."
            print_info "To run this script from a non-master branch, use: echo 'y' | ./release.sh [options]"
            return 1
        fi
        
        read -p "Continue anyway? (y/N): " -n 1 -r
        echo
        if [[ ! $REPLY =~ ^[Yy]$ ]]; then
            print_error "Release cancelled"
            return 1
        fi
    fi
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
    
    # Parse arguments
    while [[ $# -gt 0 ]]; do
        case $1 in
            -h|--help)
                print_usage
                exit 0
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
            --dry-run)
                dry_run=true
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
    
    # Pre-flight checks
    check_working_directory || exit 1
    check_branch || exit 1
    
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