using FluentAssertions;
using ModelingEvolution.AutoUpdater.Models;
using System;
using Xunit;

namespace ModelingEvolution.AutoUpdater.Tests.Models
{
    public class BackupFilenameParserTests
    {
        [Theory]
        [InlineData("backup-20250126-143022.tar.gz", 2025, 1, 26, 14, 30, 22)]
        [InlineData("backup-20231215-093045.tar.gz", 2023, 12, 15, 9, 30, 45)]
        [InlineData("backup-20240101-000000.tar.gz", 2024, 1, 1, 0, 0, 0)]
        [InlineData("backup-20240630-235959.tar.gz", 2024, 6, 30, 23, 59, 59)]
        public void ParseDateFromFilename_ValidFilename_ReturnsCorrectDateTime(
            string filename, int year, int month, int day, int hour, int minute, int second)
        {
            // Act
            var result = BackupFilenameParser.ParseDateFromFilename(filename);

            // Assert
            result.Year.Should().Be(year);
            result.Month.Should().Be(month);
            result.Day.Should().Be(day);
            result.Hour.Should().Be(hour);
            result.Minute.Should().Be(minute);
            result.Second.Should().Be(second);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("   ")]
        [InlineData("invalid-filename.tar.gz")]
        [InlineData("backup-invalid-date.tar.gz")]
        [InlineData("backup-20251326-143022.tar.gz")] // Invalid month
        [InlineData("backup-20250132-143022.tar.gz")] // Invalid day
        [InlineData("backup-20250126-253022.tar.gz")] // Invalid hour
        [InlineData("backup-20250126-146022.tar.gz")] // Invalid minute
        [InlineData("backup-20250126-143062.tar.gz")] // Invalid second
        [InlineData("backup-20250126.tar.gz")] // Missing time part
        [InlineData("backup-143022.tar.gz")] // Missing date part
        [InlineData("backup-20250126-143022.zip")] // Wrong extension
        [InlineData("file-20250126-143022.tar.gz")] // Wrong prefix
        public void ParseDateFromFilename_InvalidFilename_ReturnsUnixEpoch(string filename)
        {
            // Act
            var result = BackupFilenameParser.ParseDateFromFilename(filename);

            // Assert
            result.Should().Be(DateTime.UnixEpoch);
        }

        [Fact]
        public void ParseDateFromFilename_LeapYear_HandlesCorrectly()
        {
            // Arrange
            var filename = "backup-20240229-120000.tar.gz"; // Feb 29, 2024 (leap year)

            // Act
            var result = BackupFilenameParser.ParseDateFromFilename(filename);

            // Assert
            result.Year.Should().Be(2024);
            result.Month.Should().Be(2);
            result.Day.Should().Be(29);
        }

        [Fact]
        public void ParseDateFromFilename_NonLeapYear_ReturnsUnixEpoch()
        {
            // Arrange
            var filename = "backup-20230229-120000.tar.gz"; // Feb 29, 2023 (not a leap year)

            // Act
            var result = BackupFilenameParser.ParseDateFromFilename(filename);

            // Assert
            result.Should().Be(DateTime.UnixEpoch);
        }

        [Fact]
        public void ParseDateFromFilename_EdgeCaseMinDate_ReturnsCorrectDateTime()
        {
            // Arrange
            var filename = "backup-00010101-000000.tar.gz";

            // Act
            var result = BackupFilenameParser.ParseDateFromFilename(filename);

            // Assert
            result.Year.Should().Be(1);
            result.Month.Should().Be(1);
            result.Day.Should().Be(1);
        }

        [Fact]
        public void ParseDateFromFilename_EdgeCaseMaxDate_ReturnsCorrectDateTime()
        {
            // Arrange
            var filename = "backup-99991231-235959.tar.gz";

            // Act
            var result = BackupFilenameParser.ParseDateFromFilename(filename);

            // Assert
            result.Year.Should().Be(9999);
            result.Month.Should().Be(12);
            result.Day.Should().Be(31);
        }
    }
}
