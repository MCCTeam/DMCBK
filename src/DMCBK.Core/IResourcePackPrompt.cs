namespace DMCBK.Core;

/// <summary>
/// The host prompt seam for server resource packs.
/// Implemented by hosts with interactive input (the classic console reads a line, the TUI reads its input line); headless hosts leave <see cref="IHostInterface.ResourcePackPrompt"/> null and pushes are declined.
/// </summary>
public interface IResourcePackPrompt
{
    /// <summary>
    /// Asks the user whether to accept one server resource pack.
    /// True accepts (the server is told the pack was loaded), false declines.
    /// Implementations should treat an empty or unrecognised answer as false.
    /// </summary>
    ValueTask<bool> PromptAsync(ResourcePackPromptRequest request, CancellationToken ct);
}

/// <summary>Context handed to <see cref="IResourcePackPrompt.PromptAsync"/> for one server push.</summary>
/// <param name="Id">The server-provided pack id, or <see cref="Guid.Empty"/> on pre-UUID protocols.</param>
/// <param name="Url">The pack download URL.</param>
/// <param name="Hash">The pack SHA-1 hash, possibly empty.</param>
/// <param name="Required">True when the server marks the pack as required (declining likely kicks).</param>
/// <param name="PromptText">The server's prompt message in plain text, when it sent one.</param>
public sealed record ResourcePackPromptRequest(
    Guid Id,
    string Url,
    string Hash,
    bool Required,
    string? PromptText);
