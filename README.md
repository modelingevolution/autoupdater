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

Add configuration to your `appsettings.json` or environment variables:

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

## Architecture

The AutoUpdater monitors Git repositories for new tagged versions and automatically updates Docker Compose deployments on remote systems via SSH.

### Key Components

- **UpdateHost**: Main hosted service managing Docker container updates
- **UpdateProcessManager**: Orchestrates updates across multiple packages
- **DockerComposeConfiguration**: Represents deployable packages with Git version control
- **GitTagVersion**: Version management using Git tags

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