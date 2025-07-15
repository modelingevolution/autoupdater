using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.Services;
using ModelingEvolution.RuntimeConfiguration;

namespace ModelingEvolution.AutoUpdater
{
    public static class ContainerExtensions
    {
        public static IServiceCollection AddAutoUpdater(this IServiceCollection container)
        {
            // Add runtime configuration support
            container.AddRuntimeConfiguration();
            
            // Register core services
            container.AddSingleton<UpdateService>();
            container.AddSingleton<DockerComposeConfigurationRepository>();

            // Register the new refactored services
            container.AddSingleton<IGitService, GitService>();
            container.AddSingleton<IScriptMigrationService, ScriptMigrationService>();
            container.AddSingleton<ISshService>(sp =>
                sp.GetRequiredService<ISshConnectionManager>().CreateSshServiceAsync().Result);
            
            container.AddSingleton<ISshConnectionManager, SshConnectionManager>(x =>
                SshConnectionManager.CreateFromConfiguration(x.GetRequiredService<IConfiguration>(),
                    x.GetRequiredService<ILoggerFactory>()));
            // Register SshConnectionManager using the static factory method
            
            container.AddSingleton<IDockerComposeService, DockerComposeService>();
            container.AddSingleton<IDeploymentStateProvider, DeploymentStateProvider>();
            container.AddSingleton<IBackupService, BackupService>();
            container.AddSingleton<IHealthCheckService, HealthCheckService>();
            container.AddSingleton<IProgressService, ProgressService>();
            container.AddSingleton<IDockerAuthService, DockerAuthService>();
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
