# Backup Management Feature Specification

## Overview
Add backup management functionality to the AutoUpdater web interface, allowing users to create, list, and restore backups for packages (initially focused on RocketWelder EventStore backups). Each backup includes version metadata to enable restoring both data and corresponding code version together, ensuring data-code consistency.

## Current State Analysis

### Existing Scripts (rocket-welder package)
- **es-backup-manage.sh**: Main backup management script with commands:
  - `list` - Lists all available backups with size and date
  - `backup` - Creates a manual backup
  - `restore <backup-file>` - Restores from specified backup
  - `status` - Shows backup status and disk usage
  - `cleanup` - Removes old backups

- **backup.sh**: Simplified backup wrapper for migration system
  - Currently supports `--format=json` for create operation only
  - Returns: `{"file": "/path/to/backup.tar.gz"}` on success

- **es-backup-full.sh**: Performs actual EventStore backup
- **es-restore.sh**: Performs actual EventStore restore

### Script Locations
- Local dev: `/mnt/d/source/modelingevolution/rocketwelder-compose/`
- Remote: `/var/docker/configuration/rocket-welder/`
- Backup storage: `/var/docker/data/backups/`

## Feature Requirements

### 1. User Interface (MudBlazor Components)

#### Backup Button & Menu
- **Component**: MudMenu with MudButton
- **Location**: Package card (same level as "Check for Updates" and "Update" buttons)
- **Icon**: `Icons.Material.Filled.Backup`
- **Menu Items**:
  1. **Create Backup** - Initiates new backup with current package version
  2. **Manage Backups** - Opens backup list modal

#### Backup List Modal
- **Component**: MudDialog with MudTable
- **Title**: "Backup Management - {PackageName}"
- **Columns**:
  - **Version** (Package version at backup time) - Primary sort column
  - **Backup Name** (filename without extension)
  - **Size** (human-readable: KB, MB, GB)
  - **Date Created** (formatted: "YYYY-MM-DD HH:mm:ss")
  - **Actions** (Restore button)

- **Features**:
  - Sortable by date/version (default: newest first)
  - Searchable by filename or version
  - Version displayed as MudChip with color coding (latest=success, older=default)
  - Refresh button (MudIconButton with refresh icon)
  - Close button
  - MudProgressCircular for loading state

#### Restore Confirmation Dialog
- **Component**: MudDialog with warning styling
- **Warning**: "This will stop {PackageName}, restore data from backup, and checkout to version {version}!"
- **Backup Info Display** (MudList):
  - **Version**: `{version}` (with git tag indicator if available)
  - **Backup file**: `{filename}`
  - **Size**: `{size}`
  - **Created**: `{date}`
- **Additional Info**:
  - If git tag exists: "✓ Code will be restored to tag v{version}"
  - If git tag missing: "⚠ Code version tag not found - will keep current code"
- **Buttons** (MudButton):
  - "Yes, Restore" (destructive action - Color.Error)
  - "Cancel" (default - Color.Default)

#### Progress Indicators
- **Create Backup**: Show progress spinner with message "Creating backup..."
- **Restore**: Show progress with message "Restoring from backup..."
- **List Loading**: Skeleton loader or spinner

### 2. REST API Endpoints

All endpoints follow pattern: `/api/backup/{packageName}/...`

#### GET /api/backup/{packageName}/list
**Purpose**: List all available backups for a package with version metadata

**Response** (200 OK):
```json
{
  "backups": [
    {
      "filename": "backup-20251126-214505.tar.gz",
      "displayName": "2025-11-26 21:45:05",
      "version": "v2.4.10",
      "gitTagExists": true,
      "size": "584K",
      "sizeBytes": 597504,
      "createdDate": "2025-11-26T21:45:11Z",
      "fullPath": "/var/docker/data/backups/backup-20251126-214505.tar.gz"
    }
  ],
  "totalCount": 11,
  "totalSizeBytes": 6234112,
  "totalSize": "6.1M"
}
```

**Error Response** (500):
```json
{
  "error": "Failed to list backups",
  "message": "Backup directory not accessible"
}
```

#### POST /api/backup/{packageName}/create
**Purpose**: Create a new backup with current package version

