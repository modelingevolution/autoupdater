namespace ModelingEvolution.AutoUpdater;

public record GitTagVersion(string FriendlyName, System.Version Version) : IComparable<GitTagVersion> 
{
    public static implicit operator string(GitTagVersion v)
    {
        return v.ToString();
    }
    public static bool TryParse(string? text, out GitTagVersion? p)
    {
        if (text != null && System.Version.TryParse(text.Replace("ver", "").Replace("v", ""), out var v))
        { 
            p = new GitTagVersion(text, v);  
            return true; 
        }
        p = null;
        return false;
    }

    public int CompareTo(GitTagVersion other)
    {
        return this.Version.CompareTo(other.Version);
    }
    public override string ToString()
    {
        return FriendlyName;
    }
}