namespace DMCBK.Core;

/// <summary>Base type for typed failures surfaced by <see cref="Client"/> during a session start.</summary>
public abstract class DmcbkClientException : Exception
{
    private protected DmcbkClientException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Base type for account-authentication failures that are MCC's own precondition, not the auth flow's.
/// Every failure the auth flow itself can produce (a declined device code, an expired one, any other <see cref="Umpk.Auth.AuthException"/>) now propagates from <see cref="Umpk.Auth.MinecraftAuthFlow"/> unwrapped, so hosts catch that type directly; this base exists only so <see cref="DmcbkAuthInteractionUnavailableException"/> still fits the <see cref="DmcbkClientException"/> hierarchy.
/// </summary>
public class DmcbkAuthException : DmcbkClientException
{
    /// <summary>Creates the exception.</summary>
    public DmcbkAuthException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// An online account was configured but the host provides no <see cref="Umpk.Auth.IAuthInteraction"/> to drive the interactive login (device code, browser, or credentials).
/// </summary>
public sealed class DmcbkAuthInteractionUnavailableException : DmcbkAuthException
{
    /// <summary>Creates the exception.</summary>
    public DmcbkAuthInteractionUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// A <see cref="GameApi"/> facade member touched a gameplay feature (terrain, entities, inventory, physics, or pathfinding) that is disabled in the client's feature composition.
/// This is the typed core wrapper over UMPK's <see cref="Umpk.Client.FeatureDisabledException"/>, so hosts catch one core type.
/// </summary>
public sealed class DmcbkFeatureDisabledException : DmcbkClientException
{
    /// <summary>Creates the exception naming the disabled feature.</summary>
    public DmcbkFeatureDisabledException(string feature, Exception? inner = null)
        : base($"The '{feature}' feature is disabled for this client; enable it in the feature composition.", inner)
    {
        Feature = feature;
    }

    /// <summary>The feature that was disabled (Terrain, Entities, Inventory, Physics, or Pathfinding).</summary>
    public string Feature { get; }
}

/// <summary>
/// A <see cref="GameApi"/> facade member was used while the client is not in a live play session.
/// Facade members require a joined session; call them only while <see cref="Client.Status"/> is <see cref="Umpk.Client.ClientStatus.Playing"/>.
/// </summary>
public sealed class DmcbkNotInSessionException : DmcbkClientException
{
    /// <summary>Creates the exception.</summary>
    public DmcbkNotInSessionException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A connect was asked for with nowhere to dial: the client was built without a server (nothing in <c>servers.toml</c> or <c>client.toml</c> resolved to an address) and the caller named none either.
/// Everything else about the client works; it is idle rather than broken.
/// </summary>
public sealed class DmcbkNoServerConfiguredException : DmcbkClientException
{
    /// <summary>Creates the exception.</summary>
    public DmcbkNoServerConfiguredException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// An internal command blocked on async work while executing inside a session-loop work item, so the work it waits for can never run: the command IS what is stopping the loop.
/// Raised instead of hanging forever, in the one case where the block is provably unrecoverable (the loop failed a liveness probe while the command was waiting).
/// The fix is on the caller's side: do not dispatch a command inline from a session event handler; hand the dispatch to off-loop work first.
/// </summary>
public sealed class DmcbkSessionLoopBlockedException : DmcbkClientException
{
    /// <summary>Creates the exception.</summary>
    public DmcbkSessionLoopBlockedException(string message)
        : base(message)
    {
    }
}
