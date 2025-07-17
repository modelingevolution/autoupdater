using System;

namespace ModelingEvolution.AutoUpdater.Host
{
    [AttributeUsage(AttributeTargets.Assembly)]
    public class GitCommitShaAttribute : Attribute
    {
        public string FullSha { get; }
        public string ShortSha { get; }

        public GitCommitShaAttribute(string fullSha, string shortSha)
        {
            FullSha = fullSha ?? throw new ArgumentNullException(nameof(fullSha));
            ShortSha = shortSha ?? throw new ArgumentNullException(nameof(shortSha));
        }
    }
}