**Request Body** (optional):
```json
{
  "version": "v2.4.10"
}
```
*Note: If version is not provided, it will be automatically detected from deployment state*

**Response** (200 OK):
```json
{
  "success": true,
  "backup": {
    "filename": "backup-20251126-220000.tar.gz",
    "version": "v2.4.10",
    "size": "588K",
    "createdDate": "2025-11-26T22:00:05Z",
    "fullPath": "/var/docker/data/backups/backup-20251126-220000.tar.gz"
  }
}
```

**Error Response** (500):
```json
{
  "success": false,
  "error": "Backup creation failed",
  "message": "EventStore is not running"
}
```

#### POST /api/backup/{packageName}/restore
**Purpose**: Restore from a backup

**Request Body**:
```json
{
  "filename": "backup-20251126-214505.tar.gz"
}
```

**Response** (200 OK):
```json
{
  "success": true,
  "message": "Restore completed successfully",
  "restoredBackup": "backup-20251126-214505.tar.gz"
}
```

**Error Response** (500):
```json
{
  "success": false,
  "error": "Restore failed",
  "message": "Backup file not found"
}
```

#### GET /api/backup/{packageName}/status
**Purpose**: Get backup system status (optional - for future enhancement)

**Response** (200 OK):
```json
{
  "backupDirectory": "/var/docker/data/backups",
  "totalBackups": 11,
  "totalSize": "6.1M",
  "oldestBackup": "backup-20251125-025935.tar.gz",
  "newestBackup": "backup-20251126-214505.tar.gz",
  "retentionDays": 7,
  "backupsToCleanup": 0
}
```

### 3. Version Metadata Storage

#### Metadata File Format
Each backup will have an accompanying `.meta.json` file storing version information:

**File naming convention**:
- Backup: `backup-20251126-214505.tar.gz`
- Metadata: `backup-20251126-214505.meta.json`

**Metadata JSON structure**:
```json
{
  "version": "v2.4.10",
  "packageName": "rocket-welder",
  "createdDate": "2025-11-26T21:45:11Z",
  "backupFile": "backup-20251126-214505.tar.gz",
  "gitCommit": "abc123def456",
  "gitTagExists": true
}
```

#### Metadata Creation Process
1. When creating backup, pass version as argument: `backup --version=v2.4.10`
2. Script creates backup file as usual
3. Script creates metadata file with version info
4. Script checks if git tag exists: `git rev-parse v2.4.10`
5. Store git tag existence status in metadata

#### Metadata Reading Process
1. When listing backups, read each `.meta.json` file
2. Parse version and git tag status
3. Return in JSON response

### 4. Script Modifications

#### Update es-backup-manage.sh to support version metadata

**Add version parameter to backup command**:
```bash
manual_backup() {
    local version="${1:-unknown}"
    info "=== Starting Manual EventStore Backup (Version: $version) ==="

    # ... existing backup code ...

    # After successful backup, create metadata file
    if [ -n "$LATEST_BACKUP" ]; then
        local metadata_file="${LATEST_BACKUP%.tar.gz}.meta.json"
        local git_tag_exists="false"

        # Check if git tag exists
        if git rev-parse "$version" >/dev/null 2>&1; then
            git_tag_exists="true"
        fi

        # Create metadata JSON
        cat > "$metadata_file" <<EOF
{
  "version": "$version",
  "packageName": "$(basename "$(dirname "$SCRIPT_DIR")")",
  "createdDate": "$(date --iso-8601=seconds)",
  "backupFile": "$(basename "$LATEST_BACKUP")",
  "gitCommit": "$(git rev-parse HEAD 2>/dev/null || echo 'unknown')",
  "gitTagExists": $git_tag_exists
}
EOF
        log "Metadata file created: $metadata_file"
    fi
}
```

#### Update es-backup-manage.sh to support JSON output

Add `--format=json` flag support to `list` command:

