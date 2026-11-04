using System.Text.RegularExpressions;

namespace ModelingEvolution.AutoUpdater.Apps;

/// <summary>
/// What the Apps page says about a failed update. Anyone on the LAN can open that page, and a docker, git or ssh error names
/// registries, repositories, organisations, hosts (with or without a dot), addresses and sometimes credentials, in more
/// shapes than a pattern can recognise. So the raw text is never shown: it is mapped to one fixed sentence, and the full
/// text stays in the AutoUpdater's log, which the Packages page already shows to the operator.
/// </summary>
/// <remarks>
/// A sentence names a cause only for a signal that has no other meaning. Everything else (a local permission, SSH to the
/// host, a missing command or network, a command that timed out) gets the neutral sentence: a confident wrong cause
/// would send the operator down the wrong path.
/// </remarks>
public static class AppsFailureText
{
    private const string SeeLog = " Details are in the AutoUpdater log.";
    public const string Neutral = "The update failed." + SeeLog;

    private static readonly (string Text, Regex Signal)[] Categories =
    [
        ("The station ran out of disk space.", Signal(@"no space left on device|disk quota exceeded")),
        ("Another update is already running.", Signal(@"already in progress")),
        // A registry's 401. Checked before "not found": some registries answer an unauthorised pull with both.
        ("The registry refused this station's credentials.", Signal(@"\b401 unauthorized\b|\bunauthorized: ")),
        // The registry's own codes for a tag or repository it does not have.
        ("The release was not found in the registry.", Signal(@"\bmanifest unknown\b|\bname unknown\b|\bmanifest for \S+ not found\b")),
        // A failed DNS lookup or a refused TCP connection, as Docker's registry client reports them (a socket error to the
        // local daemon says "dial unix", and SSH reports its own way).
        ("The station could not reach the registry.",
            Signal(@"\bdial tcp\b.*(no such host|connection refused|server misbehaving|temporary failure in name resolution)")),
    ];

    private static Regex Signal(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromMilliseconds(100));

    public static string Describe(string? failure)
    {
        if (string.IsNullOrWhiteSpace(failure)) return Neutral;
        foreach (var (sentence, signal) in Categories)
        {
            try
            {
                if (signal.IsMatch(failure)) return sentence + SeeLog;
            }
            catch (RegexMatchTimeoutException)
            {
                return Neutral;
            }
        }

        return Neutral;
    }
}
