#!/bin/bash

# Create Deploy User Script
# This script creates a 'deploy' user on the host machine for SSH access by AutoUpdater

set -euo pipefail

# Default values
DEPLOY_USER="deploy"
DEPLOY_GROUP="docker"
SHELL="/bin/bash"
ENABLE_SUDO=false
DISABLE_PASSWORD=false
SSH_KEY_FILE=""
VERBOSE=false

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Function to print colored output
print_info() {
    echo -e "${BLUE}[INFO]${NC} $1"
}

print_success() {
    echo -e "${GREEN}[SUCCESS]${NC} $1"
}

print_warning() {
    echo -e "${YELLOW}[WARNING]${NC} $1"
}

print_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

# Function to show usage
show_usage() {
    cat << EOF
Usage: $0 [OPTIONS]

Creates a 'deploy' user for SSH access by AutoUpdater.

OPTIONS:
    -u, --user USER         Deploy user name (default: deploy)
    -g, --group GROUP       Primary group for user (default: docker)
    -s, --shell SHELL       User shell (default: /bin/bash)
    --enable-sudo          Add user to sudo group (requires sudo access)
    --disable-password     Disable password authentication (SSH key only)
    --ssh-key FILE         Install SSH public key from file
    -v, --verbose          Enable verbose output
    -h, --help             Show this help message

EXAMPLES:
    # Basic setup
    $0

    # Setup with sudo access and SSH key
    $0 --enable-sudo --ssh-key ~/.ssh/id_rsa.pub

    # Custom user with different group
    $0 --user automation --group users --enable-sudo

    # Disable password auth (SSH key only)
    $0 --ssh-key /path/to/key.pub --disable-password

SECURITY NOTES:
    - The user will be added to the 'docker' group by default
    - Use --enable-sudo only if the deploy user needs system administration access
    - Consider --disable-password for enhanced security with SSH keys
    - Ensure proper SSH key management and rotation
EOF
}

# Parse command line arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        -u|--user)
            DEPLOY_USER="$2"
            shift 2
            ;;
        -g|--group)
            DEPLOY_GROUP="$2"
            shift 2
            ;;
        -s|--shell)
            SHELL="$2"
            shift 2
            ;;
        --enable-sudo)
            ENABLE_SUDO=true
            shift
            ;;
        --disable-password)
            DISABLE_PASSWORD=true
            shift
            ;;
        --ssh-key)
            SSH_KEY_FILE="$2"
            shift 2
            ;;
        -v|--verbose)
            VERBOSE=true
            shift
            ;;
        -h|--help)
            show_usage
            exit 0
            ;;
        *)
            print_error "Unknown option: $1"
            show_usage
            exit 1
            ;;
    esac
done

# Function for verbose logging
log_verbose() {
    if [[ "$VERBOSE" == "true" ]]; then
        print_info "$1"
    fi
}

# Check if running as root
check_root() {
    if [[ $EUID -ne 0 ]]; then
        print_error "This script must be run as root (use sudo)"
        exit 1
    fi
}

# Check if user already exists
user_exists() {
    id "$DEPLOY_USER" &>/dev/null
}

# Check if group exists
group_exists() {
    getent group "$1" &>/dev/null
}

# Create user
create_user() {
    print_info "Creating user '$DEPLOY_USER'..."
    
    if user_exists; then
        print_warning "User '$DEPLOY_USER' already exists"
        return 0
    fi
    
    # Create user with home directory
    useradd -m -s "$SHELL" "$DEPLOY_USER"
    log_verbose "User '$DEPLOY_USER' created with home directory"
    
    print_success "User '$DEPLOY_USER' created successfully"
}

# Add user to groups
configure_groups() {
    print_info "Configuring user groups..."
    
    # Check if primary group exists
    if group_exists "$DEPLOY_GROUP"; then
        usermod -a -G "$DEPLOY_GROUP" "$DEPLOY_USER"
        log_verbose "Added user to group '$DEPLOY_GROUP'"
    else
        print_warning "Group '$DEPLOY_GROUP' does not exist, skipping"
    fi
    
    # Add to sudo group if requested
    if [[ "$ENABLE_SUDO" == "true" ]]; then
        if group_exists "sudo"; then
            usermod -a -G sudo "$DEPLOY_USER"
            log_verbose "Added user to sudo group"
            print_success "User '$DEPLOY_USER' added to sudo group"
        elif group_exists "wheel"; then
            usermod -a -G wheel "$DEPLOY_USER"
            log_verbose "Added user to wheel group"
            print_success "User '$DEPLOY_USER' added to wheel group"
        else
            print_warning "Neither sudo nor wheel group found, skipping sudo access"
        fi
    fi
}

