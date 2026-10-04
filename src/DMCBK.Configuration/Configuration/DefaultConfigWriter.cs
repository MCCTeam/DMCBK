using System.Globalization;
using Tomlet;

namespace DMCBK.Core.Configuration;

/// <summary>
/// Serializes a TOML file model to text with localized <c>#</c> comments above each key.
/// The file models carry
/// <c>$CorpusKey$</c> placeholder comments (via Tomlet comment attributes); this writer expands them from the
/// comment source through <see cref="ConfigCommentExpander"/>, the same step the plugin SDK's settings writer runs against a plugin's own <c>lang/</c> table.
/// Any placeholder (whether Tomlet renders it as a preceding or an inline comment) ends up as a preceding comment, satisfying "comments above their keys".
/// Instance-scoped: no static state.
/// </summary>
public sealed class DefaultConfigWriter
{
    private readonly IConfigCommentSource _comments;

    /// <summary>Creates a writer over the given comment source.</summary>
    public DefaultConfigWriter(IConfigCommentSource comments)
    {
        ArgumentNullException.ThrowIfNull(comments);
        _comments = comments;
    }

    /// <summary>
    /// Serializes a TOML model to a documented string.
    /// Public so hosts can generate their own files (e.g. the CLI's <c>console.toml</c>) with the same corpus-backed comments; the model carries <c>$CorpusKey$</c> placeholder comment attributes.
    /// </summary>
    public string Serialize<T>(T model) where T : notnull
    {
        // Tomlet formats numbers/dates culture-sensitively; force invariant so files are stable across locales.
        CultureInfo previous = Thread.CurrentThread.CurrentCulture;
        string toml;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            toml = TomletMain.TomlStringFrom(model);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }

        return ConfigCommentExpander.Expand(toml, _comments.GetComment);
    }
}
