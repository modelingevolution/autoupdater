#!/bin/bash

# AutoUpdater SSH Key Installation Script
# This script sets up SSH key authentication for the ModelingEvolution.AutoUpdater
# Usage: ./install-ssh-keys.sh --user deploy --hosts "host1,host2,host3"

set -e

# Default values
USER=""
HOSTS=""
KEY_PATH="./data/ssh"
KEY_TYPE="rsa"
KEY_BITS="4096"
PASSPHRASE=""
CONFIG_FILE="./src/ModelingEvolution.AutoUpdater.Host/appsettings.json"
TEST_ONLY=false
FORCE=false
VERBOSE=false

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Logging functions
log_info() {
    echo -e "${GREEN}[INFO]${NC} $1"
}

log_warn() {
    echo -e "${YELLOW}[WARN]${NC} $1"
}

log_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

log_debug() {
    if [ "$VERBOSE" = true ]; then
        echo -e "${BLUE}[DEBUG]${NC} $1"
    fi
}

# Help function
show_help() {
    cat << EOF
AutoUpdater SSH Key Installation Script

USAGE:
    $0 --user USER --hosts HOSTS [OPTIONS]

REQUIRED PARAMETERS:
    --user USER          SSH username for target hosts
    --hosts HOSTS        Comma-separated list of target hostnames/IPs

OPTIONS:
    --key-path PATH      Custom path for SSH keys (default: ./data/ssh)
    --passphrase         Prompt for passphrase to protect private key
    --key-type TYPE      SSH key type: rsa, ed25519 (default: rsa)
    --key-bits BITS      Key strength for RSA (default: 4096)
    --config-file FILE   AutoUpdater configuration file to update
                         (default: ./src/ModelingEvolution.AutoUpdater.Host/appsettings.json)
    --test-only          Validate configuration without making changes
    --force              Overwrite existing keys
    --verbose            Enable verbose output
    --help               Show this help message

EXAMPLES:
    # Basic usage
    $0 --user deploy --hosts "192.168.1.100,192.168.1.101"
    
    # With custom key path and passphrase
    $0 --user deploy --hosts "server1,server2" --key-path /custom/path --passphrase
    
    # Test only (no changes)
    $0 --user deploy --hosts "server1" --test-only
    
    # Use Ed25519 keys
    $0 --user deploy --hosts "server1" --key-type ed25519

REQUIREMENTS:
    - ssh-keygen
    - ssh-copy-id or ssh + scp
    - jq (for JSON configuration updates)
    - Network access to target hosts

EOF
}

# Parse command line arguments
parse_args() {
    while [[ $# -gt 0 ]]; do
        case $1 in
            --user)
                USER="$2"
                shift 2
                ;;
            --hosts)
                HOSTS="$2"
                shift 2
                ;;
            --key-path)
                KEY_PATH="$2"
                shift 2
                ;;
            --passphrase)
                PASSPHRASE="prompt"
                shift
                ;;
            --key-type)
                KEY_TYPE="$2"
                shift 2
                ;;
            --key-bits)
                KEY_BITS="$2"
                shift 2
                ;;
            --config-file)
                CONFIG_FILE="$2"
                shift 2
                ;;
            --test-only)
                TEST_ONLY=true
                shift
                ;;
            --force)
                FORCE=true
                shift
                ;;
            --verbose)
                VERBOSE=true
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
}

# Validate parameters
validate_params() {
    log_debug "Validating parameters..."
    
    if [ -z "$USER" ]; then
        log_error "SSH user is required. Use --user parameter."
        exit 1
    fi
    
    if [ -z "$HOSTS" ]; then
        log_error "Host list is required. Use --hosts parameter."
        exit 1
    fi
    
    # Check required tools
    for tool in ssh-keygen ssh jq; do
        if ! command -v $tool &> /dev/null; then
            log_error "Required tool '$tool' is not installed"
            exit 1
        fi
    done
    
    # Validate key type
    if [[ ! "$KEY_TYPE" =~ ^(rsa|ed25519)$ ]]; then
        log_error "Invalid key type: $KEY_TYPE. Supported: rsa, ed25519"
        exit 1
    fi
    
    # Validate key bits for RSA
    if [ "$KEY_TYPE" = "rsa" ] && [[ ! "$KEY_BITS" =~ ^(2048|3072|4096)$ ]]; then
        log_error "Invalid key bits for RSA: $KEY_BITS. Supported: 2048, 3072, 4096"
        exit 1
    fi
    
    log_info "Parameter validation successful"
}

# Check host connectivity
check_connectivity() {
    local host=$1
    log_debug "Testing connectivity to $host..."
    
    if timeout 5 nc -z "$host" 22 2>/dev/null; then
        log_debug "Host $host is reachable on port 22"
        return 0
    else
        log_warn "Host $host is not reachable on port 22"
        return 1
    fi
}

