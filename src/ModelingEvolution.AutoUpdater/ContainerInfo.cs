using Ductus.FluentDocker.Extensions;
using Ductus.FluentDocker.Services;
using Ductus.FluentDocker.Services.Extensions;

namespace ModelingEvolution.AutoUpdater;

public class ContainerInfo : IContainerInfo
{
    private readonly IContainerService _container;
    public DockerComposeConfiguration Parent { get; }
    public string Name { get; }

    public ContainerInfo(DockerComposeConfiguration Configuration, string Name, IContainerService i)
    {
        this._container = i;
        this.Parent = Configuration;
        this.Name = Name;
    }

    public IList<string> Logs()
    {
        return _container.Logs().ReadToEnd();
    }
}