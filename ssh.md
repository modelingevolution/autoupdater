# SSH Key Authentication for AutoUpdater

## Overview

This document outlines the requirements and design for implementing SSH key-based authentication in the ModelingEvolution.AutoUpdater system, replacing password-based authentication for enhanced security.

## Current State

**Password-based Authentication:**
- Configuration: `SshUser` + `SshPwd` in appsettings.json
- Security Risk: Plain text passwords in configuration
- Network Risk: Password transmitted over SSH connection
- Management: Manual password rotation required

## Requirements

### Functional Requirements

#### FR1: SSH Key Generation and Exchange
- **FR1.1**: Generate SSH key pairs (public/private) for AutoUpdater
- **FR1.2**: Automatically install public key on target SSH hosts
- **FR1.3**: Support multiple target hosts with same key pair
- **FR1.4**: Handle key rotation and renewal

#### FR2: Docker Integration
- **FR2.1**: SSH private key accessible within Docker container
- **FR2.2**: Secure key storage (not in Docker image)
- **FR2.3**: Volume mounting for key persistence
- **FR2.4**: Key permissions properly set (600 for private key)

#### FR3: Installation Automation
- **FR3.1**: Provide installation script for initial setup
- **FR3.2**: Script handles key generation if not exists
- **FR3.3**: Script distributes public key to target hosts
- **FR3.4**: Script configures AutoUpdater for key-based auth

#### FR4: Configuration Management
- **FR4.1**: Update configuration to use SSH keys instead of passwords
- **FR4.2**: Support both key-based and password fallback during transition
- **FR4.3**: Clear migration path from password to key authentication

### Non-Functional Requirements

#### NFR1: Security
- **NFR1.1**: Private keys never stored in Docker images
- **NFR1.2**: Keys stored with proper file permissions (600/700)
- **NFR1.3**: Support for passphrase-protected private keys
- **NFR1.4**: Key rotation without service downtime

#### NFR2: Usability
- **NFR2.1**: One-command installation for new deployments
- **NFR2.2**: Clear documentation and examples
- **NFR2.3**: Error messages guide users to resolution
- **NFR2.4**: Backward compatibility with existing password setups

#### NFR3: Reliability
- **NFR3.1**: Graceful fallback if key authentication fails
- **NFR3.2**: Proper error handling and logging
- **NFR3.3**: Key validation before attempting connections
- **NFR3.4**: Support for SSH agent forwarding

## Design

### Architecture

```
AutoUpdater Container
├── /app (application files)
├── /data (persistent data volume)
│   ├── repos/ (Git repositories)
│   └── ssh/ (SSH configuration)
│       ├── id_rsa (private key)
│       ├── id_rsa.pub (public key)
│       ├── config (SSH client config)
│       └── known_hosts (host keys)
└── /root/.ssh -> /data/ssh (symlink)
```

### Configuration Schema

#### Current Configuration
```json
{
  "SshUser": "deploy",
  "SshPwd": "password123",
  "Packages": [...]
}
```

#### New Configuration (SSH Keys)
```json
{
  "SshUser": "deploy",
  "SshKeyPath": "/data/ssh/id_rsa",
  "SshKeyPassphrase": "optional-passphrase",
  "Packages": [...]
}
```

#### Hybrid Configuration (Transition Period)
```json
{
  "SshUser": "deploy",
  "SshKeyPath": "/data/ssh/id_rsa",
  "SshKeyPassphrase": "optional-passphrase",
  "SshPwd": "fallback-password",
  "SshAuthMethod": "key-with-password-fallback",
  "Packages": [...]
}
```

### SSH Authentication Methods

#### Primary: Key-based Authentication
```csharp
public enum SshAuthMethod
{
    Password,
    PrivateKey,
    PrivateKeyWithPassphrase,
    KeyWithPasswordFallback
}
```

### Installation Script Design

#### Script: `install-ssh-keys.sh`

**Purpose**: Automate SSH key setup for AutoUpdater deployment

**Features:**
1. Generate SSH key pair if not exists
2. Distribute public key to target hosts
3. Configure AutoUpdater for key-based authentication
4. Validate SSH connectivity

