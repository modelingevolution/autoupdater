# AutoUpdater Refactoring Design Document

## Overview

This document outlines a comprehensive refactoring of the AutoUpdater system to improve testability, maintainability, and separation of concerns. The refactoring will extract update logic from `DockerComposeConfiguration` into dedicated services with well-defined interfaces.

## Current Architecture Issues

### 1. Monolithic DockerComposeConfiguration
- **Problem**: `DockerComposeConfiguration.Update()` contains mixed responsibilities
- **Impact**: Difficult to test, violates SRP, tightly coupled to infrastructure

### 2. Embedded Infrastructure Logic
- **Problem**: Git operations, SSH commands, and file system access are scattered throughout
- **Impact**: Cannot unit test without real infrastructure dependencies

### 3. Limited Testability
- **Problem**: No interfaces for external dependencies, hard to mock
- **Impact**: Tests require real Git repositories, SSH connections, and file systems

## Proposed Architecture

### Core Principles
1. **Separation of Concerns**: Each service has a single, well-defined responsibility
2. **Dependency Inversion**: Depend on abstractions, not concretions
3. **Testability**: All external dependencies can be mocked
4. **Clean Architecture**: Business logic isolated from infrastructure concerns

## Service Architecture

### 1. UpdateHost (Orchestrator)
**Responsibility**: Coordinates the entire update process

```csharp
public class UpdateHost
{
    private readonly IGitService _gitService;
    private readonly IScriptMigrationService _scriptMigrationService;
    private readonly ISshService _sshService;
    private readonly IDockerComposeService _dockerComposeService;
    private readonly ILogger<UpdateHost> _logger;

    public async Task UpdateAsync(DockerComposeConfiguration config)
    {
        // 1. Check for updates
        // 2. Execute git operations
        // 3. Run migration scripts
        // 4. Update Docker Compose services
        // 5. Verify deployment
    }
}
```

### 2. IGitService
**Responsibility**: All Git repository operations

```csharp
public interface IGitService
{
    Task<bool> CloneRepositoryAsync(string repositoryUrl, string targetPath);
    Task<bool> PullLatestAsync(string repositoryPath);
    Task CheckoutVersionAsync(string repositoryPath, string version);
    Task<IEnumerable<GitTagVersion>> GetAvailableVersionsAsync(string repositoryPath);
    Task<string?> GetCurrentVersionAsync(string repositoryPath);
    bool IsGitRepository(string path);
}
```

### 3. IScriptMigrationService
**Responsibility**: Migration script discovery and execution

```csharp
public interface IScriptMigrationService
{
    Task<IEnumerable<MigrationScript>> DiscoverScriptsAsync(string directory);
    Task<IEnumerable<MigrationScript>> FilterScriptsForMigrationAsync(
        IEnumerable<MigrationScript> scripts, 
        string? fromVersion, 
        string toVersion);
    Task ExecuteScriptsAsync(IEnumerable<MigrationScript> scripts, string workingDirectory);
}

public record MigrationScript(
    string FileName,
    string FilePath,
    Version Version,
    bool IsExecutable
);
```

### 4. ISshService
**Responsibility**: SSH command execution and file operations

```csharp
public interface ISshService
{
    Task<SshCommandResult> ExecuteCommandAsync(string command, string? workingDirectory = null);
    Task<string> ReadFileAsync(string filePath);
    Task WriteFileAsync(string filePath, string content);
    Task MakeExecutableAsync(string filePath);
    Task<string> GetArchitectureAsync();
}
```

### 5. IDockerComposeService
**Responsibility**: Docker Compose operations

```csharp
public interface IDockerComposeService
{
    Task<string[]> GetComposeFilesForArchitectureAsync(string composeDirectory, string architecture);
    Task<ComposeProjectStatus> GetProjectStatusAsync(string projectName);
    Task StartServicesAsync(string[] composeFiles, string workingDirectory);
    Task StopServicesAsync(string projectName);
}
```

## Domain Models

### UpdateContext
```csharp
public record UpdateContext(
    DockerComposeConfiguration Configuration,
    string? PreviousVersion,
    string TargetVersion,
    string Architecture,
    string ComposeDirectory
);
```

### UpdateResult
```csharp
public record UpdateResult(
    bool Success,
    string? Version,
    DateTime UpdatedAt,
    IReadOnlyList<string> ExecutedScripts,
    string? ErrorMessage = null
);
```

## Implementation Plan

### Phase 1: Interface Definition and Basic Structure
1. **Create interfaces** (`IGitService`, `IScriptMigrationService`, `ISshService`, `IDockerComposeService`)
2. **Create domain models** (`UpdateContext`, `UpdateResult`, `MigrationScript`)
3. **Update `UpdateHost`** to accept dependencies via constructor
4. **Create git branch** `feature/autoupdater-refactoring`

### Phase 2: Extract Git Operations
1. **Implement `GitService`** class with LibGit2Sharp
2. **Extract git operations** from `DockerComposeConfiguration`
3. **Update `UpdateHost`** to use `IGitService`
4. **Write unit tests** for `GitService`

### Phase 3: Extract Migration Script Logic
1. **Implement `ScriptMigrationService`** class
2. **Extract migration logic** from `DockerComposeConfiguration`
3. **Update `UpdateHost`** to use `IScriptMigrationService`
4. **Write unit tests** for `ScriptMigrationService`

