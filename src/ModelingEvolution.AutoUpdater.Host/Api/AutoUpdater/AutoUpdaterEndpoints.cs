using ModelingEvolution.AutoUpdater.Host.Api.AutoUpdater.Models;

namespace ModelingEvolution.AutoUpdater.Host.Api.AutoUpdater;

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

        // Health check endpoint
        app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }))
            .WithName("Health")
            .WithSummary("Health check endpoint")
            .WithDescription("Returns the health status of the application")
            .Produces<object>();

        // Debug endpoint without dependencies
        group.MapGet("/debug", () => 
        {
            return Results.Ok(new { message = "Debug endpoint working", timestamp = DateTime.UtcNow });
        })
            .WithName("Debug")
            .WithSummary("Debug endpoint without dependencies")
            .WithDescription("Returns debug info without dependencies")
            .Produces<object>();
    }

    private static async Task<IResult> GetPackagesAsync(
        AutoUpdaterService service,
        ILogger<AutoUpdaterService> logger)
    {
        try
        {
            logger.LogDebug("GetPackagesAsync endpoint called");
            var response = await service.GetPackagesAsync();
            logger.LogDebug("GetPackagesAsync completed successfully");
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