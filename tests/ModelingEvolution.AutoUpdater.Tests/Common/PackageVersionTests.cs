using System;
using FluentAssertions;
using ModelingEvolution.AutoUpdater.Common;
using Xunit;

namespace ModelingEvolution.AutoUpdater.Tests.Common;

public class PackageVersionTests
{
    public class ConstructorTests
    {
        [Theory]
        [InlineData("v1.0.0", "v1.0.0")]
        [InlineData("1.0.0", "1.0.0")]
        [InlineData("v1.2.3-alpha", "v1.2.3-alpha")]
        [InlineData("1.2.3-beta.1", "1.2.3-beta.1")]
        [InlineData("v10.20.30", "v10.20.30")]
        public void Should_Accept_Valid_Version_Formats(string input, string expected)
        {
            var version = new PackageVersion(input);
            
            version.ToString().Should().Be(expected);
            version.IsValid.Should().BeTrue();
            version.IsEmpty.Should().BeFalse();
        }

        [Theory]
        [InlineData("", "-")]
        [InlineData("   ", "-")]
        [InlineData("unknown", "-")]
        [InlineData("UNKNOWN", "-")]
        [InlineData("-", "-")]
        public void Should_Normalize_Invalid_Inputs_To_Empty(string input, string expected)
        {
            var version = new PackageVersion(input);
            
            version.ToString().Should().Be(expected);
            version.IsEmpty.Should().BeTrue();
            version.IsValid.Should().BeFalse();
        }

        [Theory]
        [InlineData("version-2.1.0")]
        [InlineData("1.0")]
        [InlineData("1")]
        [InlineData("v1")]
        [InlineData("v1.0")]
        [InlineData("abc")]
        [InlineData("1.0.0.0")]
        [InlineData("1.0.0-")]
        [InlineData("1.0.0-.")]
        public void Should_Treat_Invalid_Formats_As_Empty(string input)
        {
            var version = new PackageVersion(input);
            
            version.ToString().Should().Be("-");
            version.IsEmpty.Should().BeTrue();
            version.IsValid.Should().BeFalse();
        }

        [Fact]
        public void Should_Handle_Null_Input()
        {
            var version = new PackageVersion(null);
            
            version.ToString().Should().Be("-");
            version.IsEmpty.Should().BeTrue();
            version.IsValid.Should().BeFalse();
        }
    }

    public class PropertyTests
    {
        [Theory]
        [InlineData("v1.2.3", 1, 2, 3, "", true)]
        [InlineData("1.2.3", 1, 2, 3, "", false)]
        [InlineData("v10.20.30-alpha", 10, 20, 30, "alpha", true)]
        [InlineData("0.0.1-rc.1", 0, 0, 1, "rc.1", false)]
        public void Should_Parse_Version_Components_Correctly(string input, int major, int minor, int patch, string preRelease, bool hasVPrefix)
        {
            var version = new PackageVersion(input);
            
            version.Major.Should().Be(major);
            version.Minor.Should().Be(minor);
            version.Patch.Should().Be(patch);
            version.PreRelease.Should().Be(string.IsNullOrEmpty(preRelease) ? null : preRelease);
            version.HasVPrefix.Should().Be(hasVPrefix);
            version.IsPreRelease.Should().Be(!string.IsNullOrEmpty(preRelease));
        }

        [Theory]
        [InlineData("v1.2.3", "1.2.3")]
        [InlineData("1.2.3", "1.2.3")]
        [InlineData("v1.2.3-alpha", "1.2.3-alpha")]
        [InlineData("1.2.3-beta.1", "1.2.3-beta.1")]
        public void SemanticVersion_Should_Return_Version_Without_Prefix(string input, string expected)
        {
            var version = new PackageVersion(input);
            
            version.SemanticVersion.Should().Be(expected);
        }

        [Fact]
        public void Empty_Version_Should_Have_Zero_Components()
        {
            var version = PackageVersion.Empty;
            
            version.Major.Should().Be(0);
            version.Minor.Should().Be(0);
            version.Patch.Should().Be(0);
            version.PreRelease.Should().BeNull();
            version.HasVPrefix.Should().BeFalse();
            version.SemanticVersion.Should().Be("-");
        }
    }