**Modified list_backups() function with metadata support**:
```bash
list_backups() {
    local format="${1:-text}"

    if [ ! -d "$BACKUP_DIR" ]; then
        if [ "$format" = "json" ]; then
            echo '{"backups":[],"totalCount":0,"error":"Backup directory does not exist"}'
        else
            warn "Backup directory does not exist: $BACKUP_DIR"
        fi
        return 1
    fi

    if ! ls "$BACKUP_DIR"/backup-*.tar.gz >/dev/null 2>&1; then
        if [ "$format" = "json" ]; then
            echo '{"backups":[],"totalCount":0}'
        else
            warn "No backups found in $BACKUP_DIR"
        fi
        return 0
    fi

    if [ "$format" = "json" ]; then
        # JSON output with metadata
        echo -n '{"backups":['
        first=true
        for backup in "$BACKUP_DIR"/backup-*.tar.gz; do
            if [ -f "$backup" ]; then
                filename=$(basename "$backup")
                size=$(du -h "$backup" | cut -f1)
                size_bytes=$(stat -c %s "$backup")
                date_created=$(stat -c %y "$backup" | cut -d'.' -f1)
                date_iso=$(date -d "$date_created" --iso-8601=seconds)

                # Read metadata file if exists
                metadata_file="${backup%.tar.gz}.meta.json"
                version="unknown"
                git_tag_exists="false"

                if [ -f "$metadata_file" ]; then
                    version=$(jq -r '.version // "unknown"' "$metadata_file")
                    git_tag_exists=$(jq -r '.gitTagExists // false' "$metadata_file")
                fi

                if [ "$first" = false ]; then
                    echo -n ','
                fi
                first=false

                echo -n "{\"filename\":\"$filename\",\"version\":\"$version\",\"gitTagExists\":$git_tag_exists,\"size\":\"$size\",\"sizeBytes\":$size_bytes,\"createdDate\":\"$date_iso\",\"fullPath\":\"$backup\"}"
            fi
        done
        echo -n '],'

        local backup_count=$(ls "$BACKUP_DIR"/backup-*.tar.gz 2>/dev/null | wc -l)
        local total_size=$(du -sh "$BACKUP_DIR" 2>/dev/null | cut -f1 || echo "0")
        echo "\"totalCount\":$backup_count,\"totalSize\":\"$total_size\"}"
    else
        # Text output with version column
        info "=== Available EventStore Backups ==="
        echo
        printf "%-30s %-12s %-10s %-20s\n" "BACKUP FILE" "VERSION" "SIZE" "DATE CREATED"
        printf "%-30s %-12s %-10s %-20s\n" "-----------" "-------" "----" "------------"

        for backup in "$BACKUP_DIR"/backup-*.tar.gz; do
            if [ -f "$backup" ]; then
                filename=$(basename "$backup")
                size=$(du -h "$backup" | cut -f1)
                date_created=$(stat -c %y "$backup" | cut -d'.' -f1)

                # Read version from metadata
                metadata_file="${backup%.tar.gz}.meta.json"
                version="unknown"
                if [ -f "$metadata_file" ]; then
                    version=$(jq -r '.version // "unknown"' "$metadata_file")
                fi

                printf "%-30s %-12s %-10s %-20s\n" "$filename" "$version" "$size" "$date_created"
            fi
        done

        echo
        local backup_count=$(ls "$BACKUP_DIR"/backup-*.tar.gz 2>/dev/null | wc -l)
        info "Total backups: $backup_count"
    fi
}
```

**Modified main() function**:
```bash
main() {
    mkdir -p "$BACKUP_DIR"
    touch "$LOG_FILE"

    local format="text"
    local version=""

    # Parse arguments
    for arg in "$@"; do
        if [[ "$arg" == "--format=json" ]]; then
            format="json"
        elif [[ "$arg" == --version=* ]]; then
            version="${arg#*=}"
        fi
    done

    case "${1:-help}" in
        list|ls)
            list_backups "$format"
            ;;
        backup|manual)
            manual_backup "$version"
            ;;
        restore)
            if [ $# -lt 2 ]; then
                error "Please specify a backup file to restore"
            fi
            restore_backup "$2" "${3:-no}"
            ;;
        # ... rest of cases ...
    esac
}
```

#### Update restore command with git checkout support

