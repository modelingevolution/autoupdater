using ModelingEvolution.AutoUpdater.Common;

namespace ModelingEvolution.AutoUpdater;

public record GitTagVersion(string FriendlyName, PackageVersion Version) : IComparable<GitTagVersion> 
{
    public static implicit operator string(GitTagVersion v)
    {
        return v.ToString();
    }
    public static bool TryParse(string? text, out GitTagVersion? p)
    {
        if (text != null && PackageVersion.TryParse(text, out var v))
        { 
            p = new GitTagVersion(text, v);  
            return true; 
        }
        p = null;
        return false;
    }
    public static bool operator>(GitTagVersion a, GitTagVersion b)
    {
        return a.Version > b.Version;
    }

    public static bool operator <(GitTagVersion a, GitTagVersion b)
    {
        return a.Version < b.Version;
    }

    public int CompareTo(GitTagVersion? other)
    {
        if (other is null) return 1;
        return this.Version.CompareTo(other.Version);
    }
    public override string ToString()
    {
        return FriendlyName;
    }
}