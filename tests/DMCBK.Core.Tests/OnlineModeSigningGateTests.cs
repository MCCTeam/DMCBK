using DMCBK.Core;
using Umpk.Protocol.Java.Signing;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>Profile certificates are requested only for server-authenticated sessions, including across reconnects.</summary>
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
    public async Task AnUnauthenticatedConnection_IsNeverAskedForCertificates()
    {
        var inner = new StubProvider();
        var gated = new OnlineModeSigningGate(inner, () => false);

        Assert.Null(await gated.GetCertificatesAsync(CancellationToken.None));

        // Not merely discarded: the inner provider is never consulted, so no certificate fetch, no token store read and no network call happens for a server that could not accept the key anyway.
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task AnAuthenticatedConnection_PassesThroughToTheInnerProvider()
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
        bool authenticated = false;
        var inner = new StubProvider();
        var gated = new OnlineModeSigningGate(inner, () => authenticated);

        await gated.GetCertificatesAsync(CancellationToken.None);
        Assert.Equal(0, inner.Calls);

        authenticated = true;
        await gated.GetCertificatesAsync(CancellationToken.None);
        Assert.Equal(1, inner.Calls);

        authenticated = false;
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
