namespace ModelingEvolution.AutoUpdater.Models
{
    /// <summary>
    /// Context information for an update operation
    /// </summary>
    public record UpdateContext(
        DockerComposeConfiguration Configuration,
        string? PreviousVersion,
        string TargetVersion,
        string Architecture,
        string ComposeDirectory
    );
}