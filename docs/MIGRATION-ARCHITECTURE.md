# AutoUpdater Migration Architecture

## Overview

The AutoUpdater system supports bidirectional migrations with comprehensive state tracking and failure recovery mechanisms. This document outlines the key architectural decisions and workflows.

## Migration Script Pattern

The system uses a database-style migration pattern with paired scripts:
- **up-X.Y.Z.sh** - Forward migration to version X.Y.Z
- **down-X.Y.Z.sh** - Rollback migration from version X.Y.Z

## Deployment State Tracking

The `DeploymentState` tracks:
- **Version**: Current deployed version
- **Updated**: Timestamp of last update
- **Up**: Set of successfully executed UP script versions
- **Failed**: Set of failed script versions (for logging/debugging)

## Update Workflow

1. **Load existing DeploymentState** (with Up/Failed collections)
2. **Create backup** (execute backup.sh if exists)
3. **Stop current services** (`docker-compose down`)
4. **Execute migration scripts**
   - Filter scripts excluding already executed versions
   - Track successful executions
5. **Start new services** (`docker-compose up`)
6. **Health check** (verify container health)
7. **Update DeploymentState** based on results
8. **Cleanup backup** (only on complete success)

## Docker Partial Failure Decision Tree

```
Docker Service Startup
    │
    ├─► docker-compose up result
    │   ├─► SUCCESS → Continue to Health Check
    │   └─► FAILURE → Execute Rollback Sequence
    │
    └─► Health Check Phase
        │
        ├─► All Services Healthy
        │   ├─► Update DeploymentState (complete)
        │   ├─► Cleanup backup
        │   └─► Return SUCCESS
        │
        └─► Some Services Unhealthy/Failed
            │
            ├─► Backup Available?
            │   │
            │   ├─► YES: Recovery Possible
            │   │   ├─► Stop all services
            │   │   ├─► Execute DOWN migration scripts
            │   │   ├─► Restore from backup (restore.sh)
            │   │   ├─► Start original services
            │   │   └─► Return FAILED (with recovery)
            │   │
            │   └─► NO: Partial State (Manual Intervention Required)
            │       ├─► Keep running services
            │       ├─► Update DeploymentState (partial)
            │       ├─► Log unhealthy services
            │       └─► Return PARTIAL_SUCCESS
            │
            └─► Severity Assessment
                ├─► Critical Services Failed → Attempt Recovery
                └─► Non-Critical Failed → Accept Partial State
```

## Rollback Sequence

The rollback sequence is triggered by:
1. Migration script failure
2. Docker-compose command failure
3. Critical service health check failure (with backup)

### Rollback Steps:
1. **Stop any running services**
2. **Execute DOWN scripts** (in reverse order)
   - Only for versions that were successfully applied
   - Remove versions from DeploymentState.Up
3. **Restore from backup** (if available)
4. **Start previous version services**
5. **Update DeploymentState** to reflect rollback

## Failure Types and Recovery

### 1. Migration Script Failure
- **Recovery**: Always automatic rollback
- **State**: Revert to previous version
- **Backup**: Retained for analysis

### 2. Docker-Compose Configuration Failure
- **Symptoms**: Invalid YAML, missing images, network conflicts
- **Recovery**: Automatic rollback
- **State**: Revert to previous version

### 3. Container Runtime Failure
- **Symptoms**: Services start but become unhealthy
- **Recovery**: 
  - With backup: Full recovery possible
  - Without backup: Partial state accepted
- **State**: Depends on backup availability

## State Management Rules

### Adding to Up Collection:
- Add version when UP script executes successfully
- Only track once (idempotent)

### Removing from Up Collection:
- Remove version when DOWN script executes successfully
- Indicates the migration has been rolled back

### Failed Collection:
- Add version when script fails
- Used for debugging and audit trail
- Does not affect execution logic

## Backup Strategy

### backup.sh
- Executed before any changes
- Should create a restorable snapshot
- Must support `--format=json` parameter
- Returns JSON response: `{ "file": "/path/to/backup/file" }`
- Example: `./backup.sh --format=json`

### restore.sh
- Accepts backup file path as parameter
- Restores system to pre-migration state
- Must support `--file` and `--format=json` parameters
- Returns JSON response: 
  - Success: `{ "success": true }`
  - Failure: `{ "success": false, "error": "reason for failure" }`
- Example: `./restore.sh --file="/path/to/backup/file" --format=json`
- Used only in failure scenarios

### Script Interface Examples

```bash
# backup.sh example implementation
#!/bin/bash
BACKUP_FILE="/backups/backup-$(date +%Y%m%d-%H%M%S).tar.gz"
tar czf "$BACKUP_FILE" /data /config

if [ "$1" == "--format=json" ]; then
    echo "{\"file\": \"$BACKUP_FILE\"}"
else
    echo "Backup created: $BACKUP_FILE"
fi
```

```bash
# restore.sh example implementation
#!/bin/bash
BACKUP_FILE=""
FORMAT=""

while [[ $# -gt 0 ]]; do
    case $1 in
        --file=*)
            BACKUP_FILE="${1#*=}"
            shift
            ;;
        --format=json)
            FORMAT="json"
            shift
            ;;
        *)
            shift
            ;;
    esac
done

if [ ! -f "$BACKUP_FILE" ]; then
    if [ "$FORMAT" == "json" ]; then
        echo "{\"success\": false, \"error\": \"Backup file not found: $BACKUP_FILE\"}"
    else
        echo "ERROR: Backup file not found: $BACKUP_FILE"
    fi
    exit 1
fi

tar xzf "$BACKUP_FILE" -C / 2>/dev/null

if [ $? -eq 0 ]; then
    if [ "$FORMAT" == "json" ]; then
        echo "{\"success\": true}"
    else
        echo "Restore completed successfully"
    fi
else
    if [ "$FORMAT" == "json" ]; then
        echo "{\"success\": false, \"error\": \"Failed to extract backup file\"}"
    else
        echo "ERROR: Failed to extract backup file"
    fi
    exit 1
fi
```

### Backup Lifecycle:
1. **Create**: Before migration starts
2. **Retain**: During migration and validation
3. **Cleanup**: Only after complete success
4. **Archive**: On failure for investigation

## Service Dependencies

### Core Services:
- **IScriptMigrationService**: Discovers and executes migration scripts
- **IDeploymentStateProvider**: Manages deployment state persistence
- **IBackupService**: Handles backup/restore operations
- **IHealthCheckService**: Validates service health post-deployment
- **IDockerComposeService**: Manages Docker Compose operations

### Service Responsibilities:
- Services should have single responsibilities
- State management stays at orchestration level
- Services receive only necessary data (not full state)

## Error Handling Principles

1. **Fail Fast**: Detect problems early in the process
2. **Automatic Recovery**: Rollback when safe to do so
3. **Preserve State**: Never lose track of what was executed
4. **Manual Intervention**: Clear indication when automation cannot recover
5. **Audit Trail**: Log all actions for post-mortem analysis

## Update Result Types

```csharp
public enum UpdateStatus
{
    Success,              // Full success, all services healthy
    Failed,               // Failed with successful rollback
    PartialSuccess,       // Some services running, no backup
    RecoverableFailure    // Failed but backup available for manual recovery
}
```

## Best Practices

1. **Always create backup.sh/restore.sh** for production systems
2. **Write idempotent migration scripts** that can be safely re-run
3. **Test rollback scenarios** in staging environments
4. **Monitor service health** with appropriate timeouts
5. **Document manual recovery procedures** for partial states
6. **Version everything** including compose files and scripts