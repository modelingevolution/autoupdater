# EventPi AutoUpdater Host - Requirements

## Overview

EventPi AutoUpdater Host is a web-based application management system that provides automated deployment and version management for Docker Compose applications. It bridges Git repository version control with containerized application deployment, offering a modern web interface for monitoring and controlling updates across multiple applications.

## Core Functionality

### 1. Git-Based Version Management

#### 1.1 Repository Integration
- **Git Repository Monitoring**: Track multiple Git repositories containing Docker Compose applications
- **Automatic Cloning**: Clone repositories on first access if not locally available
- **Remote Synchronization**: Fetch latest changes and tags from remote repositories
- **Tag-Based Versioning**: Use Git tags as version identifiers following semantic versioning (e.g., v1.2.3, ver2.1.0)

#### 1.2 Version Detection and Comparison
- **Current Version Tracking**: Maintain deployment state with version information in `deployment.state.json`
- **Available Version Discovery**: Enumerate Git tags to identify available versions
- **Upgrade Detection**: Compare current deployed version against available tags to identify upgrades
- **Semantic Version Parsing**: Parse version strings from Git tags (supports v1.2.3, ver1.2.3 formats)

### 2. Docker Compose Management

#### 2.1 Container Orchestration
- **Docker Compose File Support**: Handle single or multiple docker-compose.yml files per project
- **Remote Execution**: Execute Docker Compose commands on remote hosts via SSH
- **Volume Mapping Analysis**: Analyze container volume mappings to translate paths between container and host
- **Service Lifecycle Management**: Stop, update, and restart Docker Compose services

#### 2.2 Docker Registry Integration
- **Private Registry Support**: Authenticate with private Docker registries using PAT tokens
- **Multi-Registry Support**: Handle authentication for multiple registries simultaneously
- **Docker Hub Integration**: Built-in support for Docker Hub authentication

### 3. Web-Based User Interface

#### 3.1 Dashboard Features
- **Package Overview**: Display all configured packages with current versions
- **Update Status Indicators**: Show which packages have available upgrades
- **Real-time Updates**: Live UI updates using Blazor Server's SignalR connection
- **Modern UI Framework**: Built with MudBlazor Material Design components

#### 3.2 User Interactions
- **Individual Package Management**: View details for each configured package
- **Bulk Operations**: "Update All" functionality to upgrade all packages simultaneously
- **Update Confirmation**: Clear indication of available upgrades before proceeding

### 4. Network Management Integration

#### 4.1 VPN Control
- **WireGuard Integration**: Control WireGuard VPN connections (specifically wg0 interface)
- **NetworkManager Interface**: Use D-Bus communication with NetworkManager for network control
- **Connection Status Monitoring**: Real-time VPN connection status display
- **Toggle Control**: Simple on/off switch for VPN connectivity

## Technical Architecture

### 5. Application Stack

#### 5.1 Frontend
- **Framework**: ASP.NET Core Blazor Server (.NET 9.0)
- **UI Library**: MudBlazor for Material Design components
- **Real-time Communication**: SignalR for live updates
- **Observable Pattern**: ModelingEvolution.Observable for reactive UI updates

#### 5.2 Backend Services
- **Git Operations**: LibGit2Sharp for repository management
- **SSH Communication**: SSH.NET for remote command execution
- **Docker Integration**: Docker.DotNet and Ductus.FluentDocker for container management
- **Process Execution**: CliWrap for command-line operations

#### 5.3 Configuration Management
- **JSON Configuration**: appsettings.json with hot-reload support
- **Environment Variables**: Support for environment-based configuration
- **File-based Overrides**: Support for external configuration files

### 6. Deployment Architecture

#### 6.1 Containerization
- **Docker Container**: Runs as a containerized application
- **Multi-stage Build**: Optimized Dockerfile with development and production stages
- **Volume Mounting**: `/data` volume for persistent configuration and repositories
- **Docker Socket Access**: Requires access to host Docker daemon

#### 6.2 Host Communication
- **SSH Protocol**: Secure communication with Docker host
- **Network Discovery**: Automatic detection of Docker bridge gateway (172.17.0.1)
- **Path Translation**: Convert container paths to host paths using volume mappings

## Configuration Schema

### 7. Package Configuration

#### 7.1 Standard Package Format
```json
{
  "RepositoryLocation": "/data/repos/app-name",
  "RepositoryUrl": "https://github.com/user/repository.git",
  "DockerComposeDirectory": "./docker",
  "MergerName": "deployment-user",
  "MergerEmail": "admin@example.com",
  "DockerIoAuth": "base64-encoded-credentials"
}
```

