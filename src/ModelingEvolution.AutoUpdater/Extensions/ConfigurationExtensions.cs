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
}