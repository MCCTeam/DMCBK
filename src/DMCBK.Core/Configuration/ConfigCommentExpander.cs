using System.Text;
using System.Text.RegularExpressions;

namespace DMCBK.Core.Configuration;

/// <summary>
/// Turns <c>$Key$</c> placeholder comments in serialized TOML into real, localized <c>#</c> comments.
/// <para>
/// Both writers in this repository need exactly this step and must agree on it, so it lives here rather than inside either of them: <c>the configuration writer</c> resolves the placeholders against the <see cref="IConfigCommentSource"/> corpus for <c>client.toml</c> and its siblings, and the plugin SDK's settings writer resolves them against the plugin's own <c>lang/</c> table.
/// A plugin therefore documents its settings the same way MCC documents its own, in whatever languages it ships.
/// </para>
/// <para>
/// A comment that is not a placeholder is copied through unchanged, byte for byte.
/// That is the compatibility promise the plugin side depends on: every plugin written before this existed keeps generating exactly the <c>settings.toml</c> it generated before, because a plain English literal in a Tomlet comment attribute matches neither pattern.
/// </para>
/// </summary>
public static partial class ConfigCommentExpander
{
    /// <summary>
    /// Expands every <c>$Key$</c> placeholder comment in <paramref name="toml"/>.
    /// <para>
    /// A standalone placeholder line becomes the resolved text, one <c>#</c> line per line of it, at the placeholder's own indentation.
    /// A placeholder trailing a value (Tomlet's inline-comment form) becomes a preceding comment above that value, so every comment ends up above the key it documents.
    /// A key the resolver has nothing for is written as itself, which is what MCC's corpus does with a missing string: it shows up as its key rather than as blank space.
    /// </para>
    /// </summary>
    /// <param name="toml">The serialized TOML, carrying placeholder comments.</param>
    /// <param name="resolve">Resolves a placeholder key to its text; null means "nothing has this key".</param>
    /// <returns>The TOML with placeholders replaced, keeping the input's own trailing-newline shape.</returns>
    public static string Expand(string toml, Func<string, string?> resolve)
    {
        ArgumentNullException.ThrowIfNull(toml);
        ArgumentNullException.ThrowIfNull(resolve);

        string[] lines = toml.ReplaceLineEndings("\n").Split('\n');
        var output = new StringBuilder(toml.Length);

        for (int i = 0; i < lines.Length; i++)
        {
            string rawLine = lines[i];

            // Split on '\n' yields a trailing empty segment for text that ends in a newline.
            // Appending a newline after it would add a blank line the input never had, so the last segment is written without one and the file keeps exactly the ending it arrived with.
            bool last = i == lines.Length - 1;

            Match standalone = StandalonePlaceholder().Match(rawLine);
            if (standalone.Success)
            {
                AppendComment(output, standalone.Groups["indent"].Value, standalone.Groups["key"].Value, resolve);
                continue;
            }

            Match inline = InlinePlaceholder().Match(rawLine);
            if (inline.Success)
            {
                string body = inline.Groups["body"].Value;
                string indent = body.Length - body.TrimStart().Length is var n && n > 0 ? body[..n] : string.Empty;
                AppendComment(output, indent, inline.Groups["key"].Value, resolve);
                output.Append(body);
                if (!last)
                    output.Append('\n');

                continue;
            }

            output.Append(rawLine);
            if (!last)
                output.Append('\n');
        }

        return output.ToString();
    }

    private static void AppendComment(StringBuilder output, string indent, string key, Func<string, string?> resolve)
    {
        string text = resolve(key) ?? key;
        foreach (string line in text.ReplaceLineEndings("\n").Split('\n'))
            output.Append(indent).Append("# ").Append(line).Append('\n');
    }

    [GeneratedRegex(@"^(?<indent>\s*)#\s*\$(?<key>[\w.\-]+)\$\s*$")]
    private static partial Regex StandalonePlaceholder();

    [GeneratedRegex(@"^(?<body>.*\S)\s+#\s*\$(?<key>[\w.\-]+)\$\s*$")]
    private static partial Regex InlinePlaceholder();
}
