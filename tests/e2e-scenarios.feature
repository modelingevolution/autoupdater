Feature: AutoUpdater End-to-End Integration Testing
  As a DevOps engineer
  I want AutoUpdater to automatically update containerized applications
  So that deployments are always running the latest versions

  Background:
    Given AutoUpdater is running with SSH key authentication
    And AutoUpdater is monitoring the version-app-compose repository
    And the current deployment state shows version "v0.0.0"
    And version-app container is running with image "versionapp:1.0.0"
    And the /version endpoint returns {"version":"1.0.0"}

  Scenario: Complete Version Update Flow
    Given the system is stable and healthy
    And AutoUpdater API endpoint GET /api/packages shows version-app-compose
    And AutoUpdater API endpoint GET /api/upgrades/version-app-compose shows no upgrade available

    When I develop and release a new application version:
      | Step | Action | Details |
      | 1 | Update application code | Modify source in version-app repository |
      | 2 | Build new Docker image | ./build.sh -v 1.0.5 |
      | 3 | Push image to registry | docker push versionapp:1.0.5 |
      | 4 | Update docker-compose.yml | Change image: versionapp:1.0.5 |
      | 5 | Commit compose changes | git commit -m "Update to version 1.0.5" |
      | 6 | Tag compose repository | git tag v0.0.5 |
      | 7 | Push compose repository | git push origin master && git push origin v0.0.5 |

    And I wait for AutoUpdater to detect the change (maximum 120 seconds)

    Then AutoUpdater should detect the upgrade:
      """
      GET /api/upgrades/version-app-compose returns:
      {
        "packageName": "version-app-compose",
        "currentVersion": "v0.0.0",
        "availableVersion": "v0.0.5", 
        "upgradeAvailable": true
      }
      """

    When I trigger the manual update:
      """
      POST /api/update/version-app-compose
      Response: {
        "packageName": "version-app-compose",
        "updateId": "<guid>",
        "status": "started",
        "message": "Update process initiated"
      }
      """

    Then the update process should execute:
      | Step | AutoUpdater Action | Verification |
      | 1 | Pull version-app-compose repository | Git fetch successful |
      | 2 | Checkout tag v0.0.5 | Working directory at v0.0.5 |
      | 3 | Execute docker-compose up -d via SSH | New container deployed |
      | 4 | Wait for service health check | Health endpoint responds |
      | 5 | Update deployment state | State file shows v0.0.5 |

    And the deployment should be updated:
      """
      GET http://localhost:5000/version returns {"version":"1.0.5"}
      Docker container is running image versionapp:1.0.5
      AutoUpdater deployment state file contains "v0.0.5"
      """

    And subsequent checks should show:
      """
      GET /api/upgrades/version-app-compose returns:
      {
        "currentVersion": "v0.0.5",
        "availableVersion": "v0.0.5",
        "upgradeAvailable": false
      }
      """

  Scenario: Automatic Update Detection and Execution
    Given the system is running version "v0.0.0"
    And AutoUpdater monitoring interval is set to 30 seconds

    When a new compose version "v0.1.0" is pushed to the repository
    And I wait for the next monitoring cycle (maximum 60 seconds)

    Then AutoUpdater should automatically detect and execute the update
    And the system should be running version "v0.1.0" referencing "versionapp:1.1.0"
    And GET /version should return {"version":"1.1.0"}

  Scenario: Update All Packages
    Given multiple packages are configured:
      | Package | Current Version | Available Version |
      | version-app-compose | v0.0.0 | v0.0.5 |
      | other-app-compose | v1.0.0 | v1.0.0 |

    When I call POST /api/update-all

    Then updates should be triggered for packages with available updates:
      """
      Response: {
        "updatesStarted": [
          {
            "packageName": "version-app-compose",
            "updateId": "<guid>",
            "fromVersion": "v0.0.0",
            "toVersion": "v0.0.5"
          }
        ],
        "skipped": [
          {
            "packageName": "other-app-compose", 
            "reason": "No update available"
          }
        ]
      }
      """

  Scenario: Update Failure Handling
    Given the system is running version "v0.0.0"
    And a new compose version "v0.0.6" is available
    But the docker-compose.yml in v0.0.6 references a non-existent image

    When I trigger the update via POST /api/update/version-app-compose
    And the docker-compose up command fails

    Then the update should fail gracefully:
      - Error is logged in AutoUpdater logs
      - Deployment state remains at "v0.0.0"
      - Original container continues running versionapp:1.0.0
      - GET /version still returns {"version":"1.0.0"}
      - Next monitoring cycle can retry or detect corrections

  Scenario: SSH Key Authentication
    Given AutoUpdater is configured with SSH private key at /data/ssh/id_rsa
    And the public key is installed on the Docker host
    And SSH password authentication is disabled

    When AutoUpdater attempts to execute docker-compose commands
    
    Then it should successfully authenticate using the SSH key:
      - No password prompts or failures
      - SSH commands execute successfully
      - Update process completes normally
      - Logs show successful SSH key authentication

  Scenario: Version Rollback
    Given the system is running version "v0.0.5" with versionapp:1.0.5
    And a rollback to version "v0.0.0" is required

    When I force checkout the compose repository to tag "v0.0.0"
    And trigger manual update via POST /api/update/version-app-compose

    Then the system should rollback to the previous version:
      - Container is running versionapp:1.0.0
      - GET /version returns {"version":"1.0.0"}
      - Deployment state shows "v0.0.0"

  @integration @docker @ssh
  Scenario: Full Integration Test Environment
    Given the integration test environment is running:
      - AutoUpdater container with SSH keys mounted
      - Test SSH host container accepting key authentication  
      - Version-app container accessible for API calls
      - All containers in test-network

    When I execute the complete test scenario
    Then all services should communicate successfully
    And the update flow should work end-to-end
    And cleanup should remove all test containers and images