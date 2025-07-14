using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater;
using ModelingEvolution.AutoUpdater.Models;
using ModelingEvolution.AutoUpdater.Services;
using NSubstitute;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Reqnroll;

namespace ModelingEvolution.AutoUpdater.Tests.FeatureTests
{
    [Binding]
    public class UpdateHostSteps
    {
        private readonly IConfiguration _configuration = Substitute.For<IConfiguration>();
        private readonly IGitService _gitService = Substitute.For<IGitService>();
        private readonly IScriptMigrationService _scriptService = Substitute.For<IScriptMigrationService>();
        private readonly ISshConnectionManager _sshConnectionManager = Substitute.For<ISshConnectionManager>();
        private readonly IDockerComposeService _dockerService = Substitute.For<IDockerComposeService>();
        private readonly IDeploymentStateProvider _deploymentStateProvider = Substitute.For<IDeploymentStateProvider>();
        private readonly IBackupService _backupService = Substitute.For<IBackupService>();
        private readonly IHealthCheckService _healthCheckService = Substitute.For<IHealthCheckService>();
        private readonly IProgressService _progressService = Substitute.For<IProgressService>();
        private readonly ILogger<UpdateHost> _logger = Substitute.For<ILogger<UpdateHost>>();

        private UpdateHost _updateHost = null!;
        private DockerComposeConfiguration _config = null!;
        private UpdateResult _result = null!;
        private string _currentVersion = null!;
        private string _targetVersion = null!;
        private bool _backupScriptPresent;
        private bool _migrationScriptsExist;
        private bool _backupCreationSucceeds = true;
        private bool _migrationExecutionSucceeds = true;
        private bool _dockerStartupSucceeds = true;
        private bool _healthCheckSucceeds = true;
        private bool _criticalHealthFailure;
        private bool _unexpectedErrorOccurs;
        private string _backupFailureReason = "";
        private string _migrationFailureReason = "";
        private string _dockerFailureReason = "";

        [Given(@"I have an UpdateHost configured")]
        public void GivenIHaveAnUpdateHostConfigured()
        {
            _updateHost = new UpdateHost(
                _configuration, _logger, _gitService, _scriptService, 
                _sshConnectionManager, _dockerService, _deploymentStateProvider, 
                _backupService, _healthCheckService, _progressService);
        }

        [Given(@"the system has SSH connectivity")]
        public void GivenTheSystemHasSshConnectivity()
        {
            var mockSshService = Substitute.For<ISshService>();
            mockSshService.GetArchitectureAsync().Returns("x64");
            _sshConnectionManager.CreateSshServiceAsync().Returns(mockSshService);
        }

        [Given(@"I have a valid Docker Compose configuration")]
        public void GivenIHaveAValidDockerComposeConfiguration()
        {
            var tempDir = Path.GetTempPath();
            _config = new DockerComposeConfiguration
            {
                RepositoryLocation = Path.Combine(tempDir, "test-repo"),
                RepositoryUrl = "https://github.com/test/repo.git",
                DockerComposeDirectory = "./"
            };

            _dockerService.GetComposeFilesForArchitectureAsync(Arg.Any<string>(), "x64")
                         .Returns(new[] { "docker-compose.yml", "docker-compose.x64.yml" });
        }

        [Given(@"the current deployment version is ""(.*)""")]
        public void GivenTheCurrentDeploymentVersionIs(string version)
        {
            _currentVersion = version;
            var deploymentState = new DeploymentState(version, DateTime.Now)
            {
                Up = ImmutableSortedSet<Version>.Empty,
                Failed = ImmutableSortedSet<Version>.Empty
            };
            _deploymentStateProvider.GetDeploymentStateAsync(Arg.Any<string>()).Returns(deploymentState);
        }

        [Given(@"a new version ""(.*)"" is available")]
        [Given(@"the latest available version is ""(.*)""")]
        public void GivenANewVersionIsAvailable(string version)
        {
            _targetVersion = version;
            var gitVersion = new GitTagVersion(version, new Version(version));
            _gitService.GetAvailableVersionsAsync(Arg.Any<string>()).Returns(new[] { gitVersion });
        }

        [Given(@"migration scripts exist for the update")]
        public void GivenMigrationScriptsExistForTheUpdate()
        {
            _migrationScriptsExist = true;
            var migrationScripts = new[]
            {
                new MigrationScript("up-1.0.1.sh", "/path/up-1.0.1.sh", new Version(1, 0, 1), MigrationDirection.Up, true)
            };

            _scriptService.DiscoverScriptsAsync(Arg.Any<string>()).Returns(migrationScripts);
            _scriptService.FilterScriptsForMigrationAsync(
                Arg.Any<IEnumerable<MigrationScript>>(), 
                Arg.Any<string>(), 
                Arg.Any<string>(), 
                Arg.Any<ImmutableSortedSet<Version>?>()).Returns(migrationScripts);
        }