# Generate SSH key pair
generate_ssh_keys() {
    local private_key="$KEY_PATH/id_$KEY_TYPE"
    local public_key="$private_key.pub"
    
    log_info "Generating SSH key pair..."
    
    if [ "$TEST_ONLY" = true ]; then
        log_info "[TEST MODE] Would generate SSH key: $private_key"
        return 0
    fi
    
    # Create key directory
    mkdir -p "$KEY_PATH"
    chmod 700 "$KEY_PATH"
    
    # Check if keys already exist
    if [ -f "$private_key" ] && [ "$FORCE" != true ]; then
        log_warn "SSH key already exists: $private_key"
        read -p "Overwrite existing key? (y/N): " -n 1 -r
        echo
        if [[ ! $REPLY =~ ^[Yy]$ ]]; then
            log_info "Using existing SSH key"
            return 0
        fi
    fi
    
    # Generate key
    local ssh_keygen_args=()
    ssh_keygen_args+=(-t "$KEY_TYPE")
    ssh_keygen_args+=(-f "$private_key")
    ssh_keygen_args+=(-C "autoupdater@$(hostname)")
    ssh_keygen_args+=(-q)
    
    if [ "$KEY_TYPE" = "rsa" ]; then
        ssh_keygen_args+=(-b "$KEY_BITS")
    fi
    
    if [ "$PASSPHRASE" = "prompt" ]; then
        ssh-keygen "${ssh_keygen_args[@]}"
    else
        ssh-keygen "${ssh_keygen_args[@]}" -N ""
    fi
    
    # Set proper permissions
    chmod 600 "$private_key"
    chmod 644 "$public_key"
    
    log_info "SSH key pair generated successfully"
    log_info "Private key: $private_key"
    log_info "Public key: $public_key"
}

# Install public key on target host
install_public_key() {
    local host=$1
    local private_key="$KEY_PATH/id_$KEY_TYPE"
    local public_key="$private_key.pub"
    
    log_info "Installing public key on $host..."
    
    if [ "$TEST_ONLY" = true ]; then
        log_info "[TEST MODE] Would install public key on $host"
        return 0
    fi
    
    # Try ssh-copy-id first (most reliable)
    if command -v ssh-copy-id &> /dev/null; then
        log_debug "Using ssh-copy-id to install key on $host"
        if ssh-copy-id -i "$public_key" "$USER@$host" 2>/dev/null; then
            log_info "Successfully installed public key on $host using ssh-copy-id"
            return 0
        else
            log_warn "ssh-copy-id failed for $host, trying manual installation"
        fi
    fi
    
    # Manual installation fallback
    log_debug "Manual key installation for $host"
    local public_key_content
    public_key_content=$(cat "$public_key")
    
    # Create SSH directory and install key
    ssh "$USER@$host" "
        mkdir -p ~/.ssh
        chmod 700 ~/.ssh
        echo '$public_key_content' >> ~/.ssh/authorized_keys
        chmod 600 ~/.ssh/authorized_keys
        # Remove duplicates
        sort ~/.ssh/authorized_keys | uniq > ~/.ssh/authorized_keys.tmp
        mv ~/.ssh/authorized_keys.tmp ~/.ssh/authorized_keys
        chmod 600 ~/.ssh/authorized_keys
    "
    
    if [ $? -eq 0 ]; then
        log_info "Successfully installed public key on $host manually"
    else
        log_error "Failed to install public key on $host"
        return 1
    fi
}

# Test SSH key authentication
test_ssh_key() {
    local host=$1
    local private_key="$KEY_PATH/id_$KEY_TYPE"
    
    log_info "Testing SSH key authentication for $host..."
    
    if [ "$TEST_ONLY" = true ]; then
        log_info "[TEST MODE] Would test SSH key authentication for $host"
        return 0
    fi
    
    # Test key-based authentication
    local ssh_opts=(-i "$private_key" -o PasswordAuthentication=no -o StrictHostKeyChecking=no)
    
    if ssh "${ssh_opts[@]}" "$USER@$host" "echo 'SSH key authentication successful'" 2>/dev/null; then
        log_info "SSH key authentication successful for $host"
        return 0
    else
        log_error "SSH key authentication failed for $host"
        return 1
    fi
}