### Phase 4: Extract SSH Operations
1. **Create `ISshService`** abstraction over existing SSH code
2. **Implement `SshService`** wrapper
3. **Update `UpdateHost`** to use `ISshService`
4. **Write unit tests** with mocked SSH

### Phase 5: Extract Docker Compose Operations
1. **Implement `DockerComposeService`** class
2. **Extract Docker operations** from update logic
3. **Update `UpdateHost`** to use `IDockerComposeService`
4. **Write unit tests** for Docker Compose operations

### Phase 6: Integration and Testing
1. **Wire up dependency injection** in `Program.cs`
2. **Write integration tests** for `UpdateHost`
3. **Write end-to-end tests** with all mocked dependencies
4. **Performance testing** and optimization

## Testing Strategy

### Unit Tests
```csharp
public class UpdateHostTests
{
    private readonly IGitService _gitService = Substitute.For<IGitService>();
    private readonly IScriptMigrationService _scriptService = Substitute.For<IScriptMigrationService>();
    private readonly ISshService _sshService = Substitute.For<ISshService>();
    private readonly IDockerComposeService _dockerService = Substitute.For<IDockerComposeService>();
    private readonly ILogger<UpdateHost> _logger = Substitute.For<ILogger<UpdateHost>>();
    
    [Fact]
    public async Task UpdateAsync_WithNewVersion_ExecutesMigrationScripts()
    {
        // Arrange
        var config = CreateTestConfiguration();
        _gitService.GetCurrentVersionAsync(Arg.Any<string>())
                  .Returns("1.0.0");
        _gitService.GetAvailableVersionsAsync(Arg.Any<string>())
                  .Returns([new GitTagVersion("1.1.0", new Version(1, 1, 0))]);
        
        var migrationScripts = new[]
        {
            new MigrationScript("host-1.0.1.sh", "/path/host-1.0.1.sh", new Version(1, 0, 1), true)
        };
        
        _scriptService.DiscoverScriptsAsync(Arg.Any<string>())
                     .Returns(migrationScripts);
        _scriptService.FilterScriptsForMigrationAsync(Arg.Any<IEnumerable<MigrationScript>>(), "1.0.0", "1.1.0")
                     .Returns(migrationScripts);
        
        var updateHost = new UpdateHost(_gitService, _scriptService, _sshService, _dockerService, _logger);
        
        // Act
        var result = await updateHost.UpdateAsync(config);
        
        // Assert
        result.Success.Should().BeTrue();
        result.ExecutedScripts.Should().HaveCount(1);
        await _scriptService.Received(1).ExecuteScriptsAsync(migrationScripts, Arg.Any<string>());
    }
}
```

### Integration Tests
```csharp
public class UpdateHostIntegrationTests
{
    [Fact]
    public async Task UpdateAsync_WithRealGitRepo_UpdatesSuccessfully()
    {
        // Test with real Git repository but mocked SSH and Docker
    }
}
```

## Benefits of Refactoring

### 1. Improved Testability
- **Unit Tests**: Each service can be tested in isolation
- **Mock Dependencies**: External systems can be mocked for reliable tests
- **Fast Tests**: No need for real infrastructure in unit tests

### 2. Better Separation of Concerns
- **Single Responsibility**: Each service has one clear purpose
- **Loose Coupling**: Services depend on interfaces, not implementations
- **Easy Extension**: New features can be added without modifying existing services

### 3. Enhanced Maintainability
- **Clear Boundaries**: Well-defined interfaces make the codebase easier to understand
- **Parallel Development**: Different services can be developed independently
- **Easier Debugging**: Isolated services make issues easier to track down

### 4. Production Benefits
- **Reliable Updates**: Better tested code leads to fewer production issues
- **Monitoring**: Each service can have dedicated metrics and logging
- **Configuration**: Services can be configured independently

## Migration Strategy

### Backward Compatibility
- **Existing API**: Maintain current public API during transition
- **Feature Flags**: Use feature flags to switch between old and new implementations
- **Gradual Rollout**: Phase the rollout to minimize risk

### Risk Mitigation
- **Comprehensive Testing**: Extensive test coverage before deployment
- **Rollback Plan**: Ability to quickly revert to previous implementation
- **Monitoring**: Enhanced logging and metrics during transition

## Dependencies and Tools

### New Dependencies
- **NSubstitute**: For creating substitute objects in tests
- **FluentAssertions**: For more readable test assertions
- **Microsoft.Extensions.DependencyInjection**: For dependency injection

### Testing Tools
- **xUnit**: Primary testing framework
- **Test Containers**: For integration tests requiring real services
- **Bogus**: For generating test data

## Success Criteria

### Technical Metrics
- **Code Coverage**: >90% code coverage for new services
- **Cyclomatic Complexity**: Reduced complexity in core update logic
- **Test Performance**: Unit tests run in <5 seconds

### Quality Metrics
- **Bug Rate**: Reduced production issues related to updates
- **Development Velocity**: Faster feature development due to better testing
- **Code Maintainability**: Improved code review feedback and developer experience

## Timeline

- **Week 1**: Interface design and basic structure
- **Week 2**: Git service extraction and testing
- **Week 3**: Migration script service extraction
- **Week 4**: SSH and Docker Compose service extraction
- **Week 5**: Integration testing and bug fixes
- **Week 6**: Code review, documentation, and deployment

---

*This refactoring represents a significant investment in the long-term maintainability and reliability of the AutoUpdater system. The improved architecture will enable faster development, better testing, and more reliable deployments.*