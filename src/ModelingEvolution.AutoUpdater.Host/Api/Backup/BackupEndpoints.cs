using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater;
using ModelingEvolution.AutoUpdater.Services;
using System;
using System.Threading.Tasks;

namespace ModelingEvolution.AutoUpdater.Host.Api.Backup
{
    public static class BackupEndpoints
    {
        public static void MapBackupEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/api/backup")
                .WithTags("Backup")
                .WithOpenApi();

            group.MapGet("/{packageName}/list", ListBackupsAsync)
                .WithName("ListBackups")
                .WithSummary("List all available backups for a package")
                .Produces<BackupListResponse>();

            group.MapPost("/{packageName}/create", CreateBackupAsync)
                .WithName("CreateBackup")
                .WithSummary("Create a new backup for a package")
                .Produces<BackupCreateResponse>();

            group.MapPost("/{packageName}/restore", RestoreBackupAsync)
                .WithName("RestoreBackup")
                .WithSummary("Restore a package from a backup")
                .Produces<BackupRestoreResponse>();

            group.MapGet("/{packageName}/status", GetBackupStatusAsync)
                .WithName("GetBackupStatus")
                .WithSummary("Get backup system status")
                .Produces<BackupStatusResponse>();
        }

        private static async Task<IResult> ListBackupsAsync(
            string packageName,
            IBackupManagementService backupService,
            ILogger<IBackupManagementService> logger)
        {
            try
            {
                logger.LogInformation("Listing backups for package: {PackageName}", packageName);
                var response = await backupService.ListBackupsAsync(packageName);
                return Results.Ok(response);
            }
            catch (PackageNotFoundException ex)
            {
                logger.LogWarning(ex, "Package not found: {PackageName}", packageName);
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
            [FromBody] CreateBackupRequest? request,
            IBackupManagementService backupService,
            ILogger<IBackupManagementService> logger)
        {
            try
            {
                logger.LogInformation("Creating backup for package: {PackageName}", packageName);
                var response = await backupService.CreateBackupAsync(packageName, request?.Version);

                if (response.Success)
                {
                    return Results.Ok(response);
                }
                else
                {
                    return Results.Problem(
                        detail: response.Error,
                        statusCode: 500,
                        title: "Backup creation failed");
                }
            }
            catch (PackageNotFoundException ex)
            {
                logger.LogWarning(ex, "Package not found: {PackageName}", packageName);
                return Results.NotFound(new { error = ex.Message });
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
            ILogger<IBackupManagementService> logger)
        {
            try
            {
                logger.LogInformation("Restoring backup for package: {PackageName}, file: {Filename}",
                    packageName, request.Filename);

                var response = await backupService.RestoreBackupAsync(packageName, request.Filename);

                if (response.Success)
                {
                    return Results.Ok(response);
                }
                else
                {
                    return Results.Problem(
                        detail: response.Error,
                        statusCode: 500,
                        title: "Restore failed");
                }
            }
            catch (PackageNotFoundException ex)
            {
                logger.LogWarning(ex, "Package not found: {PackageName}", packageName);
                return Results.NotFound(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to restore backup for {PackageName}", packageName);
                return Results.Problem("Failed to restore backup");
            }
        }

        private static async Task<IResult> GetBackupStatusAsync(
            string packageName,
            IBackupManagementService backupService,
            ILogger<IBackupManagementService> logger)
        {
            try
            {
                logger.LogInformation("Getting backup status for package: {PackageName}", packageName);
                var response = await backupService.GetBackupStatusAsync(packageName);
                return Results.Ok(response);
            }
            catch (PackageNotFoundException ex)
            {
                logger.LogWarning(ex, "Package not found: {PackageName}", packageName);
                return Results.NotFound(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to get backup status for {PackageName}", packageName);
                return Results.Problem("Failed to get backup status");
            }
        }
    }

    public record CreateBackupRequest(string? Version = null);
    public record RestoreBackupRequest(string Filename);
}