#### 7.2 Configuration Sections
- **StdPackages**: Standard package configurations
- **Packages**: Additional package configurations
- **SSH Credentials**: `SshUser` and `SshPwd` for host communication
- **Docker Authentication**: Registry credentials for private repositories

### 8. Security Requirements

#### 8.1 Authentication & Authorization
- **SSH Key/Password Authentication**: Secure connection to Docker host
- **Docker Registry Authentication**: Support for private registry access
- **Volume Security**: Secure handling of mounted volumes and file permissions

#### 8.2 Network Security
- **TLS/SSL Support**: HTTPS communication capability
- **VPN Integration**: Secure network connectivity through WireGuard
- **Container Isolation**: Proper container security boundaries

## Operational Requirements

### 9. Monitoring and Logging

#### 9.1 Application Logging
- **Structured Logging**: Comprehensive logging using Microsoft.Extensions.Logging
- **SSH Operation Logging**: Log all remote commands and results
- **Update Process Tracking**: Log deployment operations and results
- **Error Handling**: Comprehensive error logging and reporting

#### 9.2 Health Monitoring
- **Service Health Checks**: Monitor application and dependent service health
- **Docker Connectivity**: Verify Docker daemon connectivity
- **SSH Connectivity**: Monitor SSH connection status
- **Repository Accessibility**: Verify Git repository access

### 10. Performance Requirements

#### 10.1 Scalability
- **Multiple Package Support**: Handle dozens of packages efficiently
- **Concurrent Operations**: Support simultaneous package updates
- **Resource Management**: Efficient memory and CPU usage
- **Network Optimization**: Minimize bandwidth usage for Git operations

#### 10.2 Reliability
- **Update Rollback**: Ability to rollback failed deployments
- **State Persistence**: Maintain deployment state across application restarts
- **Error Recovery**: Graceful handling of network and deployment failures
- **Transaction Safety**: Ensure atomic deployment operations

## Integration Requirements

### 11. External System Integration

#### 11.1 Git Providers
- **GitHub Support**: Full compatibility with GitHub repositories
- **GitLab Support**: Support for GitLab hosted repositories
- **Private Git Servers**: Compatible with self-hosted Git solutions
- **Authentication Methods**: Support for HTTPS and SSH Git authentication

#### 11.2 Container Registries
- **Docker Hub**: Native support for Docker Hub
- **Private Registries**: Support for enterprise container registries
- **Multi-Registry**: Simultaneous support for multiple registries
- **Authentication**: Token-based and credential-based authentication

### 12. Notification and Alerting

#### 12.1 Update Notifications
- **Available Update Alerts**: Notify when new versions are available
- **Deployment Status**: Report success/failure of deployment operations
- **Error Notifications**: Alert on deployment or connectivity errors

#### 12.2 Communication Channels
- **Web Interface**: Real-time notifications in the web UI
- **Email Integration**: Email notifications for critical events
- **Webhook Support**: Extensible webhook system for external integrations

## Non-Functional Requirements

### 13. Usability
- **Intuitive Interface**: Simple, clear web interface requiring minimal training
- **Responsive Design**: Compatible with desktop and mobile browsers
- **Accessibility**: WCAG compliant interface design
- **Documentation**: Comprehensive user and administrator documentation

### 14. Maintainability
- **Modular Architecture**: Clean separation of concerns
- **Configuration Management**: Externalized configuration
- **Update Mechanism**: Self-update capability for the AutoUpdater itself
- **Diagnostic Tools**: Built-in diagnostic and troubleshooting features

### 15. Compatibility
- **Operating System**: Linux host support (primary target)
- **Docker Version**: Compatible with Docker 20.10+ and Docker Compose v2
- **Browser Support**: Modern browser compatibility (Chrome, Firefox, Safari, Edge)
- **Network Protocols**: IPv4/IPv6 support

## Future Enhancements

### 16. Planned Features
- **Role-Based Access Control**: Multi-user support with permissions
- **API Interface**: RESTful API for programmatic access
- **Backup Integration**: Automated backup before deployments
- **Advanced Scheduling**: Cron-based update scheduling
- **Multi-Host Management**: Manage containers across multiple Docker hosts
- **Kubernetes Support**: Extend support to Kubernetes deployments
- **Advanced Notifications**: Slack, Discord, and other notification channels
- **Deployment Pipelines**: Multi-stage deployment workflows
- **Health Check Integration**: Application-specific health monitoring
- **Metrics and Analytics**: Deployment metrics and performance analytics