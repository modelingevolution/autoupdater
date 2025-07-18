using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelingEvolution.JsonParsableConverter;

namespace ModelingEvolution.AutoUpdater
{
    /// <summary>
    /// Strongly-typed package name to prevent wrong name assignments
    /// </summary>
    [JsonConverter(typeof(JsonParsableConverter<PackageName>))]
    [DebuggerDisplay("{_value}")]
    public readonly struct PackageName :  IParsable<PackageName>
    {
        private readonly string _value;
        public static readonly PackageName Empty = new PackageName("-");
        
        public PackageName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Package name cannot be null or empty", nameof(value));

            _value = value;
        }

        public static PackageName Parse(string s, IFormatProvider? provider = null)
        {
            if (string.IsNullOrWhiteSpace(s))
                throw new ArgumentException("Package name cannot be null or empty", nameof(s));
            
            return new PackageName(s);
        }

        public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out PackageName result)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                result = default;
                return false;
            }

            result = new PackageName(s);
            return true;
        }

        public static implicit operator string(PackageName packageName) => packageName._value;
        public static implicit operator PackageName(string value) => new(value);

        public override string ToString() => _value;
        public override int GetHashCode() => _value.GetHashCode();
        public override bool Equals(object? obj) => obj is PackageName other && Equals(other);
        public bool Equals(PackageName other) => string.Equals(_value, other._value, StringComparison.InvariantCultureIgnoreCase);

        public static bool operator ==(PackageName left, PackageName right) => left.Equals(right);
        public static bool operator !=(PackageName left, PackageName right) => !left.Equals(right);
    }

    
}