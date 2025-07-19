using System;
using ModelingEvolution.AutoUpdater.Common;

namespace ModelingEvolution.AutoUpdater.Models
{
    /// <summary>
    /// Migration direction - up (forward) or down (rollback)
    /// </summary>
    public enum MigrationDirection
    {
        /// <summary>
        /// Forward migration (upgrade to this version)
        /// </summary>
        Up,
        
        /// <summary>
        /// Rollback migration (downgrade from this version)
        /// </summary>
        Down
    }

    /// <summary>
    /// Represents a migration script that can be executed during updates
    /// </summary>
    public record MigrationScript(
        string FileName,
        string FilePath,
        PackageVersion Version,
        MigrationDirection Direction//,
        //bool IsExecutable
    );
}