namespace DMCBK.Core.Configuration;

/// <summary>
/// Supplies human-readable, localized documentation for a configuration key, used when generating default files.
/// Implementations own the comment corpus (it carries the legacy ConfigComments corpus into an DMCBK.Core-owned embedded resource) and never reference the legacy assembly.
/// </summary>
public interface IConfigCommentSource
{
    /// <summary>
    /// Returns the comment text for a corpus key (e.g. <c>Main.Advanced.terrain_and_movements</c>), or null when the key has no documentation.
    /// Multi-line text is allowed; the writer prefixes each line with
    /// <c>#</c>.
    /// </summary>
    string? GetComment(string key);
}