**Enhanced restore_backup() function**:
```bash
restore_backup() {
    local backup_file="$1"
    local skip_confirm="${2:-no}"

    info "=== Restoring EventStore from Backup ==="

    # ... validation code ...

    # Read version from metadata
    local metadata_file="$BACKUP_DIR/${backup_file%.tar.gz}.meta.json"
    local version="unknown"
    local git_tag_exists="false"

    if [ -f "$metadata_file" ]; then
        version=$(jq -r '.version // "unknown"' "$metadata_file")
        git_tag_exists=$(jq -r '.gitTagExists // false' "$metadata_file")

        log "Backup version: $version"
        log "Git tag exists: $git_tag_exists"
    else
        warn "No metadata file found for backup - will not checkout code version"
    fi

    if [ "$skip_confirm" != "yes" ]; then
        # Interactive confirmation with version info
        echo -e "${YELLOW}WARNING: This will stop EventStore and replace all data!${NC}"
        echo "Backup file: $backup_file"
        echo "Backup version: $version"

        if [ "$git_tag_exists" = "true" ]; then
            echo -e "${GREEN}✓ Code will be checked out to tag: $version${NC}"
        else
            echo -e "${YELLOW}⚠ Git tag $version not found - code version will not change${NC}"
        fi

        read -p "Are you sure you want to continue? (yes/no): " confirmation

        if [ "$confirmation" != "yes" ]; then
            info "Restore cancelled by user"
            return 0
        fi
    fi

    log "Starting restore from backup: $backup_file"

    # Execute restore script (restores data)
    if "$restore_script" "$backup_file"; then
        log "Data restore completed successfully"

        # Checkout git tag if it exists
        if [ "$git_tag_exists" = "true" ] && [ "$version" != "unknown" ]; then
            log "Checking out code to version: $version"

            if git checkout "$version" 2>&1 | tee -a "$LOG_FILE"; then
                log "Successfully checked out to $version"

                # Restart services with new code version
                log "Restarting services with version $version"
                if docker compose -f "$COMPOSE_FILE" down && docker compose -f "$COMPOSE_FILE" up -d; then
                    log "Services restarted successfully"
                else
                    error "Failed to restart services after git checkout"
                fi
            else
                warn "Failed to checkout to $version - services will continue with current code"
            fi
        else
            log "Skipping git checkout - tag not available or version unknown"
        fi

        # Wait a moment and check health
        sleep 5
        if check_eventstore_health; then
            echo -e "${GREEN}EventStore is running and healthy after restore${NC}"
        else
            warn "EventStore may not be fully ready yet. Check logs if issues persist."
        fi
    else
        error "Restore failed"
    fi
}
```

### 5. Backend Implementation

#### New Service: IBackupManagementService

**Interface** (`Services/IBackupManagementService.cs`):
```csharp
public interface IBackupManagementService
{
    Task<BackupListResponse> ListBackupsAsync(PackageName packageName);
    Task<BackupCreateResponse> CreateBackupAsync(PackageName packageName, string? version = null);
    Task<BackupRestoreResponse> RestoreBackupAsync(PackageName packageName, string filename);
    Task<BackupStatusResponse> GetBackupStatusAsync(PackageName packageName);
}

public record BackupInfo(
    string Filename,
    string DisplayName,
    string Version,
    bool GitTagExists,
    string Size,
    long SizeBytes,
    DateTime CreatedDate,
    string FullPath
);

public record BackupListResponse(
    List<BackupInfo> Backups,
    int TotalCount,
    long TotalSizeBytes,
    string TotalSize,
    string? Error = null
);

public record BackupCreateResponse(
    bool Success,
    BackupInfo? Backup,
    string? Error = null,
    string? Message = null
);

public record BackupRestoreResponse(
    bool Success,
    string? RestoredBackup = null,
    string? RestoredVersion = null,
    bool CodeVersionRestored = false,
    string? Error = null,
    string? Message = null
);

public record BackupStatusResponse(
    string BackupDirectory,
    int TotalBackups,
    string TotalSize,
    string? OldestBackup,
    string? NewestBackup,
    int RetentionDays,
    int BackupsToCleanup
);
```

