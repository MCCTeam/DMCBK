using System.Text.RegularExpressions;

namespace DMCBK.PluginSdk;

/// <summary>How much a validation finding matters.</summary>
public enum PluginValidationSeverity
{
    /// <summary>The plugin will not load, or will print key names instead of text.</summary>
    Error,

    /// <summary>The plugin works; something an author would want to know is off.</summary>
    Warning,
}

/// <summary>One validation finding, located in the file it is about.</summary>
/// <param name="Severity">Whether this stops the plugin working.</param>
/// <param name="File">The file the problem is in, relative to the plugin folder, or empty for the folder itself.</param>
/// <param name="Line">The one-based line, or 0 when the problem is not on a line.</param>
/// <param name="Message">The already-localized description.</param>
public sealed record PluginValidationProblem(
    PluginValidationSeverity Severity, string File, int Line, string Message);

/// <summary>What <c>plugins validate</c> found in one plugin folder.</summary>
/// <param name="Folder">The folder that was checked.</param>
/// <param name="Id">The manifest id, or null when the manifest could not be read.</param>
/// <param name="Problems">Everything found, manifest problems first.</param>
public sealed record PluginValidationReport(
    string Folder, string? Id, IReadOnlyList<PluginValidationProblem> Problems)
{
    /// <summary>True when nothing of <see cref="PluginValidationSeverity.Error"/> severity was found.</summary>
    public bool IsValid => !Problems.Any(p => p.Severity == PluginValidationSeverity.Error);

    /// <summary>How many findings are errors.</summary>
    public int ErrorCount => Problems.Count(p => p.Severity == PluginValidationSeverity.Error);

    /// <summary>How many findings are warnings.</summary>
    public int WarningCount => Problems.Count(p => p.Severity == PluginValidationSeverity.Warning);
}

