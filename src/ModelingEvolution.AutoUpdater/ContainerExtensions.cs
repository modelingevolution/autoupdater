using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.Common;
using ModelingEvolution.AutoUpdater.Services;
using ModelingEvolution.RuntimeConfiguration;

namespace ModelingEvolution.AutoUpdater
{
    public static class ContainerExtensions
    {
        public static async Task<IServiceCollection> AddAutoUpdaterAsync(this IServiceCollection container, IConfiguration configuration)
        {
            // Add runtime configuration support
            container.AddRuntimeConfiguration();

            // Register core services
            container.AddSingleton<PackageManager>();
            container.AddSingleton<DockerComposeConfigurationModel>();

            // Register the new refactored services
            container.AddSingleton<IGitService, GitService>();
            container.AddSingleton<IScriptMigrationService, ScriptMigrationService>();
            
            // Create SshConnectionManager instance manually from configuration
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            var sshConnectionManager = SshConnectionManager.CreateFromConfiguration(configuration, loggerFactory);
            container.AddSingleton<ISshConnectionManager>(sshConnectionManager);
            
            // Create ISshService with proper await - no more .Result deadlock
            var sshService = await sshConnectionManager.CreateSshServiceAsync();
            container.AddSingleton<ISshService>(sshService);
            
            container.AddSingleton<IDockerComposeService, DockerComposeService>();
            container.AddSingleton<IDeploymentStateProvider, DeploymentStateProvider>();
            container.AddSingleton<IBackupService, BackupService>();
            container.AddSingleton<IHealthCheckService, HealthCheckService>();
            container.AddSingleton<IProgressService, ProgressService>();
            container.AddSingleton<IDockerAuthService, DockerAuthService>();
            container.AddSingleton<IEventHub, EventHub>();
            container.AddSingleton<IInMemoryLoggerSink>(sp => sp.GetRequiredService<InMemoryLoggerSink>());
            container.AddSingleton<InMemoryLoggerSink>(sp =>
            {
                var l = new InMemoryLoggerSink();
                l.Enabled = false; 
                return l;
            });

            // Register UpdateHost - it depends on the services above
            container.AddSingleton<UpdateHost>();
            container.AddHostedService(sp => sp.GetRequiredService<UpdateHost>());
            
            return container;
        }

        public static ILoggingBuilder AddInMemoryLogging(this ILoggingBuilder builder)
        {
            builder.Services.AddSingleton<ILoggerProvider>(provider =>
            {
                var sink = provider.GetRequiredService<IInMemoryLoggerSink>();
                return new InMemoryLoggerProvider(sink);
            });
            
            return builder;
        }

    }
}
