namespace ModelingEvolution.AutoUpdater.Apps;

/// <summary>
/// What the Apps page says about a failed update. Anyone on the LAN can open that page, and a docker, git or ssh error names
/// registries, repositories, organisations, hosts (with or without a dot), addresses and sometimes credentials, in more
/// shapes than a pattern can recognise. So the raw text is never shown: it is mapped to one fixed sentence, and the full
/// text stays in the AutoUpdater's log, which the Packages page already shows to the operator.
/// </summary>
public static class AppsFailureText
{
    private const string SeeLog = " The AutoUpdater's log has the details.";

    private static readonly (string Text, string[] Signs)[] Categories =
    [
        ("The station ran out of disk space.", ["no space left", "disk full", "disk quota"]),
        ("Another update is already running.", ["already in progress"]),
        ("The registry or repository refused this station's credentials.",
            ["unauthorized", "authentication required", "permission denied", "access denied", "denied", "forbidden", "login"]),
        ("The release was not found in the registry.", ["manifest unknown", "name unknown", "no such image", "not found"]),
        ("The station could not reach the registry or the network.",
            ["timeout", "timed out", "connection refused", "connection reset", "no such host", "could not resolve",
             "temporary failure", "network", "unreachable", "dial tcp", "i/o", "eof"]),
    ];

    public static string Describe(string? failure)
    {
        var text = failure?.ToLowerInvariant() ?? string.Empty;
        foreach (var (sentence, signs) in Categories)
        {
            if (signs.Any(text.Contains)) return sentence + SeeLog;
        }

        return "The update failed." + SeeLog;
    }
}
