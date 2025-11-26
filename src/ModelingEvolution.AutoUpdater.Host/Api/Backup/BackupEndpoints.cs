using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater;
using ModelingEvolution.AutoUpdater.Services;
using ModelingEvolution.AutoUpdater.Models;
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
                .Produces<BackupListResult>();

            group.MapPost("/{packageName}/create", CreateBackupAsync)
                .WithName("CreateBackup")
                .WithSummary("Create a new backup for a package")
                .Produces<BackupResult>();

            group.MapPost("/{packageName}/restore", RestoreBackupAsync)
                .WithName("RestoreBackup")
                .WithSummary("Restore a package from a backup")
                .Produces<RestoreResult>();
        }

        private static async Task<IResult> ListBackupsAsync(
            string packageName,
            DockerComposeConfigurationModel configModel,
            IBackupService backupService,
            ILogger<IBackupService> logger)
        {
            try
            {
                logger.LogInformation("Listing backups for package: {PackageName}", packageName);

                var config = configModel.GetPackage(packageName);
                if (config == null)
                {
                    logger.LogWarning("Package not found: {PackageName}", packageName);
                    return Results.NotFound(new { error = $"Package {packageName} not found" });
                }

                var result = await backupService.ListBackupsAsync(config.HostComposeFolderPath);

                if (result.Success)
                {
                    return Results.Ok(result);
                }
                else
                {
                    return Results.Problem(detail: result.Error, statusCode: 500);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to list backups for {PackageName}", packageName);
                return Results.Problem("Failed to list backups");
            }
        }

        private static async Task<IResult> CreateBackupAsync(
            string packageName,
            DockerComposeConfigurationModel configModel,
            UpdateService updateService,
            [FromBody] CreateBackupRequest? request,
            ILogger<IBackupService> logger)
        {
            try
            {
                logger.LogInformation("Creating backup for package: {PackageName}", packageName);

                var config = configModel.GetPackage(packageName);
                if (config == null)
                {
                    return Results.NotFound(new { error = $"Package {packageName} not found" });
                }

                var result = await updateService.BackupPackageAsync(config, request?.Version);

                if (result.Success)
                {
                    return Results.Ok(result);
                }
                else
                {
                    return Results.Problem(detail: result.Error, statusCode: 500);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to create backup for {PackageName}", packageName);
                return Results.Problem("Failed to create backup");
            }
        }

        private static async Task<IResult> RestoreBackupAsync(
            string packageName,
            DockerComposeConfigurationModel configModel,
            UpdateService updateService,
            [FromBody] RestoreBackupRequest request,
            ILogger<IBackupService> logger)
        {
            try
            {
                logger.LogInformation("Restoring backup for package: {PackageName}, file: {Filename}",
                    packageName, request.Filename);

                var config = configModel.GetPackage(packageName);
                if (config == null)
                {
                    return Results.NotFound(new { error = $"Package {packageName} not found" });
                }

                // Validate filename to prevent path traversal
                if (request.Filename.Contains("..") || request.Filename.Contains("/") || request.Filename.Contains("\\"))
                {
                    logger.LogWarning("Invalid backup filename: {Filename}", request.Filename);
                    return Results.BadRequest(new { error = "Invalid backup filename" });
                }

                var result = await updateService.RestorePackageAsync(config, request.Filename);

                if (result.Success)
                {
                    return Results.Ok(result);
                }
                else
                {
                    return Results.Problem(detail: result.Error, statusCode: 500);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to restore backup for {PackageName}", packageName);
                return Results.Problem("Failed to restore backup");
            }
        }
    }

    public record CreateBackupRequest(string? Version = null);
    public record RestoreBackupRequest(string Filename);
}
