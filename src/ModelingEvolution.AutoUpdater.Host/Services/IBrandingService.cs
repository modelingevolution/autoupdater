using System;

namespace ModelingEvolution.AutoUpdater.Host.Services
{
    /// <summary>
    /// Service for managing branding assets like logos and favicons
    /// </summary>
    public interface IBrandingService
    {
        /// <summary>
        /// Gets the favicon path and MIME type. Returns default favicon if no custom logo is found.
        /// </summary>
        /// <returns>Tuple containing the favicon path and MIME type</returns>
        (string Path, string MimeType) GetFavicon();

        /// <summary>
        /// Forces a refresh of the cached logo files
        /// </summary>
        void RefreshCache();
    }
}