using System.Text.RegularExpressions;

namespace ModelingEvolution.AutoUpdater.Services;

static class StringExtensions
{
    /// <summary>
    /// Matches a Unix-style glob pattern like "*.txt", "foo?bar*", etc.
    /// </summary>
    public static bool Like(this string input, string globPattern, bool ignoreCase = true)
    {
        if (globPattern == null) throw new ArgumentNullException(nameof(globPattern));
        if (input == null) return false;

        var regexPattern = "^" + Regex.Escape(globPattern)
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".") + "$";

        var options = ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;

        return Regex.IsMatch(input, regexPattern, options);
    }
}