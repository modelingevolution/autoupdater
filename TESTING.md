# AutoUpdater End-to-End Testing Guide

This document describes the complete testing setup and workflow for the ModelingEvolution.AutoUpdater system.

## Overview

The testing infrastructure simulates a real-world deployment scenario where:
1. **Application Repository** (`version-app`) contains the source code
2. **Compose Repository** (`version-app-compose`) contains deployment configurations
3. **AutoUpdater** monitors the compose repository for new versions
4. **Version Mapping**: Compose `v0.X.Y` → App `v1.X.Y` (compose version + 1.0.0)

## Repository Structure

```
autoupdater/
├── examples/
│   ├── testproject/          # Git submodule: version-app repository
│   │   ├── src/Program.cs    # .NET 9 Minimal API
│   │   ├── Dockerfile        # Multi-stage build with version args
│   │   └── build.sh          # Automated build script
│   ├── compose/              # Git submodule: version-app-compose repository
│   │   ├── docker-compose.yml # Deployment configuration
│   │   └── README.md         # Compose documentation
│   ├── integration-tests/    # Docker integration test environment
│   ├── test-update-flow.sh   # End-to-end test orchestration
│   └── local-test-config.json # Local testing configuration
├── tests/
│   ├── e2e-scenarios.feature # BDD test scenarios
│   └── integration-scenarios.md # Detailed scenarios
└── TESTING.md               # This document
```

## Quick Start

### 1. Prerequisites

```bash
# Required tools
- Docker and docker-compose
- Git with submodule support
- curl and jq for API testing
- .NET 9 SDK (for local builds)

# Clone with submodules
git clone --recursive https://github.com/modelingevolution/autoupdater.git
cd autoupdater
```

### 2. Build Initial Versions

```bash
# Build version-app:1.0.0
cd examples/testproject
./build.sh -v 1.0.0

# Verify image
docker images | grep versionapp
```

### 3. Run AutoUpdater

```bash
# Option A: Use local configuration
docker run -d -p 8080:8080 \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -v $(pwd)/examples:/data/repos \
  -v $(pwd)/examples/local-test-config.json:/app/appsettings.json:ro \
  modelingevolution/autoupdater:latest

# Option B: Use integration test environment
cd examples/integration-tests
./setup-test-env.sh
docker-compose -f docker-compose.test.yml up -d
```

### 4. Run End-to-End Test

```bash
# Full automated test
./examples/test-update-flow.sh

# Test with specific versions
./examples/test-update-flow.sh --to-app-version 1.0.5 --to-compose-version v0.0.5

# Manual trigger test
./examples/test-update-flow.sh --manual-trigger
```

## Detailed Test Scenarios

### Scenario 1: Complete Update Flow

**Objective**: Test the full version update process from app changes to deployment

**Steps**:
1. **Initial State**: version-app:1.0.0, compose:v0.0.0
2. **Build New Version**: `./build.sh -v 1.0.5`
3. **Update Compose**: Modify docker-compose.yml to reference versionapp:1.0.5
4. **Tag & Push**: `git tag v0.0.5 && git push origin v0.0.5`
5. **AutoUpdater Detection**: Monitors compose repo, detects v0.0.5
6. **Update Execution**: Pulls compose repo, checks out v0.0.5, runs docker-compose up -d
7. **Verification**: GET /version returns {"version":"1.0.5"}

**Expected Results**:
- AutoUpdater API shows upgrade available
- Update process completes without errors
- New container runs correct version
- Deployment state updated to v0.0.5

### Scenario 2: SSH Key Authentication

**Objective**: Verify SSH key-based authentication works correctly

**Setup**:
```bash
# Generate SSH keys
mkdir -p ./data/ssh
ssh-keygen -t rsa -b 4096 -f ./data/ssh/id_rsa -N ""

# Install public key (replace with your setup)
ssh-copy-id -i ./data/ssh/id_rsa.pub user@target-host
```

**Configuration**:
```json
{
  "SshUser": "deploy",
  "SshAuthMethod": "PrivateKey", 
  "SshKeyPath": "/data/ssh/id_rsa"
}
```

**Verification**:
- AutoUpdater logs show successful SSH key authentication
- No password prompts or authentication failures
- Docker commands execute successfully via SSH

### Scenario 3: API Endpoint Testing

**Test All AutoUpdater Endpoints**:

```bash
# List packages
curl http://localhost:8080/api/packages

# Check upgrade status
curl http://localhost:8080/api/upgrades/version-app-compose

# Trigger single update
curl -X POST http://localhost:8080/api/update/version-app-compose

# Update all packages
curl -X POST http://localhost:8080/api/update-all
```

**Expected Responses**:
- JSON responses with correct structure
- Appropriate HTTP status codes
- Accurate version information
- Update process tracking

## Test Orchestration Scripts

### test-update-flow.sh

**Purpose**: Automates the complete end-to-end update flow

**Key Features**:
- ✅ Builds new application versions
- ✅ Updates compose configurations
- ✅ Triggers Git operations (commit, tag, push)
- ✅ Monitors AutoUpdater detection
- ✅ Verifies update completion
- ✅ Comprehensive error handling

**Usage Examples**:
```bash
# Basic test
./test-update-flow.sh

# Custom versions
./test-update-flow.sh \
  --to-app-version 1.1.0 \
  --to-compose-version v0.1.0

# Skip build (use existing images)
./test-update-flow.sh --skip-build

# Manual API trigger
./test-update-flow.sh --manual-trigger

# Remote registry
./test-update-flow.sh \
  --registry ghcr.io/modelingevolution \
  --to-app-version 1.0.5
```

### build.sh (in testproject)

