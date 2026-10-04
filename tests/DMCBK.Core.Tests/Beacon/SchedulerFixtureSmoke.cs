using DMCBK.Core.Tests.Fakes;
using Umpk.Client;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Proves the scheduler fixture works on its own: a real client over the fake-server pipe reaches <see cref="ClientStatus.Playing"/> with the virtual clock injected into the engine.
/// </summary>
public sealed class SchedulerFixtureSmoke
{
    [Fact]
    public async Task Login_ReachesPlaying_WithVirtualClockInjected()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;

        await using SchedulerFixture fixture = SchedulerFixture.Create();

        Assert.Same(fixture.Clock, fixture.Engine.Clock);
        Assert.Same(fixture.Rng, fixture.Engine.Rng);

        await fixture.StartAndLoginAsync(ct);

        Assert.Equal(ClientStatus.Playing, fixture.Client.Status);
    }
}