**Implementation** (`Services/BackupManagementService.cs`):
```csharp
public class BackupManagementService : IBackupManagementService
{
    private readonly DockerComposeConfigurationModel _configModel;
    private readonly ISshService _sshService;
    private readonly IDeploymentStateProvider _deploymentStateProvider;
    private readonly ILogger<BackupManagementService> _logger;

    public BackupManagementService(
        DockerComposeConfigurationModel configModel,
        ISshService sshService,
        IDeploymentStateProvider deploymentStateProvider,
        ILogger<BackupManagementService> logger)
    {
        _configModel = configModel;
        _sshService = sshService;
        _deploymentStateProvider = deploymentStateProvider;
        _logger = logger;
    }

    public async Task<BackupListResponse> ListBackupsAsync(PackageName packageName)
    {
        var config = _configModel.GetPackage(packageName);
        if (config == null)
            throw new PackageNotFoundException($"Package {packageName} not found");

        var scriptPath = Path.Combine(config.HostComposeFolderPath, "es-backup-manage.sh");
        var command = $"sudo bash \"{scriptPath}\" list --format=json";

        var result = await _sshService.ExecuteCommandAsync(command);

        if (!result.IsSuccess)
        {
            _logger.LogError("Failed to list backups: {Error}", result.Error);
            return new BackupListResponse([], 0, 0, "0", result.Error);
        }

        var response = JsonSerializer.Deserialize<BackupListResponse>(result.Output);
        return response ?? new BackupListResponse([], 0, 0, "0", "Failed to parse response");
    }

    public async Task<BackupCreateResponse> CreateBackupAsync(PackageName packageName, string? version = null)
    {
        var config = _configModel.GetPackage(packageName);
        if (config == null)
            throw new PackageNotFoundException($"Package {packageName} not found");

        // Get current version from deployment state if not provided
        if (string.IsNullOrEmpty(version))
        {
            version = await _deploymentStateProvider.GetCurrentVersionAsync(config.HostComposeFolderPath);
            _logger.LogInformation("Auto-detected version for backup: {Version}", version);
        }

        if (string.IsNullOrEmpty(version))
        {
            version = "unknown";
            _logger.LogWarning("Could not determine package version for backup");
        }

        var scriptPath = Path.Combine(config.HostComposeFolderPath, "es-backup-manage.sh");
        var command = $"sudo bash \"{scriptPath}\" backup --version={version}";

        _logger.LogInformation("Creating backup for {PackageName} version {Version}", packageName, version);

        var result = await _sshService.ExecuteCommandAsync(command, TimeSpan.FromMinutes(5));

        if (!result.IsSuccess)
        {
            _logger.LogError("Backup creation failed: {Error}", result.Error);
            return new BackupCreateResponse(false, null, "Backup creation failed", result.Error);
        }

        _logger.LogInformation("Backup created successfully for {PackageName} version {Version}", packageName, version);
        return new BackupCreateResponse(true, null, null, "Backup created successfully");
    }

    public async Task<BackupRestoreResponse> RestoreBackupAsync(PackageName packageName, string filename)
    {
        var config = _configModel.GetPackage(packageName);
        if (config == null)
            throw new PackageNotFoundException($"Package {packageName} not found");

        var scriptPath = Path.Combine(config.HostComposeFolderPath, "es-backup-manage.sh");
        var command = $"sudo bash \"{scriptPath}\" restore \"{filename}\" --yes";

        _logger.LogInformation("Restoring {PackageName} from backup: {Filename}", packageName, filename);

        var result = await _sshService.ExecuteCommandAsync(command, TimeSpan.FromMinutes(10));

        if (!result.IsSuccess)
        {
            _logger.LogError("Restore failed: {Error}", result.Error);
            return new BackupRestoreResponse(false, null, null, false, "Restore failed", result.Error);
        }

        // Check output for version restoration info
        var codeRestored = result.Output.Contains("Successfully checked out to");
        var restoredVersion = "unknown"; // Could parse from output

        _logger.LogInformation("Restore completed for {PackageName}, code version restored: {CodeRestored}",
            packageName, codeRestored);

        return new BackupRestoreResponse(true, filename, restoredVersion, codeRestored, null,
            "Restore completed successfully");
    }
}
```

#### API Endpoints

