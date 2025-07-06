
namespace ModelingEvolution.AutoUpdater
{
    public interface IContainerInfo
    {
        string Name { get; }

        IList<string> Logs();
    }
}