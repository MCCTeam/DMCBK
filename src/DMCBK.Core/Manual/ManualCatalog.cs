using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace DMCBK.Core.Manual;

/// <summary>
/// The manual: MCC's own pages plus whatever plugins contribute, read in the caller's language and falling back to English.
/// <para>
/// Pages are Markdown.
/// MCC's own set is authored under <c>Manual/Resources/en/</c> and folded into the SAME string corpus as every other user-facing string, as <c>man.page.&lt;topic&gt;</c> entries, by <c>tools/gen_translations.py</c>.
/// That is what makes them translatable through the pipeline that already exists: one Crowdin source, one satellite assembly per culture, no second file format and no second loader.
/// A plugin cannot add to that corpus, so a plugin's pages are files under its own <c>man/&lt;lang&gt;/</c> folder instead; both answer through <see cref="IManualSource"/>.
/// </para>
/// <para>
/// The core loads the SOURCE and stops there.
/// Rendering belongs to the host, because the classic console and the TUI need entirely different output from the same text, and because DMCBK.Core is console-free.
/// </para>
/// </summary>
public sealed class ManualCatalog
{
    private const string FallbackLanguage = "en";

    private readonly ConcurrentDictionary<string, string?> Cache = new(StringComparer.Ordinal);
    private readonly List<IManualSource> Sources = [new CorpusManualSource()];
    private readonly Lock Gate = new();

    /// <summary>
    /// Registers a manual source and returns the handle that removes it again.
    /// This is how a plugin's pages join the manual, and how they leave it when the plugin unloads: an unloaded plugin's topics must not stay in the index pointing at files nobody will read.
    /// </summary>
    public IDisposable Register(IManualSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (Gate)
        {
            Sources.Add(source);
            Cache.Clear();
        }

        return new Registration(this, source);
    }

    /// <summary>Every topic currently in the manual: the built-in set, then whatever plugins added.</summary>
    public IReadOnlyList<ManualTopic> Topics()
    {
        lock (Gate)
            return [.. Sources.SelectMany(s => s.Topics)];
    }

    /// <summary>Finds a topic by id across every source, case-insensitively.</summary>
    public ManualTopic? Find(string? id)
        => id is null
            ? null
            : Topics().FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The Markdown source of a topic in the current UI language, falling back to English.
    /// Null when the topic does not exist at all.
    /// </summary>
    public string? Read(string topicId) => Read(topicId, CultureInfo.CurrentUICulture);

    /// <summary>The Markdown source of a topic in a specific culture, falling back to English.</summary>
    public string? Read(string topicId, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(topicId);
        ArgumentNullException.ThrowIfNull(culture);

        if (Find(topicId) is not { } topic)
            return null;

        string key = $"{culture.Name}|{topic.Id}";
        return Cache.GetOrAdd(key, _ => Load(topic.Id, culture));
    }

    /// <summary>The languages the manual is shipped in. Used by the coverage test.</summary>
    public IReadOnlyList<string> Languages()
    {
        lock (Gate)
        {
            var languages = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (IManualSource source in Sources)
                languages.UnionWith(source.Languages);

            return [.. languages];
        }
    }

    /// <summary>Whether a topic exists in a given language, without falling back.</summary>
    public bool Has(string topicId, string language) => ReadExact(topicId, language) is not null;

    private string? Load(string topicId, CultureInfo culture)
    {
        // Try the specific culture ("pt-BR"), then its language ("pt"), then English.
        // Same order the resource manager uses for the string corpus, so a user who sees translated command output does not get an English page for no visible reason.
        for (CultureInfo? c = culture; c is not null && c != CultureInfo.InvariantCulture; c = c.Parent)
        {
            if (c.Name.Length > 0 && ReadExact(topicId, c.Name) is { } text)
                return text;
        }

        return ReadExact(topicId, FallbackLanguage);
    }

    private string? ReadExact(string topicId, string language)
    {
        if (language.Length == 0)
            return null;

        IManualSource[] sources;
        lock (Gate)
            sources = [.. Sources];

        foreach (IManualSource source in sources)
        {
            if (source.Read(topicId, language) is { } text)
                return text;
        }

        return null;
    }

    /// <summary>Clears the page cache. Test seam, and used when the source set changes.</summary>
    internal void ClearCache() => Cache.Clear();

    private sealed class Registration(ManualCatalog owner, IManualSource source) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            lock (owner.Gate)
            {
                owner.Sources.Remove(source);
                owner.Cache.Clear();
            }
        }
    }

    /// <summary>
    /// MCC's own pages, read out of the SAME string corpus as every other user-facing string.
    /// <para>
    /// The pages are authored as Markdown under <c>Manual/Resources/en/</c> (readable to write, sane to diff) and folded into the corpus as <c>man.page.&lt;topic&gt;</c> entries by <c>tools/gen_translations.py</c>.
    /// That is what makes them translatable through the pipeline that already exists: Crowdin has the corpus as a source, a translated page comes back in the satellite <c>McStrings.&lt;culture&gt;.resx</c> alongside everything else, and there is no second file format and no second loader to keep in step.
    /// </para>
    /// <para>
    /// A plugin cannot add to that corpus, so plugin pages use per-language folders instead; see <see cref="DirectoryManualSource"/>.
    /// </para>
    /// </summary>
    private sealed class CorpusManualSource : IManualSource
    {
        private const string KeyPrefix = "man.page.";

        public IReadOnlyList<ManualTopic> Topics => ManualTopics.All;

        /// <summary>
        /// The languages the corpus itself is shipped in, which is the set of satellite assemblies present.
        /// English is always there because it is the neutral resource.
        /// </summary>
        public IReadOnlyList<string> Languages
        {
            get
            {
                // The satellite assemblies sit in culture-named folders beside this one.
                // Reading those is one directory listing; probing CultureInfo.GetCultures would be several thousand assembly-load attempts to answer the same question.
                var languages = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { FallbackLanguage };
                try
                {
                    string dir = AppContext.BaseDirectory;
                    if (!string.IsNullOrEmpty(dir))
                    {
                        string satellite = typeof(ManualCatalog).Assembly.GetName().Name + ".resources.dll";
                        foreach (string sub in Directory.EnumerateDirectories(dir))
                        {
                            if (File.Exists(Path.Combine(sub, satellite)))
                                languages.Add(Path.GetFileName(sub));
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Single-file or otherwise unreadable layout: English is still true.
                }

                return [.. languages];
            }
        }

        public string? Read(string topicId, string language)
        {
            if (ManualTopics.Find(topicId) is null)
                return null;

            CultureInfo culture;
            try
            {
                culture = CultureInfo.GetCultureInfo(language);
            }
            catch (CultureNotFoundException)
            {
                return null;
            }

            // The corpus resolves through its own ResourceManager, which already implements the culture fallback.
            // Asking for an exact language and getting the neutral resource back is expected; ManualCatalog's own walk is what decides which language wins.
            string page = Localization.McStrings.GetExact(KeyPrefix + topicId, culture) ?? string.Empty;
            return page.Length == 0 ? null : page;
        }
    }
}