**File**: `Api/Backup/BackupEndpoints.cs`
```csharp
public static class BackupEndpoints
{
    public static void MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/backup/{packageName}")
            .WithTags("Backup");

        group.MapGet("/list", ListBackupsAsync)
            .WithName("ListBackups")
            .WithSummary("List all backups for a package")
            .Produces<BackupListResponse>();

        group.MapPost("/create", CreateBackupAsync)
            .WithName("CreateBackup")
            .WithSummary("Create a new backup")
            .Produces<BackupCreateResponse>();

        group.MapPost("/restore", RestoreBackupAsync)
            .WithName("RestoreBackup")
            .WithSummary("Restore from a backup")
            .Produces<BackupRestoreResponse>();
    }

    private static async Task<IResult> ListBackupsAsync(
        string packageName,
        IBackupManagementService backupService,
        ILogger<BackupEndpoints> logger)
    {
        try
        {
            var response = await backupService.ListBackupsAsync(packageName);
            return Results.Ok(response);
        }
        catch (PackageNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to list backups for {PackageName}", packageName);
            return Results.Problem("Failed to list backups");
        }
    }

    private static async Task<IResult> CreateBackupAsync(
        string packageName,
        IBackupManagementService backupService,
        ILogger<BackupEndpoints> logger)
    {
        try
        {
            var response = await backupService.CreateBackupAsync(packageName);
            return Results.Ok(response);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create backup for {PackageName}", packageName);
            return Results.Problem("Failed to create backup");
        }
    }

    private static async Task<IResult> RestoreBackupAsync(
        string packageName,
        [FromBody] RestoreBackupRequest request,
        IBackupManagementService backupService,
        ILogger<BackupEndpoints> logger)
    {
        try
        {
            var response = await backupService.RestoreBackupAsync(packageName, request.Filename);
            return Results.Ok(response);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to restore backup for {PackageName}", packageName);
            return Results.Problem("Failed to restore backup");
        }
    }
}

public record RestoreBackupRequest(string Filename);
```

### 5. Frontend Implementation

#### Backup Button Component

**File**: `Components/Shared/BackupButton.razor`
```razor
@inject IBackupManagementService BackupService
@inject IDialogService DialogService
@inject ISnackbar Snackbar

<MudMenu Icon="@Icons.Material.Filled.Backup"
         Color="Color.Secondary"
         Variant="Variant.Filled"
         Size="Size.Small"
         Label="Backup">
    <MudMenuItem OnClick="@CreateBackupAsync" Icon="@Icons.Material.Filled.Add">
        Create Backup
    </MudMenuItem>
    <MudMenuItem OnClick="@OpenBackupListAsync" Icon="@Icons.Material.Filled.List">
        Manage Backups
    </MudMenuItem>
</MudMenu>

@code {
    [Parameter]
    public PackageName PackageName { get; set; }

    private async Task CreateBackupAsync()
    {
        try
        {
            Snackbar.Add("Creating backup...", Severity.Info);
            var response = await BackupService.CreateBackupAsync(PackageName);

            if (response.Success)
            {
                Snackbar.Add("Backup created successfully", Severity.Success);
            }
            else
            {
                Snackbar.Add($"Backup failed: {response.Error}", Severity.Error);
            }
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Error creating backup: {ex.Message}", Severity.Error);
        }
    }

    private async Task OpenBackupListAsync()
    {
        var parameters = new DialogParameters
        {
            ["PackageName"] = PackageName
        };

        var options = new DialogOptions
        {
            MaxWidth = MaxWidth.Medium,
            FullWidth = true,
            CloseButton = true
        };

        await DialogService.ShowAsync<BackupListDialog>("Backup Management", parameters, options);
    }
}
```

#### Backup List Dialog Component

