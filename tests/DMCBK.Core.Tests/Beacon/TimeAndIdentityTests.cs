using DMCBK.Core.Beacon;
using DMCBK.Core.Commands;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Time, identity (me), and server reads.
/// </summary>
public sealed class TimeAndIdentityTests : IDisposable
{
    public void Dispose() => ScriptTestHelpers.Reset();

    [Fact]
    public async Task Time_FieldsShape()
    {
        var host = new ScriptTestHost();
        var clock = new VirtualClock(new DateTimeOffset(2026, 9, 9, 13, 5, 0, TimeSpan.Zero));
        var engine = new BeaconEngine(host, clock, new SeededRng(7), new FuelBudget());
        engine.Variables = new VariableStore();
        Assert.Equal("13:05", await ScriptTestHelpers.ShowAsync(engine, "t", "time.now"));
        Assert.Equal("2026-09-09", await ScriptTestHelpers.ShowAsync(engine, "t2", "time.date"));
        Assert.Equal("Wednesday", await ScriptTestHelpers.ShowAsync(engine, "t3", "time.today"));
        Assert.Equal("13", await ScriptTestHelpers.ShowAsync(engine, "t4", "time.hour"));
        Assert.Equal("5", await ScriptTestHelpers.ShowAsync(engine, "t5", "time.minute"));
        string stamp = await ScriptTestHelpers.ShowAsync(engine, "t6", "time.stamp");
        Assert.Equal(new DateTimeOffset(2026, 9, 9, 13, 5, 0, TimeSpan.Zero).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), stamp);
    }

    [Fact]
    public async Task Time_Format_Renders()
    {
        var host = new ScriptTestHost();
        var clock = new VirtualClock(new DateTimeOffset(2026, 9, 9, 13, 5, 0, TimeSpan.Zero));
        var engine = new BeaconEngine(host, clock, new SeededRng(7), new FuelBudget());
        engine.Variables = new VariableStore();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "t", "show time.format(time.stamp, \"HH:mm\")\n");
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.Equal("13:05", Assert.Single(run.LocalOutput));
    }

    [Fact]
    public async Task Time_Format_BadFormat_Refuses()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        BeaconRunResult run = await ScriptTestHelpers.RunAsync(engine, "t", "show time.format(time.stamp, \"\\\"\")\n");
        Assert.False(run.Success);
    }

    [Fact]
    public async Task Time_Ago_FutureClampsToJustNow()
    {
        var host = new ScriptTestHost();
        var clock = new VirtualClock(new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero));
        var engine = new BeaconEngine(host, clock, new SeededRng(7), new FuelBudget());
        engine.Variables = new VariableStore();
        long future = new DateTimeOffset(2026, 9, 9, 13, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.Equal("just now", await ScriptTestHelpers.ShowAsync(engine, "a", $"time.ago({future})"));
    }

    [Fact]
    public async Task Me_FullFieldMap()
    {
        var (engine, host, _) = ScriptTestHelpers.NewEngine();
        host.VitalsValue = new BeaconVitals(10, 20, 14, 5, 300, 3, 2);
        host.PositionValue = new BeaconPosition(100, 64, -30, 90, 10);
        host.GamemodeValue = "survival";
        host.PingValue = 42;
        host.SneakingValue = true;
        host.EffectsValue = [new BeaconEffectInfo("minecraft:speed", 1, 60)];
        Assert.Equal("Tester", await ScriptTestHelpers.ShowAsync(engine, "m", "me.name"));
        Assert.Equal("300", await ScriptTestHelpers.ShowAsync(engine, "m2", "me.air"));
        Assert.Equal("3", await ScriptTestHelpers.ShowAsync(engine, "m3", "me.xp_level"));
        Assert.Equal("2", await ScriptTestHelpers.ShowAsync(engine, "m4", "me.armor"));
        Assert.Equal("5", await ScriptTestHelpers.ShowAsync(engine, "m5", "me.saturation"));
        Assert.Equal("90", await ScriptTestHelpers.ShowAsync(engine, "m6", "me.yaw"));
        Assert.Equal("10", await ScriptTestHelpers.ShowAsync(engine, "m7", "me.pitch"));
        Assert.Equal("yes", await ScriptTestHelpers.ShowAsync(engine, "m8", "me.is_sneaking"));
        Assert.Equal("minecraft:speed", await ScriptTestHelpers.ShowAsync(engine, "m9", "me.effects[0].name"));
    }

    [Fact]
    public async Task Me_MissingVitals_SurfacesNone()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        Assert.Equal("none", await ScriptTestHelpers.ShowAsync(engine, "m", "me.health"));
        Assert.Equal("none", await ScriptTestHelpers.ShowAsync(engine, "m2", "me.pos"));
    }

    [Fact]
    public async Task Server_IdentityFields()
    {
        var (engine, host, _) = ScriptTestHelpers.NewEngine();
        host.ServerInfoValue = new BeaconServerInfo("play.example.com", 25565, "1.21", 100, "Hi", 6000, 12, "clear", "normal");
        host.ProtocolValue = 769;
        Assert.Equal("play.example.com", await ScriptTestHelpers.ShowAsync(engine, "s", "server.ip"));
        Assert.Equal("25565", await ScriptTestHelpers.ShowAsync(engine, "s2", "server.port"));
        Assert.Equal("1.21", await ScriptTestHelpers.ShowAsync(engine, "s3", "server.version_name"));
        Assert.Equal("769", await ScriptTestHelpers.ShowAsync(engine, "s4", "server.protocol"));
        Assert.Equal("100", await ScriptTestHelpers.ShowAsync(engine, "s5", "server.max_players"));
        Assert.Equal("Hi", await ScriptTestHelpers.ShowAsync(engine, "s6", "server.motd"));
        Assert.Equal("6000", await ScriptTestHelpers.ShowAsync(engine, "s7", "server.day_time"));
        Assert.Equal("12", await ScriptTestHelpers.ShowAsync(engine, "s8", "server.day"));
        Assert.Equal("normal", await ScriptTestHelpers.ShowAsync(engine, "s9", "server.difficulty"));
    }

    [Fact]
    public async Task Server_MissingInfo_SurfacesNone()
    {
        var (engine, _, _) = ScriptTestHelpers.NewEngine();
        Assert.Equal("none", await ScriptTestHelpers.ShowAsync(engine, "s", "server.ip"));
        Assert.Equal("none", await ScriptTestHelpers.ShowAsync(engine, "s2", "server.motd"));
    }

    [Fact]
    public async Task Server_TpsNone_SurfacesNone()
    {
        var (engine, host, _) = ScriptTestHelpers.NewEngine();
        host.ServerTpsValue = null;
        host.ServerMsptValue = null;
        Assert.Equal("none", await ScriptTestHelpers.ShowAsync(engine, "s", "server.tps"));
        Assert.Equal("none", await ScriptTestHelpers.ShowAsync(engine, "s2", "server.mspt"));
    }
}
