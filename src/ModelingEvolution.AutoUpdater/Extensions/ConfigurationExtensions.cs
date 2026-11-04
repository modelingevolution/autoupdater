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

        // The Apps settings are logged by the host from the one AppsSettings it registered (AppsSettings.Log).

        // Environment Information
        logger.LogInformation("Environment:");
        logger.LogInformation("  ASPNETCORE_ENVIRONMENT: {Environment}", Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Not set");
        logger.LogInformation("  Current Directory: {Directory}", Directory.GetCurrentDirectory());
        
        logger.LogInformation("=========================================");
    }
    
    // Extension methods for common configuration values 
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

    private const string AppsEnabledKey = "Apps:Enabled";
    private const string AppsSourceUrlKey = "Apps:SourceUrl";
    private const string AppsTitleKey = "Apps:Title";
    private const string AppsPackageNameKey = "Apps:PackageName";

    /// <summary><c>Apps:Enabled</c> as configured, or null when unset. Validated by <see cref="AppsSettings(IConfiguration)"/>.</summary>
    public static string? AppsEnabled(this IConfiguration configuration) => configuration.GetValue<string>(AppsEnabledKey);

    /// <summary><c>Apps:SourceUrl</c> as configured, or null when unset.</summary>
    public static string? AppsSourceUrl(this IConfiguration configuration) => configuration.GetValue<string>(AppsSourceUrlKey);

    /// <summary><c>Apps:Title</c> as configured, or null when unset.</summary>
    public static string? AppsTitle(this IConfiguration configuration) => configuration.GetValue<string>(AppsTitleKey);

    /// <summary><c>Apps:PackageName</c> as configured, or null when unset.</summary>
    public static string? AppsPackageName(this IConfiguration configuration) => configuration.GetValue<string>(AppsPackageNameKey);

    /// <summary>The Apps page's settings, validated. Read once at startup; the host registers the result.</summary>
    public static Apps.AppsSettings AppsSettings(this IConfiguration configuration) => Apps.AppsSettings.Create(
        configuration.AppsEnabled(), configuration.AppsSourceUrl(), configuration.AppsTitle(), configuration.AppsPackageName());

    private const string HostAddressKey = "HostAddress";

    /// <summary>The Docker host's address on the default bridge, as seen from inside a container.</summary>
    public const string DefaultDockerHost = "172.17.0.1";

    /// <summary>The host the AutoUpdater container reaches: SshHost, else HostAddress, else the Docker bridge.</summary>
    public static string DockerHostAddress(this IConfiguration configuration)
    {
        var sshHost = configuration.SshHost();
        if (!string.IsNullOrWhiteSpace(sshHost)) return sshHost;
        var hostAddress = configuration.GetValue<string>(HostAddressKey);
        return string.IsNullOrWhiteSpace(hostAddress) ? DefaultDockerHost : hostAddress;
    }
}