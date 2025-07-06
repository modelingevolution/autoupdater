# AutoUpdater Process Flow

This document describes the detailed update process flow for the ModelingEvolution.AutoUpdater system.

## Overview

The AutoUpdater system monitors Git repositories for new tagged versions and automatically updates Docker Compose deployments on remote systems via SSH. The process involves version detection, Git operations, Docker authentication, and remote execution.

## Main Components

- **UpdateHost**: Main hosted service managing Docker container updates
- **UpdateProcessManager**: Orchestrates updates across multiple packages
- **DockerComposeConfiguration**: Represents deployable packages with Git version control
- **GitTagVersion**: Version management using Git tags

## Detailed Update Flow

### 1. Initialization Phase

```
UpdateHost Service Start
├── Load configuration from appsettings.json
├── Initialize DockerComposeConfigurationRepository
├── Detect running container and extract volume mappings
├── Start background monitoring timer
└── Register with DI container
```

#### 1.1 Container Detection
- **Purpose**: Identify the AutoUpdater's own container to understand volume mappings
- **Process**: 
  - Search Docker containers for `modelingevolution/autoupdater` image
  - Extract volume mappings from container configuration
  - Map container paths to host paths for SSH operations

#### 1.2 Volume Mapping Resolution
- **Container Path**: `/data/repos/myproject`
- **Host Path**: `/home/user/docker-volumes/data/repos/myproject`
- **Mapping**: Used to translate paths when executing SSH commands on host

### 2. Monitoring Phase

```
Background Timer (every X minutes)
├── UpdateProcessManager.UpdateAll()
├── Iterate through configured packages
├── For each package:
│   ├── Check for available updates
│   ├── Determine if update is needed
│   └── Execute update if required
└── Log results and errors
```

### 3. Update Detection Process

```
For each DockerComposeConfiguration:
├── 3.1 Repository Validation
├── 3.2 Git Operations
├── 3.3 Version Comparison
└── 3.4 Update Decision
```

#### 3.1 Repository Validation
```
IsGitVersioned Check
├── Directory exists: RepositoryLocation
├── .git folder exists: RepositoryLocation/.git
├── If not versioned:
│   ├── Create directory if needed
│   ├── Clone repository: Repository.Clone(RepositoryUrl, RepositoryLocation)
│   └── Set up Git tracking
└── Continue with Git operations
```

#### 3.2 Git Operations
```
Git Version Fetching
├── Open repository: new Repository(RepositoryLocation)
├── Get remote specifications: repo.Network.Remotes["origin"].FetchRefSpecs
├── Configure fetch options:
│   ├── TagFetchMode = TagFetchMode.All
│   └── Fetch all tags from remote
├── Execute fetch: Commands.Fetch(repo, "origin", refSpecs, fetchOptions, null)
├── Parse tags: repo.Tags
├── Filter valid versions: GitTagVersion.TryParse()
├── Cache results for 10 seconds
└── Return available versions
```

#### 3.3 Version Comparison
```
Current Version Detection
├── Read deployment state: ComposeFolderPath/deployment.state.json
├── Parse DeploymentState.Version
├── Compare with available versions
├── Filter versions > current version
└── Select latest available version
```

#### 3.4 Update Decision
```
Update Required Check
├── Latest version available?
├── Current version != latest version?
├── Latest version > current version?
├── If yes: Proceed with update
└── If no: Skip update
```

### 4. Update Execution Process

```
DockerComposeConfiguration.Update(UpdateHost host)
├── 4.1 Version Checkout
├── 4.2 Path Resolution
├── 4.3 Docker Compose Command Preparation
├── 4.4 SSH Execution
└── 4.5 State Update
```

#### 4.1 Version Checkout
```
Git Checkout Process
├── Open repository: new Repository(RepositoryLocation)
├── Find tag: repo.Tags[version]
├── Validate tag exists
├── Configure checkout options:
│   ├── CheckoutModifiers = None
│   └── CheckoutNotifyFlags = None
├── Execute checkout: Commands.Checkout(repo, tag.Target.Sha, options)
└── Working directory now at target version
```

#### 4.2 Path Resolution
```
Host Path Mapping
├── Container path: ComposeFolderPath
├── Volume mappings: host.Volumes
├── Resolve host path: GetHostDockerComposeFolder()
├── Example transformation:
│   ├── Container: /data/repos/myproject/docker
│   └── Host: /home/user/volumes/data/repos/myproject/docker
└── Validate path exists on host
```

