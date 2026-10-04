namespace DMCBK.Core.Configuration;

/// <summary>
/// Writes <c>Localization.Language</c> back into an existing <c>client.toml</c> for <c>/lang &lt;tag&gt;</c>.
/// <para>
/// It is a LINE EDIT, not a re-serialization, and deliberately so.
/// The account and server write-backs can re-emit their whole file because <c>accounts.toml</c> and <c>servers.toml</c> are lists this client owns outright; <c>client.toml</c> is not.
/// It is the file a user edits by hand, its comments are the documentation, and re-emitting it through the serializer would preserve every value and destroy every comment, blank line and key the reader put there.
/// So exactly one line changes and every other byte of the file is the byte it was.
/// </para>
/// </summary>
internal static class ClientLanguageWriter
{
    private const string Table = "Localization";
    private const string Key = "Language";

    /// <summary>
    /// The pure half, so the byte-preservation promise is testable without touching a disk: returns <paramref name="text"/> with only <c>Localization.Language</c> changed.
    /// <para>
    /// A missing key is inserted directly under the <c>[Localization]</c> header, and a missing table is appended with the key inside it, so a hand-trimmed file still ends up with a language it can read back.
    /// </para>
    /// </summary>
    internal static string Rewrite(string text, string language)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(language);

        string value = "\"" + language + "\"";
        string[] lines = text.Split('\n');
        bool crlf = text.Contains("\r\n", StringComparison.Ordinal);
        string tail = crlf ? "\r" : string.Empty;

        int header = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            string body = Body(lines[i]);
            string trimmed = body.TrimStart();

            if (trimmed.StartsWith('#'))
                continue;

            if (trimmed.StartsWith('['))
            {
                if (header >= 0)
                    // Past the end of [Localization] without finding the key: put it right under the header, where the writer itself would have emitted it.
                    return Insert(lines, header + 1, Key + " = " + value + tail);

                if (IsTableHeader(trimmed))
                    header = i;

                continue;
            }

            if (header < 0 || !IsKeyAssignment(trimmed))
                continue;

            string indent = body[..(body.Length - trimmed.Length)];
            string rewritten = indent + Key + " = " + value + TrailingComment(trimmed);
            lines[i] = EndsWithCarriageReturn(lines[i]) ? rewritten + "\r" : rewritten;
            return string.Join('\n', lines);
        }

        if (header >= 0)
            return Insert(lines, header + 1, Key + " = " + value + tail);

        // No [Localization] table at all.
        // Append one; a table header is always safe at the end of a file.
        string newline = crlf ? "\r\n" : "\n";
        string separator = text.Length == 0 || text.EndsWith('\n') ? string.Empty : newline;
        return text + separator + "[" + Table + "]" + newline + Key + " = " + value + newline;
    }

    private static string Insert(string[] lines, int index, string line)
    {
        var rebuilt = new List<string>(lines.Length + 1);
        rebuilt.AddRange(lines[..index]);
        rebuilt.Add(line);
        rebuilt.AddRange(lines[index..]);
        return string.Join('\n', rebuilt);
    }

    private static bool EndsWithCarriageReturn(string line) => line.EndsWith('\r');

    /// <summary>The line as authored, minus the \r a CRLF file leaves behind after the split on \n.</summary>
    private static string Body(string line) => EndsWithCarriageReturn(line) ? line[..^1] : line;

    private static bool IsTableHeader(string trimmed)
    {
        int close = trimmed.IndexOf(']');
        return close > 0
            && trimmed[1..close].Trim().Equals(Table, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKeyAssignment(string trimmed)
    {
        if (!trimmed.StartsWith(Key, StringComparison.OrdinalIgnoreCase))
            return false;

        return trimmed[Key.Length..].TrimStart().StartsWith('=');
    }

    /// <summary>Keeps an inline comment the reader put after the value.</summary>
    private static string TrailingComment(string trimmed)
    {
        int hash = trimmed.IndexOf('#');
        return hash < 0 ? string.Empty : "  " + trimmed[hash..].TrimEnd();
    }
}