**Purpose**: Builds and tests VersionApp Docker images

**Features**:
- ✅ Multi-version Docker builds
- ✅ Automated endpoint testing
- ✅ Registry push support
- ✅ Local validation

**Usage Examples**:
```bash
# Build locally
./build.sh -v 1.0.5

# Build and push to registry
./build.sh -v 1.0.5 --push --registry ghcr.io/modelingevolution

# Custom image name
./build.sh -v 1.0.5 --name custom-app
```

## Version Management

### Version Mapping Strategy

| Compose Tag | App Version | Image Tag | Use Case |
|-------------|-------------|-----------|----------|
| v0.0.0 | 1.0.0 | versionapp:1.0.0 | Initial baseline |
| v0.0.5 | 1.0.5 | versionapp:1.0.5 | Patch update |
| v0.1.0 | 1.1.0 | versionapp:1.1.0 | Minor update |
| v1.0.0 | 2.0.0 | versionapp:2.0.0 | Major update |

### Release Process

1. **Development**:
   ```bash
   cd examples/testproject
   # Make code changes
   git add .
   git commit -m "Feature: Add new endpoint"
   ```

2. **Build & Test**:
   ```bash
   ./build.sh -v 1.0.5
   # Script automatically tests endpoints
   ```

3. **Update Deployment**:
   ```bash
   cd ../compose
   # Update docker-compose.yml
   sed -i 's/versionapp:1.0.0/versionapp:1.0.5/g' docker-compose.yml
   git add docker-compose.yml
   git commit -m "Update to version-app 1.0.5"
   git tag v0.0.5
   git push origin master
   git push origin v0.0.5
   ```

4. **Monitor AutoUpdater**:
   ```bash
   # Check detection
   curl http://localhost:8080/api/upgrades/version-app-compose
   
   # Monitor logs
   docker logs autoupdater-container -f
   ```

## Troubleshooting

### Common Issues

**1. AutoUpdater Not Detecting Updates**
```bash
# Check configuration
curl http://localhost:8080/api/packages

# Verify Git access
docker exec autoupdater-container git ls-remote https://github.com/modelingevolution/version-app-compose.git

# Check logs
docker logs autoupdater-container | grep -i "version-app-compose"
```

**2. SSH Authentication Failures**
```bash
# Verify key permissions
ls -la ./data/ssh/
# Should be: -rw------- id_rsa, -rw-r--r-- id_rsa.pub

# Test SSH manually
ssh -i ./data/ssh/id_rsa user@target-host "echo 'SSH works'"

# Check AutoUpdater SSH config
curl http://localhost:8080/api/packages  # Look for SSH config in logs
```

**3. Docker Compose Failures**
```bash
# Verify compose file syntax
cd examples/compose
docker-compose config

# Test manual deployment
docker-compose up -d

# Check container health
docker ps
curl http://localhost:5000/health
```

**4. Version Mismatch Issues**
```bash
# Check running container
docker ps --format "table {{.Names}}\t{{.Image}}"

# Verify version endpoint
curl http://localhost:5000/version

# Check deployment state
cat ./examples/compose/deployment.state.json
```

### Debugging Commands

```bash
# AutoUpdater status
curl -s http://localhost:8080/api/packages | jq .

# Version app status  
curl -s http://localhost:5000/version | jq .

# Container inspection
docker inspect version-app-container

# Network connectivity
docker network ls
docker network inspect autoupdater_test-network

# Log analysis
docker logs autoupdater-container --tail 50
docker logs version-app-container --tail 20
```

## Performance Testing

### Load Testing
```bash
# Multiple concurrent updates
for i in {1..5}; do
  curl -X POST http://localhost:8080/api/update/version-app-compose &
done
wait
```

### Timing Measurements
```bash
# Detection time
time ./test-update-flow.sh --skip-build

# Update completion time
start_time=$(date +%s)
curl -X POST http://localhost:8080/api/update/version-app-compose
# Wait for completion
end_time=$(date +%s)
echo "Update took $((end_time - start_time)) seconds"
```

## Cleanup

### Remove Test Environment
```bash
# Stop containers
docker-compose -f examples/integration-tests/docker-compose.test.yml down -v

# Remove test images
docker rmi versionapp:1.0.0 versionapp:1.0.5 versionapp:1.1.0

# Clean up volumes
docker volume prune

# Reset git repositories to initial state
cd examples/compose
git checkout v0.0.0
cd ../testproject  
git checkout v1.0.0
```

## Continuous Integration

### GitHub Actions Integration
```yaml
name: AutoUpdater E2E Tests
on: [push, pull_request]
jobs:
  e2e-test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          submodules: recursive
      - name: Run E2E Tests
        run: |
          cd examples
          ./test-update-flow.sh --skip-push
```

### Local CI Testing
```bash
# Simulate CI environment
docker run --rm -v $(pwd):/workspace -w /workspace \
  ubuntu:latest bash -c "
    apt-get update && apt-get install -y docker.io git curl jq
    cd examples
    ./test-update-flow.sh --skip-push
  "
```

## Security Considerations

1. **SSH Keys**: Private keys should never be committed to repositories
2. **Registry Authentication**: Use secure methods for registry access
3. **Network Isolation**: Test environment should be isolated from production
4. **Secrets Management**: Use environment variables or secret stores
5. **Log Sanitization**: Ensure no sensitive data in logs

## Next Steps

1. **Advanced Scenarios**: Multi-service updates, dependency management
2. **Monitoring Integration**: Prometheus metrics, alerting
3. **Rollback Testing**: Automated rollback scenarios
4. **Performance Optimization**: Reduce update times, parallel processing
5. **Production Deployment**: Hardening, monitoring, backup strategies