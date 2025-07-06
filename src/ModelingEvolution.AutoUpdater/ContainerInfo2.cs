namespace ModelingEvolution.AutoUpdater;

public class ContainerInfo2 : IContainerInfo
{
    private DockerComposeConfiguration dockerComposeConfiguration;

    private string _id;
    public ContainerInfo2(DockerComposeConfiguration dockerComposeConfiguration, string v, string iD)
    {
        this.dockerComposeConfiguration = dockerComposeConfiguration;
        this.Name = v;
        this._id = iD;
    }

    public string Name { get; }

    public IList<string> Logs()
    {
        throw new NotImplementedException();
    }
}