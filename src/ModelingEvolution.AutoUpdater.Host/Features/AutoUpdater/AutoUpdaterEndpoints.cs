using ModelingEvolution.AutoUpdater.Host.Features.AutoUpdater.Models;
using Microsoft.AspNetCore.Mvc;

namespace ModelingEvolution.AutoUpdater.Host.Features.AutoUpdater;

public static class AutoUpdaterEndpoints
{
    public static void MapAutoUpdaterEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api")
            .WithTags("AutoUpdater");

        group.MapGet("/packages", GetPackagesAsync)
            .WithName("GetPackages")
            .WithSummary("Get all configured packages")
            .WithDescription("Returns a list of all packages configured for auto-updating")
            .Produces<PackagesResponse>();

        group.MapGet("/upgrades/{packageName}", GetUpgradeStatusAsync)
            .WithName("GetUpgradeStatus")
            .WithSummary("Check upgrade status for a package")
            .WithDescription("Returns the current and available versions for a specific package")
            .Produces<UpgradeStatusResponse>()
            .Produces(404);

        group.MapPost("/update/{packageName}", TriggerUpdateAsync)
            .WithName("TriggerUpdate")
            .WithSummary("Trigger update for a specific package")
            .WithDescription("Initiates an update process for the specified package")
            .Produces<UpdateResponse>()
            .Produces(404);

        group.MapPost("/update-all", TriggerUpdateAllAsync)
            .WithName("TriggerUpdateAll")
            .WithSummary("Trigger updates for all packages")
            .WithDescription("Initiates update processes for all packages with available upgrades")
            .Produces<UpdateAllResponse>();
    }

    private static async Task<IResult> GetPackagesAsync(
        AutoUpdaterService service,
        ILogger<AutoUpdaterService> logger)
    {
        try
        {
            var response = await service.GetPackagesAsync();
            return Results.Ok(response);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting packages");
            return Results.Problem("Failed to retrieve packages", statusCode: 500);
        }
    }

    private static async Task<IResult> GetUpgradeStatusAsync(
        string packageName,
        AutoUpdaterService service,
        ILogger<AutoUpdaterService> logger)
    {
        try
        {
            var response = await service.GetUpgradeStatusAsync(packageName);
            return Results.Ok(response);
        }
        catch (PackageNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error checking upgrade status for {PackageName}", packageName);
            return Results.Problem("Failed to check upgrade status", statusCode: 500);
        }
    }

    private static async Task<IResult> TriggerUpdateAsync(
        string packageName,
        AutoUpdaterService service,
        ILogger<AutoUpdaterService> logger)
    {
        try
        {
            var response = await service.TriggerUpdateAsync(packageName);
            return Results.Ok(response);
        }
        catch (PackageNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error starting update for {PackageName}", packageName);
            return Results.Problem("Failed to start update", statusCode: 500);
        }
    }

    private static async Task<IResult> TriggerUpdateAllAsync(
        AutoUpdaterService service,
        ILogger<AutoUpdaterService> logger)
    {
        try
        {
            var response = await service.TriggerUpdateAllAsync();
            return Results.Ok(response);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error starting update all");
            return Results.Problem("Failed to start update all", statusCode: 500);
        }
    }
}