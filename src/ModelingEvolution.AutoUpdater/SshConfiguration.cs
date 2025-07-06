using System.Text.Json.Serialization;

namespace ModelingEvolution.AutoUpdater;

/// <summary>
/// SSH authentication method options
/// </summary>
public enum SshAuthMethod
{
    /// <summary>
    /// Password-based authentication
    /// </summary>
    Password,
    
    /// <summary>
    /// Private key authentication without passphrase
    /// </summary>
    PrivateKey,
    
    /// <summary>
    /// Private key authentication with passphrase
    /// </summary>
    PrivateKeyWithPassphrase,
    
    /// <summary>
    /// Try private key first, fallback to password if key fails
    /// </summary>
    KeyWithPasswordFallback
}

/// <summary>
/// SSH connection configuration
/// </summary>
public class SshConfiguration
{
    /// <summary>
    /// SSH hostname or IP address
    /// </summary>
    public string Host { get; set; } = string.Empty;
    
    /// <summary>
    /// SSH username
    /// </summary>
    public string User { get; set; } = string.Empty;
    
    /// <summary>
    /// SSH password (optional, used for password auth or fallback)
    /// </summary>
    public string? Password { get; set; }
    
    /// <summary>
    /// Path to SSH private key file
    /// </summary>
    public string? KeyPath { get; set; }
    
    /// <summary>
    /// Passphrase for encrypted private key
    /// </summary>
    public string? Passphrase { get; set; }
    
    /// <summary>
    /// SSH authentication method to use
    /// </summary>
    public SshAuthMethod AuthMethod { get; set; } = SshAuthMethod.Password;
    
    /// <summary>
    /// SSH port (default: 22)
    /// </summary>
    public int Port { get; set; } = 22;
    
    /// <summary>
    /// Connection timeout
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    
    /// <summary>
    /// Enable SSH compression
    /// </summary>
    public bool EnableCompression { get; set; } = true;
    
    /// <summary>
    /// SSH client keep-alive interval
    /// </summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Returns a configuration summary safe for logging (without sensitive data)
    /// </summary>
    public string GetSafeConfigurationSummary()
    {
        return $"SSH Config - Host: {Host}, User: {User}, Port: {Port}, " +
               $"AuthMethod: {AuthMethod}, " +
               $"HasPassword: {!string.IsNullOrEmpty(Password)}, " +
               $"HasKeyPath: {!string.IsNullOrEmpty(KeyPath)}, " +
               $"HasPassphrase: {!string.IsNullOrEmpty(Passphrase)}, " +
               $"Timeout: {Timeout.TotalSeconds}s, " +
               $"Compression: {EnableCompression}, " +
               $"KeepAlive: {KeepAliveInterval.TotalSeconds}s";
    }
}

/// <summary>
/// Global SSH configuration from appsettings
/// </summary>
public class GlobalSshConfiguration
{
    /// <summary>
    /// Default SSH user for all connections
    /// </summary>
    public string? SshUser { get; set; }
    
    /// <summary>
    /// Default SSH password (legacy, prefer key-based auth)
    /// </summary>
    public string? SshPwd { get; set; }
    
    /// <summary>
    /// Path to SSH private key file
    /// </summary>
    public string? SshKeyPath { get; set; }
    
    /// <summary>
    /// Passphrase for SSH private key
    /// </summary>
    public string? SshKeyPassphrase { get; set; }
    
    /// <summary>
    /// SSH authentication method
    /// </summary>
    public SshAuthMethod SshAuthMethod { get; set; } = SshAuthMethod.Password;
    
    /// <summary>
    /// SSH port (default: 22)
    /// </summary>
    public int SshPort { get; set; } = 22;
    
    /// <summary>
    /// SSH connection timeout in seconds
    /// </summary>
    public int SshTimeoutSeconds { get; set; } = 30;
    
    /// <summary>
    /// Enable SSH compression
    /// </summary>
    public bool SshEnableCompression { get; set; } = true;
    
    /// <summary>
    /// SSH keep-alive interval in seconds
    /// </summary>
    public int SshKeepAliveSeconds { get; set; } = 30;

    /// <summary>
    /// Converts global configuration to SSH configuration for a specific host
    /// </summary>
    public SshConfiguration ToSshConfiguration(string host)
    {
        return new SshConfiguration
        {
            Host = host,
            User = SshUser ?? string.Empty,
            Password = SshPwd,
            KeyPath = SshKeyPath,
            Passphrase = SshKeyPassphrase,
            AuthMethod = SshAuthMethod,
            Port = SshPort,
            Timeout = TimeSpan.FromSeconds(SshTimeoutSeconds),
            EnableCompression = SshEnableCompression,
            KeepAliveInterval = TimeSpan.FromSeconds(SshKeepAliveSeconds)
        };
    }

    /// <summary>
    /// Returns a configuration summary safe for logging (without sensitive data)
    /// </summary>
    public string GetSafeConfigurationSummary()
    {
        return $"Global SSH Config - User: {(!string.IsNullOrEmpty(SshUser) ? SshUser : "not set")}, " +
               $"AuthMethod: {SshAuthMethod}, " +
               $"HasPassword: {!string.IsNullOrEmpty(SshPwd)}, " +
               $"HasKeyPath: {!string.IsNullOrEmpty(SshKeyPath)}, " +
               $"HasPassphrase: {!string.IsNullOrEmpty(SshKeyPassphrase)}, " +
               $"Port: {SshPort}, " +
               $"Timeout: {SshTimeoutSeconds}s, " +
               $"Compression: {SshEnableCompression}, " +
               $"KeepAlive: {SshKeepAliveSeconds}s";
    }
}