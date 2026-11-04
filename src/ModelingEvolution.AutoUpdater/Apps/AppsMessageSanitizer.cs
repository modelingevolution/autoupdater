using System.Text.RegularExpressions;

namespace ModelingEvolution.AutoUpdater.Apps;

/// <summary>
/// Makes an update failure fit for a page anyone on the LAN can open: a docker or git error names registries, repository
/// URLs and addresses, and a URL may even carry a credential. The full text stays in the AutoUpdater's logs.
/// </summary>
public static partial class AppsMessageSanitizer
{
    private const int MaxLength = 300;

    [GeneratedRegex(@"\b[a-z][a-z0-9+.-]*://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    // key=value or key: value pairs that name a secret, with an optional Basic/Bearer scheme, and bare Basic/Bearer values.
    [GeneratedRegex(@"\b(?:password|passwd|pwd|token|secret|auth|authorization)\b\s*[=:]\s*(?:(?:basic|bearer)\s+)?\S+|\b(?:basic|bearer)\s+\S+", RegexOptions.IgnoreCase)]
    private static partial Regex Secret();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}(?::\d+)?\b")]
    private static partial Regex IpAddress();

    // A dotted host name, with or without a path after it. File names such as docker-compose.yml are left alone.
    [GeneratedRegex(@"\b(?:[a-z0-9-]+\.)+(?!ya?ml\b|sh\b|json\b|exe\b|apk\b|deb\b)[a-z]{2,}(?::\d+)?(?:/\S*)?", RegexOptions.IgnoreCase)]
    private static partial Regex HostName();

    public static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "The update failed. The AutoUpdater's log has the details.";

        var text = Url().Replace(message, "[address]");
        text = Secret().Replace(text, "[hidden]");
        text = IpAddress().Replace(text, "[address]");
        text = HostName().Replace(text, "[address]");
        text = text.Trim();

        return text.Length <= MaxLength ? text : text[..MaxLength] + "...";
    }
}
