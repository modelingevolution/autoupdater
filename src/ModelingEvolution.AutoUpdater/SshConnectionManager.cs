using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.Extensions;
using ModelingEvolution.AutoUpdater.Services;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace ModelingEvolution.AutoUpdater;

/// <summary>
/// Manages SSH connections with support for multiple authentication methods
/// </summary>
public class SshConnectionManager : ISshConnectionManager, IDisposable
{
    private readonly SshConfiguration _config;
    private readonly ILogger _logger;
    private SshClient? _client;
    private bool _disposed;

    public SshConnectionManager(SshConfiguration config, ILogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
    }

    /// <summary>
    /// Creates an SshConnectionManager from IConfiguration and ILoggerFactory
    /// </summary>
    /// <param name="configuration">The configuration containing SSH settings</param>
    /// <param name="loggerFactory">The logger factory for creating loggers</param>
    /// <param name="hostOverride">Optional host override. If not provided, uses Host from configuration or throws if missing</param>
    /// <returns>A configured SshConnectionManager instance</returns>
    public static SshConnectionManager CreateFromConfiguration(IConfiguration configuration, ILoggerFactory loggerFactory, string? hostOverride = null)
    {
        var globalConfig = new GlobalSshConfiguration();
        configuration.Bind(globalConfig);
        
        var logger = loggerFactory.CreateLogger<SshConnectionManager>();
        
        // Determine the host to connect to
        string host;
        if (!string.IsNullOrEmpty(hostOverride))
        {
            host = hostOverride;
        }
        else
        {
            host = configuration.SshHost();
            if (string.IsNullOrEmpty(host))
            {
                throw new InvalidOperationException("SSH Host must be provided either in configuration or as hostOverride parameter");
            }
        }
        
        var sshConfig = globalConfig.ToSshConfiguration(host);
        
        logger.LogDebug("Creating SshConnectionManager with configuration: {Config}", 
            sshConfig.GetSafeConfigurationSummary());
        
        return new SshConnectionManager(sshConfig, logger);
    }