#### 4.3 Docker Compose Command Preparation
```
Command Building
├── Find Docker Compose files: docker-compose*.yml
├── Sort by filename length (priority)
├── Build file arguments: -f docker-compose.yml -f docker-compose.override.yml
├── Generate log filename: ~/project/docker_compose_up_d_YYYYMMDD_HHMMSS.log
├── Construct command: nohup docker compose -f ... up -d > logfile 2>&1 &
└── Command ready for SSH execution
```

#### 4.4 SSH Execution
```
Remote Command Execution
├── Connect to SSH host using configured credentials
├── Change to target directory: dockerComposeFolder
├── Execute command: nohup docker compose -f ... up -d > logfile 2>&1 &
├── Command runs in background (nohup &)
├── Capture command execution result
└── Handle SSH connection cleanup
```

#### 4.5 State Update
```
Deployment State Recording
├── Create DeploymentState object:
│   ├── Version: latest.ToString()
│   └── Timestamp: DateTime.Now
├── Serialize to JSON: JsonSerializer.Serialize(state)
├── Write to file: ComposeFolderPath/deployment.state.json
├── File serves as current version marker
└── Next update cycle will read this state
```

### 5. Docker Registry Authentication

```
Docker Authentication Flow
├── Configuration loading:
│   ├── DockerAuth: base64-encoded credentials
│   └── DockerRegistryUrl: registry URL (default: Docker Hub)
├── Registry URL resolution:
│   ├── If DockerRegistryUrl provided: use specified registry
│   └── If not provided: default to "https://index.docker.io/v1/"
├── Add to DockerAuths collection: DockerRegistryPat(registry, auth)
└── Available for future Docker login operations
```

### 6. Error Handling and Recovery

```
Error Scenarios and Handling
├── Git operations failed:
│   ├── Log error with package name
│   ├── Continue with next package
│   └── Don't interrupt other updates
├── SSH connection failed:
│   ├── Log connection error
│   ├── Retry logic (if configured)
│   └── Mark update as failed
├── Docker Compose command failed:
│   ├── Error captured in log file
│   ├── State not updated (keeps previous version)
│   └── Next cycle will retry
└── Path resolution failed:
    ├── Volume mapping not found
    ├── Log critical error
    └── Skip package update
```

### 7. Logging and Monitoring

```
Logging Points
├── Update process start/completion
├── Package-level update start/success/failure
├── Git operations (fetch, checkout)
├── SSH command execution
├── Version comparisons
├── Error conditions with full exception details
└── Performance metrics (update duration)
```

## Configuration Flow

```
Configuration Loading
├── appsettings.json parsing
├── Environment variable override
├── DockerComposeConfiguration creation:
│   ├── RepositoryLocation: Local Git repository path
│   ├── RepositoryUrl: Remote Git repository URL
│   ├── DockerComposeDirectory: Relative path to docker-compose files
│   ├── DockerAuth: Base64-encoded Docker registry credentials
│   └── DockerRegistryUrl: Custom Docker registry URL
├── Validation:
│   ├── Required fields present
│   ├── Paths accessible
│   └── Git repository reachable
└── Repository registration in DI container
```

## Security Considerations

1. **SSH Credentials**: Stored in configuration, used for remote execution
2. **Docker Registry Auth**: Base64-encoded, stored in memory
3. **Git Access**: Uses system Git configuration and credentials
4. **File System Access**: Volume mappings must be properly configured
5. **Remote Execution**: Commands executed with SSH user privileges

## Performance Optimizations

1. **Version Caching**: Git tag fetching cached for 10 seconds
2. **Lazy Loading**: Docker Compose services created on-demand
3. **Background Processing**: Updates run asynchronously
4. **Batch Operations**: Multiple packages updated in sequence
5. **SSH Connection Reuse**: Single connection per update cycle

## Failure Recovery

1. **State Persistence**: deployment.state.json tracks successful deployments
2. **Retry Logic**: Failed updates retried on next monitoring cycle
3. **Rollback Capability**: Previous version state maintained
4. **Log Analysis**: Detailed logging for troubleshooting
5. **Health Monitoring**: Container and service status tracking