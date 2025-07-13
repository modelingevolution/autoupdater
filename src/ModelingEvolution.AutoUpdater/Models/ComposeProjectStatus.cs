using System.Collections.Generic;

namespace ModelingEvolution.AutoUpdater.Models
{
    /// <summary>
    /// Status of a Docker Compose project
    /// </summary>
    public record ComposeProjectStatus(
        string Status,
        IReadOnlyList<string> ConfigFiles,
        int RunningServices,
        int TotalServices
    );
}