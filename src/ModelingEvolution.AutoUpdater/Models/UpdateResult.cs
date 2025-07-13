using System;
using System.Collections.Generic;

namespace ModelingEvolution.AutoUpdater.Models
{
    /// <summary>
    /// Result of an update operation
    /// </summary>
    public record UpdateResult(
        bool Success,
        string? Version,
        DateTime UpdatedAt,
        IReadOnlyList<string> ExecutedScripts,
        string? ErrorMessage = null
    );
}