    public class ComparisonTests
    {
        [Theory]
        [InlineData("1.0.0", "2.0.0", -1)]
        [InlineData("2.0.0", "1.0.0", 1)]
        [InlineData("1.0.0", "1.0.0", 0)]
        [InlineData("1.0.0", "1.1.0", -1)]
        [InlineData("1.1.0", "1.0.0", 1)]
        [InlineData("1.0.0", "1.0.1", -1)]
        [InlineData("1.0.1", "1.0.0", 1)]
        [InlineData("v1.0.0", "1.0.0", 0)]
        [InlineData("v1.2.3", "v1.2.3", 0)]
        public void CompareTo_Should_Compare_Versions_Correctly(string left, string right, int expected)
        {
            var v1 = new PackageVersion(left);
            var v2 = new PackageVersion(right);
            
            var result = v1.CompareTo(v2);
            
            result.Should().Be(expected);
        }

        [Theory]
        [InlineData("1.0.0-alpha", "1.0.0", -1)]
        [InlineData("1.0.0", "1.0.0-alpha", 1)]
        [InlineData("1.0.0-alpha", "1.0.0-beta", -1)]
        [InlineData("1.0.0-beta", "1.0.0-alpha", 1)]
        [InlineData("1.0.0-rc.1", "1.0.0-rc.2", -1)]
        public void CompareTo_Should_Handle_PreRelease_Versions(string left, string right, int expected)
        {
            var v1 = new PackageVersion(left);
            var v2 = new PackageVersion(right);
            
            var result = v1.CompareTo(v2);
            
            result.Should().Be(expected);
        }

        [Theory]
        [InlineData("-", "1.0.0", -1)]
        [InlineData("1.0.0", "-", 1)]
        [InlineData("-", "-", 0)]
        [InlineData("invalid", "1.0.0", -1)]
        [InlineData("1.0.0", "invalid", 1)]
        [InlineData("invalid", "invalid", 0)]
        public void CompareTo_Should_Handle_Empty_And_Invalid_Versions(string left, string right, int expected)
        {
            var v1 = new PackageVersion(left);
            var v2 = new PackageVersion(right);
            
            var result = v1.CompareTo(v2);
            
            result.Should().Be(expected);
        }

        [Fact]
        public void CompareTo_Object_Should_Handle_Null()
        {
            var version = new PackageVersion("1.0.0");
            
            var result = version.CompareTo((object?)null);
            
            result.Should().Be(1);
        }

        [Fact]
        public void CompareTo_Object_Should_Throw_For_Wrong_Type()
        {
            var version = new PackageVersion("1.0.0");
            
            var act = () => version.CompareTo(123);
            
            act.Should().Throw<ArgumentException>();
        }
    }

    public class OperatorTests
    {
        [Theory]
        [InlineData("2.0.0", "1.0.0", true)]
        [InlineData("1.0.0", "2.0.0", false)]
        [InlineData("1.0.0", "1.0.0", false)]
        public void GreaterThan_Operator_Should_Work(string left, string right, bool expected)
        {
            var v1 = new PackageVersion(left);
            var v2 = new PackageVersion(right);
            
            (v1 > v2).Should().Be(expected);
        }

        [Theory]
        [InlineData("1.0.0", "2.0.0", true)]
        [InlineData("2.0.0", "1.0.0", false)]
        [InlineData("1.0.0", "1.0.0", false)]
        public void LessThan_Operator_Should_Work(string left, string right, bool expected)
        {
            var v1 = new PackageVersion(left);
            var v2 = new PackageVersion(right);
            
            (v1 < v2).Should().Be(expected);
        }

        [Theory]
        [InlineData("1.0.0", "1.0.0", true)]
        [InlineData("v1.0.0", "1.0.0", true)]  // Semantic equality
        [InlineData("1.0.0", "2.0.0", false)]
        public void Equality_Operators_Should_Work(string left, string right, bool expected)
        {
            var v1 = new PackageVersion(left);
            var v2 = new PackageVersion(right);
            
            (v1 == v2).Should().Be(expected);
            (v1 != v2).Should().Be(!expected);
        }

        [Theory]
        [InlineData("2.0.0", "1.0.0", true)]
        [InlineData("1.0.0", "1.0.0", true)]
        [InlineData("1.0.0", "2.0.0", false)]
        public void GreaterThanOrEqual_Operator_Should_Work(string left, string right, bool expected)
        {
            var v1 = new PackageVersion(left);
            var v2 = new PackageVersion(right);
            
            (v1 >= v2).Should().Be(expected);
        }

