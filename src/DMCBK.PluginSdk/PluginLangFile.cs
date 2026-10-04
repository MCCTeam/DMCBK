using Tomlet;
using Tomlet.Models;

namespace DMCBK.PluginSdk;

/// <summary>
/// One <c>lang/&lt;language&gt;.toml</c> read twice: once through the TOML parser, for the keys the plugin will actually resolve, and once as plain text, for the line each key sits on.
/// The line numbers are what lets a lint point at the mistake instead of describing it.
/// </summary>
internal sealed class PluginLangFile
{
    private PluginLangFile(
        string path,
        string language,
        IReadOnlyList<LangKey> keys,
        IReadOnlyDictionary<string, string> table,
        string? parseError)
    {
        Path = path;
        Language = language;
        Keys = keys;
        Table = table;
        ParseError = parseError;
    }

    /// <summary>The absolute file path.</summary>
    public string Path { get; }

    /// <summary>The language tag, taken from the file name.</summary>
    public string Language { get; }

    /// <summary>Every key in the file, with the table it landed in and the line it was written on.</summary>
    public IReadOnlyList<LangKey> Keys { get; }

    /// <summary>The flattened table the plugin resolves against, dotted the way <see cref="PluginLocalization"/> does.</summary>
    public IReadOnlyDictionary<string, string> Table { get; }

    /// <summary>Why the file could not be parsed, or null when it parsed.</summary>
    public string? ParseError { get; }

    /// <summary>Reads one language file. A parse failure is reported, never thrown.</summary>
    public static PluginLangFile Read(string path)
    {
        string language = System.IO.Path.GetFileNameWithoutExtension(path);
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PluginLangFile(path, language, [], new Dictionary<string, string>(), ex.Message);
        }

        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        string? error = null;
        try
        {
            Flatten(new TomlParser().Parse(text), string.Empty, table);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            error = ex.Message;
        }

        return new PluginLangFile(path, language, ScanKeys(text), table, error);
    }

    /// <summary>
    /// Walks the file as text, tracking the table header in force, and records every key with its line.
    /// Deliberately not the parser's job: the parser gives the same dotted path for a key the author meant to write at the top level and one they meant to nest, and the difference is where it sits in the file.
    /// </summary>
    private static IReadOnlyList<LangKey> ScanKeys(string text)
    {
        var keys = new List<LangKey>();
        string prefix = string.Empty;
        bool inMultilineString = false;
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');

            // A """ block can hold anything, brackets and equals signs included.
            int fences = CountFences(line);
            if (inMultilineString)
            {
                inMultilineString = fences % 2 == 0;
                continue;
            }

            string trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            if (trimmed.StartsWith('['))
            {
                int close = trimmed.LastIndexOf(']');
                if (close > 0)
                    prefix = trimmed[1..close].Trim('[', ']', ' ').Trim();

                continue;
            }

            int equals = IndexOfAssignment(trimmed);
            if (equals > 0)
            {
                string name = trimmed[..equals].Trim().Trim('"', '\'');
                if (name.Length > 0)
                {
                    keys.Add(new LangKey(
                        prefix.Length == 0 ? name : $"{prefix}.{name}",
                        prefix.Length == 0 ? null : prefix,
                        name,
                        i + 1));
                }
            }

            inMultilineString = fences % 2 == 1;
        }

        return keys;
    }

    private static int CountFences(string line)
    {
        int count = 0;
        int at = 0;
        while ((at = line.IndexOf("\"\"\"", at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += 3;
        }

        return count;
    }

    /// <summary>The index of the assignment '=', ignoring one inside a quoted key.</summary>
    private static int IndexOfAssignment(string line)
    {
        bool quoted = false;
        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == quote)
                    quoted = false;

                continue;
            }

            if (c is '"' or '\'')
            {
                quoted = true;
                quote = c;
                continue;
            }

            if (c == '=')
                return i;

            if (c == '#')
                return -1;
        }

        return -1;
    }

    private static void Flatten(TomlTable table, string prefix, Dictionary<string, string> into)
    {
        foreach ((string key, TomlValue value) in table.Entries)
        {
            string path = prefix.Length == 0 ? key : $"{prefix}.{key}";
            if (value is TomlTable nested)
                Flatten(nested, path, into);
            else
                into[path] = value.StringValue;
        }
    }
}

/// <summary>One key in a language file: where it resolves, which table it fell into, and its line.</summary>
/// <param name="Path">The dotted name the plugin resolves it by.</param>
/// <param name="Table">The table header in force, or null at the top level.</param>
/// <param name="Name">The key as written, without the table prefix.</param>
/// <param name="Line">The one-based line it is written on.</param>
internal readonly record struct LangKey(string Path, string? Table, string Name, int Line);
