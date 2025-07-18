using System.Collections.Generic;
using System.Collections.Immutable;

namespace ModelingEvolution.AutoUpdater.Models
{
    /// <summary>
    /// Status of a Docker Compose project
    /// </summary>
    public record ComposeProjectStatus(
        string Status,
        ImmutableArray<string> ConfigFiles,
        int RunningServices,
        int TotalServices
    );
}