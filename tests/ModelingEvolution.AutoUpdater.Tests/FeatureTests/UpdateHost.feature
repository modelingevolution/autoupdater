Feature: AutoUpdater Update Process
    As a DevOps engineer
    I want to update applications using Docker Compose
    So that I can deploy new versions safely with proper rollback capabilities

Background:
    Given I have an UpdateHost configured
    And the system has SSH connectivity
    And I have a valid Docker Compose configuration

Scenario: Successful update with migration scripts
    Given the current deployment version is "1.0.0"
    And a new version "1.1.0" is available
    And migration scripts exist for the update
    And no backup script is present
    And all services will be healthy after deployment
    When I perform an update
    Then the update should succeed
    And the version should be updated to "1.1.0"
    And migration scripts should be executed
    And all services should be healthy

Scenario: No update needed when already at latest version
    Given the current deployment version is "1.0.0"
    And the latest available version is "1.0.0"
    When I perform an update
    Then the update should succeed
    And no migration scripts should be executed
    And no Docker services should be restarted

Scenario: Update fails when backup creation fails
    Given the current deployment version is "1.0.0"
    And a new version "1.1.0" is available
    And a backup script is present
    But backup creation fails with "Insufficient disk space"
    When I perform an update
    Then the update should fail immediately
    And the error should mention "Backup creation failed"
    And no migration scripts should be executed

Scenario: Update fails during migration but recovers with backup
    Given the current deployment version is "1.0.0"
    And a new version "1.1.0" is available
    And migration scripts exist for the update
    And a backup script is present
    And backup creation succeeds
    But migration script execution fails
    When I perform an update
    Then the update should fail with recovery
    And a rollback should be performed using the backup
    And the error should mention "Migration failed"

Scenario: Update fails during Docker startup but recovers with backup
    Given the current deployment version is "1.0.0"
    And a new version "1.1.0" is available
    And migration scripts exist for the update
    And a backup script is present
    And backup creation succeeds
    And migration scripts execute successfully
    But Docker Compose startup fails
    When I perform an update
    Then the update should fail with recovery
    And a rollback should be performed using the backup
    And the error should mention "Docker startup failed"

Scenario: Update succeeds partially when non-critical services fail health check
    Given the current deployment version is "1.0.0"
    And a new version "1.1.0" is available
    And migration scripts exist for the update
    And no backup script is present
    And migration scripts execute successfully
    And Docker Compose starts successfully
    But some non-critical services fail health checks
    When I perform an update
    Then the update should result in partial success
    And the version should be updated to "1.1.0"
    And healthy services should remain running

Scenario: Update fails when critical services fail health check with backup recovery
    Given the current deployment version is "1.0.0"
    And a new version "1.1.0" is available
    And migration scripts exist for the update
    And a backup script is present
    And backup creation succeeds
    And migration scripts execute successfully
    And Docker Compose starts successfully
    But critical services fail health checks
    When I perform an update
    Then the update should fail with recovery
    And a rollback should be performed using the backup
    And the error should mention "Critical services unhealthy"

Scenario: Update fails with emergency rollback on unexpected error
    Given the current deployment version is "1.0.0"
    And a new version "1.1.0" is available
    And migration scripts exist for the update
    And a backup script is present
    And backup creation succeeds
    But an unexpected error occurs during health check
    When I perform an update
    Then the update should fail with recovery
    And an emergency rollback should be performed
    And the error should mention "Unexpected error"

Scenario: Migration failure without backup has no recovery options
    Given the current deployment version is "1.0.0"
    And a new version "1.1.0" is available
    And migration scripts exist for the update
    And no backup script is present
    But migration script execution fails
    When I perform an update
    Then the update should fail without recovery
    And the error should mention "No recovery possible without backup"