using FluentAssertions;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.Services;
using NSubstitute;

namespace ModelingEvolution.AutoUpdater.Tests.Services;

public class HealthCheckServiceTests
{
    private static readonly string[] ComposeFiles = ["docker-compose.yml", "docker-compose.arm64.yml"];
    private const string Dir = "/var/docker/configuration/rocket-welder";

    // A station has only the compose v2 plugin: `docker-compose` is "command not found", which failed every update's
    // health check and rolled the package back (Neuron, 2026-11-07).
    [Fact]
    public async Task Uses_the_compose_v2_plugin_and_reports_running_services_healthy()
    {
        var ssh = Substitute.For<ISshService>();
        ssh.ExecuteCommandAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(new SshCommandResult { ExitCode = 127, Error = "bash: line 1: docker-compose: command not found" });
        ssh.ExecuteCommandAsync("sudo docker compose -f docker-compose.yml -f docker-compose.arm64.yml config --services", Dir)
            .Returns(new SshCommandResult { Output = "eventstore.db\nmodelingevolution.rocketwelder\n" });
        foreach (var service in new[] { "eventstore.db", "modelingevolution.rocketwelder" })
            ssh.ExecuteCommandAsync($"sudo docker compose -f docker-compose.yml -f docker-compose.arm64.yml ps --format json {service}", Dir)
                .Returns(new SshCommandResult { Output = $"{{\"Name\":\"{service}\",\"State\":\"running\",\"Status\":\"Up 45 seconds\"}}\n" });

        var result = await new HealthCheckService(ssh, Substitute.For<ILogger<HealthCheckService>>())
            .CheckServicesHealthAsync(ComposeFiles, Dir);

        result.AllHealthy.Should().BeTrue(result.ErrorMessage);
        result.HealthyServices.Should().BeEquivalentTo("eventstore.db", "modelingevolution.rocketwelder");
        await ssh.DidNotReceive().ExecuteCommandAsync(Arg.Is<string>(c => c.StartsWith("docker-compose")), Arg.Any<string?>());
    }
}
