using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.Common;
using ModelingEvolution.AutoUpdater.Models;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// Implementation of script migration service
    /// </summary>
    public class ScriptMigrationService : IScriptMigrationService
    {
        private readonly ISshService _sshService;
        private readonly ILogger<ScriptMigrationService> _logger;
        private static readonly Regex ScriptNamePattern = new(@"^(up|down)-(\d+\.\d+\.\d+(?:\.\d+)?)\.sh$", RegexOptions.Compiled);

        public ScriptMigrationService(ISshService sshService, ILogger<ScriptMigrationService> logger)
        {
            _sshService = sshService ?? throw new ArgumentNullException(nameof(sshService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<IEnumerable<MigrationScript>> DiscoverScriptsAsync(string directoryPath)
        {
            try
            {
                _logger.LogDebug("Discovering migration scripts in directory {DirectoryPath}", directoryPath);

                if (string.IsNullOrWhiteSpace(directoryPath))
                {
                    _logger.LogWarning("Directory path is null or whitespace");
                    return Enumerable.Empty<MigrationScript>();
                }

                // Check if directory exists
                if (!await _sshService.DirectoryExistsAsync(directoryPath))
                {
                    _logger.LogWarning("Directory {DirectoryPath} does not exist on remote host", directoryPath);
                    return Enumerable.Empty<MigrationScript>();
                }

                // Get migration script files using SshService
                var scriptFiles = _sshService.GetFiles(directoryPath, "*-*.sh");
                var scripts = new List<MigrationScript>();

                foreach (var scriptFile in scriptFiles)
                {
                    var fileName = Path.GetFileName(scriptFile);
                    var match = ScriptNamePattern.Match(fileName);

                    if (!match.Success)
                    {
                        _logger.LogInformation("Script file {FileName} does not match expected naming pattern (up-X.Y.Z.sh or down-X.Y.Z.sh), ignoring.", fileName);
                        continue;
                    }

                    var directionStr = match.Groups[1].Value;
                    var versionStr = match.Groups[2].Value;
                    
                    // Parse as PackageVersion - this will handle v-prefix and validation
                    var version = PackageVersion.Parse(versionStr);
                    if (!version.IsValid)
                    {
                        _logger.LogWarning("Script file {FileName} has invalid version format", fileName);
                        continue;
                    }

                    if (!Enum.TryParse<MigrationDirection>(directionStr, true, out var direction))
                    {
                        _logger.LogWarning("Script file {FileName} has invalid direction format", fileName);
                        continue;
                    }

                    var isExecutable = await _sshService.IsExecutableAsync(scriptFile);
                    //var script = new MigrationScript(fileName, scriptFile, version, direction, isExecutable);
                    var script = new MigrationScript(fileName, scriptFile, version, direction);
                    scripts.Add(script);
                    
                    _logger.LogDebug("Discovered migration script: {FileName} (v{Version}, {Direction}, executable: {IsExecutable})", 
                        fileName, version, direction, isExecutable);
                }

                _logger.LogInformation("Discovered {Count} migration scripts in {DirectoryPath}", scripts.Count, directoryPath);
                return scripts.OrderBy(s => s.Version);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to discover migration scripts in {DirectoryPath}", directoryPath);
                return Enumerable.Empty<MigrationScript>();
            }
        }
        
        public async Task<IEnumerable<MigrationScript>> FilterScriptsForMigrationAsync(
            IEnumerable<MigrationScript> allScripts, 
            PackageVersion? fromVersion, 
            PackageVersion targetVersion,
            ImmutableSortedSet<PackageVersion>? excludeVersions = null)
        {
            try
            {
                _logger.LogDebug("Filtering migration scripts from {FromVersion} to {TargetVersion}", 
                    fromVersion?.ToString() ?? "initial", targetVersion);
                
                // Validate target version
                if (!targetVersion.IsValid)
                {
                    _logger.LogError("Invalid target version format: {TargetVersion}", targetVersion);
                    return [];
                }

                // Handle fromVersion - null or Empty means initial migration
                var from = fromVersion.HasValue && !fromVersion.Value.IsEmpty ? fromVersion : null;

                List<MigrationScript> filteredScripts;

                excludeVersions ??= ImmutableSortedSet<PackageVersion>.Empty;

                if (from == null || targetVersion > from)
                {
                    // Forward migration: use UP scripts
                    _logger.LogDebug("Forward migration detected, using UP scripts");
                    filteredScripts = allScripts.Where(script =>
                    {
                        // Only UP scripts
                        if (script.Direction != MigrationDirection.Up)
                            return false;

                        // Script version must be <= target version
                        if (script.Version > targetVersion)
                            return false;

                        // If we have a from version, script version must be > from version
                        if (from != null && script.Version <= from.Value)
                            return false;

                        // Exclude already executed scripts
                        if (excludeVersions.Contains(script.Version))
                        {
                            _logger.LogDebug("Excluding already executed UP script: {FileName} (v{Version})", 
                                script.FileName, script.Version);
                            return false;
                        }

                        // Only include executable scripts
                        //return script.IsExecutable;
                        return true;
                    }).OrderBy(s => s.Version).ToList();
                }
                else if (targetVersion < from)
                {
                    // Rollback migration: use DOWN scripts
                    _logger.LogDebug("Rollback migration detected, using DOWN scripts");
                    filteredScripts = allScripts.Where(script =>
                    {
                        // Only DOWN scripts
                        if (script.Direction != MigrationDirection.Down)
                            return false;

                        // For rollback, we need down scripts for versions > target and <= from
                        if (script.Version <= targetVersion || script.Version > from.Value)
                            return false;

                        // For DOWN scripts, we should execute them if the UP version WAS executed
                        // (i.e., the version is in the excludeVersions set, meaning UP was run)
                        if (!excludeVersions.Contains(script.Version))
                        {
                            _logger.LogDebug("Skipping DOWN script for version that was never applied: {FileName} (v{Version})", 
                                script.FileName, script.Version);
                            return false;
                        }

                        // Only include executable scripts
                        //return script.IsExecutable;
                        return true;
                    }).OrderByDescending(s => s.Version).ToList(); // Execute in reverse order for rollback
                }
                else
                {
                    // Same version, no migration needed
                    _logger.LogDebug("Target version equals current version, no migration needed");
                    filteredScripts = new List<MigrationScript>(0);
                }

                _logger.LogInformation("Filtered {Count} migration scripts for execution", filteredScripts.Count);
                
                foreach (var script in filteredScripts)
                {
                    _logger.LogDebug("Will execute: {FileName} (v{Version}, {Direction})", 
                        script.FileName, script.Version, script.Direction);
                }

                return filteredScripts;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to filter migration scripts");
                return [];
            }
        }

        public async Task<IEnumerable<PackageVersion>> ExecuteScriptsAsync(IEnumerable<MigrationScript> scripts, string workingDirectory)
        {
            var executedVersions = new List<PackageVersion>();
            
            try
            {
                var scriptList = scripts.ToList();
                _logger.LogInformation("Executing {Count} migration scripts in {WorkingDirectory}", 
                    scriptList.Count, workingDirectory);

                foreach (var script in scriptList)
                {
                    await ExecuteScriptAsync(script, workingDirectory);
                    executedVersions.Add(script.Version);
                    _logger.LogDebug("Successfully executed {Direction} script for version {Version}", 
                        script.Direction, script.Version);
                }

                _logger.LogInformation("All migration scripts executed successfully");
                return executedVersions;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute migration scripts. Successfully executed: {ExecutedVersions}", 
                    string.Join(", ", executedVersions));
                throw;
            }
        }

        private async Task ExecuteScriptAsync(MigrationScript script, string workingDirectory)
        {
            try
            {
                _logger.LogInformation("Executing {Direction} migration script: {FileName} (v{Version})", 
                    script.Direction, script.FileName, script.Version);

                //if (!script.IsExecutable)
                //{
                //    throw new InvalidOperationException($"Script {script.FileName} is not executable");
                //}

                // Make script executable if it isn't already
                // await _sshService.MakeExecutableAsync(script.FilePath);

                // Execute the script
                var executeCommand = $"sudo bash \"{script.FilePath}\"";
                var result = await _sshService.ExecuteCommandAsync(executeCommand, workingDirectory);
                
                if (!result.IsSuccess)
                {
                    throw new InvalidOperationException($"Script execution failed with exit code {result.ExitCode}: {result.Error}");
                }

                _logger.LogInformation("{Direction} migration script {FileName} executed successfully", 
                    script.Direction, script.FileName);
                _logger.LogDebug("Script output: {Output}", result.Output);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute {Direction} migration script {FileName}", 
                    script.Direction, script.FileName);
                throw new InvalidOperationException($"Migration script {script.FileName} failed: {ex.Message}", ex);
            }
        }

        public async Task<bool> ValidateScriptAsync(string scriptPath)
        {
            try
            {
                _logger.LogDebug("Validating script: {ScriptPath}", scriptPath);

                if (string.IsNullOrWhiteSpace(scriptPath))
                {
                    _logger.LogWarning("Script path is null or empty");
                    return false;
                }

                var fileName = Path.GetFileName(scriptPath);
                var match = ScriptNamePattern.Match(fileName);

                if (!match.Success)
                {
                    _logger.LogWarning("Script {FileName} does not match expected naming pattern (up-X.Y.Z.sh or down-X.Y.Z.sh)", fileName);
                    return false;
                }

                var directionStr = match.Groups[1].Value;
                var versionStr = match.Groups[2].Value;

                if (!Enum.TryParse<MigrationDirection>(directionStr, true, out _))
                {
                    _logger.LogWarning("Script {FileName} has invalid direction format", fileName);
                    return false;
                }

                var version = PackageVersion.Parse(versionStr);
                if (!version.IsValid)
                {
                    _logger.LogWarning("Script {FileName} has invalid version format", fileName);
                    return false;
                }

                var isExecutable = await _sshService.IsExecutableAsync(scriptPath);
                if (!isExecutable)
                {
                    _logger.LogWarning("Script {ScriptPath} is not executable", scriptPath);
                    return false;
                }

                _logger.LogDebug("Script {ScriptPath} is valid", scriptPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to validate script: {ScriptPath}", scriptPath);
                return false;
            }
        }
    }
}