# Setup SSH directory and permissions
setup_ssh_directory() {
    local home_dir
    home_dir=$(eval echo "~$DEPLOY_USER")
    local ssh_dir="$home_dir/.ssh"
    
    print_info "Setting up SSH directory..."
    
    # Create .ssh directory
    mkdir -p "$ssh_dir"
    chown "$DEPLOY_USER:$DEPLOY_USER" "$ssh_dir"
    chmod 700 "$ssh_dir"
    
    log_verbose "SSH directory created: $ssh_dir"
}

# Install SSH public key
install_ssh_key() {
    if [[ -z "$SSH_KEY_FILE" ]]; then
        return 0
    fi
    
    print_info "Installing SSH public key..."
    
    if [[ ! -f "$SSH_KEY_FILE" ]]; then
        print_error "SSH key file not found: $SSH_KEY_FILE"
        exit 1
    fi
    
    local home_dir
    home_dir=$(eval echo "~$DEPLOY_USER")
    local authorized_keys="$home_dir/.ssh/authorized_keys"
    
    # Install the key
    cat "$SSH_KEY_FILE" >> "$authorized_keys"
    chown "$DEPLOY_USER:$DEPLOY_USER" "$authorized_keys"
    chmod 600 "$authorized_keys"
    
    log_verbose "SSH key installed to: $authorized_keys"
    print_success "SSH public key installed successfully"
}

# Configure password authentication
configure_password_auth() {
    if [[ "$DISABLE_PASSWORD" == "true" ]]; then
        print_info "Disabling password authentication for user '$DEPLOY_USER'..."
        passwd -l "$DEPLOY_USER"
        log_verbose "Password authentication disabled"
        print_success "Password authentication disabled"
    else
        print_info "Setting up password for user '$DEPLOY_USER'..."
        print_warning "Please set a strong password for the deploy user:"
        passwd "$DEPLOY_USER"
    fi
}

# Verify Docker access
verify_docker_access() {
    if group_exists "docker"; then
        print_info "Verifying Docker access..."
        if sudo -u "$DEPLOY_USER" docker version &>/dev/null; then
            print_success "Docker access verified successfully"
        else
            print_warning "Docker access verification failed - user may need to log out and back in"
        fi
    fi
}

# Display summary
show_summary() {
    echo
    print_success "=== Deploy User Setup Complete ==="
    echo -e "User: ${GREEN}$DEPLOY_USER${NC}"
    echo -e "Home: ${GREEN}$(eval echo "~$DEPLOY_USER")${NC}"
    echo -e "Shell: ${GREEN}$SHELL${NC}"
    echo -e "Groups: ${GREEN}$(groups "$DEPLOY_USER" | cut -d: -f2)${NC}"
    
    if [[ "$SSH_KEY_FILE" != "" ]]; then
        echo -e "SSH Key: ${GREEN}Installed${NC}"
    fi
    
    if [[ "$DISABLE_PASSWORD" == "true" ]]; then
        echo -e "Password Auth: ${YELLOW}Disabled${NC}"
    else
        echo -e "Password Auth: ${GREEN}Enabled${NC}"
    fi
    
    echo
    print_info "Next steps:"
    echo "1. Test SSH connectivity: ssh $DEPLOY_USER@localhost"
    echo "2. Verify Docker access: docker ps"
    echo "3. Update AutoUpdater configuration with SshUser: '$DEPLOY_USER'"
    
    if [[ "$ENABLE_SUDO" == "true" ]]; then
        echo "4. User has sudo access - use responsibly"
    fi
}

# Main execution
main() {
    print_info "Starting deploy user setup..."
    print_info "User: $DEPLOY_USER, Group: $DEPLOY_GROUP, Shell: $SHELL"
    
    check_root
    create_user
    configure_groups
    setup_ssh_directory
    install_ssh_key
    configure_password_auth
    verify_docker_access
    show_summary
    
    print_success "Deploy user setup completed successfully!"
}

# Run main function
main "$@"