        [Given(@"no backup script is present")]
        public void GivenNoBackupScriptIsPresent()
        {
            _backupScriptPresent = false;
            _backupService.BackupScriptExistsAsync(Arg.Any<string>()).Returns(false);
        }

        [Given(@"a backup script is present")]
        public void GivenABackupScriptIsPresent()
        {
            _backupScriptPresent = true;
            _backupService.BackupScriptExistsAsync(Arg.Any<string>()).Returns(true);
        }

        [Given(@"backup creation succeeds")]
        public void GivenBackupCreationSucceeds()
        {
            _backupCreationSucceeds = true;
            _backupService.CreateBackupAsync(Arg.Any<string>())
                          .Returns(BackupResult.CreateSuccess("/backup/backup-123.tar.gz"));
            _backupService.RestoreBackupAsync(Arg.Any<string>(), Arg.Any<string>())
                          .Returns(RestoreResult.CreateSuccess());
        }

        [Given(@"backup creation fails with ""(.*)""")]
        public void GivenBackupCreationFailsWith(string reason)
        {
            _backupCreationSucceeds = false;
            _backupFailureReason = reason;
            _backupService.CreateBackupAsync(Arg.Any<string>())
                          .Returns(BackupResult.CreateFailure(reason));
        }

        [Given(@"migration scripts execute successfully")]
        public void GivenMigrationScriptsExecuteSuccessfully()
        {
            _migrationExecutionSucceeds = true;
            _scriptService.ExecuteScriptsAsync(Arg.Any<IEnumerable<MigrationScript>>(), Arg.Any<string>())
                         .Returns(new[] { new Version(1, 0, 1) });
        }

        [Given(@"migration script execution fails")]
        public void GivenMigrationScriptExecutionFails()
        {
            _migrationExecutionSucceeds = false;
            _migrationFailureReason = "Script execution failed";
            _scriptService.When(x => x.ExecuteScriptsAsync(Arg.Any<IEnumerable<MigrationScript>>(), Arg.Any<string>()))
                         .Do(x => throw new Exception(_migrationFailureReason));
        }

        [Given(@"Docker Compose starts successfully")]
        public void GivenDockerComposeStartsSuccessfully()
        {
            _dockerStartupSucceeds = true;
            // Docker service will work normally
        }

        [Given(@"Docker Compose startup fails")]
        public void GivenDockerComposeStartupFails()
        {
            _dockerStartupSucceeds = false;
            _dockerFailureReason = "Docker compose startup failed";
            
            var startCallCount = 0;
            _dockerService.When(x => x.StartServicesAsync(Arg.Any<string[]>(), Arg.Any<string>()))
                         .Do(x => {
                             startCallCount++;
                             if (startCallCount == 1)
                                 throw new Exception(_dockerFailureReason);
                         });
        }

        [Given(@"all services will be healthy after deployment")]
        public void GivenAllServicesWillBeHealthyAfterDeployment()
        {
            _healthCheckSucceeds = true;
            _healthCheckService.CheckServicesHealthAsync(Arg.Any<string[]>(), Arg.Any<string>())
                              .Returns(HealthCheckResult.Healthy(new List<string> { "api", "database" }));
        }

        [Given(@"some non-critical services fail health checks")]
        public void GivenSomeNonCriticalServicesFailHealthChecks()
        {
            _healthCheckSucceeds = false;
            _criticalHealthFailure = false;
            var healthCheck = HealthCheckResult.Unhealthy(
                new List<string> { "api", "database" },
                new List<string> { "cache", "worker" },
                critical: false);
            _healthCheckService.CheckServicesHealthAsync(Arg.Any<string[]>(), Arg.Any<string>())
                              .Returns(healthCheck);
        }

        [Given(@"critical services fail health checks")]
        public void GivenCriticalServicesFailHealthChecks()
        {
            _healthCheckSucceeds = false;
            _criticalHealthFailure = true;
            var healthCheck = HealthCheckResult.Unhealthy(
                new List<string> { "worker" },
                new List<string> { "api", "database" },
                critical: true);
            _healthCheckService.CheckServicesHealthAsync(Arg.Any<string[]>(), Arg.Any<string>())
                              .Returns(healthCheck);
        }

