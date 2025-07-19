using System.Collections.Immutable;
using ModelingEvolution.AutoUpdater.Common;

namespace ModelingEvolution.AutoUpdater;

public record DeploymentState(PackageVersion Version, DateTime Updated)
{
    public ImmutableSortedSet<PackageVersion> Up { get; set; } = [];
    
    // We don't do anything yet with Failed scripts. We will only log them.
    public ImmutableSortedSet<PackageVersion> Failed { get; set; } = [];
}