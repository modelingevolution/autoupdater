using System.Threading.Tasks;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// Manages SSH connections and provides ISshService instances
    /// </summary>
    public interface ISshConnectionManager
    {
        /// <summary>
        /// Creates an SSH service instance for the configured host
        /// </summary>
        /// <returns>An ISshService instance ready for use</returns>
        Task<ISshService> CreateSshServiceAsync();
        
        /// <summary>
        /// Tests SSH connectivity to the configured host
        /// </summary>
        /// <returns>True if connection is successful, false otherwise</returns>
        Task<bool> TestConnectivityAsync();
    }
}