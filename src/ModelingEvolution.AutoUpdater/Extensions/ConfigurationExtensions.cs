using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ModelingEvolution.AutoUpdater.Extensions;

public static class ConfigurationExtensions
{
    public static void DisplayConfigurationValues(this IConfiguration configuration, ILogger logger)
    {
        logger.LogInformation("=== AutoUpdater Configuration Values ===");
        
        // SSH Configuration
        logger.LogInformation("SSH Configuration:");
        logger.LogInformation("  SshHost: {SshHost}", configuration.SshHost() ?? "Not set");
        logger.LogInformation("  SshUser: {SshUser}", configuration.SshUser() ?? "Not set");
        logger.LogInformation("  SshPort: {SshPort}", configuration.SshPort());
        logger.LogInformation("  SshAuthMethod: {SshAuthMethod}", configuration.GetValue<string>("SshAuthMethod") ?? "Not set");
        logger.LogInformation("  SshKeyPath: {SshKeyPath}", configuration.SshKeyPath() ?? "Not set");
        logger.LogInformation("  SshTimeoutSeconds: {SshTimeoutSeconds}", configuration.SshTimeoutSeconds());
        logger.LogInformation("  SshKeepAliveSeconds: {SshKeepAliveSeconds}", configuration.SshKeepAliveSeconds());
        logger.LogInformation("  SshEnableCompression: {SshEnableCompression}", configuration.SshEnableCompression());
        
        // VPN Configuration
        logger.LogInformation("VPN Configuration:");
        logger.LogInformation("  VpnProviderAccess: {VpnProviderAccess}", configuration.VpnProviderAccess());

        // Apps page
        logger.LogInformation("Apps Configuration:");
        logger.LogInformation("  Apps:PackageName: {AppsPackageName}", configuration.AppsPackageName());
        logger.LogInformation("  Apps:Port: {AppsPort}", configuration.AppsPort());
        logger.LogInformation("  Apps:InternalBaseUrl: {AppsInternalBaseUrl}", configuration.AppsInternalBaseUrl());

        // Environment Information
        logger.LogInformation("Environment:");
        logger.LogInformation("  ASPNETCORE_ENVIRONMENT: {Environment}", Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Not set");
        logger.LogInformation("  Current Directory: {Directory}", Directory.GetCurrentDirectory());
        
        logger.LogInformation("=========================================");
    }
    
    // Extension methods for common configuration values following the rocket-welder pattern
    public static string SshUser(this IConfiguration configuration) => 
        configuration.GetValue<string>("SshUser") ?? string.Empty;
    
    public static string SshPassword(this IConfiguration configuration) => 
        configuration.GetValue<string>("SshPwd") ?? string.Empty;
    
    public static string SshKeyPath(this IConfiguration configuration) => 
        configuration.GetValue<string>("SshKeyPath") ?? string.Empty;
    
    public static string SshKeyPassphrase(this IConfiguration configuration) => 
        configuration.GetValue<string>("SshKeyPassphrase") ?? string.Empty;
    
    public static int SshPort(this IConfiguration configuration) => 
        configuration.GetValue<int?>("SshPort") ?? 22;
    
    public static int SshTimeoutSeconds(this IConfiguration configuration) => 
        configuration.GetValue<int?>("SshTimeoutSeconds") ?? 30;
    
    public static int SshKeepAliveSeconds(this IConfiguration configuration) => 
        configuration.GetValue<int?>("SshKeepAliveSeconds") ?? 30;
    
    public static bool SshEnableCompression(this IConfiguration configuration) => 
        configuration.GetValue<bool?>("SshEnableCompression") ?? true;
    
    
    
    public static string VpnProviderAccess(this IConfiguration configuration) => 
        configuration.GetValue<string>("VpnProviderAccess") ?? "None";
    
    public static string SshHost(this IConfiguration configuration) =>
        configuration.GetValue<string>("SshHost") ?? string.Empty;

    private const string HostAddressKey = "HostAddress";
    private const string AppsPackageNameKey = "Apps:PackageName";
    private const string AppsPortKey = "Apps:Port";
    private const string AppsInternalBaseUrlKey = "Apps:InternalBaseUrl";

    /// <summary>The Docker host's address on the default bridge, as seen from inside a container.</summary>
    public const string DefaultDockerHost = "172.17.0.1";

    public const string DefaultAppsPackageName = "rocketwelder-apps";
    public const int DefaultAppsPort = 8095;

    /// <summary>The host the AutoUpdater container reaches: SshHost, else HostAddress, else the Docker bridge.</summary>
    public static string DockerHostAddress(this IConfiguration configuration)
    {
        var sshHost = configuration.SshHost();
        if (!string.IsNullOrWhiteSpace(sshHost)) return sshHost;
        var hostAddress = configuration.GetValue<string>(HostAddressKey);
        return string.IsNullOrWhiteSpace(hostAddress) ? DefaultDockerHost : hostAddress;
    }

    /// <summary>The package whose container serves the apps (packaging/apps/compose).</summary>
    public static string AppsPackageName(this IConfiguration configuration)
    {
        var name = configuration.GetValue<string>(AppsPackageNameKey);
        return string.IsNullOrWhiteSpace(name) ? DefaultAppsPackageName : name;
    }

    /// <summary>The LAN port the apps container publishes; browsers download from it.</summary>
    public static int AppsPort(this IConfiguration configuration) =>
        int.TryParse(configuration.GetValue<string>(AppsPortKey), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536
            ? port
            : DefaultAppsPort;

    /// <summary>Where the AutoUpdater reads the apps manifest: configured, else the Docker host at the apps port.</summary>
    public static Uri AppsInternalBaseUrl(this IConfiguration configuration) =>
        Uri.TryCreate(configuration.GetValue<string>(AppsInternalBaseUrlKey), UriKind.Absolute, out var configured)
            && (configured.Scheme == Uri.UriSchemeHttp || configured.Scheme == Uri.UriSchemeHttps)
            ? configured
            : new UriBuilder(Uri.UriSchemeHttp, configuration.DockerHostAddress(), configuration.AppsPort()).Uri;
}