# Update AutoUpdater configuration
update_configuration() {
    log_info "Updating AutoUpdater configuration..."
    
    if [ "$TEST_ONLY" = true ]; then
        log_info "[TEST MODE] Would update configuration file: $CONFIG_FILE"
        return 0
    fi
    
    if [ ! -f "$CONFIG_FILE" ]; then
        log_warn "Configuration file not found: $CONFIG_FILE"
        log_info "Creating basic configuration file..."
        
        mkdir -p "$(dirname "$CONFIG_FILE")"
        cat > "$CONFIG_FILE" << EOF
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*"
}
EOF
    fi
    
    # Backup existing configuration
    cp "$CONFIG_FILE" "$CONFIG_FILE.backup.$(date +%Y%m%d_%H%M%S)"
    
    # Update SSH configuration
    local private_key="$KEY_PATH/id_$KEY_TYPE"
    local auth_method="PrivateKey"
    
    if [ "$PASSPHRASE" = "prompt" ]; then
        auth_method="PrivateKeyWithPassphrase"
    fi
    
    # Use jq to update JSON configuration
    local temp_config
    temp_config=$(mktemp)
    
    jq --arg user "$USER" \
       --arg keyPath "$private_key" \
       --arg authMethod "$auth_method" \
       '. + {
         "SshUser": $user,
         "SshKeyPath": $keyPath,
         "SshAuthMethod": $authMethod
       }' "$CONFIG_FILE" > "$temp_config"
    
    mv "$temp_config" "$CONFIG_FILE"
    
    log_info "Configuration updated successfully"
    log_info "Backup saved as: $CONFIG_FILE.backup.*"
}

# Main execution
main() {
    log_info "AutoUpdater SSH Key Installation Script"
    log_info "======================================="
    
    parse_args "$@"
    validate_params
    
    # Convert comma-separated hosts to array
    IFS=',' read -ra HOST_ARRAY <<< "$HOSTS"
    
    log_info "Configuration:"
    log_info "  User: $USER"
    log_info "  Hosts: ${HOST_ARRAY[*]}"
    log_info "  Key Path: $KEY_PATH"
    log_info "  Key Type: $KEY_TYPE"
    log_info "  Config File: $CONFIG_FILE"
    log_info "  Test Only: $TEST_ONLY"
    
    # Check connectivity to all hosts
    log_info "Checking host connectivity..."
    failed_hosts=()
    for host in "${HOST_ARRAY[@]}"; do
        if ! check_connectivity "$host"; then
            failed_hosts+=("$host")
        fi
    done
    
    if [ ${#failed_hosts[@]} -gt 0 ]; then
        log_warn "Some hosts are not reachable: ${failed_hosts[*]}"
        if [ "$TEST_ONLY" != true ]; then
            read -p "Continue anyway? (y/N): " -n 1 -r
            echo
            if [[ ! $REPLY =~ ^[Yy]$ ]]; then
                log_error "Aborted by user"
                exit 1
            fi
        fi
    fi
    
    # Generate SSH keys
    generate_ssh_keys
    
    # Install public keys on hosts
    installation_failures=()
    for host in "${HOST_ARRAY[@]}"; do
        if ! install_public_key "$host"; then
            installation_failures+=("$host")
        fi
    done
    
    # Test SSH key authentication
    authentication_failures=()
    for host in "${HOST_ARRAY[@]}"; do
        if ! test_ssh_key "$host"; then
            authentication_failures+=("$host")
        fi
    done
    
    # Update configuration
    update_configuration
    
    # Final report
    log_info ""
    log_info "Installation Summary:"
    log_info "===================="
    log_info "Total hosts: ${#HOST_ARRAY[@]}"
    log_info "Installation failures: ${#installation_failures[@]}"
    log_info "Authentication failures: ${#authentication_failures[@]}"
    
    if [ ${#installation_failures[@]} -gt 0 ]; then
        log_warn "Installation failed for: ${installation_failures[*]}"
    fi
    
    if [ ${#authentication_failures[@]} -gt 0 ]; then
        log_warn "Authentication failed for: ${authentication_failures[*]}"
    fi
    
    if [ ${#installation_failures[@]} -eq 0 ] && [ ${#authentication_failures[@]} -eq 0 ]; then
        log_info "✅ SSH key installation completed successfully!"
        log_info ""
        log_info "Next steps:"
        log_info "1. Deploy AutoUpdater with the updated configuration"
        log_info "2. Ensure the SSH key directory is mounted in Docker: -v $KEY_PATH:/data/ssh:ro"
        log_info "3. Monitor AutoUpdater logs for SSH connectivity"
    else
        log_error "❌ SSH key installation completed with errors"
        log_info ""
        log_info "Troubleshooting:"
        log_info "1. Verify SSH service is running on failed hosts"
        log_info "2. Check firewall settings for port 22"
        log_info "3. Verify user '$USER' exists on target hosts"
        log_info "4. Try manual SSH connection: ssh $USER@<host>"
        exit 1
    fi
}

# Handle script interruption
trap 'log_error "Script interrupted"; exit 130' INT TERM

# Run main function
main "$@"