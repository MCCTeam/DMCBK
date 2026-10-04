using System.Globalization;

namespace DMCBK.Core.Manual;

/// <summary>
/// Somewhere manual pages come from.
/// The built-in pages are embedded resources; a plugin's pages are files in its own folder.
/// Both answer the same two questions, so <see cref="ManualCatalog"/> does not care which it is talking to.
/// </summary>
public interface IManualSource
{
    /// <summary>The topics this source provides, in the order it wants them listed.</summary>
    IReadOnlyList<ManualTopic> Topics { get; }

    /// <summary>
    /// The Markdown for a topic in a specific LANGUAGE (not a culture: the caller has already walked the culture chain).
    /// Null when this source does not have that topic in that language.
    /// </summary>
    /// <param name="topicId">The topic id.</param>
    /// <param name="language">A language tag such as <c>en</c> or <c>pt-BR</c>.</param>
    string? Read(string topicId, string language);

    /// <summary>Every language this source ships at least one page in.</summary>
    IReadOnlyList<string> Languages { get; }
}

/// <summary>
/// A manual source reading per-language Markdown out of a directory tree.
/// <para>
/// This is what a plugin gets.
/// Pages live at <c>&lt;root&gt;/&lt;lang&gt;/&lt;topic&gt;.md</c>, the same shape the built-in pages use, so translating a plugin's manual is the same job as translating MCC's: copy the <c>en</c> folder, translate the files, ship them next to the plugin.
/// A flat <c>&lt;root&gt;/&lt;topic&gt;.md</c> is also accepted and treated as English, so a plugin with one language does not have to nest a folder to say so.
/// </para>
/// </summary>
public sealed class DirectoryManualSource : IManualSource
{
    private const string FallbackLanguage = "en";

    private readonly string _root;

    /// <summary>Creates a source over a directory of pages.</summary>
    /// <param name="root">The <c>man</c> directory.</param>
    /// <param name="topics">The topics to expose, usually from the plugin's manifest.</param>
    public DirectoryManualSource(string root, IReadOnlyList<ManualTopic> topics)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(topics);
        _root = root;
        Topics = topics;
    }

    /// <inheritdoc/>
    public IReadOnlyList<ManualTopic> Topics { get; }

    /// <inheritdoc/>
    public IReadOnlyList<string> Languages
    {
        get
        {
            var languages = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string dir in Directory.EnumerateDirectories(_root))
                {
                    if (Directory.EnumerateFiles(dir, "*.md").Any())
                        languages.Add(Path.GetFileName(dir));
                }

                if (Directory.EnumerateFiles(_root, "*.md").Any())
                    languages.Add(FallbackLanguage);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A plugin folder that vanished mid-session is not worth failing a manual listing over.
            }

            return [.. languages];
        }
    }

    /// <inheritdoc/>
    public string? Read(string topicId, string language)
    {
        ArgumentNullException.ThrowIfNull(topicId);
        ArgumentNullException.ThrowIfNull(language);

        // A topic id is used as a path segment, so it must not be able to walk out of the plugin folder.
        if (topicId.Length == 0 || topicId.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;

        string nested = Path.Combine(_root, language, topicId + ".md");
        if (TryRead(nested) is { } text)
            return text;

        // The flat form counts as English only.
        return language.Equals(FallbackLanguage, StringComparison.OrdinalIgnoreCase)
            ? TryRead(Path.Combine(_root, topicId + ".md"))
            : null;
    }

    private static string? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
