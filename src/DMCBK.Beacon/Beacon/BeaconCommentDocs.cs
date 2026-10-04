namespace DMCBK.Core.Beacon;

/// <summary>
/// Shared doc-comment parsing for <c># desc:</c> and <c>// desc:</c> lines.
/// Consolidates the interpreter and script-command copies so doc metadata can never disagree between registration and help.
/// </summary>
internal static class BeaconCommentDocs
{
    /// <summary>Strips a leading <c>#</c> or <c>//</c> marker; false when neither is present.</summary>
    internal static bool TryStripMarker(string text, out string stripped)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.StartsWith("#", StringComparison.Ordinal))
        {
            stripped = text[1..].Trim();
            return true;
        }

        if (text.StartsWith("//", StringComparison.Ordinal))
        {
            stripped = text[2..].Trim();
            return true;
        }

        stripped = string.Empty;
        return false;
    }

    /// <summary>Reads a <c>tag:</c> value prefix (case-insensitive); false when absent.</summary>
    internal static bool TryGetTag(string stripped, string tag, out string value)
    {
        ArgumentNullException.ThrowIfNull(stripped);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        if (stripped.StartsWith(tag, StringComparison.OrdinalIgnoreCase))
        {
            value = stripped[tag.Length..].Trim();
            return true;
        }

        value = string.Empty;
        return false;
    }
}