        [Theory]
        [InlineData("1.0.0", "2.0.0", true)]
        [InlineData("1.0.0", "1.0.0", true)]
        [InlineData("2.0.0", "1.0.0", false)]
        public void LessThanOrEqual_Operator_Should_Work(string left, string right, bool expected)
        {
            var v1 = new PackageVersion(left);
            var v2 = new PackageVersion(right);
            
            (v1 <= v2).Should().Be(expected);
        }
    }

    public class OperatorUsageTests
    {
        [Theory]
        [InlineData("2.0.0", "1.0.0", true)]
        [InlineData("1.0.0", "2.0.0", false)]
        [InlineData("1.0.0", "1.0.0", false)]
        [InlineData("1.0.1", "1.0.0", true)]
        [InlineData("1.0.0", "1.0.0-alpha", true)]
        public void GreaterThan_Should_Work_For_Newer_Versions(string left, string right, bool expected)
        {
            var v1 = new PackageVersion(left);
            var v2 = new PackageVersion(right);
            
            (v1 > v2).Should().Be(expected);
        }

        [Theory]
        [InlineData("1.0.0", "2.0.0", true)]
        [InlineData("2.0.0", "1.0.0", false)]
        [InlineData("1.0.0", "1.0.0", false)]
        [InlineData("1.0.0", "1.0.1", true)]
        [InlineData("1.0.0-alpha", "1.0.0", true)]
        public void LessThan_Should_Work_For_Older_Versions(string left, string right, bool expected)
        {
            var v1 = new PackageVersion(left);
            var v2 = new PackageVersion(right);
            
            (v1 < v2).Should().Be(expected);
        }

        [Theory]
        [InlineData("1.0.0", "1.0.0", true)]
        [InlineData("v1.0.0", "1.0.0", true)]  // Semantic equality
        [InlineData("1.0.0", "2.0.0", false)]
        [InlineData("1.0.0-alpha", "1.0.0-alpha", true)]
        public void Equality_Should_Work_For_Same_Versions(string left, string right, bool expected)
        {
            var v1 = new PackageVersion(left);
            var v2 = new PackageVersion(right);
            
            (v1 == v2).Should().Be(expected);
        }
    }

    public class ParsingTests
    {
        [Theory]
        [InlineData("v1.0.0")]
        [InlineData("1.2.3")]
        [InlineData("10.20.30-beta")]
        [InlineData("-")]
        public void TryParse_Should_Return_True_For_Valid_Versions(string input)
        {
            var result = PackageVersion.TryParse(input, out var version);
            
            result.Should().BeTrue();
            version.ToString().Should().Be(input);
        }

        [Theory]
        [InlineData("invalid")]
        [InlineData("1.0")]
        [InlineData("version-1.0.0")]
        public void TryParse_Should_Return_True_But_Create_Empty_For_Invalid_Versions(string input)
        {
            var result = PackageVersion.TryParse(input, out var version);
            
            result.Should().BeTrue();
            version.IsEmpty.Should().BeTrue();
        }

        [Theory]
        [InlineData("v1.0.0")]
        [InlineData("1.2.3")]
        [InlineData("-")]
       
        public void Parse_Should_Return_Version_For_Valid_Input(string input)
        {
            var version = PackageVersion.Parse(input);
            
            version.ToString().Should().Be(input);
        }
        [Theory]
        [InlineData(null)]
        [InlineData("-")]
        [InlineData("")]
        public void Parse_Should_Return_Empty(string input)
        {
            var version = PackageVersion.Parse(input);

            version.ToString().Should().Be("-");
            version.IsEmpty.Should().BeTrue();
            version.IsValid.Should().BeFalse();
        }
        [Theory]
        [InlineData("1.0.0")]
        [InlineData("0.0.0")]
        [InlineData("0.0.1")]
        public void AnythingIsHigher_Than_Empty(string something)
        {
            var version = PackageVersion.Parse(something);

            (version > PackageVersion.Empty).Should().BeTrue();
            (PackageVersion.Empty < version).Should().BeTrue();
            
        }
        [Fact]
        public void Parse_Should_Return_Empty_For_Invalid_Input()
        {
            var version = PackageVersion.Parse("invalid");
            
            version.IsEmpty.Should().BeTrue();
        }

