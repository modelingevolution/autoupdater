using System;

namespace ModelingEvolution.AutoUpdater
{
    /// <summary>
    /// A read of the installed state failed for a reason other than "not installed" (SSH failure, timeout, unreadable state
    /// file, failed <c>docker compose ls</c>). The installed-state endpoint answers 503 with <see cref="Exception.Message"/>,
    /// so a broken probe is never reported as "nothing installed".
    /// </summary>
    public class InstalledStateUnavailableException : Exception
    {
        public InstalledStateUnavailableException(string message) : base(message) { }
        public InstalledStateUnavailableException(string message, Exception innerException) : base(message, innerException) { }
    }
}