    /// <summary>
    /// Creates and connects SSH client based on configuration
    /// </summary>
    public async Task<SshClient> CreateConnectionAsync()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SshConnectionManager));

        _logger.LogInformation("Creating SSH connection to {Host}:{Port} with user {User} using {AuthMethod}",
            _config.Host, _config.Port, _config.User, _config.AuthMethod);

        var connectionInfo = _config.AuthMethod switch
        {
            SshAuthMethod.Password => CreatePasswordAuth(),
            SshAuthMethod.PrivateKey => CreateKeyAuth(),
            SshAuthMethod.PrivateKeyWithPassphrase => CreateKeyAuthWithPassphrase(),
            SshAuthMethod.KeyWithPasswordFallback => CreateKeyAuthWithFallback(),
            _ => throw new NotSupportedException($"SSH auth method {_config.AuthMethod} is not supported")
        };

        _client = new SshClient(connectionInfo);
        
        // Configure client settings
        _client.KeepAliveInterval = _config.KeepAliveInterval;
        _client.ConnectionInfo.Timeout = _config.Timeout;

        try
        {
            await ConnectWithRetryAsync(_client);
            _logger.LogInformation("Successfully connected to SSH host {Host}:{Port}", _config.Host, _config.Port);
            return _client;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to SSH host {Host}:{Port}", _config.Host, _config.Port);
            _client?.Dispose();
            _client = null;
            throw;
        }
    }

    /// <summary>
    /// Creates an SSH service instance for the configured host
    /// </summary>
    public async Task<ISshService> CreateSshServiceAsync()
    {
        var sshClient = await CreateConnectionAsync();
        var scpClient = await CreateScpConnectionAsync();
        var logger = _logger as ILogger<SshService> ?? 
                    new LoggerFactory().CreateLogger<SshService>();
        
        return new SshService(sshClient, scpClient, logger);
    }

    /// <summary>
    /// Tests SSH connectivity to the configured host
    /// </summary>
    public async Task<bool> TestConnectivityAsync()
    {
        try
        {
            using var testClient = await CreateConnectionAsync();
            return testClient.IsConnected;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSH connectivity test failed");
            return false;
        }
    }

    /// <summary>
    /// Creates and connects SCP client based on configuration
    /// </summary>
    private async Task<ScpClient> CreateScpConnectionAsync()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SshConnectionManager));

        _logger.LogInformation("Creating SCP connection to {Host}:{Port} with user {User} using {AuthMethod}",
            _config.Host, _config.Port, _config.User, _config.AuthMethod);

        var connectionInfo = _config.AuthMethod switch
        {
            SshAuthMethod.Password => CreatePasswordAuth(),
            SshAuthMethod.PrivateKey => CreateKeyAuth(),
            SshAuthMethod.PrivateKeyWithPassphrase => CreateKeyAuthWithPassphrase(),
            SshAuthMethod.KeyWithPasswordFallback => CreateKeyAuthWithFallback(),
            _ => throw new NotSupportedException($"SSH auth method {_config.AuthMethod} is not supported")
        };

        var scpClient = new ScpClient(connectionInfo);
        
        // Configure client settings
        scpClient.KeepAliveInterval = _config.KeepAliveInterval;
        scpClient.ConnectionInfo.Timeout = _config.Timeout;

        try
        {
            await ConnectWithRetryAsync(scpClient);
            _logger.LogInformation("Successfully connected SCP client to {Host}:{Port}", _config.Host, _config.Port);
            return scpClient;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect SCP client to {Host}:{Port}", _config.Host, _config.Port);
            scpClient?.Dispose();
            throw;
        }
    }

    private ConnectionInfo CreatePasswordAuth()
    {
        if (string.IsNullOrEmpty(_config.Password))
            throw new InvalidOperationException("Password is required for password authentication");

        var authMethod = new PasswordAuthenticationMethod(_config.User, _config.Password);
        return new ConnectionInfo(_config.Host, _config.Port, _config.User, authMethod);
    }

    private ConnectionInfo CreateKeyAuth()
    {
        if (string.IsNullOrEmpty(_config.KeyPath))
            throw new InvalidOperationException("KeyPath is required for private key authentication");

        ValidateKeyFile(_config.KeyPath);
        
        var keyFile = new PrivateKeyFile(_config.KeyPath);
        var authMethod = new PrivateKeyAuthenticationMethod(_config.User, keyFile);
        return new ConnectionInfo(_config.Host, _config.Port, _config.User, authMethod);
    }

    private ConnectionInfo CreateKeyAuthWithPassphrase()
    {
        if (string.IsNullOrEmpty(_config.KeyPath))
            throw new InvalidOperationException("KeyPath is required for private key authentication");
        
        if (string.IsNullOrEmpty(_config.Passphrase))
            throw new InvalidOperationException("Passphrase is required for passphrase-protected private key authentication");

        ValidateKeyFile(_config.KeyPath);

        var keyFile = new PrivateKeyFile(_config.KeyPath, _config.Passphrase);
        var authMethod = new PrivateKeyAuthenticationMethod(_config.User, keyFile);
        return new ConnectionInfo(_config.Host, _config.Port, _config.User, authMethod);
    }

    private ConnectionInfo CreateKeyAuthWithFallback()
    {
        var authMethods = new List<AuthenticationMethod>();

        // Try key authentication first
        if (!string.IsNullOrEmpty(_config.KeyPath))
        {
            try
            {
                ValidateKeyFile(_config.KeyPath);
                
                var keyFile = string.IsNullOrEmpty(_config.Passphrase) 
                    ? new PrivateKeyFile(_config.KeyPath)
                    : new PrivateKeyFile(_config.KeyPath, _config.Passphrase);
                    
                authMethods.Add(new PrivateKeyAuthenticationMethod(_config.User, keyFile));
                _logger.LogDebug("Added private key authentication method");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load SSH private key from {KeyPath}, will try password fallback", _config.KeyPath);
            }
        }

        // Add password fallback
        if (!string.IsNullOrEmpty(_config.Password))
        {
            authMethods.Add(new PasswordAuthenticationMethod(_config.User, _config.Password));
            _logger.LogDebug("Added password authentication method as fallback");
        }

        if (authMethods.Count == 0)
            throw new InvalidOperationException("No valid authentication methods available. Provide either KeyPath or Password.");

        return new ConnectionInfo(_config.Host, _config.Port, _config.User, authMethods.ToArray());
    }

    private void ValidateKeyFile(string keyPath)
    {
        if (!File.Exists(keyPath))
            throw new FileNotFoundException($"SSH private key file not found: {keyPath}");

        // Check file permissions on Unix systems
        if (Environment.OSVersion.Platform == PlatformID.Unix)
        {
            var fileInfo = new FileInfo(keyPath);
            // On Unix, we should check that the key file is not world-readable
            // This is a basic check - in production, you might want more sophisticated permission checking
            _logger.LogDebug("SSH private key file found: {KeyPath}", keyPath);
        }
    }

    private async Task ConnectWithRetryAsync(SshClient client)
    {
        const int maxRetries = 3;
        const int retryDelayMs = 1000;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await client.ConnectAsync(CancellationToken.None);
                return;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                _logger.LogWarning(ex, "SSH connection attempt {Attempt} failed, retrying in {DelayMs}ms", 
                    attempt, retryDelayMs);
                await Task.Delay(retryDelayMs);
            }
        }

        // Final attempt without catching exception
        await client.ConnectAsync(CancellationToken.None);
    }

    private async Task ConnectWithRetryAsync(ScpClient client)
    {
        const int maxRetries = 3;
        const int retryDelayMs = 1000;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await client.ConnectAsync(CancellationToken.None);
                return;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                _logger.LogWarning(ex, "SCP connection attempt {Attempt} failed, retrying in {DelayMs}ms", 
                    attempt, retryDelayMs);
                await Task.Delay(retryDelayMs);
            }
        }

        // Final attempt without catching exception
        await client.ConnectAsync(CancellationToken.None);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _client?.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Result of SSH command execution
/// </summary>
public record SshCommandResult
{
    public static SshCommandResult Failed(string command, int exitCode, string output, string error)
    {
        return new SshCommandResult
        {
            Command = command,
            ExitCode = exitCode,
            Output = output,
            Error = error
        };
    }

    public SshCommandResult()
    {
        
    }
    public SshCommandResult(string command, string output)
    {
        this.Command = command;
        this.Output = output;
    }
    /// <summary>
    /// The command that was executed
    /// </summary>
    public string Command { get; init; } = string.Empty;
    
    /// <summary>
    /// Exit code of the command
    /// </summary>
    public int ExitCode { get; init; }
    
    /// <summary>
    /// Standard output of the command
    /// </summary>
    public string Output { get; init; } = string.Empty;
    
    /// <summary>
    /// Standard error of the command
    /// </summary>
    public string Error { get; init; } = string.Empty;
    
    /// <summary>
    /// Whether the command succeeded (exit code 0)
    /// </summary>
    public bool IsSuccess => ExitCode == 0;
}