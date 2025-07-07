using System.Net.Http;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.IntegrationTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace ModelingEvolution.AutoUpdater.IntegrationTests;

/// <summary>
/// Shared test fixture for AutoUpdater integration tests
/// </summary>
public class AutoUpdaterTestFixture : IAsyncLifetime
{
    private IMessageSink? _messageSink;
    
    public DockerClient DockerClient { get; }
    public HttpClient HttpClient { get; }
    public string ProjectName { get; } = "autoupdater-test";
    public int TestPort { get; } = 8090;
    public string TestApiUrl => $"http://localhost:{TestPort}";

    public AutoUpdaterTestFixture(IMessageSink messageSink)
    {
        _messageSink = messageSink;
        DockerClient = new DockerClientConfiguration().CreateClient();
        HttpClient = new HttpClient { BaseAddress = new Uri(TestApiUrl) };
    }

    public async Task InitializeAsync()
    {
        _messageSink?.OnMessage(new Xunit.Sdk.DiagnosticMessage("Initializing AutoUpdater test fixture..."));
        
        // Ensure no conflicting containers are running
        await CleanupContainersAsync();
    }

    public async Task DisposeAsync()
    {
        _messageSink?.OnMessage(new Xunit.Sdk.DiagnosticMessage("Disposing AutoUpdater test fixture..."));
        
        HttpClient?.Dispose();
        DockerClient?.Dispose();
        
        // Final cleanup
        await CleanupContainersAsync();
    }

    private async Task CleanupContainersAsync()
    {
        try
        {
            // Find and remove any test containers
            var containers = await DockerClient.Containers.ListContainersAsync(new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["label"] = new Dictionary<string, bool>
                    {
                        [$"com.docker.compose.project={ProjectName}"] = true
                    }
                }
            });

            foreach (var container in containers)
            {
                _messageSink?.OnMessage(new Xunit.Sdk.DiagnosticMessage($"Removing test container: {container.Names.FirstOrDefault()}"));
                
                await DockerClient.Containers.RemoveContainerAsync(container.ID, new ContainerRemoveParameters
                {
                    Force = true,
                    RemoveVolumes = true
                });
            }
        }
        catch (Exception ex)
        {
            _messageSink?.OnMessage(new Xunit.Sdk.DiagnosticMessage($"Error during container cleanup: {ex.Message}"));
        }
    }
}