        [Theory]
        [InlineData("v1.0.0")]
        [InlineData("1.2.3")]
        public void IParsable_Methods_Should_Work(string input)
        {
            var version = PackageVersion.Parse(input, null);
            version.ToString().Should().Be(input);
            
            var result = PackageVersion.TryParse(input, null, out var parsed);
            result.Should().BeTrue();
            parsed.ToString().Should().Be(input);
        }
    }

    public class ImplicitConversionTests
    {
        [Fact]
        public void Should_Convert_From_String_Implicitly()
        {
            PackageVersion version = "v1.0.0";
            
            version.ToString().Should().Be("v1.0.0");
            version.IsValid.Should().BeTrue();
        }

        [Fact]
        public void Should_Convert_To_String_Implicitly()
        {
            var version = new PackageVersion("v1.0.0");
            
            string versionString = version;
            
            versionString.Should().Be("v1.0.0");
        }

        [Fact]
        public void Should_Convert_From_System_Version()
        {
            var systemVersion = new Version(1, 2, 3);
            
            PackageVersion packageVersion = systemVersion;
            
            packageVersion.ToString().Should().Be("1.2.3");
            packageVersion.Major.Should().Be(1);
            packageVersion.Minor.Should().Be(2);
            packageVersion.Patch.Should().Be(3);
        }

        [Fact]
        public void Should_Convert_From_Null_System_Version()
        {
            Version systemVersion = null;
            
            PackageVersion packageVersion = systemVersion;
            
            packageVersion.IsEmpty.Should().BeTrue();
            packageVersion.ToString().Should().Be("-");
        }

        [Fact]
        public void Should_Convert_From_Null_String()
        {
            string versionString = null;
            
            PackageVersion packageVersion = versionString;
            
            packageVersion.IsEmpty.Should().BeTrue();
            packageVersion.ToString().Should().Be("-");
        }
    }

    public class RecordEqualityTests
    {
        [Fact]
        public void Should_Be_Equal_When_Same_Value()
        {
            var v1 = new PackageVersion("v1.0.0");
            var v2 = new PackageVersion("v1.0.0");
            
            v1.Should().Be(v2);
            v1.GetHashCode().Should().Be(v2.GetHashCode());
        }

        [Fact]
        public void Should_Not_Be_Equal_When_Different_Value()
        {
            var v1 = new PackageVersion("v1.0.0");
            var v2 = new PackageVersion("v1.0.1");
            
            v1.Should().NotBe(v2);
        }

        [Fact]
        public void Should_Be_Equal_When_Same_Semantic_Version()
        {
            var v1 = new PackageVersion("v1.0.0");
            var v2 = new PackageVersion("1.0.0");
            
            // They are equal for both comparison and record equality (semantic equality)
            (v1 == v2).Should().BeTrue();
            v1.Equals(v2).Should().BeTrue();
            v1.GetHashCode().Should().Be(v2.GetHashCode());
        }
    }

    public class EdgeCaseTests
    {
        [Fact]
        public void Should_Handle_Very_Large_Version_Numbers()
        {
            var version = new PackageVersion("v999.888.777");
            
            version.IsValid.Should().BeTrue();
            version.Major.Should().Be(999);
            version.Minor.Should().Be(888);
            version.Patch.Should().Be(777);
        }

        [Fact]
        public void Should_Handle_Zero_Versions()
        {
            var version = new PackageVersion("0.0.0");
            
            version.IsValid.Should().BeTrue();
            version.Major.Should().Be(0);
            version.Minor.Should().Be(0);
            version.Patch.Should().Be(0);
        }

        [Theory]
        [InlineData("v1.0.0-")]
        [InlineData("1.0.0-")]
        [InlineData("1.0.0-.")]
        public void Should_Reject_Invalid_PreRelease_Formats(string input)
        {
            var version = new PackageVersion(input);
            
            version.IsEmpty.Should().BeTrue();
            version.IsValid.Should().BeFalse();
        }

        [Theory]
        [InlineData(" v1.0.0 ", "v1.0.0")]
        [InlineData("\tv1.0.0\t", "v1.0.0")]
        [InlineData("\nv1.0.0\n", "v1.0.0")]
        public void Should_Trim_Whitespace(string input, string expected)
        {
            var version = new PackageVersion(input);
            
            version.ToString().Should().Be(expected);
            version.IsValid.Should().BeTrue();
        }
    }
}