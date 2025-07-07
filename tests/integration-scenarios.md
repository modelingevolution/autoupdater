# AutoUpdater Integration Test Scenarios

## Feature: Automatic Docker Container Updates

As a DevOps engineer
I want the AutoUpdater to automatically update my Docker containers
So that my applications are always running the latest version

### Background
Given the AutoUpdater is running in a Docker container
And the TestProject API is running with version "1.0.0"
And the TestProject repository is configured in AutoUpdater
And SSH connectivity is established between AutoUpdater and host

### Scenario: Detect and Apply Version Update

Given the system is up and running
And the TestProject API responds to GET /version with "1.0.0"
And the AutoUpdater API shows no upgrades available

When I push a new commit to the TestProject repository
And I create a new Git tag "1.1.0"
And I wait for AutoUpdater to check for updates (max 30 seconds)

Then the AutoUpdater API endpoint GET /api/packages should list TestProject
And the AutoUpdater API endpoint GET /api/upgrades/testproject should return:
  """
  {
    "packageName": "testproject",
    "currentVersion": "1.0.0",
    "availableVersion": "1.1.0",
    "upgradeAvailable": true
  }
  """

When I call POST /api/update/testproject
Then the response should indicate update started
And I wait for the update to complete (max 60 seconds)
And the TestProject API responds to GET /version with "1.1.0"
And the Docker container for TestProject should be running the new version

### Scenario: Update All Packages

Given multiple packages are configured:
  | Package    | Current Version | Available Version |
  | testapp1   | 1.0.0          | 1.1.0            |
  | testapp2   | 2.0.0          | 2.1.0            |
  | testapp3   | 3.0.0          | 3.0.0            |

When I call POST /api/update-all
Then updates should be triggered for testapp1 and testapp2
And testapp3 should not be updated (no new version)
And all updated applications should be running their new versions

### Scenario: Handle Update Failure

Given the TestProject API is running with version "1.0.0"
And a new version "1.1.0" is available
But the Docker Compose file in version "1.1.0" has an error

When I call POST /api/update/testproject
Then the update should fail with an appropriate error message
And the TestProject API should still respond with version "1.0.0"
And the deployment state should remain at "1.0.0"

### Scenario: SSH Key Authentication

Given the AutoUpdater is configured with SSH key authentication
And the SSH private key is mounted at /data/ssh/id_rsa
And the public key is installed on the host system

When the AutoUpdater attempts to execute Docker commands
Then it should successfully authenticate using the SSH key
And no password should be transmitted

## API Endpoints Specification

### GET /api/packages
Returns list of all configured packages with their current status:
```json
{
  "packages": [
    {
      "name": "testproject",
      "repositoryUrl": "https://github.com/example/testproject.git",
      "currentVersion": "1.0.0",
      "lastChecked": "2024-01-15T10:30:00Z",
      "status": "running"
    }
  ]
}
```

### GET /api/upgrades/{packageName}
Check if upgrade is available for a specific package:
```json
{
  "packageName": "testproject",
  "currentVersion": "1.0.0",
  "availableVersion": "1.1.0",
  "upgradeAvailable": true,
  "changelog": "Bug fixes and performance improvements"
}
```

### POST /api/update/{packageName}
Trigger update for a specific package:
```json
{
  "packageName": "testproject",
  "updateId": "550e8400-e29b-41d4-a716-446655440000",
  "status": "started",
  "message": "Update process initiated"
}
```

### POST /api/update-all
Trigger updates for all packages with available upgrades:
```json
{
  "updatesStarted": [
    {
      "packageName": "testapp1",
      "updateId": "550e8400-e29b-41d4-a716-446655440001",
      "fromVersion": "1.0.0",
      "toVersion": "1.1.0"
    }
  ],
  "skipped": [
    {
      "packageName": "testapp3",
      "reason": "No update available"
    }
  ]
}
```

## Test Environment Setup

### Directory Structure
```
autoupdater/
├── examples/
│   ├── testproject/
│   │   ├── src/
│   │   │   └── Program.cs
│   │   ├── Dockerfile
│   │   ├── docker-compose.yml
│   │   └── TestProject.csproj
│   └── integration-tests/
│       ├── docker-compose.test.yml
│       ├── autoupdater-config/
│       │   └── appsettings.test.json
│       └── ssh-keys/
│           ├── id_rsa
│           └── id_rsa.pub
└── tests/
    └── ModelingEvolution.AutoUpdater.IntegrationTests/
        ├── AutoUpdaterIntegrationTests.cs
        ├── GitTestUtilities.cs
        └── DockerTestUtilities.cs
```

### Docker Compose Test Environment
```yaml
version: '3.8'
services:
  autoupdater:
    build: ../../
    ports:
      - "8080:8080"
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
      - ./test-repos:/data/repos
      - ./ssh-keys:/data/ssh:ro
    environment:
      - ASPNETCORE_ENVIRONMENT=Development
    networks:
      - test-network

  testproject:
    image: testproject:${TEST_VERSION:-1.0.0}
    ports:
      - "5000:5000"
    networks:
      - test-network

networks:
  test-network:
    driver: bridge
```

## Test Execution Flow

1. **Setup Phase**
   - Create temporary Git repository for TestProject
   - Build TestProject Docker image with version 1.0.0
   - Start docker-compose test environment
   - Wait for all services to be healthy

2. **Test Execution**
   - Execute BDD scenarios
   - Capture logs from all containers
   - Verify state transitions

3. **Cleanup Phase**
   - Stop all containers
   - Clean up temporary repositories
   - Remove test Docker images

## Success Criteria

- [ ] All BDD scenarios pass
- [ ] No manual intervention required
- [ ] Tests are repeatable and deterministic
- [ ] Clear error messages on failure
- [ ] Test execution time < 5 minutes
- [ ] Works on both Linux and Windows (WSL2)