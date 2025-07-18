using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater;
using ModelingEvolution.AutoUpdater.Common;
using ModelingEvolution.AutoUpdater.Models;
using ModelingEvolution.AutoUpdater.Services;
using NSubstitute;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ModelingEvolution.AutoUpdater.Tests.Services
{
    public class UpdateHostTests
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
        private readonly IEventHub _eventHub = Substitute.For<IEventHub>();
        private readonly ILogger<UpdateHost> _logger = Substitute.For<ILogger<UpdateHost>>();

        [Fact]
        public async Task UpdateAsync_WithNewVersion_ExecutesMigrationScripts()
        {
            // Arrange
            var config = CreateTestConfiguration();
            var existingDeploymentState = new DeploymentState(new PackageVersion("1.0.0"), DateTime.Now)
            {
                Up = ImmutableSortedSet<PackageVersion>.Empty,
                Failed = ImmutableSortedSet<PackageVersion>.Empty
            };
            
            // Setup basic flow mocks
            _deploymentStateProvider.GetDeploymentStateAsync(Arg.Any<string>())
                      .Returns(existingDeploymentState);
            _gitService.GetAvailableVersionsAsync(Arg.Any<string>())
                      .Returns(new[] { new GitTagVersion("1.1.0", new PackageVersion("1.1.0")) });

            var migrationScripts = new[]
            {
                new MigrationScript("up-1.0.1.sh", "/path/up-1.0.1.sh", new PackageVersion("1.0.1"), MigrationDirection.Up)
            };

            _scriptService.DiscoverScriptsAsync(Arg.Any<string>())
                         .Returns(migrationScripts);
            _scriptService.FilterScriptsForMigrationAsync(Arg.Any<IEnumerable<MigrationScript>>(), Arg.Any<PackageVersion?>(), Arg.Any<PackageVersion>(), Arg.Any<ImmutableSortedSet<PackageVersion>?>())
                         .Returns(migrationScripts);
            _scriptService.ExecuteScriptsAsync(Arg.Any<IEnumerable<MigrationScript>>(), Arg.Any<string>())
                         .Returns(new[] { new PackageVersion("1.0.1") });

            var mockSshService = Substitute.For<ISshService>();
            mockSshService.GetArchitectureAsync().Returns(CpuArchitecture.X64);
            _sshConnectionManager.CreateSshServiceAsync().Returns(mockSshService);

            _dockerService.GetComposeFiles(Arg.Any<string>(), CpuArchitecture.X64)
                         .Returns(new[] { "docker-compose.yml", "docker-compose.x64.yml" });

            // Setup backup and health check mocks
            _backupService.BackupScriptExistsAsync(Arg.Any<string>()).Returns(false);
            _healthCheckService.CheckServicesHealthAsync(Arg.Any<string[]>(), Arg.Any<string>())
                              .Returns(HealthCheckResult.Healthy(new List<string> { "api", "database" }));

            var updateHost = new UpdateHost(_configuration, _logger, _gitService, _scriptService, _sshConnectionManager, _dockerService, _deploymentStateProvider, _backupService, _healthCheckService, _progressService, _eventHub);

            // Act
            var result = await updateHost.UpdateAsync(config);

            // Assert
            result.Status.Should().Be(UpdateStatus.Success);
            result.Success.Should().BeTrue();
            result.ExecutedScripts.Should().HaveCount(1);
            result.ExecutedScripts.Should().Contain("up-1.0.1.sh");
            result.Version.Should().Be("1.1.0");
            result.PreviousVersion.Should().Be("1.0.0");
            
            // Verify new workflow steps
            await _backupService.Received(1).BackupScriptExistsAsync(Arg.Any<string>());
            await _dockerService.Received(1).StopServicesAsync(Arg.Any<string[]>(), Arg.Any<string>());
            await _scriptService.Received(1).ExecuteScriptsAsync(migrationScripts, Arg.Any<string>());
            await _dockerService.Received(1).StartServicesAsync(Arg.Any<string[]>(), Arg.Any<string>());
            await _healthCheckService.Received(1).CheckServicesHealthAsync(Arg.Any<string[]>(), Arg.Any<string>());
        }

        [Fact]
        public async Task UpdateAsync_WithNoNewVersion_DoesNotUpdate()
        {
            // Arrange
            var config = CreateTestConfiguration();
            var existingDeploymentState = new DeploymentState(new PackageVersion("1.0.0"), DateTime.Now)
            {
                Up = ImmutableSortedSet<PackageVersion>.Empty,
                Failed = ImmutableSortedSet<PackageVersion>.Empty
            };
            _deploymentStateProvider.GetDeploymentStateAsync(Arg.Any<string>())
                      .Returns(existingDeploymentState);
            _gitService.GetAvailableVersionsAsync(Arg.Any<string>())
                      .Returns(new[] { new GitTagVersion("1.0.0", new PackageVersion("1.0.0")) });

            var updateHost = new UpdateHost(_configuration, _logger, _gitService, _scriptService, _sshConnectionManager, _dockerService, _deploymentStateProvider, _backupService, _healthCheckService, _progressService, _eventHub);

            // Act
            var result = await updateHost.UpdateAsync(config);

            // Assert
            result.Status.Should().Be(UpdateStatus.Success);
            result.Success.Should().BeTrue();
            result.ExecutedScripts.Should().BeEmpty();
            result.Version.Should().Be("1.0.0");
            result.PreviousVersion.Should().Be("1.0.0");
            
            // Should not perform any update operations when versions are the same
            await _scriptService.DidNotReceive().ExecuteScriptsAsync(Arg.Any<IEnumerable<MigrationScript>>(), Arg.Any<string>());
            await _dockerService.DidNotReceive().StartServicesAsync(Arg.Any<string[]>(), Arg.Any<string>());
            await _dockerService.DidNotReceive().StopServicesAsync(Arg.Any<string[]>(), Arg.Any<string>());
            await _backupService.DidNotReceive().CreateBackupAsync(Arg.Any<string>());
        }

        [Fact]
        public async Task UpdateAsync_WithScriptFailure_ReturnsFailure()
        {
            // Arrange
            var config = CreateTestConfiguration();
            var existingDeploymentState = new DeploymentState(new PackageVersion("1.0.0"), DateTime.Now)
            {
                Up = ImmutableSortedSet<PackageVersion>.Empty,
                Failed = ImmutableSortedSet<PackageVersion>.Empty
            };
            _deploymentStateProvider.GetDeploymentStateAsync(Arg.Any<string>())
                      .Returns(existingDeploymentState);
            _gitService.GetAvailableVersionsAsync(Arg.Any<string>())
                      .Returns(new[] { new GitTagVersion("1.1.0", new PackageVersion("1.1.0")) });

            var migrationScripts = new[]
            {
                new MigrationScript("up-1.0.1.sh", "/path/up-1.0.1.sh", new PackageVersion("1.0.1"), MigrationDirection.Up)
            };

            _scriptService.DiscoverScriptsAsync(Arg.Any<string>())
                         .Returns(migrationScripts);
            _scriptService.FilterScriptsForMigrationAsync(Arg.Any<IEnumerable<MigrationScript>>(), Arg.Any<PackageVersion?>(), Arg.Any<PackageVersion>(), Arg.Any<ImmutableSortedSet<PackageVersion>?>())
                         .Returns(migrationScripts);
            _scriptService.When(x => x.ExecuteScriptsAsync(Arg.Any<IEnumerable<MigrationScript>>(), Arg.Any<string>()))
                         .Do(x => throw new Exception("Script execution failed"));

            var mockSshService2 = Substitute.For<ISshService>();
            mockSshService2.GetArchitectureAsync().Returns(CpuArchitecture.X64);
            _sshConnectionManager.CreateSshServiceAsync().Returns(mockSshService2);

            _dockerService.GetComposeFiles(Arg.Any<string>(), CpuArchitecture.X64)
                         .Returns(new[] { "docker-compose.yml", "docker-compose.x64.yml" });

            // Setup backup to test decision tree: no backup available
            _backupService.BackupScriptExistsAsync(Arg.Any<string>()).Returns(false);

            var updateHost = new UpdateHost(_configuration, _logger, _gitService, _scriptService, _sshConnectionManager, _dockerService, _deploymentStateProvider, _backupService, _healthCheckService, _progressService, _eventHub);

            // Act
            var result = await updateHost.UpdateAsync(config);

            // Assert
            result.Status.Should().Be(UpdateStatus.Failed);
            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("Script execution failed");
            result.ErrorMessage.Should().Contain("No recovery possible without backup");
            result.RecoveryPerformed.Should().BeFalse();
            
            // Verify workflow: stopped services but migration failed, no docker startup
            await _dockerService.Received(1).StopServicesAsync(Arg.Any<string[]>(), Arg.Any<string>());
            await _dockerService.DidNotReceive().StartServicesAsync(Arg.Any<string[]>(), Arg.Any<string>());
            await _healthCheckService.DidNotReceive().CheckServicesHealthAsync(Arg.Any<string[]>(), Arg.Any<string>());
        }

        [Fact]
        public async Task UpdateAsync_WithBackupFailure_ReturnsFailedImmediately()
        {
            // Arrange
            var config = CreateTestConfiguration();
            var existingDeploymentState = new DeploymentState(new PackageVersion("1.0.0"), DateTime.Now)
            {
                Up = ImmutableSortedSet<PackageVersion>.Empty,
                Failed = ImmutableSortedSet<PackageVersion>.Empty
            };
            
            _deploymentStateProvider.GetDeploymentStateAsync(Arg.Any<string>())
                      .Returns(existingDeploymentState);
            _gitService.GetAvailableVersionsAsync(Arg.Any<string>())
                      .Returns(new[] { new GitTagVersion("1.1.0", new PackageVersion("1.1.0")) });

            // Test decision point: Backup Script Exists? YES, but fails
            _backupService.BackupScriptExistsAsync(Arg.Any<string>()).Returns(true);
            _backupService.CreateBackupAsync(Arg.Any<string>())
                          .Returns(BackupResult.CreateFailure("Insufficient disk space"));

            var updateHost = new UpdateHost(_configuration, _logger, _gitService, _scriptService, _sshConnectionManager, _dockerService, _deploymentStateProvider, _backupService, _healthCheckService, _progressService, _eventHub);

            // Act
            var result = await updateHost.UpdateAsync(config);

            // Assert
            result.Status.Should().Be(UpdateStatus.Failed);
            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("Backup creation failed - cannot proceed");
            result.ErrorMessage.Should().Contain("Insufficient disk space");
            
            // Should not proceed with any update operations after backup failure
            await _dockerService.DidNotReceive().StopServicesAsync(Arg.Any<string[]>(), Arg.Any<string>());
            await _scriptService.DidNotReceive().ExecuteScriptsAsync(Arg.Any<IEnumerable<MigrationScript>>(), Arg.Any<string>());
        }

        [Fact]
        public async Task UpdateAsync_WithMigrationFailureAndBackup_PerformsRollback()
        {
            // Arrange
            var config = CreateTestConfiguration();
            var existingDeploymentState = new DeploymentState(new PackageVersion("1.0.0"), DateTime.Now)
            {
                Up = ImmutableSortedSet<PackageVersion>.Empty,
                Failed = ImmutableSortedSet<PackageVersion>.Empty
            };
            
            _deploymentStateProvider.GetDeploymentStateAsync(Arg.Any<string>())
                      .Returns(existingDeploymentState);
            _gitService.GetAvailableVersionsAsync(Arg.Any<string>())
                      .Returns(new[] { new GitTagVersion("1.1.0", new PackageVersion("1.1.0")) });

            var migrationScripts = new[]
            {
                new MigrationScript("up-1.0.1.sh", "/path/up-1.0.1.sh", new PackageVersion("1.0.1"), MigrationDirection.Up)
            };

            _scriptService.DiscoverScriptsAsync(Arg.Any<string>())
                         .Returns(migrationScripts);
            _scriptService.FilterScriptsForMigrationAsync(Arg.Any<IEnumerable<MigrationScript>>(), Arg.Any<PackageVersion?>(), Arg.Any<PackageVersion>(), Arg.Any<ImmutableSortedSet<PackageVersion>?>())
                         .Returns(migrationScripts);
            _scriptService.When(x => x.ExecuteScriptsAsync(Arg.Any<IEnumerable<MigrationScript>>(), Arg.Any<string>()))
                         .Do(x => throw new Exception("Migration script execution failed"));

            var mockSshService = Substitute.For<ISshService>();
            mockSshService.GetArchitectureAsync().Returns(CpuArchitecture.X64);
            _sshConnectionManager.CreateSshServiceAsync().Returns(mockSshService);

            _dockerService.GetComposeFiles(Arg.Any<string>(), CpuArchitecture.X64)
                         .Returns(new[] { "docker-compose.yml", "docker-compose.x64.yml" });

            // Test decision point: Backup Available? YES
            _backupService.BackupScriptExistsAsync(Arg.Any<string>()).Returns(true);
            _backupService.CreateBackupAsync(Arg.Any<string>())
                          .Returns(BackupResult.CreateSuccess("/backup/backup-123.tar.gz"));
            _backupService.RestoreBackupAsync(Arg.Any<string>(), Arg.Any<string>())
                          .Returns(RestoreResult.CreateSuccess());

            var updateHost = new UpdateHost(_configuration, _logger, _gitService, _scriptService, _sshConnectionManager, _dockerService, _deploymentStateProvider, _backupService, _healthCheckService, _progressService, _eventHub);

            // Act
            var result = await updateHost.UpdateAsync(config);

            // Assert
            result.Status.Should().Be(UpdateStatus.Failed);
            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("Migration failed");
            result.RecoveryPerformed.Should().BeTrue();
            result.BackupId.Should().Be("/backup/backup-123.tar.gz");
            
            // Verify rollback sequence was performed
            await _backupService.Received(1).CreateBackupAsync(Arg.Any<string>());
            await _dockerService.Received(2).StopServicesAsync(Arg.Any<string[]>(), Arg.Any<string>()); // Stop current services + Stop in rollback
            await _backupService.Received(1).RestoreBackupAsync(Arg.Any<string>(), "/backup/backup-123.tar.gz");
            await _dockerService.Received(1).StartServicesAsync(Arg.Any<string[]>(), Arg.Any<string>()); // Start original services
        }

        [Fact]
        public async Task UpdateAsync_WithDockerFailureAndBackup_PerformsRollback()
        {
            // Arrange
            var config = CreateTestConfiguration();
            var existingDeploymentState = new DeploymentState(new PackageVersion("1.0.0"), DateTime.Now)
            {
                Up = ImmutableSortedSet<PackageVersion>.Empty,
                Failed = ImmutableSortedSet<PackageVersion>.Empty
            };
            
            _deploymentStateProvider.GetDeploymentStateAsync(Arg.Any<string>())
                      .Returns(existingDeploymentState);
            _gitService.GetAvailableVersionsAsync(Arg.Any<string>())
                      .Returns(new[] { new GitTagVersion("1.1.0", new PackageVersion("1.1.0")) });

            var migrationScripts = new[]
            {
                new MigrationScript("up-1.0.1.sh", "/path/up-1.0.1.sh", new PackageVersion("1.0.1"), MigrationDirection.Up)
            };

            _scriptService.DiscoverScriptsAsync(Arg.Any<string>())
                         .Returns(migrationScripts);
            _scriptService.FilterScriptsForMigrationAsync(Arg.Any<IEnumerable<MigrationScript>>(), Arg.Any<PackageVersion?>(), Arg.Any<PackageVersion>(), Arg.Any<ImmutableSortedSet<PackageVersion>?>())
                         .Returns(migrationScripts);
            _scriptService.ExecuteScriptsAsync(Arg.Any<IEnumerable<MigrationScript>>(), Arg.Any<string>())
                         .Returns(new[] { new PackageVersion("1.0.1") });

            var mockSshService = Substitute.For<ISshService>();
            mockSshService.GetArchitectureAsync().Returns(CpuArchitecture.X64);
            _sshConnectionManager.CreateSshServiceAsync().Returns(mockSshService);

            _dockerService.GetComposeFiles(Arg.Any<string>(), CpuArchitecture.X64)
                         .Returns(new[] { "docker-compose.yml", "docker-compose.x64.yml" });

            // Test decision point: docker-compose up fails (but rollback should succeed)
            var startCallCount = 0;
            _dockerService.When(x => x.StartServicesAsync(Arg.Any<string[]>(), Arg.Any<string>()))
                         .Do(x => {
                             startCallCount++;
                             if (startCallCount == 1) // First call fails (new services)
                                 throw new Exception("Docker compose startup failed");
                             // Second call succeeds (rollback services)
                         });

            // Test decision point: Backup Available? YES
            _backupService.BackupScriptExistsAsync(Arg.Any<string>()).Returns(true);
            _backupService.CreateBackupAsync(Arg.Any<string>())
                          .Returns(BackupResult.CreateSuccess("/backup/backup-123.tar.gz"));
            _backupService.RestoreBackupAsync(Arg.Any<string>(), Arg.Any<string>())
                          .Returns(RestoreResult.CreateSuccess());

            var updateHost = new UpdateHost(_configuration, _logger, _gitService, _scriptService, _sshConnectionManager, _dockerService, _deploymentStateProvider, _backupService, _healthCheckService, _progressService, _eventHub);

            // Act
            var result = await updateHost.UpdateAsync(config);

            // Assert
            result.Status.Should().Be(UpdateStatus.Failed);
            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("Docker startup failed");
            result.RecoveryPerformed.Should().BeTrue();
            result.BackupId.Should().Be("/backup/backup-123.tar.gz");
            
            // Verify migration succeeded but docker failed, then rollback
            await _scriptService.Received(1).ExecuteScriptsAsync(Arg.Any<IEnumerable<MigrationScript>>(), Arg.Any<string>());
            await _backupService.Received(1).RestoreBackupAsync(Arg.Any<string>(), "/backup/backup-123.tar.gz");
        }

        [Fact]
        public async Task UpdateAsync_WithPartialHealthCheckFailure_ReturnsPartialSuccess()
        {
            // Arrange
            var config = CreateTestConfiguration();
            var existingDeploymentState = new DeploymentState(new PackageVersion("1.0.0"), DateTime.Now)
            {
                Up = ImmutableSortedSet<PackageVersion>.Empty,
                Failed = ImmutableSortedSet<PackageVersion>.Empty
            };
            
            _deploymentStateProvider.GetDeploymentStateAsync(Arg.Any<string>())
                      .Returns(existingDeploymentState);
            _gitService.GetAvailableVersionsAsync(Arg.Any<string>())
                      .Returns(new[] { new GitTagVersion("1.1.0", new PackageVersion("1.1.0")) });

            var migrationScripts = new[]
            {
                new MigrationScript("up-1.0.1.sh", "/path/up-1.0.1.sh", new PackageVersion("1.0.1"), MigrationDirection.Up)
            };

            _scriptService.DiscoverScriptsAsync(Arg.Any<string>())
                         .Returns(migrationScripts);
            _scriptService.FilterScriptsForMigrationAsync(Arg.Any<IEnumerable<MigrationScript>>(), Arg.Any<PackageVersion?>(), Arg.Any<PackageVersion>(), Arg.Any<ImmutableSortedSet<PackageVersion>?>())
                         .Returns(migrationScripts);
            _scriptService.ExecuteScriptsAsync(Arg.Any<IEnumerable<MigrationScript>>(), Arg.Any<string>())
                         .Returns(new[] { new PackageVersion("1.0.1") });

            var mockSshService = Substitute.For<ISshService>();
            mockSshService.GetArchitectureAsync().Returns(CpuArchitecture.X64);
            _sshConnectionManager.CreateSshServiceAsync().Returns(mockSshService);

            _dockerService.GetComposeFiles(Arg.Any<string>(), CpuArchitecture.X64)
                         .Returns(new[] { "docker-compose.yml", "docker-compose.x64.yml" });

            _backupService.BackupScriptExistsAsync(Arg.Any<string>()).Returns(false);

            // Test decision point: All Services Healthy? NO, but non-critical failure
            var healthCheck = HealthCheckResult.Unhealthy(
                new List<string> { "api", "database" }, 
                new List<string> { "cache", "worker" }, 
                critical: false);
            _healthCheckService.CheckServicesHealthAsync(Arg.Any<string[]>(), Arg.Any<string>())
                              .Returns(healthCheck);

            var updateHost = new UpdateHost(_configuration, _logger, _gitService, _scriptService, _sshConnectionManager, _dockerService, _deploymentStateProvider, _backupService, _healthCheckService, _progressService, _eventHub);

            // Act
            var result = await updateHost.UpdateAsync(config);

            // Assert
            result.Status.Should().Be(UpdateStatus.PartialSuccess);
            result.Success.Should().BeFalse(); // PartialSuccess means Success = false
            result.Version.Should().Be("1.1.0");
            result.HealthCheck.Should().NotBeNull();
            result.HealthCheck!.AllHealthy.Should().BeFalse();
            result.HealthCheck.CriticalFailure.Should().BeFalse();
            result.HealthCheck.HealthyServices.Should().Contain("api");
            result.HealthCheck.UnhealthyServices.Should().Contain("cache");
            
            // Verify no rollback performed for non-critical failure
            await _backupService.DidNotReceive().RestoreBackupAsync(Arg.Any<string>(), Arg.Any<string>());
            await _scriptService.Received(1).ExecuteScriptsAsync(Arg.Any<IEnumerable<MigrationScript>>(), Arg.Any<string>());
        }

        [Fact]
        public async Task UpdateAsync_WithCriticalHealthCheckFailureAndBackup_PerformsRollback()
        {
            // Arrange
            var config = CreateTestConfiguration();
            var existingDeploymentState = new DeploymentState(new PackageVersion("1.0.0"), DateTime.Now)
            {
                Up = ImmutableSortedSet<PackageVersion>.Empty,
                Failed = ImmutableSortedSet<PackageVersion>.Empty
            };
            
            _deploymentStateProvider.GetDeploymentStateAsync(Arg.Any<string>())
                      .Returns(existingDeploymentState);
            _gitService.GetAvailableVersionsAsync(Arg.Any<string>())
                      .Returns(new[] { new GitTagVersion("1.1.0", new PackageVersion("1.1.0")) });

            var migrationScripts = new[]
            {
                new MigrationScript("up-1.0.1.sh", "/path/up-1.0.1.sh", new PackageVersion("1.0.1"), MigrationDirection.Up)
            };

            _scriptService.DiscoverScriptsAsync(Arg.Any<string>())
                         .Returns(migrationScripts);
            _scriptService.FilterScriptsForMigrationAsync(Arg.Any<IEnumerable<MigrationScript>>(), Arg.Any<PackageVersion?>(), Arg.Any<PackageVersion>(), Arg.Any<ImmutableSortedSet<PackageVersion>?>())
                         .Returns(migrationScripts);
            _scriptService.ExecuteScriptsAsync(Arg.Any<IEnumerable<MigrationScript>>(), Arg.Any<string>())
                         .Returns(new[] { new PackageVersion("1.0.1") });

            var mockSshService = Substitute.For<ISshService>();
            mockSshService.GetArchitectureAsync().Returns(CpuArchitecture.X64);
            _sshConnectionManager.CreateSshServiceAsync().Returns(mockSshService);

            _dockerService.GetComposeFiles(Arg.Any<string>(), CpuArchitecture.X64)
                         .Returns(new[] { "docker-compose.yml", "docker-compose.x64.yml" });

            // Test decision point: Backup Available? YES
            _backupService.BackupScriptExistsAsync(Arg.Any<string>()).Returns(true);
            _backupService.CreateBackupAsync(Arg.Any<string>())
                          .Returns(BackupResult.CreateSuccess("/backup/backup-123.tar.gz"));
            _backupService.RestoreBackupAsync(Arg.Any<string>(), Arg.Any<string>())
                          .Returns(RestoreResult.CreateSuccess());

            // Test decision point: All Services Healthy? NO, and CRITICAL failure
            var healthCheck = HealthCheckResult.Unhealthy(
                new List<string> { "worker" }, 
                new List<string> { "api", "database" }, 
                critical: true);
            _healthCheckService.CheckServicesHealthAsync(Arg.Any<string[]>(), Arg.Any<string>())
                              .Returns(healthCheck);

            var updateHost = new UpdateHost(_configuration, _logger, _gitService, _scriptService, _sshConnectionManager, _dockerService, _deploymentStateProvider, _backupService, _healthCheckService, _progressService, _eventHub);

            // Act
            var result = await updateHost.UpdateAsync(config);

            // Assert
            result.Status.Should().Be(UpdateStatus.Failed);
            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("Critical services unhealthy");
            result.RecoveryPerformed.Should().BeTrue();
            result.BackupId.Should().Be("/backup/backup-123.tar.gz");
            
            // Verify rollback performed for critical failure
            await _backupService.Received(1).RestoreBackupAsync(Arg.Any<string>(), "/backup/backup-123.tar.gz");
            await _scriptService.Received(1).ExecuteScriptsAsync(Arg.Any<IEnumerable<MigrationScript>>(), Arg.Any<string>());
        }

        [Fact]
        public async Task UpdateAsync_WithUnexpectedExceptionAndBackup_PerformsEmergencyRollback()
        {
            // Arrange
            var config = CreateTestConfiguration();
            var existingDeploymentState = new DeploymentState(new PackageVersion("1.0.0"), DateTime.Now)
            {
                Up = ImmutableSortedSet<PackageVersion>.Empty,
                Failed = ImmutableSortedSet<PackageVersion>.Empty
            };
            
            _deploymentStateProvider.GetDeploymentStateAsync(Arg.Any<string>())
                      .Returns(existingDeploymentState);
            _gitService.GetAvailableVersionsAsync(Arg.Any<string>())
                      .Returns(new[] { new GitTagVersion("1.1.0", new PackageVersion("1.1.0")) });

            var mockSshService = Substitute.For<ISshService>();
            mockSshService.GetArchitectureAsync().Returns(CpuArchitecture.X64);
            _sshConnectionManager.CreateSshServiceAsync().Returns(mockSshService);

            _dockerService.GetComposeFiles(Arg.Any<string>(), CpuArchitecture.X64)
                         .Returns(new[] { "docker-compose.yml", "docker-compose.x64.yml" });

            // Test decision point: Backup Available? YES
            _backupService.BackupScriptExistsAsync(Arg.Any<string>()).Returns(true);
            _backupService.CreateBackupAsync(Arg.Any<string>())
                          .Returns(BackupResult.CreateSuccess("/backup/backup-123.tar.gz"));
            _backupService.RestoreBackupAsync(Arg.Any<string>(), Arg.Any<string>())
                          .Returns(RestoreResult.CreateSuccess());

            // Cause unexpected exception in health check
            _healthCheckService.When(x => x.CheckServicesHealthAsync(Arg.Any<string[]>(), Arg.Any<string>()))
                              .Do(x => throw new Exception("Unexpected health check error"));

            var updateHost = new UpdateHost(_configuration, _logger, _gitService, _scriptService, _sshConnectionManager, _dockerService, _deploymentStateProvider, _backupService, _healthCheckService, _progressService, _eventHub);

            // Act
            var result = await updateHost.UpdateAsync(config);

            // Assert
            result.Status.Should().Be(UpdateStatus.Failed);
            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("Unexpected error");
            result.ErrorMessage.Should().Contain("Unexpected health check error");
            result.RecoveryPerformed.Should().BeTrue();
            result.BackupId.Should().Be("/backup/backup-123.tar.gz");
            
            // Verify emergency rollback was performed
            await _backupService.Received(1).RestoreBackupAsync(Arg.Any<string>(), "/backup/backup-123.tar.gz");
        }

        private static DockerComposeConfiguration CreateTestConfiguration()
        {
            var tempDir = Path.GetTempPath();
            return new DockerComposeConfiguration
            {
                RepositoryLocation = Path.Combine(tempDir, "test-repo"),
                RepositoryUrl = "https://github.com/test/repo.git",
                DockerComposeDirectory = "./"
            };
        }
    }
}