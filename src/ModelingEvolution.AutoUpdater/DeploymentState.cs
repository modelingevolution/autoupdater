using System.Collections.Immutable;

namespace ModelingEvolution.AutoUpdater;

public record DeploymentState(string Version, DateTime Updated)
{
    public ImmutableSortedSet<Version> Up { get; set; } = [];
    
    // We don't do anything yet with Failed scripts. We will only log them.
    public ImmutableSortedSet<Version> Failed { get; set; } = [];
}