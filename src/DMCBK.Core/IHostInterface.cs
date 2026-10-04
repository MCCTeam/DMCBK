using DMCBK.Core.Commands;
using Umpk.Auth;

namespace DMCBK.Core;

/// <summary>
/// Everything a host may provide to the core: the auth-interaction seam used to drive interactive online logins (device code, browser, Yggdrasil credentials), and the command output sink and the optional UI hooks the command system consults (both default to text-only when absent).
/// </summary>
public interface IHostInterface
{
    /// <summary>
    /// An optional prompt the core calls when it cannot resolve the server version automatically.
    /// Null means the host offers no prompt, and an unresolved version becomes a typed failure.
    /// </summary>
    IUserPrompt? Prompt { get; }

    /// <summary>
    /// The host seam that renders interactive login steps (UMPK's <see cref="IAuthInteraction"/>).
    /// Null means the host cannot drive an interactive login; the core then fails an online account with <see cref="DmcbkAuthInteractionUnavailableException"/>.
    /// Offline accounts never use it.
    /// </summary>
    IAuthInteraction? AuthInteraction { get; }

    /// <summary>The sink command output is written to; null uses <see cref="NullCommandOutput"/> (dropped).</summary>
    ICommandOutput? CommandOutput => null;

    /// <summary>Optional host UI hooks (book editor, container view, tab overlay, chunk viewport); null is text-only.</summary>
    IHostUi? Ui => null;

    /// <summary>
    /// An optional prompt the core calls once per server resource-pack push when <c>Localization.ResourcePackPolicy</c> is <c>prompt</c>.
    /// Null means the host offers no prompt, and the push is declined.
    /// </summary>
    IResourcePackPrompt? ResourcePackPrompt => null;
}

/// <summary>
/// The host prompt seam.
/// It exposes only version resolution; a host with no interactive prompt may return a null version to signal "give up".
/// </summary>
public interface IUserPrompt
{
    /// <summary>
    /// Asks the host to choose a version after automatic detection failed.
    /// Return null to abort the start (the core then throws <see cref="Umpk.Client.VersionResolutionException"/>).
    /// </summary>
    ValueTask<Umpk.Protocol.Java.JavaVersion?> ResolveVersionAsync(
        VersionResolutionRequest request, CancellationToken ct);
}

/// <summary>Context handed to <see cref="IUserPrompt.ResolveVersionAsync"/> describing what failed.</summary>
/// <param name="Host">The server host that was queried.</param>
/// <param name="Port">The server port that was queried.</param>
/// <param name="DetectedProtocol">The protocol number the ping reported, when it reported one with no catalog match.</param>
/// <param name="Reason">A short, non-localized diagnostic describing the failure.</param>
public sealed record VersionResolutionRequest(
    string Host,
    ushort Port,
    int? DetectedProtocol,
    string Reason);

/// <summary>
/// A no-op host interface: no prompt and no auth interaction, so unresolved versions and online accounts fail fast with typed errors.
/// </summary>
public sealed class NullHostInterface : IHostInterface
{
    /// <inheritdoc />
    public IUserPrompt? Prompt => null;

    /// <inheritdoc />
    public IAuthInteraction? AuthInteraction => null;
}
