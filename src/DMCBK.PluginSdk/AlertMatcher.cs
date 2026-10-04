// AlertMatcher - the matching half of trigger-word alerts: plain substrings or .NET regexes over the full raw chat line.
// Pure and precompiled, so single-file plugins (which the test assembly cannot reference by type) still get tested logic: they call in, the suite pins it here.

using System.Text.RegularExpressions;

namespace DMCBK.PluginSdk;

/// <summary>One compiled trigger list: plain substrings or regexes, always case-insensitive.</summary>
public sealed class AlertMatcher
{
    private readonly List<string> _plain = [];
    private readonly List<Regex> _patterns = [];

    /// <summary>Pattern names that failed to compile, for one warning per bad entry.</summary>
    public IReadOnlyList<string> Errors { get; private set; } = [];

    private AlertMatcher()
    {
    }

    /// <summary>
    /// Builds a matcher.
    /// Blank entries are skipped.
    /// Bad regexes are skipped and reported in <see cref="Errors"/> instead of throwing on the session loop.
    /// </summary>
    public static AlertMatcher Build(IEnumerable<string>? entries, bool useRegex)
    {
        var matcher = new AlertMatcher();
        var errors = new List<string>();
        foreach (string entry in entries ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            if (!useRegex)
            {
                matcher._plain.Add(entry);
                continue;
            }

            try
            {
                matcher._patterns.Add(new Regex(
                    entry,
                    RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100)));
            }
            catch (ArgumentException)
            {
                errors.Add(entry);
            }
        }

        matcher.Errors = errors;
        return matcher;
    }

    /// <summary>True when anything in the list matches the line. A timed-out regex counts as no match.</summary>
    public bool IsMatch(string? line)
        => FirstMatch(line) is not null;

    /// <summary>The first matching entry (the pattern text, not the hit), or null when nothing matches.</summary>
    public string? FirstMatch(string? line)
    {
        if (string.IsNullOrEmpty(line))
            return null;

        foreach (string plain in _plain)
        {
            if (line.Contains(plain, StringComparison.OrdinalIgnoreCase))
                return plain;
        }

        foreach (Regex pattern in _patterns)
        {
            bool hit;
            try
            {
                hit = pattern.IsMatch(line);
            }
            catch (RegexMatchTimeoutException)
            {
                hit = false;
            }

            if (hit)
                return pattern.ToString();
        }

        return null;
    }
}
