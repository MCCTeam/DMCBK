using DMCBK.Core.Beacon;
using DMCBK.Core.Tests.Fakes;
using Umpk.Geometry;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Light and biome reads degrade to unknown (never invented numbers, never a throw) where the session tracks no chunk.
/// </summary>
public sealed class WorldLightBiomeTests
{
    [Fact]
    public async Task LightAndBiome_OutsideLoadedChunks_AreNull()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;

        await using SchedulerFixture fixture = SchedulerFixture.Create();
        await fixture.StartAndLoginAsync(ct);

        // The fake pipe carries no chunk data: both reads must answer unknown.
        Assert.Null(await fixture.Client.Game.World.GetLightAsync(new BlockPos(0, 64, 0), ct));
        Assert.Null(await fixture.Client.Game.World.GetBiomeIdAsync(new BlockPos(0, 64, 0), ct));

        var host = new BeaconClientHost(fixture.Client, () => false, () => { });
        var reads = (IBeaconExtendedReads)host;
        Assert.Null(reads.GetLight(0, 64, 0));
        Assert.Null(reads.GetBiome(0, 64, 0));
    }
}
