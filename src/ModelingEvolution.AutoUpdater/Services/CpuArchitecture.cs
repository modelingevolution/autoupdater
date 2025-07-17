using System.Collections.Immutable;

namespace ModelingEvolution.AutoUpdater.Services;

public readonly record struct CpuArchitecture(string Value)
{
    public static readonly CpuArchitecture X64 = new("x64");
    public static readonly CpuArchitecture X86 = new("x86");
    public static readonly CpuArchitecture Arm64 = new("arm64");
    public static readonly CpuArchitecture Arm = new("arm");

    public static readonly ImmutableHashSet<CpuArchitecture> All = [X64, X86, Arm64, Arm];

    public override string ToString() => Value;

    public static explicit operator CpuArchitecture(string value)
    {
        if (value.Contains("amd")) value = value.Replace("amd", "x");
        return new CpuArchitecture(value.ToLowerInvariant());
    }

    public static implicit operator string(CpuArchitecture cpuArchitecture) => cpuArchitecture.Value;

    public bool Equals(CpuArchitecture other) =>
        string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
}