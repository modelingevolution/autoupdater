using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ModelingEvolution.JsonParsableConverter;

namespace ModelingEvolution.AutoUpdater.Common;

/// <summary>
/// Represents a strongly typed package version that handles semantic versioning with optional 'v' prefix.
/// Supports formats like "v1.0.0", "1.0.2", "1.0.0-alpha", "-" (empty), and normalizes invalid values.
/// </summary>
[JsonConverter(typeof(JsonParsableConverter<PackageVersion>))]
public readonly record struct PackageVersion : IComparable<PackageVersion>, IComparable, IParsable<PackageVersion>
{
    private static readonly Regex VersionRegex = new(@"^v?(\d+)\.(\d+)\.(\d+)(?:-([a-zA-Z0-9\-\.]+))?$", RegexOptions.Compiled);
    
    /// <summary>
    /// Represents an empty/no version state (displayed as "-")
    /// </summary>
    public static readonly PackageVersion Empty = new("-");
    
    private readonly string _value;
    
    /// <summary>
    /// Initializes a new instance of PackageVersion
    /// </summary>
    /// <param name="value">The version string to parse</param>
    public PackageVersion(string? value)
    {
        _value = NormalizeVersion(value);
        
        if (IsValid)
        {
            var match = VersionRegex.Match(_value);
            if (match.Success)
            {
                Major = int.Parse(match.Groups[1].Value);
                Minor = int.Parse(match.Groups[2].Value);
                Patch = int.Parse(match.Groups[3].Value);
                PreRelease = match.Groups[4].Success ? match.Groups[4].Value : null;
                HasVPrefix = _value.StartsWith("v", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                Major = Minor = Patch = 0;
                PreRelease = null;
                HasVPrefix = false;
            }
        }
        else
        {
            Major = Minor = Patch = 0;
            PreRelease = null;
            HasVPrefix = false;
        }
    }
    
    /// <summary>
    /// Gets the major version number
    /// </summary>
    public int Major { get; }
    
    /// <summary>
    /// Gets the minor version number
    /// </summary>
    public int Minor { get; }
    
    /// <summary>
    /// Gets the patch version number
    /// </summary>
    public int Patch { get; }
    
    /// <summary>
    /// Gets the pre-release identifier (e.g., "alpha", "beta", "rc1")
    /// </summary>
    public string? PreRelease { get; }
    
    /// <summary>
    /// Gets whether the version has a 'v' prefix
    /// </summary>
    public bool HasVPrefix { get; }
    
    /// <summary>
    /// Gets whether this version is valid (follows semantic versioning)
    /// </summary>
    public bool IsValid => !string.IsNullOrEmpty(_value) && _value != "-" && VersionRegex.IsMatch(_value);
    
    /// <summary>
    /// Gets whether this version is empty (no version)
    /// </summary>
    public bool IsEmpty => _value == "-";
    
    /// <summary>
    /// Gets whether this version is a pre-release version
    /// </summary>
    public bool IsPreRelease => IsValid && !string.IsNullOrEmpty(PreRelease);
    
    /// <summary>
    /// Gets the semantic version without prefix (e.g., "1.0.0" or "1.0.0-alpha")
    /// </summary>
    public string SemanticVersion => IsValid ? $"{Major}.{Minor}.{Patch}{(IsPreRelease ? $"-{PreRelease}" : "")}" : "-";
    
    /// <summary>
    /// Normalizes the version string to a consistent format
    /// </summary>
    private static string NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "unknown")
            return "-";
        
        var trimmed = value.Trim();
        
        // Already normalized empty
        if (trimmed == "-")
            return "-";
        
        // Only allow versions that match our regex pattern
        if (VersionRegex.IsMatch(trimmed))
            return trimmed;
        
        // Invalid format, treat as empty
        return "-";
    }
    
    /// <summary>
    /// Attempts to parse a version string
    /// </summary>
    public static bool TryParse(string? value, out PackageVersion version)
    {
        version = new PackageVersion(value);
        return version.IsValid || version.IsEmpty;
    }
    
    /// <summary>
    /// Attempts to parse a version string with format provider (IParsable implementation)
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out PackageVersion result)
    {
        return TryParse(s, out result);
    }
    
    /// <summary>
    /// Parses a version string, throwing an exception if invalid
    /// </summary>
    public static PackageVersion Parse(string value)
    {
        if (TryParse(value, out var version))
            return version;
        
        throw new ArgumentException($"Invalid version format: {value}", nameof(value));
    }
    
    /// <summary>
    /// Parses a version string with format provider (IParsable implementation)
    /// </summary>
    public static PackageVersion Parse(string s, IFormatProvider? provider)
    {
        return Parse(s);
    }
    
    /// <summary>
    /// Compares this version to another version
    /// </summary>
    public int CompareTo(PackageVersion other)
    {
        // Empty versions are always less than valid versions
        if (IsEmpty && other.IsEmpty) return 0;
        if (IsEmpty) return -1;
        if (other.IsEmpty) return 1;
        
        // Invalid versions are treated as empty
        if (!IsValid && !other.IsValid) return 0;
        if (!IsValid) return -1;
        if (!other.IsValid) return 1;
        
        // Compare major.minor.patch
        var majorComparison = Major.CompareTo(other.Major);
        if (majorComparison != 0) return majorComparison;
        
        var minorComparison = Minor.CompareTo(other.Minor);
        if (minorComparison != 0) return minorComparison;
        
        var patchComparison = Patch.CompareTo(other.Patch);
        if (patchComparison != 0) return patchComparison;
        
        // Handle pre-release versions
        if (IsPreRelease && !other.IsPreRelease) return -1; // Pre-release < release
        if (!IsPreRelease && other.IsPreRelease) return 1;  // Release > pre-release
        if (!IsPreRelease && !other.IsPreRelease) return 0; // Both are releases
        
        // Both are pre-releases, compare pre-release identifiers
        return string.Compare(PreRelease, other.PreRelease, StringComparison.OrdinalIgnoreCase);
    }
    
    /// <summary>
    /// Compares this version to another object
    /// </summary>
    public int CompareTo(object? obj)
    {
        if (obj is null) return 1;
        if (obj is PackageVersion other) return CompareTo(other);
        throw new ArgumentException($"Object is not a {nameof(PackageVersion)}", nameof(obj));
    }
    
    
    
    /// <summary>
    /// Returns the string representation of this version
    /// </summary>
    public override string ToString() => _value;
    
    /// <summary>
    /// Implicit conversion from string to PackageVersion
    /// </summary>
    public static implicit operator PackageVersion(string? value) => new(value);
    
    /// <summary>
    /// Implicit conversion from System.Version to PackageVersion
    /// </summary>
    public static implicit operator PackageVersion(Version? version) => new(version?.ToString());
    
    /// <summary>
    /// Implicit conversion from PackageVersion to string
    /// </summary>
    public static implicit operator string(PackageVersion version) => version._value;
    
    // Comparison operators
    public static bool operator >(PackageVersion left, PackageVersion right) => left.CompareTo(right) > 0;
    public static bool operator <(PackageVersion left, PackageVersion right) => left.CompareTo(right) < 0;
    public static bool operator >=(PackageVersion left, PackageVersion right) => left.CompareTo(right) >= 0;
    public static bool operator <=(PackageVersion left, PackageVersion right) => left.CompareTo(right) <= 0;
    
}