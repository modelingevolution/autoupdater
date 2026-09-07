using FluentAssertions;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.Services;
using NSubstitute;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace ModelingEvolution.AutoUpdater.Tests.Services
{
    public class DockerComposeServiceTests
    {
        private readonly ISshService _sshService = Substitute.For<ISshService>();
        private readonly ILogger<DockerComposeService> _logger = Substitute.For<ILogger<DockerComposeService>>();
        private readonly DockerComposeService _service;

        public DockerComposeServiceTests()
        {
            _service = new DockerComposeService(_sshService, _logger);
            // Note: Tests should call SetupDockerComposeV2Detection() or SetupDockerComposeV1Detection()
            // explicitly if they need default docker compose detection, or set up custom mocks.
        }

        private void SetupDockerComposeV2Detection()
        {
            // Default to Docker Compose v2 for tests
            _sshService.ExecuteCommandAsync("sudo docker compose version")
                .Returns(new SshCommandResult("sudo docker compose version", "Docker Compose version v2.20.2"));
        }

        private void SetupDockerComposeV2Detection(string versionOutput)
        {
            _sshService.ExecuteCommandAsync("sudo docker compose version")
                .Returns(new SshCommandResult("sudo docker compose version", versionOutput));
        }

        /// <summary>
        /// Makes the streamed SSH overload replay <paramref name="lines"/> into the caller's line handler,
        /// then finish with <paramref name="exitCode"/>.
        /// </summary>
        private void SetupStreamedCommand(string expectedCommand, string workingDirectory, IEnumerable<string> lines, int exitCode = 0)
        {
            _sshService.ExecuteCommandAsync(expectedCommand, Arg.Any<TimeSpan>(), workingDirectory, Arg.Any<Action<string>>())
                .Returns(call =>
                {
                    var onLine = call.Arg<Action<string>>();
                    foreach (var line in lines)
                    {
                        onLine(line);
                    }
                    return new SshCommandResult { Command = expectedCommand, ExitCode = exitCode, Output = string.Join("\n", lines) };
                });
        }

        private const string JsonPullCommand = "sudo docker compose --progress json -f \"docker-compose.yml\" pull 2>&1";
        private const string BlockingPullCommand = "sudo docker compose -f \"docker-compose.yml\" pull";

        private sealed class RecordingProgress : IProgress<PullProgress>
        {
            public List<PullProgress> Reports { get; } = new();
            public void Report(PullProgress value) => Reports.Add(value);
        }

        [Fact]
        public async Task PullAsync_WithJsonCapableCompose_StreamsProgressFromRemoteOutput()
        {
            // Arrange
            SetupDockerComposeV2Detection("Docker Compose version 2.40.3+ds1-0ubuntu1");
            SetupStreamedCommand(JsonPullCommand, "/app", DockerPullProgressParserTests.CapturedPull);
            _service.ProgressReportInterval = TimeSpan.Zero;
            var progress = new RecordingProgress();

            // Act
            await _service.PullAsync(new[] { "docker-compose.yml" }, "/app", TimeSpan.FromMinutes(1), progress);

            // Assert
            await _sshService.Received(1).ExecuteCommandAsync(JsonPullCommand, Arg.Any<TimeSpan>(), "/app", Arg.Any<Action<string>>());
            await _sshService.DidNotReceive().ExecuteCommandAsync(BlockingPullCommand, Arg.Any<TimeSpan>(), Arg.Any<string?>());

            progress.Reports.Should().NotBeEmpty();
            progress.Reports.Select(r => r.ImagesPulled).Should().BeInAscendingOrder();
            progress.Reports.Select(r => r.ImagesPulled).Distinct().Should().Equal(0, 1, 2);
            progress.Reports.Last().Should().Be(new PullProgress(2, 2, 0, 0, 100f));
        }

        [Fact]
        public async Task PullAsync_WithThrottling_StillReportsEveryPulledImageAndTheFinalSnapshot()
        {
            // Arrange: an interval no test can outrun, so only image-count changes and the final report get through
            SetupDockerComposeV2Detection("Docker Compose version v2.27.0");
            SetupStreamedCommand(JsonPullCommand, "/app", DockerPullProgressParserTests.CapturedPull);
            _service.ProgressReportInterval = TimeSpan.FromHours(1);
            var progress = new RecordingProgress();

            // Act
            await _service.PullAsync(new[] { "docker-compose.yml" }, "/app", TimeSpan.FromMinutes(1), progress);

            // Assert
            progress.Reports.Select(r => (r.ImagesTotal, r.ImagesPulled)).Should().Equal((1, 0), (2, 0), (2, 1), (2, 2));
            progress.Reports.Last().BytesPercent.Should().Be(100f);
        }

        [Fact]
        public async Task PullAsync_WhenLastLinesFallInsideTheInterval_StillFlushesTheLatestState()
        {
            // Arrange: a burst of Downloading lines for one image, all inside the interval, then silence while compose extracts.
            SetupDockerComposeV2Detection("Docker Compose version v2.40.3");
            var gate = new TaskCompletionSource();
            var lines = new[]
            {
                """{"id":"a","text":"Pulling"}""",
                """{"id":"l1","parent_id":"a","text":"Downloading","current":100,"total":1000}""",
                """{"id":"l1","parent_id":"a","text":"Downloading","current":900,"total":1000}""",
                """{"id":"l1","parent_id":"a","text":"Download complete","percent":100}""",
                """{"id":"l1","parent_id":"a","text":"Extracting","status":"1 s","current":1}""",
            };
            _sshService.ExecuteCommandAsync(JsonPullCommand, Arg.Any<TimeSpan>(), "/app", Arg.Any<Action<string>>())
                .Returns(async call =>
                {
                    var onLine = call.Arg<Action<string>>();
                    foreach (var line in lines) onLine(line);
                    await gate.Task;                       // compose is "extracting": no more lines for a while
                    onLine("""{"id":"a","text":"Pulled"}""");
                    return new SshCommandResult { Command = JsonPullCommand, ExitCode = 0 };
                });
            _service.ProgressReportInterval = TimeSpan.FromMilliseconds(100);
            var progress = new RecordingProgress();

            // Act
            var pull = _service.PullAsync(new[] { "docker-compose.yml" }, "/app", TimeSpan.FromMinutes(1), progress);
            await Task.Delay(400);                          // longer than the interval, shorter than any real extraction
            var reportsDuringExtraction = progress.Reports.ToList();
            gate.SetResult();
            await pull;

            // Assert: while compose was silent, the trailing flush delivered the extracting state
            reportsDuringExtraction.Last().Should().Be(new PullProgress(1, 0, 1000, 1000, 100f, LayersExtracting: 1));
            progress.Reports.Last().IsComplete.Should().BeTrue();
        }

        [Fact]
        public async Task PullAsync_WithComposeOlderThan227_FallsBackToBlockingPull()
        {
            // Arrange
            SetupDockerComposeV2Detection("Docker Compose version v2.20.2");
            _sshService.ExecuteCommandAsync(BlockingPullCommand, Arg.Any<TimeSpan>(), "/app")
                .Returns(new SshCommandResult(BlockingPullCommand, "Pulled"));
            var progress = new RecordingProgress();

            // Act
            await _service.PullAsync(new[] { "docker-compose.yml" }, "/app", TimeSpan.FromMinutes(1), progress);

            // Assert
            await _sshService.Received(1).ExecuteCommandAsync(BlockingPullCommand, Arg.Any<TimeSpan>(), "/app");
            await _sshService.DidNotReceive().ExecuteCommandAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<string?>(), Arg.Any<Action<string>>());
            progress.Reports.Should().BeEmpty();
        }

        [Fact]
        public async Task PullAsync_WithComposeV1_FallsBackToBlockingPull()
        {
            // Arrange
            SetupDockerComposeV1Detection();
            const string v1Command = "sudo docker-compose -f \"docker-compose.yml\" pull";
            _sshService.ExecuteCommandAsync(v1Command, Arg.Any<TimeSpan>(), "/app")
                .Returns(new SshCommandResult(v1Command, "Pulled"));

            // Act
            await _service.PullAsync(new[] { "docker-compose.yml" }, "/app", TimeSpan.FromMinutes(1), new RecordingProgress());

            // Assert
            await _sshService.Received(1).ExecuteCommandAsync(v1Command, Arg.Any<TimeSpan>(), "/app");
            await _sshService.DidNotReceive().ExecuteCommandAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<string?>(), Arg.Any<Action<string>>());
        }

        [Fact]
        public async Task PullAsync_WithoutProgressReceiver_UsesBlockingPullEvenOnNewCompose()
        {
            // Arrange
            SetupDockerComposeV2Detection("Docker Compose version v2.40.3");
            _sshService.ExecuteCommandAsync(BlockingPullCommand, Arg.Any<TimeSpan>(), "/app")
                .Returns(new SshCommandResult(BlockingPullCommand, "Pulled"));

            // Act
            await _service.PullAsync(new[] { "docker-compose.yml" }, "/app", TimeSpan.FromMinutes(1));

            // Assert
            await _sshService.Received(1).ExecuteCommandAsync(BlockingPullCommand, Arg.Any<TimeSpan>(), "/app");
        }

        [Fact]
        public async Task PullAsync_WhenStreamedPullFails_ThrowsWithComposeErrorMessage()
        {
            // Arrange
            SetupDockerComposeV2Detection("Docker Compose version v2.40.3");
            SetupStreamedCommand(JsonPullCommand, "/app", DockerPullProgressParserTests.CapturedFailedPull, exitCode: 1);

            // Act
            var act = async () => await _service.PullAsync(new[] { "docker-compose.yml" }, "/app", TimeSpan.FromMinutes(1), new RecordingProgress());

            // Assert
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*Error response from daemon: pull access denied for modelingevolution/does-not-exist-zz*");
        }

        [Fact]
        public async Task PullAsync_WhenStreamedPullFailsWithoutJsonError_ThrowsWithTailOfOutput()
        {
            // Arrange: stderr is redirected into stdout on the streamed path, so Error is empty and the reason must come from Output
            SetupDockerComposeV2Detection("Docker Compose version v2.40.3");
            var lines = new[] { """{"id":"a","text":"Pulling"}""", "Error response from daemon: Get https://registry: dial tcp: i/o timeout" };
            SetupStreamedCommand(JsonPullCommand, "/app", lines, exitCode: 1);

            // Act
            var act = async () => await _service.PullAsync(new[] { "docker-compose.yml" }, "/app", TimeSpan.FromMinutes(1), new RecordingProgress());

            // Assert
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*dial tcp: i/o timeout*");
        }

        [Fact]
        public void DescribeFailure_PrefersStderr_ThenTailOfStdout_ThenExitCode()
        {
            DockerComposeService.DescribeFailure(new SshCommandResult { ExitCode = 1, Error = " boom \n", Output = "x" }).Should().Be("boom");
            DockerComposeService.DescribeFailure(new SshCommandResult { ExitCode = 1, Output = "l1\nl2\nl3\nl4\nl5\nl6\nl7" }).Should().Be("l3 | l4 | l5 | l6 | l7");
            DockerComposeService.DescribeFailure(new SshCommandResult { ExitCode = 137 }).Should().Be("exit code 137, no output");
        }

        [Theory]
        [InlineData("Docker Compose version v2.40.3", 2, 40, 3)]
        [InlineData("Docker Compose version 2.40.3+ds1-0ubuntu1", 2, 40, 3)]
        [InlineData("Docker Compose version v2.27.0", 2, 27, 0)]
        public void ParseComposeVersion_ReadsSemanticVersion(string output, int major, int minor, int build)
        {
            DockerComposeService.ParseComposeVersion(output).Should().Be(new Version(major, minor, build));
        }

        [Theory]
        [InlineData("")]
        [InlineData("docker: command not found")]
        public void ParseComposeVersion_WithoutVersion_ReturnsNull(string output)
        {
            DockerComposeService.ParseComposeVersion(output).Should().BeNull();
        }

        [Theory]
        [InlineData("Docker Compose version v2.26.1", false)]
        [InlineData("Docker Compose version v2.27.0", true)]
        [InlineData("Docker Compose version 2.40.3+ds1-0ubuntu1", true)]
        public async Task SupportsJsonProgress_DependsOnDetectedVersion(string versionOutput, bool expected)
        {
            SetupDockerComposeV2Detection(versionOutput);
            _sshService.ExecuteCommandAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<string?>())
                .Returns(new SshCommandResult("pull", "Pulled"));
            _sshService.ExecuteCommandAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<string?>(), Arg.Any<Action<string>>())
                .Returns(new SshCommandResult("pull", ""));

            await _service.PullAsync(new[] { "docker-compose.yml" }, "/app", TimeSpan.FromMinutes(1));

            _service.SupportsJsonProgress.Should().Be(expected);
        }

        private void SetupDockerComposeV1Detection()
        {
            // Setup v1 detection - v2 fails, v1 succeeds
            _sshService.ExecuteCommandAsync("sudo docker compose version")
                .Returns(new SshCommandResult
                {
                    Command = "sudo docker compose version",
                    Output = "",
                    Error = "command not found",
                    ExitCode = 1
                });
            _sshService.ExecuteCommandAsync("sudo docker-compose --version")
                .Returns(new SshCommandResult("sudo docker-compose --version", "docker-compose version 1.29.2"));
        }

        [Fact]
        public void Constructor_WithNullSshService_ShouldThrowArgumentNullException()
        {
            // Act & Assert
            var act = () => new DockerComposeService(null!, _logger);
            act.Should().Throw<ArgumentNullException>().WithParameterName("sshService");
        }

        [Fact]
        public void Constructor_WithNullLogger_ShouldThrowArgumentNullException()
        {
            // Act & Assert
            var act = () => new DockerComposeService(_sshService, null!);
            act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
        }

        [Theory]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData(null)]
        public async Task GetComposeFilesForArchitectureAsync_WithInvalidDirectoryPath_ShouldThrowArgumentException(string? directoryPath)
        {
            // Act & Assert
            var act = async () => await _service.GetComposeFiles(directoryPath!, CpuArchitecture.X64);
            await act.Should().ThrowAsync<ArgumentException>()
                .WithParameterName("directoryPath");
        }

        
        


        [Fact]
        public async Task GetComposeFilesForArchitectureAsync_WithArchFileOnly_ShouldReturnArchFile()
        {
            // Arrange
            const string directoryPath = "/app";
            CpuArchitecture architecture = CpuArchitecture.X64;

            
            _sshService.GetFiles("/app", Arg.Any<string>())
                .Returns(["/app/docker-compose.yml", "/app/docker-compose.x64.yml"]);
            // Act
            var result = await _service.GetComposeFiles(directoryPath, architecture);

            // Assert
            result.Should().HaveCount(2);
            result[0].Should().Be("docker-compose.yml");
            result[1].Should().Be("docker-compose.x64.yml");
        }

        [Fact]
        public async Task StartServicesAsync_WithNullComposeFiles_ShouldThrowArgumentException()
        {
            // Act & Assert
            var act = async () => await _service.StartServicesAsync(null!, "/app");
            await act.Should().ThrowAsync<ArgumentException>()
                .WithParameterName("composeFiles");
        }

        [Fact]
        public async Task StartServicesAsync_WithEmptyComposeFiles_ShouldThrowArgumentException()
        {
            // Act & Assert
            var act = async () => await _service.StartServicesAsync(Array.Empty<string>(), "/app");
            await act.Should().ThrowAsync<ArgumentException>()
                .WithParameterName("composeFiles");
        }

        [Theory]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData(null)]
        public async Task StartServicesAsync_WithInvalidWorkingDirectory_ShouldThrowArgumentException(string? workingDirectory)
        {
            // Arrange
            var composeFiles = new[] { "docker-compose.yml" };

            // Act & Assert
            var act = async () => await _service.StartServicesAsync(composeFiles, workingDirectory!);
            await act.Should().ThrowAsync<ArgumentException>()
                .WithParameterName("workingDirectory");
        }

        [Fact]
        public async Task StartServicesAsync_WithSingleFile_ShouldExecuteCorrectCommand()
        {
            // Arrange
            SetupDockerComposeV2Detection();
            var composeFiles = new[] { "docker-compose.yml" };
            const string workingDirectory = "/app";
            const string expectedCommand = "sudo docker compose -f \"docker-compose.yml\" up -d";

            _sshService.ExecuteCommandAsync(expectedCommand, workingDirectory).Returns(new SshCommandResult(expectedCommand,"Started successfully"));

            // Act
            await _service.StartServicesAsync(composeFiles, workingDirectory);

            // Assert
            await _sshService.Received(1).ExecuteCommandAsync(expectedCommand, workingDirectory);
        }

        [Fact]
        public async Task StartServicesAsync_WithMultipleFiles_ShouldExecuteCorrectCommand()
        {
            // Arrange
            SetupDockerComposeV2Detection();
            var composeFiles = new[] { "docker-compose.yml", "docker-compose.x64.yml" };
            const string workingDirectory = "/app";
            const string expectedCommand = "sudo docker compose -f \"docker-compose.yml\" -f \"docker-compose.x64.yml\" up -d";

            _sshService.ExecuteCommandAsync(expectedCommand, workingDirectory).Returns(new SshCommandResult(expectedCommand, "Started successfully"));

            // Act
            await _service.StartServicesAsync(composeFiles, workingDirectory);

            // Assert
            await _sshService.Received(1).ExecuteCommandAsync(expectedCommand, workingDirectory);
        }

        [Fact]
        public async Task StopServicesAsync_WithValidParameters_ShouldExecuteCorrectCommand()
        {
            // Arrange
            SetupDockerComposeV2Detection();
            var composeFiles = new[] { "docker-compose.yml", "docker-compose.arm64.yml" };
            const string workingDirectory = "/app";
            const string expectedCommand = "sudo docker compose -f \"docker-compose.yml\" -f \"docker-compose.arm64.yml\" down";

            _sshService.ExecuteCommandAsync(expectedCommand, workingDirectory).Returns(new SshCommandResult(expectedCommand,"Stopped successfully"));

            // Act
            await _service.StopServicesAsync(composeFiles, workingDirectory);

            // Assert
            await _sshService.Received(1).ExecuteCommandAsync(expectedCommand, workingDirectory);
        }

        
        [Fact]
        public async Task PullImagesAsync_WithValidParameters_ShouldExecuteCorrectCommand()
        {
            // Arrange
            SetupDockerComposeV2Detection();
            var composeFiles = new[] { "docker-compose.yml" };
            const string workingDirectory = "/app";
            const string expectedCommand = "sudo docker compose -f \"docker-compose.yml\" pull";

            _sshService.ExecuteCommandAsync(expectedCommand, Arg.Any<TimeSpan>(), workingDirectory).Returns(new SshCommandResult(expectedCommand,"Pulled successfully"));

            // Act
            await _service.PullImagesAsync(composeFiles, workingDirectory);

            // Assert
            await _sshService.Received(1).ExecuteCommandAsync(expectedCommand, Arg.Any<TimeSpan>(), workingDirectory);
        }

        [Fact]
        public async Task GetServicesStatusAsync_WithValidParameters_ShouldExecuteCorrectCommand()
        {
            // Arrange
            SetupDockerComposeV2Detection();
            var composeFiles = new[] { "docker-compose.yml" };
            const string workingDirectory = "/app";
            const string expectedCommand = "sudo docker compose -f \"docker-compose.yml\" ps";
            const string expectedOutput = "service1  running\nservice2  stopped";

            _sshService.ExecuteCommandAsync(expectedCommand, workingDirectory).Returns(new SshCommandResult(expectedCommand, expectedOutput));

            // Act
            var result = await _service.GetServicesStatusAsync(composeFiles, workingDirectory);

            // Assert
            result.Should().Be(expectedOutput);
            await _sshService.Received(1).ExecuteCommandAsync(expectedCommand, workingDirectory);
        }

        [Fact]
        public async Task RestartServicesAsync_WithValidParameters_ShouldExecuteCorrectCommand()
        {
            // Arrange
            SetupDockerComposeV2Detection();
            var composeFiles = new[] { "docker-compose.yml", "docker-compose.x64.yml" };
            const string workingDirectory = "/app";
            const string expectedCommand = "sudo docker compose -f \"docker-compose.yml\" -f \"docker-compose.x64.yml\" down && sudo docker compose -f \"docker-compose.yml\" -f \"docker-compose.x64.yml\" up -d";

            _sshService.ExecuteCommandAsync(expectedCommand, workingDirectory).Returns(new SshCommandResult(expectedCommand, "Restarted successfully"));

            // Act
            await _service.RestartServicesAsync(composeFiles, workingDirectory);

            // Assert
            await _sshService.Received(1).ExecuteCommandAsync(expectedCommand, workingDirectory);
        }

        [Fact]
        public async Task StartServicesAsync_WhenSshServiceThrows_ShouldPropagateException()
        {
            // Arrange
            var composeFiles = new[] { "docker-compose.yml" };
            const string workingDirectory = "/app";
            var expectedException = new Exception("SSH command failed");
            
            _sshService.ExecuteCommandAsync(Arg.Any<string>(), Arg.Any<string>()).Throws(expectedException);

            // Act & Assert
            var act = async () => await _service.StartServicesAsync(composeFiles, workingDirectory);
            await act.Should().ThrowAsync<Exception>()
                .WithMessage("SSH command failed");
        }

        [Fact]
        public async Task StopServicesAsync_WithNullComposeFiles_ShouldThrowArgumentException()
        {
            // Act & Assert
            var act = async () => await _service.StopServicesAsync(null!, "/app");
            await act.Should().ThrowAsync<ArgumentException>()
                .WithParameterName("composeFiles");
        }

        [Fact]
        public async Task PullImagesAsync_WithEmptyComposeFiles_ShouldThrowArgumentException()
        {
            // Act & Assert
            var act = async () => await _service.PullImagesAsync(Array.Empty<string>(), "/app");
            await act.Should().ThrowAsync<ArgumentException>()
                .WithParameterName("composeFiles");
        }

        [Theory]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData(null)]
        public async Task GetServicesStatusAsync_WithInvalidWorkingDirectory_ShouldThrowArgumentException(string? workingDirectory)
        {
            // Arrange
            var composeFiles = new[] { "docker-compose.yml" };

            // Act & Assert
            var act = async () => await _service.GetServicesStatusAsync(composeFiles, workingDirectory!);
            await act.Should().ThrowAsync<ArgumentException>()
                .WithParameterName("workingDirectory");
        }

        [Fact]
        public async Task RestartServicesAsync_WithNullComposeFiles_ShouldThrowArgumentException()
        {
            // Act & Assert
            var act = async () => await _service.RestartServicesAsync(null!, "/app");
            await act.Should().ThrowAsync<ArgumentException>()
                .WithParameterName("composeFiles");
        }

        [Fact]
        public async Task GetComposeFiles_WithArm64Architecture_ShouldReturnCorrectFiles()
        {
            // Arrange
            const string directoryPath = "/var/docker/configuration/rocket-welder";
            var architecture = CpuArchitecture.Arm64;

            // Setup mock to return all compose files including ARM64 specific ones
            _sshService.GetFiles(directoryPath, "docker-compose*yml")
                .Returns([
                    "/var/docker/configuration/rocket-welder/docker-compose.yml",
                    "/var/docker/configuration/rocket-welder/docker-compose.arm64.yml",
                    "/var/docker/configuration/rocket-welder/docker-compose.x64.yml"
                ]);

            // Act
            var result = await _service.GetComposeFiles(directoryPath, architecture);

            // Assert
            result.Should().HaveCount(2);
            result[0].Should().Be("docker-compose.yml");
            result[1].Should().Be("docker-compose.arm64.yml");
            
            // Should exclude x64 file for ARM64 architecture
            result.Should().NotContain("docker-compose.x64.yml");
        }

        [Fact]
        public async Task StartServicesAsync_WithArm64ComposeFiles_ShouldExecuteCorrectCommand()
        {
            // Arrange - This reproduces the exact scenario from the logs
            SetupDockerComposeV2Detection();
            var composeFiles = new[] { "docker-compose.yml", "docker-compose.arm64.yml" };
            const string workingDirectory = "/var/docker/configuration/rocket-welder";
            const string expectedCommand = "sudo docker compose -f \"docker-compose.yml\" -f \"docker-compose.arm64.yml\" up -d";

            _sshService.ExecuteCommandAsync(expectedCommand, workingDirectory)
                .Returns(new SshCommandResult(expectedCommand, "Started successfully"));

            // Act
            await _service.StartServicesAsync(composeFiles, workingDirectory);

            // Assert
            await _sshService.Received(1).ExecuteCommandAsync(expectedCommand, workingDirectory);
        }

        [Fact]
        public async Task RestartServicesAsync_WithArm64ComposeFiles_ShouldExecuteCorrectCommand()
        {
            // Arrange - This reproduces the exact scenario from the system logs
            SetupDockerComposeV2Detection();
            var composeFiles = new[] { "docker-compose.yml", "docker-compose.arm64.yml" };
            const string workingDirectory = "/var/docker/configuration/rocket-welder";
            const string expectedCommand = "sudo docker compose -f \"docker-compose.yml\" -f \"docker-compose.arm64.yml\" down && sudo docker compose -f \"docker-compose.yml\" -f \"docker-compose.arm64.yml\" up -d";

            _sshService.ExecuteCommandAsync(expectedCommand, workingDirectory)
                .Returns(new SshCommandResult(expectedCommand, "Restarted successfully"));

            // Act
            await _service.RestartServicesAsync(composeFiles, workingDirectory);

            // Assert
            await _sshService.Received(1).ExecuteCommandAsync(expectedCommand, workingDirectory);
        }

        [Fact]
        public async Task StartServicesAsync_WithDockerComposeV1_ShouldUseHyphenatedCommand()
        {
            // Arrange
            SetupDockerComposeV1Detection();
            
            var composeFiles = new[] { "docker-compose.yml" };
            const string workingDirectory = "/app";
            const string expectedCommand = "sudo docker-compose -f \"docker-compose.yml\" up -d";
            
            _sshService.ExecuteCommandAsync(expectedCommand, workingDirectory)
                .Returns(new SshCommandResult(expectedCommand, "Started successfully"));

            // Act
            await _service.StartServicesAsync(composeFiles, workingDirectory);

            // Assert
            await _sshService.Received(1).ExecuteCommandAsync("sudo docker-compose --version");
            await _sshService.Received(1).ExecuteCommandAsync(expectedCommand, workingDirectory);
        }

        [Fact]
        public async Task GetDockerComposeCommand_WhenBothVersionsFail_ShouldDefaultToV2()
        {
            // Arrange
            _sshService.ExecuteCommandAsync("sudo docker compose version")
                .Returns(new SshCommandResult
                {
                    Command = "sudo docker compose version",
                    Output = "",
                    Error = "command not found",
                    ExitCode = 1
                });
            _sshService.ExecuteCommandAsync("sudo docker-compose --version")
                .Returns(new SshCommandResult
                {
                    Command = "sudo docker-compose --version",
                    Output = "",
                    Error = "command not found",
                    ExitCode = 1
                });
            
            var composeFiles = new[] { "docker-compose.yml" };
            const string workingDirectory = "/app";
            const string expectedCommand = "sudo docker compose -f \"docker-compose.yml\" up -d";
            
            _sshService.ExecuteCommandAsync(expectedCommand, workingDirectory)
                .Returns(new SshCommandResult(expectedCommand, "Started successfully"));

            // Act
            await _service.StartServicesAsync(composeFiles, workingDirectory);

            // Assert
            await _sshService.Received(1).ExecuteCommandAsync("sudo docker compose version");
            await _sshService.Received(1).ExecuteCommandAsync("sudo docker-compose --version");
            await _sshService.Received(1).ExecuteCommandAsync(expectedCommand, workingDirectory);
        }

        [Fact]
        public async Task GetDockerComposeCommand_ShouldCacheDetectionResult()
        {
            // Arrange
            SetupDockerComposeV2Detection();
            var composeFiles = new[] { "docker-compose.yml" };
            const string workingDirectory = "/app";
            const string expectedCommand = "sudo docker compose -f \"docker-compose.yml\" up -d";

            _sshService.ExecuteCommandAsync(expectedCommand, workingDirectory)
                .Returns(new SshCommandResult(expectedCommand, "Started successfully"));

            // Act - Call twice
            await _service.StartServicesAsync(composeFiles, workingDirectory);
            await _service.StartServicesAsync(composeFiles, workingDirectory);

            // Assert - Detection should only happen once
            await _sshService.Received(1).ExecuteCommandAsync("sudo docker compose version");
            await _sshService.Received(2).ExecuteCommandAsync(expectedCommand, workingDirectory);
        }

        [Fact]
        public async Task StopServicesAsync_WithProjectName_ShouldDetectAndUseCorrectCommand()
        {
            // Arrange
            SetupDockerComposeV2Detection();
            const string projectName = "rocket-welder";
            const string expectedCommand = "sudo docker compose -p \"rocket-welder\" down";

            _sshService.ExecuteCommandAsync(expectedCommand)
                .Returns(new SshCommandResult(expectedCommand, "Stopped successfully"));

            // Act
            await _service.StopServicesAsync(projectName);

            // Assert
            await _sshService.Received(1).ExecuteCommandAsync(expectedCommand);
        }

        [Fact]
        public async Task GetProjectStatusAsync_WithDockerComposeV1_ShouldUseHyphenatedCommand()
        {
            // Arrange
            SetupDockerComposeV1Detection();
            
            const string projectName = "rocket-welder";
            const string expectedCommand = "sudo docker-compose -p \"rocket-welder\" ps --format json";
            
            _sshService.ExecuteCommandAsync(expectedCommand)
                .Returns(new SshCommandResult(expectedCommand, "[]"));

            // Act
            var result = await _service.GetProjectStatusAsync(projectName);

            // Assert
            await _sshService.Received(1).ExecuteCommandAsync(expectedCommand);
            result.Status.Should().Be("stopped");
        }

        [Fact]
        public async Task GetDockerComposeStatusAsync_ShouldDetectAndUseCorrectCommand()
        {
            // Arrange
            SetupDockerComposeV2Detection();
            const string expectedCommand = "sudo docker compose ls --format json";
            const string jsonOutput = "[{\"Name\":\"test-project\",\"Status\":\"running\",\"ConfigFiles\":\"docker-compose.yml\"}]";

            _sshService.ExecuteCommandAsync(expectedCommand)
                .Returns(new SshCommandResult(expectedCommand, jsonOutput));

            // Act
            var result = await _service.GetDockerComposeStatusAsync();

            // Assert
            await _sshService.Received(1).ExecuteCommandAsync(expectedCommand);
            result.Should().ContainKey(new PackageName("test-project"));
        }
    }
}