**Usage:**
```bash
# Basic usage
./install-ssh-keys.sh --user deploy --hosts "host1,host2,host3"

# With custom key path
./install-ssh-keys.sh --user deploy --hosts "host1,host2" --key-path /custom/path

# With passphrase protection
./install-ssh-keys.sh --user deploy --hosts "host1,host2" --passphrase

# Test only (no changes)
./install-ssh-keys.sh --user deploy --hosts "host1,host2" --test-only
```

**Parameters:**
- `--user`: SSH username for target hosts
- `--hosts`: Comma-separated list of target hostnames/IPs
- `--key-path`: Custom path for SSH keys (default: ./data/ssh)
- `--passphrase`: Prompt for passphrase to protect private key
- `--key-type`: SSH key type (rsa, ed25519) default: rsa
- `--key-bits`: Key strength for RSA (default: 4096)
- `--test-only`: Validate configuration without making changes
- `--force`: Overwrite existing keys
- `--config-file`: AutoUpdater configuration file to update

#### Script Workflow

```
install-ssh-keys.sh execution flow:
├── 1. Parameter Validation
│   ├── Validate required parameters
│   ├── Check host connectivity
│   └── Verify user permissions
├── 2. SSH Key Management
│   ├── Check for existing key pair
│   ├── Generate new keys if needed:
│   │   ├── ssh-keygen -t rsa -b 4096 -f /data/ssh/id_rsa
│   │   ├── Set permissions: chmod 600 id_rsa, chmod 644 id_rsa.pub
│   │   └── Optional passphrase protection
│   └── Validate key pair integrity
├── 3. Public Key Distribution
│   ├── For each target host:
│   │   ├── Test SSH connectivity (with password)
│   │   ├── Create ~/.ssh directory if needed
│   │   ├── Append public key to ~/.ssh/authorized_keys
│   │   ├── Set proper permissions (700 ~/.ssh, 600 authorized_keys)
│   │   └── Validate key-based login
│   └── Log success/failure per host
├── 4. AutoUpdater Configuration
│   ├── Backup existing configuration
│   ├── Update appsettings.json:
│   │   ├── Add SshKeyPath
│   │   ├── Add SshKeyPassphrase (if provided)
│   │   ├── Remove/comment SshPwd
│   │   └── Set SshAuthMethod
│   └── Validate configuration syntax
├── 5. Connectivity Testing
│   ├── Test SSH connection with keys for each host
│   ├── Execute simple command (whoami, pwd)
│   ├── Verify Docker access if needed
│   └── Report connectivity status
└── 6. Final Report
    ├── Summary of actions taken
    ├── List of successful/failed hosts
    ├── Next steps for user
    └── Troubleshooting guidance
```

### Docker Integration

#### Volume Configuration
```yaml
version: '3.8'
services:
  autoupdater:
    image: modelingevolution/autoupdater:latest
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
      - ./data:/data
      - ./data/ssh:/root/.ssh:ro  # SSH keys read-only
    environment:
      - SshUser=deploy
      - SshKeyPath=/data/ssh/id_rsa
```

#### Dockerfile Considerations
```dockerfile
# Add SSH client and setup
RUN apt-get update && apt-get install -y openssh-client

# Create SSH directory structure
RUN mkdir -p /data/ssh && chmod 700 /data/ssh

# SSH configuration template
COPY ssh-config-template /data/ssh/config.template
```

### Code Implementation

#### SSH Connection Class
```csharp
public class SshConnectionManager
{
    private readonly SshConfiguration _config;
    
    public SshConnectionManager(SshConfiguration config)
    {
        _config = config;
    }
    
    public async Task<SshClient> CreateConnectionAsync()
    {
        var connectionInfo = _config.AuthMethod switch
        {
            SshAuthMethod.Password => CreatePasswordAuth(),
            SshAuthMethod.PrivateKey => CreateKeyAuth(),
            SshAuthMethod.PrivateKeyWithPassphrase => CreateKeyAuthWithPassphrase(),
            SshAuthMethod.KeyWithPasswordFallback => CreateKeyAuthWithFallback(),
            _ => throw new NotSupportedException($"Auth method {_config.AuthMethod} not supported")
        };
        
        var client = new SshClient(connectionInfo);
        await client.ConnectAsync();
        return client;
    }
    
    private ConnectionInfo CreateKeyAuth()
    {
        var keyFile = new PrivateKeyFile(_config.KeyPath);
        return new ConnectionInfo(_config.Host, _config.User, 
            new PrivateKeyAuthenticationMethod(_config.User, keyFile));
    }
    
    private ConnectionInfo CreateKeyAuthWithPassphrase()
    {
        var keyFile = new PrivateKeyFile(_config.KeyPath, _config.Passphrase);
        return new ConnectionInfo(_config.Host, _config.User,
            new PrivateKeyAuthenticationMethod(_config.User, keyFile));
    }
    
    private ConnectionInfo CreateKeyAuthWithFallback()
    {
        var methods = new List<AuthenticationMethod>();
        
        // Try key authentication first
        try
        {
            var keyFile = new PrivateKeyFile(_config.KeyPath, _config.Passphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(_config.User, keyFile));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load SSH key, will use password fallback");
        }
        
        // Add password fallback
        if (!string.IsNullOrEmpty(_config.Password))
        {
            methods.Add(new PasswordAuthenticationMethod(_config.User, _config.Password));
        }
        
        return new ConnectionInfo(_config.Host, _config.User, methods.ToArray());
    }
}
```

