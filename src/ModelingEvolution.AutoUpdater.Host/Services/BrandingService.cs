using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ModelingEvolution.AutoUpdater.Host.Services
{
    /// <summary>
    /// Service for managing branding assets like logos and favicons
    /// </summary>
    public class BrandingService : IBrandingService
    {
        private readonly IWebHostEnvironment _environment;
        private readonly ILogger<BrandingService> _logger;
        private readonly object _lock = new object();
        private (string Path, string MimeType)? _cachedFavicon;
        private DateTime _lastScanTime = DateTime.MinValue;
        private readonly TimeSpan _cacheTimeout = TimeSpan.FromMinutes(5);

        // Supported logo file extensions and their MIME types
        private static readonly Dictionary<string, string> SupportedFormats = new()
        {
            { ".png", "image/png" },
            { ".ico", "image/x-icon" },
            { ".svg", "image/svg+xml" },
            { ".jpg", "image/jpeg" },
            { ".jpeg", "image/jpeg" },
            { ".gif", "image/gif" },
            { ".webp", "image/webp" }
        };

        public BrandingService(IWebHostEnvironment environment, ILogger<BrandingService> logger)
        {
            _environment = environment;
            _logger = logger;
        }

        public (string Path, string MimeType) GetFavicon()
        {
            lock (_lock)
            {
                // Check if cache is still valid
                if (_cachedFavicon.HasValue && 
                    DateTime.UtcNow - _lastScanTime < _cacheTimeout)
                {
                    return _cachedFavicon.Value;
                }

                // Scan for logo files
                _cachedFavicon = ScanForLogoFiles();
                _lastScanTime = DateTime.UtcNow;

                _logger.LogInformation("Favicon resolved to: {Path} ({MimeType})", 
                    _cachedFavicon.Value.Path, _cachedFavicon.Value.MimeType);

                return _cachedFavicon.Value;
            }
        }

        public void RefreshCache()
        {
            lock (_lock)
            {
                _cachedFavicon = null;
                _lastScanTime = DateTime.MinValue;
                _logger.LogInformation("Branding cache refreshed");
            }
        }

        private (string Path, string MimeType) ScanForLogoFiles()
        {
            var wwwrootPath = _environment.WebRootPath;
            if (string.IsNullOrEmpty(wwwrootPath) || !Directory.Exists(wwwrootPath))
            {
                _logger.LogWarning("wwwroot directory not found, using default favicon");
                return ("favicon.png", "image/png");
            }

            try
            {
                // Look for logo files with supported extensions
                var logoFiles = SupportedFormats.Keys
                    .SelectMany(ext => Directory.GetFiles(wwwrootPath, $"logo{ext}", SearchOption.TopDirectoryOnly))
                    .ToList();

                if (!logoFiles.Any())
                {
                    _logger.LogDebug("No custom logo files found, using default favicon");
                    return ("favicon.png", "image/png");
                }

                // Prefer certain formats: .ico > .png > .svg > others
                var preferredOrder = new[] { ".ico", ".png", ".svg", ".jpg", ".jpeg", ".webp", ".gif" };
                var selectedLogo = logoFiles
                    .OrderBy(file => 
                    {
                        var ext = Path.GetExtension(file).ToLowerInvariant();
                        var index = Array.IndexOf(preferredOrder, ext);
                        return index == -1 ? int.MaxValue : index;
                    })
                    .First();

                var fileName = Path.GetFileName(selectedLogo);
                var extension = Path.GetExtension(selectedLogo).ToLowerInvariant();
                var mimeType = SupportedFormats.GetValueOrDefault(extension, "application/octet-stream");

                _logger.LogInformation("Found custom logo file: {FileName}", fileName);
                return (fileName, mimeType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scanning for logo files, using default favicon");
                return ("favicon.png", "image/png");
            }
        }
    }
}