**File**: `Components/Shared/BackupListDialog.razor`
```razor
@inject IBackupManagementService BackupService
@inject IDialogService DialogService
@inject ISnackbar Snackbar

<MudDialog>
    <DialogContent>
        @if (_loading)
        {
            <MudProgressCircular Indeterminate="true" />
        }
        else if (_backups.Count == 0)
        {
            <MudText>No backups available</MudText>
        }
        else
        {
            <MudTable Items="@_backups" Dense="true" Hover="true">
                <HeaderContent>
                    <MudTh>Backup Name</MudTh>
                    <MudTh>Size</MudTh>
                    <MudTh>Created</MudTh>
                    <MudTh>Actions</MudTh>
                </HeaderContent>
                <RowTemplate>
                    <MudTd DataLabel="Name">@context.Filename</MudTd>
                    <MudTd DataLabel="Size">@context.Size</MudTd>
                    <MudTd DataLabel="Created">@context.CreatedDate.ToString("yyyy-MM-dd HH:mm:ss")</MudTd>
                    <MudTd>
                        <MudButton Size="Size.Small"
                                   Color="Color.Warning"
                                   OnClick="@(() => RestoreBackupAsync(context))">
                            Restore
                        </MudButton>
                    </MudTd>
                </RowTemplate>
            </MudTable>
        }
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="@RefreshAsync" StartIcon="@Icons.Material.Filled.Refresh">
            Refresh
        </MudButton>
        <MudButton OnClick="@Close">Close</MudButton>
    </DialogActions>
</MudDialog>

@code {
    [CascadingParameter]
    MudDialogInstance MudDialog { get; set; } = null!;

    [Parameter]
    public PackageName PackageName { get; set; }

    private List<BackupInfo> _backups = new();
    private bool _loading = true;

    protected override async Task OnInitializedAsync()
    {
        await LoadBackupsAsync();
    }

    private async Task LoadBackupsAsync()
    {
        _loading = true;
        try
        {
            var response = await BackupService.ListBackupsAsync(PackageName);
            _backups = response.Backups;
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to load backups: {ex.Message}", Severity.Error);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task RefreshAsync()
    {
        await LoadBackupsAsync();
    }

    private async Task RestoreBackupAsync(BackupInfo backup)
    {
        var confirmed = await DialogService.ShowMessageBox(
            "Confirm Restore",
            $"This will stop {PackageName} and replace all data with backup: {backup.Filename}. Are you sure?",
            yesText: "Yes, Restore",
            noText: "Cancel");

        if (confirmed == true)
        {
            try
            {
                Snackbar.Add("Restoring from backup...", Severity.Info);
                var response = await BackupService.RestoreBackupAsync(PackageName, backup.Filename);

                if (response.Success)
                {
                    Snackbar.Add("Restore completed successfully", Severity.Success);
                    MudDialog.Close();
                }
                else
                {
                    Snackbar.Add($"Restore failed: {response.Error}", Severity.Error);
                }
            }
            catch (Exception ex)
            {
                Snackbar.Add($"Error restoring backup: {ex.Message}", Severity.Error);
            }
        }
    }

    private void Close() => MudDialog.Close();
}
```

### 6. Configuration & Feature Detection

Add configuration to `DockerComposeConfiguration` to indicate if backup is available:

```csharp
public class DockerComposeConfiguration
{
    // Existing properties...

    public bool BackupEnabled { get; init; } = false;
    public string? BackupScriptPath { get; init; }
}
```

**Configuration Example** (`appsettings.Production.json`):
```json
{
  "Packages": [
    {
      "RepositoryLocation": "/data/rocket-welder",
      "RepositoryUrl": "https://github.com/modelingevolution/rocketwelder-compose.git",
      "DockerComposeDirectory": "./",
      "BackupEnabled": true,
      "BackupScriptPath": "es-backup-manage.sh"
    }
  ]
}
```

### 7. Security Considerations

1. **Authorization**: Backup operations should require authentication (future enhancement)
2. **File Path Validation**: Ensure restore filename doesn't contain path traversal attempts
3. **Rate Limiting**: Limit backup creation frequency to prevent abuse
4. **Audit Logging**: Log all backup create/restore operations with timestamps and user info

### 8. Testing Plan

#### Unit Tests
- `BackupManagementService` methods
- Script output parsing
- Error handling

#### Integration Tests
- API endpoints
- End-to-end backup/restore flow

#### Manual Testing Checklist
- [ ] Create backup via UI
- [ ] List backups shows all files
- [ ] Restore backup with confirmation
- [ ] Cancel restore operation
- [ ] Error handling when backup fails
- [ ] Error handling when restore fails
- [ ] Refresh backup list
- [ ] Package without backup support doesn't show button

### 9. Future Enhancements

