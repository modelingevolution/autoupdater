using System;

namespace ModelingEvolution.AutoUpdater.Models
{
    /// <summary>
    /// Represents a migration script that can be executed during updates
    /// </summary>
    public record MigrationScript(
        string FileName,
        string FilePath,
        Version Version,
        bool IsExecutable
    );
}