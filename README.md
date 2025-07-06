# ModelingEvolution.AutoUpdater

[![CI](https://github.com/modelingevolution/autoupdater/actions/workflows/ci.yml/badge.svg)](https://github.com/modelingevolution/autoupdater/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/ModelingEvolution.AutoUpdater.svg)](https://www.nuget.org/packages/ModelingEvolution.AutoUpdater/)

A comprehensive Docker-based auto-updater for IoT devices and containerized applications. This library provides self-updating containers, git-based configuration management, and system updates via SSH.

## Features

- **Git-based Configuration Management**: Track deployment versions using Git tags
- **Docker Integration**: Seamless integration with Docker and Docker Compose
- **SSH Remote Updates**: Execute updates on remote systems via SSH
- **Version Management**: Automatic version detection and upgrade availability checking
- **Flexible Authentication**: Support for Docker registry authentication
- **Blazor UI**: Web-based management interface for monitoring and controlling updates
- **Background Services**: Hosted services for continuous monitoring and updating

## Installation

### NuGet Package
```bash
dotnet add package ModelingEvolution.AutoUpdater
```

### Docker Image
```bash
docker pull modelingevolution/autoupdater:latest
```

## Quick Start

### 1. Configure Services

```csharp
using ModelingEvolution.AutoUpdater;

var builder = WebApplication.CreateBuilder(args);

// Add AutoUpdater services
builder.Services.AddAutoUpdater();

var app = builder.Build();
app.Run();
```

### 2. Configuration

Add configuration to your `appsettings.json`:

```json
{
  "SshUser": "your-ssh-user",
  "SshPwd": "your-ssh-password",
  "Packages": [
    {
      "RepositoryLocation": "/path/to/local/repo",
      "RepositoryUrl": "https://github.com/your-org/your-repo.git",
      "DockerComposeDirectory": "./",
      "DockerIoAuth": "base64-encoded-auth"
    }
  ]
}
```

### 3. Basic Usage

```csharp
// Inject services
public class MyService
{
    private readonly UpdateProcessManager _updateManager;
    private readonly DockerComposeConfigurationRepository _configRepo;

    public MyService(
        UpdateProcessManager updateManager,
        DockerComposeConfigurationRepository configRepo)
    {
        _updateManager = updateManager;
        _configRepo = configRepo;
    }

    public async Task CheckForUpdates()
    {
        var packages = _configRepo.GetPackages();
        
        foreach (var package in packages)
        {
            if (package.IsUpgradeAvailable())
            {
                Console.WriteLine($"Update available for {package.FriendlyName}: {package.AvailableUpgrade()}");
            }
        }
    }

    public async Task UpdateAll()
    {
        await _updateManager.UpdateAll();
    }
}
```

## Architecture

### Core Components

- **`UpdateHost`**: Main hosted service that manages Docker container updates via SSH
- **`UpdateProcessManager`**: Orchestrates updates across multiple packages
- **`DockerComposeConfiguration`**: Represents a deployable package with Git version control
- **`DockerComposeConfigurationRepository`**: Manages multiple deployment packages
- **`GitTagVersion`**: Version management using Git tags

### Update Process

1. **Version Detection**: Monitor Git repositories for new tagged versions
2. **Update Availability**: Compare current deployed version with latest available
3. **Git Checkout**: Switch to the target version tag in the repository
4. **Docker Compose Update**: Execute `docker compose up -d` on the target system
5. **State Tracking**: Record successful deployments with timestamps

## Configuration Options

### Package Configuration

```json
{
  "RepositoryLocation": "/local/path/to/repo",
  "RepositoryUrl": "https://github.com/org/repo.git",
  "DockerComposeDirectory": "./docker",
  "MergerName": "AutoUpdater",
  "MergerEmail": "updater@example.com",
  "DockerIoAuth": "base64-docker-auth"
}
```

### SSH Configuration

```json
{
  "SshUser": "deployment-user",
  "SshPwd": "secure-password"
}
```

## Advanced Features

### Custom Docker Registry Authentication

```csharp
var config = new DockerComposeConfiguration
{
    RepositoryLocation = "/path/to/repo",
    RepositoryUrl = "https://github.com/org/repo.git",
    DockerIoAuth = Convert.ToBase64String(
        Encoding.UTF8.GetBytes("username:password"))
};
```

### Volume Mapping Detection

The system automatically detects Docker volume mappings to translate container paths to host paths:

```csharp
var volumeMappings = await UpdateHost.GetVolumeMappings(containerId);
```

### Version Management

```csharp
// Check available versions
var versions = package.Versions();
var latestVersion = package.AvailableUpgrade();

// Manual checkout
package.Checkout(new GitTagVersion("v1.2.3", new Version(1, 2, 3)));
```

## Blazor UI

The AutoUpdater includes a web-based management interface built with Blazor Server and MudBlazor:

### Setup Host Application

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddMudServices()
    .AddAutoUpdater()
    .AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
```

## Docker Integration

### Dockerfile Example

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY . .
ENTRYPOINT ["dotnet", "YourApp.dll"]
```

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

## Security Considerations

- **SSH Authentication**: Use key-based authentication instead of passwords in production
- **Docker Socket**: Mounting Docker socket requires elevated privileges
- **Registry Authentication**: Store Docker registry credentials securely
- **Network Access**: Ensure proper firewall rules for SSH access

## Contributing

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Add tests for new functionality
5. Submit a pull request

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Support

- GitHub Issues: [Report bugs or request features](https://github.com/modelingevolution/autoupdater/issues)
- Documentation: [Wiki](https://github.com/modelingevolution/autoupdater/wiki)