        [Given(@"an unexpected error occurs during health check")]
        public void GivenAnUnexpectedErrorOccursDuringHealthCheck()
        {
            _unexpectedErrorOccurs = true;
            _healthCheckService.When(x => x.CheckServicesHealthAsync(Arg.Any<string[]>(), Arg.Any<string>()))
                              .Do(x => throw new Exception("Unexpected health check error"));
        }

        [When(@"I perform an update")]
        public async Task WhenIPerformAnUpdate()
        {
            // Set up default behaviors if not already configured
            if (_migrationScriptsExist && _migrationExecutionSucceeds)
            {
                _scriptService.ExecuteScriptsAsync(Arg.Any<IEnumerable<MigrationScript>>(), Arg.Any<string>())
                             .Returns(new[] { new Version(1, 0, 1) });
            }

            _result = await _updateHost.UpdateAsync(_config);
        }

        [Then(@"the update should succeed")]
        public void ThenTheUpdateShouldSucceed()
        {
            _result.Status.Should().Be(UpdateStatus.Success);
            _result.Success.Should().BeTrue();
        }

        [Then(@"the update should fail immediately")]
        public void ThenTheUpdateShouldFailImmediately()
        {
            _result.Status.Should().Be(UpdateStatus.Failed);
            _result.Success.Should().BeFalse();
        }

        [Then(@"the update should fail with recovery")]
        public void ThenTheUpdateShouldFailWithRecovery()
        {
            _result.Status.Should().Be(UpdateStatus.Failed);
            _result.Success.Should().BeFalse();
            _result.RecoveryPerformed.Should().BeTrue();
        }

        [Then(@"the update should fail without recovery")]
        public void ThenTheUpdateShouldFailWithoutRecovery()
        {
            _result.Status.Should().Be(UpdateStatus.Failed);
            _result.Success.Should().BeFalse();
            _result.RecoveryPerformed.Should().BeFalse();
        }

        [Then(@"the update should result in partial success")]
        public void ThenTheUpdateShouldResultInPartialSuccess()
        {
            _result.Status.Should().Be(UpdateStatus.PartialSuccess);
            _result.Success.Should().BeFalse();
        }

        [Then(@"the version should be updated to ""(.*)""")]
        public void ThenTheVersionShouldBeUpdatedTo(string expectedVersion)
        {
            _result.Version.Should().Be(expectedVersion);
        }

        [Then(@"migration scripts should be executed")]
        public void ThenMigrationScriptsShouldBeExecuted()
        {
            _result.ExecutedScripts.Should().NotBeEmpty();
        }

        [Then(@"no migration scripts should be executed")]
        public void ThenNoMigrationScriptsShouldBeExecuted()
        {
            _result.ExecutedScripts.Should().BeEmpty();
            _scriptService.DidNotReceive().ExecuteScriptsAsync(Arg.Any<IEnumerable<MigrationScript>>(), Arg.Any<string>());
        }

        [Then(@"all services should be healthy")]
        public void ThenAllServicesShouldBeHealthy()
        {
            _result.HealthCheck?.AllHealthy.Should().BeTrue();
        }

        [Then(@"healthy services should remain running")]
        public void ThenHealthyServicesShouldRemainRunning()
        {
            _result.HealthCheck?.HealthyServices.Should().NotBeEmpty();
        }

        [Then(@"no Docker services should be restarted")]
        public void ThenNoDockerServicesShouldBeRestarted()
        {
            _dockerService.DidNotReceive().StartServicesAsync(Arg.Any<string[]>(), Arg.Any<string>());
            _dockerService.DidNotReceive().StopServicesAsync(Arg.Any<string[]>(), Arg.Any<string>());
        }

        [Then(@"the error should mention ""(.*)""")]
        public void ThenTheErrorShouldMention(string expectedError)
        {
            _result.ErrorMessage.Should().Contain(expectedError);
        }

        [Then(@"a rollback should be performed using the backup")]
        public void ThenARollbackShouldBePerformedUsingTheBackup()
        {
            _backupService.Received(1).RestoreBackupAsync(Arg.Any<string>(), Arg.Any<string>());
            _result.BackupId.Should().NotBeNullOrEmpty();
        }

        [Then(@"an emergency rollback should be performed")]
        public void ThenAnEmergencyRollbackShouldBePerformed()
        {
            _backupService.Received(1).RestoreBackupAsync(Arg.Any<string>(), Arg.Any<string>());
            _result.BackupId.Should().NotBeNullOrEmpty();
        }
    }
}