#### Configuration Model
```csharp
public class SshConfiguration
{
    public string Host { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public string? Password { get; set; }
    public string? KeyPath { get; set; }
    public string? Passphrase { get; set; }
    public SshAuthMethod AuthMethod { get; set; } = SshAuthMethod.Password;
    public int Port { get; set; } = 22;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}

public enum SshAuthMethod
{
    Password,
    PrivateKey,
    PrivateKeyWithPassphrase,
    KeyWithPasswordFallback
}
```

## Security Considerations

### Key Management
1. **Private Key Storage**: Never in Docker images, only in mounted volumes
2. **File Permissions**: 600 for private keys, 644 for public keys
3. **Passphrase Protection**: Optional but recommended for production
4. **Key Rotation**: Regular rotation with zero-downtime deployment

### Network Security
1. **SSH Protocol**: Uses strong encryption by default
2. **Host Key Verification**: Validate remote host identity
3. **Key Exchange**: Secure initial key distribution process
4. **Audit Trail**: Log all SSH operations and key usage

### Operational Security
1. **Backup Strategy**: Secure backup of private keys
2. **Access Control**: Limit access to key files and SSH configuration
3. **Monitoring**: Alert on failed authentication attempts
4. **Revocation**: Process for revoking compromised keys

## Migration Strategy

### Phase 1: Implementation
1. Add SSH key authentication support to codebase
2. Create installation script
3. Update documentation
4. Maintain backward compatibility with passwords

### Phase 2: Testing
1. Test key-based authentication in development
2. Validate installation script on various platforms
3. Performance testing with key authentication
4. Security review of implementation

### Phase 3: Deployment
1. Deploy with hybrid configuration (key + password fallback)
2. Run installation script on production systems
3. Monitor for authentication issues
4. Gradually disable password fallback

### Phase 4: Cleanup
1. Remove password-based authentication code
2. Update documentation to reflect key-only authentication
3. Security audit of final implementation
4. User training and support documentation

## Testing Strategy

### Unit Tests
- SSH connection management
- Key loading and validation
- Configuration parsing
- Error handling scenarios

### Integration Tests
- End-to-end SSH connectivity
- Docker container key mounting
- Multi-host key distribution
- Fallback authentication scenarios

### Security Tests
- Key permission validation
- Passphrase protection
- Host key verification
- Authentication method precedence

## Documentation Requirements

### User Documentation
1. **Installation Guide**: Step-by-step SSH key setup
2. **Configuration Reference**: All SSH-related settings
3. **Troubleshooting Guide**: Common issues and solutions
4. **Security Best Practices**: Key management recommendations

### Developer Documentation
1. **API Reference**: SSH connection classes and methods
2. **Architecture Overview**: SSH integration design
3. **Testing Guide**: How to test SSH functionality
4. **Contributing Guide**: Adding new SSH features

## Success Criteria

### Functional Success
- ✅ SSH key authentication works for all target hosts
- ✅ Installation script completes without errors
- ✅ Configuration migration from password to keys successful
- ✅ Fallback authentication works when configured

### Security Success
- ✅ No private keys stored in Docker images
- ✅ Proper file permissions enforced
- ✅ Passphrase protection available
- ✅ No password transmission over network

### Operational Success
- ✅ Zero-downtime deployment during migration
- ✅ Clear error messages and logging
- ✅ Documentation complete and accurate
- ✅ User feedback positive on ease of use