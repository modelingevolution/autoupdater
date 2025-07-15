# ModelingEvolution.AutoUpdater

[![CI](https://github.com/modelingevolution/autoupdater/actions/workflows/ci.yml/badge.svg)](https://github.com/modelingevolution/autoupdater/actions/workflows/ci.yml)
[![Docker](https://github.com/modelingevolution/autoupdater/actions/workflows/docker.yml/badge.svg)](https://github.com/modelingevolution/autoupdater/actions/workflows/docker.yml)
[![Test Results](https://github.com/modelingevolution/autoupdater/actions/workflows/test-results.yml/badge.svg)](https://github.com/modelingevolution/autoupdater/actions/workflows/test-results.yml)
[![Docker Hub](https://img.shields.io/docker/v/modelingevolution/autoupdater?label=Docker%20Hub)](https://hub.docker.com/r/modelingevolution/autoupdater)

A Docker-based auto-updater for containerized applications with Git-based version management and SSH deployment.

## Production Deployment

```bash
wget https://raw.githubusercontent.com/modelingevolution/autoupdater-compose/main/installation.sh
sudo ./installation.sh rocket-welder https://github.com/modelingevolution/rocketwelder-compose.git POC-400
```

See [autoupdater-compose](https://github.com/modelingevolution/autoupdater-compose) for production setup.

## 📊 Test Results and Coverage

View the latest test results and coverage reports at: [https://modelingevolution.github.io/autoupdater/](https://modelingevolution.github.io/autoupdater/)

## Features

- **Git-based Configuration Management**: Track deployment versions using Git tags
- **Docker Integration**: Seamless integration with Docker and Docker Compose
- **SSH Remote Updates**: Execute updates on remote systems via SSH
- **Version Management**: Automatic version detection and upgrade availability checking
- **Flexible Authentication**: Support for Docker registry authentication
- **Blazor UI**: Web-based management interface for monitoring and controlling updates
- **Runtime Configuration**: Edit Docker authentication and other settings through the web interface
- **Version Tracking**: Assembly version information with git commit hash displayed in the web interface
- **Background Services**: Hosted services for continuous monitoring and updating

## 🚀 Quick Start (Empty Project)

### Method 1: Using the Startup Script (Recommended)
```bash
# Clone the repository
git clone https://github.com/modelingevolution/autoupdater.git
cd autoupdater

# Run the startup script (automatically sets up everything)
./start.sh
```

### Method 2: Manual Docker Compose
```bash
# Set required environment variable
export SSH_USER=deploy

# Create minimal configuration
cp appsettings.example.json appsettings.json

# Start the services
docker-compose up --build
```

### Method 3: Using .env File
```bash
# Copy example environment file
cp .env.example .env

# Edit .env file with your settings
nano .env

# Start the services
docker-compose up --build
```

**🎉 The AutoUpdater will be available at: http://localhost:8080**

## Installation

### Docker Image
```bash
docker pull modelingevolution/autoupdater:latest
```

## Configuration

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
      - ./appsettings.json:/app/appsettings.json:ro  # Configuration mapping
      - /var/docker/data/autoupdater/appsettings.runtime.json:/app/appsettings.runtime.json  # Runtime configuration
    environment:
      - SshUser=deploy
      - SshAuthMethod=PrivateKey
      - SshKeyPath=/data/ssh/id_rsa
```

### Configuration

The AutoUpdater configuration is mapped through Docker Compose. Create an `appsettings.json` file in the root directory (not in the data folder).

### Runtime Configuration

The AutoUpdater supports runtime configuration changes through the web interface. Settings like Docker authentication can be modified per package and are stored in `appsettings.runtime.json`. This file should be mapped to a persistent volume to survive container restarts.

#### Docker Authentication Management

Docker authentication can be configured and edited through the web interface:

1. **Access the Packages page** in the AutoUpdater web interface
2. **Click the "Edit Auth" button** for any package to open the authentication dialog
3. **Enter Docker authentication** in the format `username:password` (will be automatically base64-encoded)
4. **Save changes** to update the runtime configuration immediately
5. **Reset to default** to remove custom authentication for a package

The authentication dialog supports multi-line input for complex authentication strings and provides real-time validation.

#### Version Information

The AutoUpdater displays version information in the bottom left of the navigation menu:

- **Development builds**: Shows `v1.0.0+dev` for local development
- **CI/CD builds**: Shows `v1.0.0 (abcd123)` with git commit hash for deployed versions
- **Version tracking**: Automatically embedded during Docker build process using assembly attributes

The version information helps identify which build is currently running and provides traceability back to the source code commit.

#### Installation Script (install.sh)

The install.sh script should be created in the deployment repository and should:

1. Create the required directory structure for persistent data
2. Set up the runtime configuration file mapping
3. Execute all up*.sh migration scripts in order
4. Start the AutoUpdater service

Example install.sh structure:
```bash
#!/bin/bash

# Create data directory for persistent storage
mkdir -p /var/docker/data/autoupdater

# Create runtime configuration file if it doesn't exist
touch /var/docker/data/autoupdater/appsettings.runtime.json

# Set proper permissions
chmod 644 /var/docker/data/autoupdater/appsettings.runtime.json

# Execute all migration scripts in order
for script in up-*.sh; do
    if [ -f "$script" ]; then
        echo "Executing $script..."
        bash "$script"
    fi
done

# Start AutoUpdater services
docker-compose up -d
```

#### Runtime Configuration Structure

Runtime configuration is automatically managed through the web interface. The structure is:
```json
{
  "DockerAuth": {
    "package-name": "base64-encoded-auth-string"
  }
}
```

**Note**: Docker authentication should be entered in the web interface as `username:password` format - the system automatically handles base64 encoding and storage in the runtime configuration file. 

#### Configuration Structure

The configuration supports two package arrays:
- **StdPackages**: Contains the autoupdater itself and system-critical packages
- **Packages**: Contains application packages to be managed

This separation ensures the autoupdater can update itself independently from the applications it manages.

#### Production Configuration

For production deployments, use `appsettings.Production.json`:

```json
{
  "SshUser": "deploy",
  "SshAuthMethod": "PrivateKey",
  "SshKeyPath": "/data/ssh/id_rsa",
  "StdPackages": [
    {
      "RepositoryLocation": "/data/repositories/autoupdater-compose",
      "RepositoryUrl": "https://github.com/modelingevolution/autoupdater-compose.git",
      "DockerComposeDirectory": "./"
    }
  ],
  "Packages": [
    {
      "RepositoryLocation": "/data/repositories/your-app-compose",
      "RepositoryUrl": "https://github.com/your-org/your-app-compose.git",
      "DockerComposeDirectory": "./"
    }
  ]
}
```

The default configuration monitors the AutoUpdater itself for updates, ensuring the updater stays up-to-date automatically.

#### SSH Key Authentication (Default)

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

#### Password Authentication (Not Recommended)

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
| `Password` | `SshUser` + `SshPwd` | Not recommended, less secure |
| `PrivateKey` | `SshUser` + `SshKeyPath` | Default, most secure |
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