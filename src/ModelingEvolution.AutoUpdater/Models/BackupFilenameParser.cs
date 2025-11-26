using System;
using System.Text.RegularExpressions;

namespace ModelingEvolution.AutoUpdater.Models
{
    /// <summary>
    /// Helper class for parsing backup filenames
    /// </summary>
    public static class BackupFilenameParser
    {
        private static readonly Regex FilenameRegex = new(@"backup-(\d{8})-(\d{6})\.tar\.gz", RegexOptions.Compiled);

        /// <summary>
        /// Parses the creation date from a backup filename.
        /// Expected format: backup-YYYYMMDD-HHMMSS.tar.gz
        /// Example: backup-20250126-143022.tar.gz
        /// </summary>
        /// <param name="filename">The backup filename</param>
        /// <returns>The parsed DateTime, or DateTime.UnixEpoch if parsing fails</returns>
        public static DateTime ParseDateFromFilename(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename))
            {
                return DateTime.UnixEpoch;
            }

            try
            {
                var match = FilenameRegex.Match(filename);

                if (match.Success)
                {
                    var dateStr = match.Groups[1].Value; // YYYYMMDD
                    var timeStr = match.Groups[2].Value; // HHMMSS

                    var year = int.Parse(dateStr.Substring(0, 4));
                    var month = int.Parse(dateStr.Substring(4, 2));
                    var day = int.Parse(dateStr.Substring(6, 2));
                    var hour = int.Parse(timeStr.Substring(0, 2));
                    var minute = int.Parse(timeStr.Substring(2, 2));
                    var second = int.Parse(timeStr.Substring(4, 2));

                    return new DateTime(year, month, day, hour, minute, second);
                }
            }
            catch
            {
                // Invalid format or date components
            }

            return DateTime.UnixEpoch;
        }
    }
}