/// <summary>
/// Checks a plugin folder the way the loader and the language table will read it, without loading anything.
/// It answers the questions a plugin author cannot answer by running the plugin, because the failures are silent: a missing string resolves to its own key, and a key written under a table header resolves to a dotted name the code never asks for.
/// </summary>
public static class PluginValidator
{
    // A localization receiver is either something named ...Strings, or a local that was assigned from one.
    private static readonly Regex StringsAlias = new(
        @"(?<alias>\b[A-Za-z_][A-Za-z0-9_]*)\s*=\s*[A-Za-z_][A-Za-z0-9_.]*\.Strings\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Anything declared as the localization type: a field, a local, a parameter.
    private static readonly Regex StringsDeclaration = new(
        @"\bIPluginLocalization\??\s+(?<alias>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // The null test a nullable field is read through: `_strings is { } strings`.
    private static readonly Regex StringsPattern = new(
        @"\b(?<source>[A-Za-z_][A-Za-z0-9_]*)\s+is\s*\{\s*\}\s*(?<alias>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Placeholder = new(
        @"\$(?<key>[A-Za-z0-9_][A-Za-z0-9_.\-]*)\$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // The expander only resolves comments Tomlet wrote from these attributes, so a $word$ anywhere else in a source file is ordinary text.
    private static readonly Regex CommentAttribute = new(
        @"\[Toml(?:Inline|Preceding)Comment\s*\(\s*""(?<text>[^""]*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] IgnoredFolders = ["bin", "obj", ".cache", "data"];

    /// <summary>Validates the plugin folder at <paramref name="folder"/>.</summary>
    public static PluginValidationReport Validate(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        string full = Path.GetFullPath(folder);
        var problems = new List<PluginValidationProblem>();

        if (!Directory.Exists(full))
        {
            problems.Add(Error(string.Empty, 0, PluginStrings.PathNotFound(folder)));
            return new PluginValidationReport(full, null, problems);
        }

        string manifestPath = Path.Combine(full, PluginManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            problems.Add(Error(PluginManifest.FileName, 0, PluginStrings.ManifestMissing(full)));
            return new PluginValidationReport(full, null, problems);
        }

        if (!PluginManifest.TryParse(File.ReadAllText(manifestPath), out PluginManifest manifest, out string? error))
        {
            problems.Add(Error(PluginManifest.FileName, 0, error ?? PluginStrings.ManifestInvalid("unreadable")));
            return new PluginValidationReport(full, null, problems);
        }

        CheckManifest(full, manifest, problems);
        IReadOnlyList<PluginLangFile> languages = ReadLanguages(full, problems);
        CheckSources(full, manifest, languages, problems);
        return new PluginValidationReport(full, manifest.Id, problems);
    }

    private static void CheckManifest(string folder, PluginManifest manifest, List<PluginValidationProblem> problems)
    {
        if (!File.Exists(Path.Combine(folder, manifest.Entry)))
            problems.Add(Error(PluginManifest.FileName, 0, PluginStrings.EntryMissing(manifest.Entry)));

        if (!PluginApiVersion.IsCompatible(manifest.ApiVersion))
        {
            problems.Add(Error(
                PluginManifest.FileName,
                0,
                PluginStrings.ApiIncompatible(manifest.Id, manifest.ApiVersion, PluginApiVersion.Current)));
        }

        if (!manifest.DmcbkRange.IsAny && !manifest.DmcbkRange.Satisfies(DMCBK.Core.DmcbkVersion.Current))
        {
            problems.Add(Error(
                PluginManifest.FileName,
                0,
                PluginStrings.MccIncompatible(manifest.Id, manifest.DmcbkRange.Text, DMCBK.Core.DmcbkVersion.Current)));
        }

        if (!manifest.UmpkRange.IsAny && !manifest.UmpkRange.Satisfies(UmpkVersionProbe.Version))
        {
            problems.Add(Error(
                PluginManifest.FileName,
                0,
                PluginStrings.UmpkIncompatible(manifest.Id, manifest.UmpkRange.Text, UmpkVersionProbe.Current)));
        }

        foreach (string topic in manifest.Man)
        {
            string id = Path.GetFileNameWithoutExtension(topic.Trim());
            if (id.Length == 0)
                continue;

            bool present = File.Exists(Path.Combine(folder, "man", "en", id + ".md"))
                || File.Exists(Path.Combine(folder, "man", id + ".md"));
            if (!present)
                problems.Add(Warning(PluginManifest.FileName, 0, PluginStrings.ManPageMissing(id)));
        }
    }

    /// <summary>
    /// Reads <c>lang/</c>.
    /// English is the table every other language falls back to and the one the missing-key report is measured against, so a folder that ships other languages and not English has no floor.
    /// </summary>
    private static IReadOnlyList<PluginLangFile> ReadLanguages(
        string folder, List<PluginValidationProblem> problems)
    {
        string root = Path.Combine(folder, PluginLocalization.FolderName);
        if (!Directory.Exists(root))
            return [];

        var files = new List<PluginLangFile>();
        foreach (string path in Directory.EnumerateFiles(root, "*.toml").Order(StringComparer.Ordinal))
        {
            PluginLangFile file = PluginLangFile.Read(path);
            files.Add(file);
            if (file.ParseError is { Length: > 0 } parseError)
                problems.Add(Error(Relative(folder, path), 0, PluginStrings.LangUnparsable(file.Language, parseError)));
        }

        if (files.Count > 0 && !files.Any(f => f.Language.Equals("en", StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add(Error(
                Path.Combine(PluginLocalization.FolderName, "en.toml"),
                0,
                PluginStrings.LangEnglishMissing([.. files.Select(f => f.Language)])));
        }

        return files;
    }

    /// <summary>
    /// The two silent failures, both decided by reading the sources: a key the code asks for bare that the language file nested under a table header, and a settings comment placeholder no language file answers.
    /// </summary>
    private static void CheckSources(
        string folder,
        PluginManifest manifest,
        IReadOnlyList<PluginLangFile> languages,
        List<PluginValidationProblem> problems)
    {
        PluginLangFile? english = languages.FirstOrDefault(
            f => f.Language.Equals("en", StringComparison.OrdinalIgnoreCase));
        if (english is null)
            return;

        var read = new HashSet<string>(StringComparer.Ordinal);
        var placeholders = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in Sources(folder))
        {
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            CollectKeys(text, read);
            foreach (Match attribute in CommentAttribute.Matches(text))
            {
                foreach (Match match in Placeholder.Matches(attribute.Groups["text"].Value))
                    placeholders.Add(match.Groups["key"].Value);
            }
        }

        string langFile = Relative(folder, english.Path);
        foreach (string key in read.Order(StringComparer.Ordinal))
        {
            if (english.Table.ContainsKey(key))
                continue;

            // The key IS in the file, one table header too far down, so it resolves as "<table>.<key>"
            // and the plugin prints the bare name it asked for.
            LangKey nested = english.Keys.FirstOrDefault(
                k => k.Table is not null && string.Equals(k.Name, key, StringComparison.Ordinal));
            if (nested.Table is { Length: > 0 } table)
                problems.Add(Error(langFile, nested.Line, PluginStrings.LangKeyNested(key, table)));
            else
                problems.Add(Warning(langFile, 0, PluginStrings.LangKeyMissing(key)));
        }

        foreach (string key in placeholders.Order(StringComparer.Ordinal))
        {
            if (english.Table.ContainsKey(key))
                continue;

            problems.Add(Error(langFile, 0, PluginStrings.LangPlaceholderMissing(key, manifest.Id)));
        }
    }

    /// <summary>Every <c>.cs</c> file that is part of the plugin, skipping build output and its data folder.</summary>
    private static IEnumerable<string> Sources(string folder)
    {
        foreach (string path in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(folder, path);
            string[] parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!parts.Any(p => IgnoredFolders.Contains(p, StringComparer.OrdinalIgnoreCase)))
                yield return path;
        }
    }

    /// <summary>The keys a source file resolves through its plugin's own string table.</summary>
    private static void CollectKeys(string text, HashSet<string> into)
    {
        var receivers = new HashSet<string>(StringComparer.Ordinal) { "Strings" };
        foreach (Match match in StringsAlias.Matches(text))
            receivers.Add(match.Groups["alias"].Value);

        foreach (Match match in StringsDeclaration.Matches(text))
            receivers.Add(match.Groups["alias"].Value);

        // A pattern names a second identifier for a receiver already known, so this runs last.
        foreach (Match match in StringsPattern.Matches(text))
        {
            if (receivers.Contains(match.Groups["source"].Value))
                receivers.Add(match.Groups["alias"].Value);
        }

        foreach (string receiver in receivers)
        {
            var calls = new Regex(
                $@"\b{Regex.Escape(receiver)}\s*\.\s*(?:Get|Format)\s*\(\s*""(?<key>[^""]+)""",
                RegexOptions.CultureInvariant);
            foreach (Match match in calls.Matches(text))
                into.Add(match.Groups["key"].Value);
        }
    }

    private static string Relative(string folder, string path) => Path.GetRelativePath(folder, path);

    private static PluginValidationProblem Error(string file, int line, string message)
        => new(PluginValidationSeverity.Error, file, line, message);

    private static PluginValidationProblem Warning(string file, int line, string message)
        => new(PluginValidationSeverity.Warning, file, line, message);
}
