using DMCBK.Core;
using Umpk.Protocol.Java.Signing;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The offline-mode chat-signing gate: MCC must not announce a Mojang-signed profile key to a server that never asked for encryption.
/// </summary>
/// <remarks>
/// An offline-mode server assigns a UUID derived from the player name, while the key's Mojang signature covers the account's REAL uuid (1.21.11 <c>ProfilePublicKey.java:64-70</c> puts the uuid in the signed payload, and <c>RemoteChatSession.java:38</c> validates it against the server's own <c>GameProfile.id()</c>).
/// The two can never agree, so the join is closed with "Invalid signature for profile public key".
/// Legacy MCC avoided this structurally: <c>Protocol18.SendPlayerSession</c> returned false whenever <c>isOnlineMode</c> was false, and that flag was set only on receiving an encryption request.
/// This restores that behaviour on the new stack.
/// </remarks>
public sealed class OnlineModeSigningGateTests
{
    private sealed class StubProvider : IChatSigningProvider
    {
        public int Calls { get; private set; }

        public ValueTask<PlayerCertificates?> GetCertificatesAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult<PlayerCertificates?>(null);
        }
    }

    [Fact]
    public async Task AnUnencryptedConnection_IsNeverAskedForCertificates()
    {
        var inner = new StubProvider();
        var gated = new OnlineModeSigningGate(inner, () => false);

        Assert.Null(await gated.GetCertificatesAsync(CancellationToken.None));

        // Not merely discarded: the inner provider is never consulted, so no certificate fetch, no token store read and no network call happens for a server that could not accept the key anyway.
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task AnEncryptedConnection_PassesThroughToTheInnerProvider()
    {
        var inner = new StubProvider();
        var gated = new OnlineModeSigningGate(inner, () => true);

        await gated.GetCertificatesAsync(CancellationToken.None);

        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task TheGateIsReEvaluatedPerCall()
    {
        // The provider is built before the connection exists and outlives every reconnect, so the answer has to be read at call time rather than captured once at construction.
        bool encrypted = false;
        var inner = new StubProvider();
        var gated = new OnlineModeSigningGate(inner, () => encrypted);

        await gated.GetCertificatesAsync(CancellationToken.None);
        Assert.Equal(0, inner.Calls);

        encrypted = true;
        await gated.GetCertificatesAsync(CancellationToken.None);
        Assert.Equal(1, inner.Calls);

        encrypted = false;
        await gated.GetCertificatesAsync(CancellationToken.None);
        Assert.Equal(1, inner.Calls);
    }

    [Theory]
    [InlineData("v3", true)]    // 1.19.3+: the key is announced post-login, so online mode is knowable.
    [InlineData("v1", false)]   // 1.19:   the key rides in login_start, before the encryption request.
    [InlineData("v2", false)]   // 1.19.1: same.
    [InlineData("none", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyThePostLoginEraCanBeGated(string? feature, bool expected)
        => Assert.Equal(expected, OnlineModeSigningGate.AppliesTo(feature));

    [Fact]
    public void TheGateRejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new OnlineModeSigningGate(null!, () => true));
        Assert.Throws<ArgumentNullException>(() => new OnlineModeSigningGate(new StubProvider(), null!));
    }
}
