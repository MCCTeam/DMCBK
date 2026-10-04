namespace DMCBK.Core.Commands.Impl;

/// <summary>
/// Splits the one greedy argument a command takes when its real grammar has more than one token.
/// <para>
/// Brigadier's unquoted character set is <c>[0-9A-Za-z_.+-]</c>, so a token holding a colon, a slash or a
/// <c>#</c> stops the reader mid-word and the parse then fails on trailing data. Every value that carries
/// one (a <c>host:port</c>, a URL, a path) therefore arrives as one greedy string and is split back out here, which keeps the grammar the user types unchanged.
/// </para>
/// </summary>
internal static class CommandTokens
{
    /// <summary>
    /// Whitespace-separated tokens, with a double- or single-quoted run counting as one.
    /// Returns null for an unbalanced quote, which the caller answers with the usage line.
    /// </summary>
    internal static List<string>? Split(string text)
    {
        var tokens = new List<string>();
        int cursor = 0;
        while (cursor < text.Length)
        {
            while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
                cursor++;

            if (cursor >= text.Length)
                break;

            char quote = text[cursor];
            if (quote is '"' or '\'')
            {
                int closing = text.IndexOf(quote, cursor + 1);
                if (closing < 0)
                    return null;

                tokens.Add(text[(cursor + 1)..closing]);
                cursor = closing + 1;
                continue;
            }

            int endOfToken = cursor;
            while (endOfToken < text.Length && !char.IsWhiteSpace(text[endOfToken]))
                endOfToken++;

            tokens.Add(text[cursor..endOfToken]);
            cursor = endOfToken;
        }

        return tokens;
    }
}