1. **Scheduled Backups**: Automatic backup creation on schedule
2. **Backup Download**: Download backup files to local machine
3. **Backup Upload**: Upload backup files from local machine
4. **Backup Metadata**: Store custom notes/tags with backups
5. **Multi-Package Backup**: Backup multiple packages at once
6. **Backup Cleanup UI**: Manage retention policy from UI
7. **Backup Verification**: Verify backup integrity before restore
8. **Progress Tracking**: Real-time progress for long-running operations
9. **Backup Comparison**: Show differences between backups

### 10. Implementation Phases

#### Phase 1: Core Functionality (MVP)
- Script modifications for JSON output
- Backend API endpoints
- Basic UI with list and restore

#### Phase 2: Enhanced UX
- Progress indicators
- Better error messages
- Confirmation dialogs

#### Phase 3: Advanced Features
- Backup metadata
- Scheduled backups
- Download/upload functionality

## Technical Notes

### Script Execution Context
- All backup scripts must run with `sudo` for file permissions
- SSH service executes commands on remote host
- Backup directory: `/var/docker/data/backups/`

### Error Scenarios to Handle
1. Backup directory not accessible
2. EventStore not running (for backup creation)
3. Insufficient disk space
4. Backup file corrupted
5. Restore interrupted
6. Script not executable
7. Permission denied

### Performance Considerations
- Large backup files may take time to create/restore
- Use timeouts for long-running operations
- Consider background job processing for backups
- Cache backup list with short TTL

## Acceptance Criteria

- [ ] User can create backup from UI
- [ ] User can view list of all backups
- [ ] User can restore from any backup with confirmation
- [ ] All operations show appropriate feedback (success/error)
- [ ] Scripts support JSON output format
- [ ] API returns proper error codes and messages
- [ ] Backup button only shows for packages with backup support
- [ ] Restore operation stops service and restarts after restore
## Version Metadata Feature Summary

### Why Version Metadata Makes Sense

#### Problem Statement
When backing up application data, the backup is tightly coupled to the code version that created it:
- Database schemas may differ between versions
- Data formats may have changed
- Application logic may expect specific data structures

Restoring just the data without the corresponding code version can lead to:
- Runtime errors due to schema mismatches
- Data corruption from incompatible formats  
- Application failures from missing/changed fields

#### Solution: Version-Tagged Backups
By storing the package version with each backup and checking out the corresponding git tag during restore:
1. **Data-Code Consistency**: Ensures restored data matches the code that created it
2. **Safe Rollbacks**: Can safely rollback to any previous version (data + code together)
3. **Audit Trail**: Know exactly which version created each backup
4. **Selective Restore**: Choose to restore specific versions, not just latest

#### Implementation Flow

**Creating Backup with Version:**
```
User clicks "Create Backup" 
  → Get current package version from deployment.state.json
  → Execute: sudo bash es-backup-manage.sh backup --version=v2.4.10
  → Script creates backup-TIMESTAMP.tar.gz
  → Script creates backup-TIMESTAMP.meta.json with version info
  → Check if git tag v2.4.10 exists, store result in metadata
```

**Restoring Backup with Version:**
```
User selects backup v2.4.10 to restore
  → Read backup-TIMESTAMP.meta.json for version and git tag status
  → Show confirmation: "Will restore data AND checkout code to v2.4.10"
  → Execute restore script (restores data)
  → If git tag exists: git checkout v2.4.10
  → Restart services with restored code version
  → Data and code are now consistent at v2.4.10
```

#### Benefits

1. **Atomic Version Restore**: Single operation restores both data and code
2. **No Manual Steps**: No need to manually find and checkout correct version
3. **Prevents Mismatch Errors**: Impossible to restore v2.3 data with v2.5 code
4. **Version Visibility**: UI shows which version each backup belongs to
5. **Git Integration**: Leverages existing git tags for version management

#### Edge Cases Handled

1. **Missing Git Tag**: If tag doesn't exist, backup still works but only restores data
2. **Unknown Version**: Legacy backups without metadata still work (version shown as "unknown")
3. **Modified Working Directory**: Git checkout only happens if tag exists and is safe
4. **Failed Checkout**: If git checkout fails, data is still restored and warning is shown

This approach ensures production systems can safely rollback to any previous version with confidence that data and code will be compatible.

