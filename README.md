# ModelingEvolution.AutoUpdater

[![CI](https://github.com/modelingevolution/autoupdater/actions/workflows/ci.yml/badge.svg)](https://github.com/modelingevolution/autoupdater/actions/workflows/ci.yml)
[![Docker](https://github.com/modelingevolution/autoupdater/actions/workflows/docker.yml/badge.svg)](https://github.com/modelingevolution/autoupdater/actions/workflows/docker.yml)
[![Test Results](https://github.com/modelingevolution/autoupdater/actions/workflows/test-results.yml/badge.svg)](https://github.com/modelingevolution/autoupdater/actions/workflows/test-results.yml)
[![Docker Hub](https://img.shields.io/docker/v/modelingevolution/autoupdater?label=Docker%20Hub)](https://hub.docker.com/r/modelingevolution/autoupdater)

A comprehensive Docker-based auto-updater for IoT devices and containerized applications. Supports self-updating containers, git-based configuration management, and system updates via SSH.

## 📊 Test Results and Coverage

View the latest test results and coverage reports at: [https://modelingevolution.github.io/autoupdater/](https://modelingevolution.github.io/autoupdater/)

## Features

- **Git-based Configuration Management**: Track deployment versions using Git tags
- **Docker Integration**: Seamless integration with Docker and Docker Compose
- **SSH Remote Updates**: Execute updates on remote systems via SSH
- **Version Management**: Automatic version detection and upgrade availability checking
- **Flexible Authentication**: Support for Docker registry authentication
- **Blazor UI**: Web-based management interface for monitoring and controlling updates
- **Background Services**: Hosted services for continuous monitoring and updating

## Installation

### Docker Image
```bash
docker pull modelingevolution/autoupdater:latest
```

## Quick Start

### Docker Compose

```yaml
version: '3.8'
services:
  autoupdater:
    image: modelingevolution/autoupdater:latest
    ports:
      - "8080:8080"
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
      - ./data:/data
    environment:
      - SshUser=deploy
      - SshPwd=password
```

### Configuration

#### SSH Key Authentication (Recommended)

For enhanced security, use SSH key-based authentication:

```json
{
  "SshUser": "deploy",
  "SshAuthMethod": "PrivateKey",
  "SshKeyPath": "/data/ssh/id_rsa",
  "Packages": [
    {
      "RepositoryLocation": "/data/repos/example-app",
      "RepositoryUrl": "https://github.com/your-org/example-app.git",
      "DockerComposeDirectory": "./",
      "DockerAuth": "base64-encoded-username:password",
      "DockerRegistryUrl": "https://index.docker.io/v1/"
    }
  ]
}
```

#### Password Authentication (Legacy)

```json
{
  "SshUser": "your-ssh-user",
  "SshPwd": "your-ssh-password",
  "SshAuthMethod": "Password",
  "Packages": [
    {
      "RepositoryLocation": "/path/to/local/repo",
      "RepositoryUrl": "https://github.com/your-org/your-repo.git",
      "DockerComposeDirectory": "./",
      "DockerAuth": "base64-encoded-username:password",
      "DockerRegistryUrl": "https://myregistry.example.com"
    }
  ]
}
```

## SSH Key Setup

### Automated Setup (Recommended)

Use the provided installation script to automatically set up SSH keys:

```bash
# Download and run the installation script
./install-ssh-keys.sh --user deploy --hosts "192.168.1.100,192.168.1.101"

# With custom key path and passphrase protection
./install-ssh-keys.sh --user deploy --hosts "server1,server2" --key-path ./custom/ssh --passphrase

# Test configuration without making changes
./install-ssh-keys.sh --user deploy --hosts "server1" --test-only
```

The script will:
1. Generate SSH key pair (RSA 4096-bit by default)
2. Install public key on target hosts
3. Test SSH connectivity
4. Update AutoUpdater configuration
5. Provide setup verification

### Manual Setup

If you prefer manual setup:

1. **Generate SSH key pair:**
   ```bash
   mkdir -p ./data/ssh
   ssh-keygen -t rsa -b 4096 -f ./data/ssh/id_rsa -C "autoupdater@$(hostname)"
   chmod 600 ./data/ssh/id_rsa
   chmod 644 ./data/ssh/id_rsa.pub
   ```

2. **Install public key on target hosts:**
   ```bash
   ssh-copy-id -i ./data/ssh/id_rsa.pub deploy@your-host
   ```

3. **Test SSH connectivity:**
   ```bash
   ssh -i ./data/ssh/id_rsa deploy@your-host "echo 'SSH key authentication successful'"
   ```

4. **Update configuration:**
   ```json
   {
     "SshUser": "deploy",
     "SshAuthMethod": "PrivateKey",
     "SshKeyPath": "/data/ssh/id_rsa"
   }
   ```

### Docker Volume Mounting

Ensure SSH keys are accessible in the container:

```yaml
version: '3.8'
services:
  autoupdater:
    image: modelingevolution/autoupdater:latest
    ports:
      - "8080:8080"
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
      - ./data:/data
      - ./data/ssh:/data/ssh:ro  # Mount SSH keys read-only
    environment:
      - SshUser=deploy
      - SshAuthMethod=PrivateKey
      - SshKeyPath=/data/ssh/id_rsa
```

### SSH Authentication Methods

| Method | Configuration | Use Case |
|--------|---------------|----------|
| `Password` | `SshUser` + `SshPwd` | Legacy, less secure |
| `PrivateKey` | `SshUser` + `SshKeyPath` | Most secure, recommended |
| `PrivateKeyWithPassphrase` | `SshUser` + `SshKeyPath` + `SshKeyPassphrase` | Enhanced security with passphrase |
| `KeyWithPasswordFallback` | All of the above | Transition period, tries key first |

## Architecture

The AutoUpdater monitors Git repositories for new tagged versions and automatically updates Docker Compose deployments on remote systems via SSH.

### Key Components

- **UpdateHost**: Main hosted service managing Docker container updates
- **UpdateProcessManager**: Orchestrates updates across multiple packages
- **DockerComposeConfiguration**: Represents deployable packages with Git version control
- **GitTagVersion**: Version management using Git tags

## Custom Docker Registries

The AutoUpdater supports authentication with any Docker registry:

### Configuration Examples

**Docker Hub (default)**:
```json
{
  "DockerAuth": "base64-encoded-username:password"
}
```

**Private Registry**:
```json
{
  "DockerAuth": "base64-encoded-username:password",
  "DockerRegistryUrl": "https://myregistry.example.com"
}
```

**Google Container Registry**:
```json
{
  "DockerAuth": "base64-encoded-_json_key:service-account-json",
  "DockerRegistryUrl": "https://gcr.io"
}
```

**Amazon ECR**:
```json
{
  "DockerAuth": "base64-encoded-AWS:token",
  "DockerRegistryUrl": "https://123456789.dkr.ecr.us-east-1.amazonaws.com"
}
```

### Programmatic Configuration

```csharp
var config = new DockerComposeConfiguration
{
    RepositoryLocation = "/path/to/repo",
    RepositoryUrl = "https://github.com/org/repo.git",
    DockerAuth = Convert.ToBase64String(
        Encoding.UTF8.GetBytes("username:password")),
    DockerRegistryUrl = "https://myregistry.example.com"
};
```

## Development

### Prerequisites
- .NET 9.0 SDK
- Docker Desktop
- Git

### Building
```bash
dotnet build
dotnet test
```

### Running locally
```bash
docker compose up
```

## Contributing

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Submit a pull request

## License

This project is licensed under the MIT License.