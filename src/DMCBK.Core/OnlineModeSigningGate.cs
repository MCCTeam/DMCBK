using Umpk.Protocol.Java.Signing;

namespace DMCBK.Core;

/// <summary>Wraps a chat-signing provider so it is consulted only when the server requested session authentication during login.</summary>
/// <remarks>Offline servers assign a UUID derived from the player name. A Mojang profile certificate covers the account UUID, so announcing it under an offline UUID can cause an invalid-profile-key disconnect even when secure profiles are optional. Since 1.20.5 an offline server can request encryption without session authentication; use the login authentication result rather than the cipher state to decide whether to announce certificates.</remarks>
public sealed class OnlineModeSigningGate : IChatSigningProvider
{
    private readonly IChatSigningProvider _inner;
    private readonly Func<bool> _isAuthenticated;

    /// <summary>
    /// Whether this gate can be applied to a version whose <c>ChatSigning</c> feature is <paramref name="chatSigningFeature"/>.
    /// </summary>
    /// <remarks>
    /// Only the <c>v3</c> era (1.19.3+) announces the profile key AFTER login, in <c>chat_session_update</c>, which is the earliest point at which the server's authentication requirement is known.
    /// The <c>v1</c> and <c>v2</c> eras (1.19 and 1.19.1) carry the key in <c>login_start</c>, which goes out BEFORE the server's encryption request, so a gate there would read "not authenticated yet" on every server and strip signing from online-mode joins too.
    /// Those eras keep vanilla behaviour; the offline-mode kick they can still take is the same one vanilla takes.
    /// </remarks>
    public static bool AppliesTo(string? chatSigningFeature)
        => string.Equals(chatSigningFeature, "v3", StringComparison.Ordinal);

    /// <summary>Creates a gate over <paramref name="inner"/>.</summary>
    /// <param name="inner">The provider consulted when the server required session authentication.</param>
    /// <param name="isEncrypted">
    /// Reads whether the live session completed server-requested authentication. The parameter retains its original name for source compatibility.
    /// Evaluated per call, not captured: the provider is built before a connection exists and outlives every reconnect, so a value sampled at construction would describe the wrong session (or no session at all).
    /// </param>
    public OnlineModeSigningGate(IChatSigningProvider inner, Func<bool> isEncrypted)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(isEncrypted);
        _inner = inner;
        _isAuthenticated = isEncrypted;
    }

    /// <inheritdoc />
    public ValueTask<PlayerCertificates?> GetCertificatesAsync(CancellationToken cancellationToken)
        => _isAuthenticated()
            ? _inner.GetCertificatesAsync(cancellationToken)
            : ValueTask.FromResult<PlayerCertificates?>(null);
}
