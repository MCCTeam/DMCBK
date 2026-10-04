using Umpk.Protocol.Java.Signing;

namespace DMCBK.Core;

/// <summary>
/// Wraps a chat-signing provider so it is consulted only on a connection that negotiated encryption, which is exactly a server running in online mode.
/// </summary>
/// <remarks>
/// <para>
/// An offline-mode server never sends an encryption request and assigns each player a UUID derived from their name.
/// A Mojang profile key's signature covers the account's REAL uuid: 1.21.11 <c>ProfilePublicKey.java:64-70</c> writes the uuid into the signed payload, and <c>RemoteChatSession.java:38</c> validates that signature against the server's own <c>GameProfile.id()</c>.
/// The offline uuid and the real one can never agree, so announcing the key can only ever end the join with "Invalid signature for profile public key" a second or two after it succeeded.
/// Note that <c>enforce-secure-profile=false</c> does not help: it governs whether a key is REQUIRED, not whether a key that was sent gets validated.
/// </para>
/// <para>
/// Legacy MCC never hit this because the guard was structural rather than configurable: <c>Protocol18.SendPlayerSession</c> returned false whenever <c>isOnlineMode</c> was false, and that flag was set in exactly one place, on receiving an encryption request.
/// The port lost the guard while keeping the <c>Chat.Signature.LoginWithSecureProfile</c> setting it was ANDed with.
/// </para>
/// <para>
/// This deliberately lives in MCC and not in UMPK.
/// Vanilla's own client announces its chat session unconditionally (which is why a legitimately signed vanilla client is also refused by offline-mode servers), so suppressing it is a client policy, not protocol behaviour, and UMPK's rule is that vanilla is the oracle.
/// </para>
/// </remarks>
public sealed class OnlineModeSigningGate : IChatSigningProvider
{
    private readonly IChatSigningProvider _inner;
    private readonly Func<bool> _isEncrypted;

    /// <summary>
    /// Whether this gate can be applied to a version whose <c>ChatSigning</c> feature is <paramref name="chatSigningFeature"/>.
    /// </summary>
    /// <remarks>
    /// Only the <c>v3</c> era (1.19.3+) announces the profile key AFTER login, in <c>chat_session_update</c>, which is the earliest point at which online mode is knowable at all.
    /// The <c>v1</c> and <c>v2</c> eras (1.19 and 1.19.1) carry the key in <c>login_start</c>, which goes out BEFORE the server's encryption request, so a gate there would read "not encrypted yet" on every server and strip signing from online-mode joins too.
    /// Those eras keep vanilla behaviour; the offline-mode kick they can still take is the same one vanilla takes.
    /// </remarks>
    public static bool AppliesTo(string? chatSigningFeature)
        => string.Equals(chatSigningFeature, "v3", StringComparison.Ordinal);

    /// <summary>Creates a gate over <paramref name="inner"/>.</summary>
    /// <param name="inner">The provider consulted when the connection is encrypted.</param>
    /// <param name="isEncrypted">
    /// Reads whether the live connection negotiated encryption.
    /// Evaluated per call, not captured: the provider is built before a connection exists and outlives every reconnect, so a value sampled at construction would describe the wrong session (or no session at all).
    /// </param>
    public OnlineModeSigningGate(IChatSigningProvider inner, Func<bool> isEncrypted)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(isEncrypted);
        _inner = inner;
        _isEncrypted = isEncrypted;
    }

    /// <inheritdoc />
    public ValueTask<PlayerCertificates?> GetCertificatesAsync(CancellationToken cancellationToken)
        => _isEncrypted()
            ? _inner.GetCertificatesAsync(cancellationToken)
            : ValueTask.FromResult<PlayerCertificates?>(null);
}
