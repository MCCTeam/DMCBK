using System.Globalization;

namespace DMCBK.Core.Commands.Impl;

/// <summary>
/// The <c>lang</c> command's user-facing text.
/// It lives beside the command rather than in the generated corpus because the corpus is produced from the archived 1.x resx and the 1.x client had no such command, so there is no legacy key to reuse; <c>AGENTS.md</c> names this file's neighbours as the home for exactly that case.
/// No em dashes.
/// </summary>
internal static class LangCommandText
{
    private static string F(string format, params object?[] args)
        => string.Format(CultureInfo.InvariantCulture, format, args);

    public const string Desc = "Show or set the language MCC reads and writes in.";

    public const string Usage = "lang [<tag>|auto]";

    /// <summary>The current UI language line, naming the configured value and what it resolved to.</summary>
    public static string Current(string configured, string resolved)
        => F("UI language: {0} (reading {1}).", configured, resolved);

    /// <summary>The locale announced to the server, which is a different axis from the UI language.</summary>
    public static string ServerLocale(string configured, string resolved)
        => F("Locale sent to the server: {0} (sending {1}).", configured, resolved);

    /// <summary>The header above the per-plugin translation rows.</summary>
    public static string PluginsHeader(int count) => F("Plugin translations ({0}):", count);

    /// <summary>One plugin's row: the tags it ships, the one in use, and how far behind English it is.</summary>
    public static string PluginRow(string id, string languages, string active, int missing)
        => missing == 0
            ? F("  {0}: ships {1}, reading {2}.", id, languages, active)
            : F("  {0}: ships {1}, reading {2}, {3} key(s) missing against en.", id, languages, active, missing);

    /// <summary>A plugin that ships no <c>lang/</c> folder at all.</summary>
    public static string PluginNoLanguages(string id) => F("  {0}: ships no translations.", id);

    /// <summary>Said when no plugin is loaded, so there is nothing to report on.</summary>
    public const string NoPlugins = "No plugin is loaded.";

    /// <summary>The refusal for a tag no installed culture matches.</summary>
    public static string UnknownTag(string tag)
        => F("'{0}' is not a language this system knows. Use a tag like 'de', 'pt-BR' or 'pt_br', or 'auto'"
            + " to follow the operating system.", tag);

    /// <summary>Said when the command has no configurations folder to write to.</summary>
    public const string NoConfig = "No configuration folder is known, so the language cannot be saved.";

    /// <summary>The write-back failure.</summary>
    public static string SaveFailed(string reason) => F("Could not save the language: {0}", reason);

    /// <summary>The success line, followed by <see cref="RegenHint"/>.</summary>
    public static string Changed(string configured, string resolved)
        => F("UI language set to {0} (reading {1}).", configured, resolved);

    /// <summary>
    /// The hint that follows a change.
    /// Settings comments are frozen at generation time by design, so the language does not reach a file that already exists until the user asks for it.
    /// </summary>
    public const string RegenHint =
        "Run 'plugins settings all regen' to rewrite plugin settings comments in the new language.";
}
