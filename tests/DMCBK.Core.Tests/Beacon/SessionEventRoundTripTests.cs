using System.Text;
using DMCBK.Core.Beacon;
using DMCBK.Core.Commands;
using DMCBK.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Umpk;
using Umpk.Client;
using Umpk.Data.Java;
using Umpk.Game.Items;
using Umpk.Game.Players;
using Umpk.Game.Registries;
using Umpk.Protocol.Java;
using Umpk.Protocol.Java.Packets;
using DMCBK.Testing.Server;
using Umpk.Text;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Session to bus to script round trips on the fake-server pipe: the server sends a packet, the runtime translates it to hook fields, the handler answers with <c>say</c>, and the serverbound chat frame arrives.
/// No live server needed.
/// </summary>
public sealed class SessionEventRoundTripTests : IDisposable
{
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (string root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static string WithHeader(string body) => "# beacon 1\n# needs: chat.send\n" + body;

    private static string WithBareHeader(string body) => "# beacon 1\n" + body;

    private async Task<(FakeJavaServer Server, Client Client, string Root)> StartClientAsync(
        CancellationToken ct)
    {
        string root = Path.Combine(Path.GetTempPath(), "mcc-beacon-events-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        var server = FakeJavaServer.Create();
        Client client = McPluginSessionHarness.BuildClient(
            server, "Tester", Path.Combine(root, "plugins"), NullLoggerFactory.Instance);
        Task starting = client.StartAsync(ct);
        await McPluginSessionHarness.DriveLoginAsync(server, ct);
        await starting.WaitAsync(McPluginSessionHarness.Budget, ct);
        Assert.Equal(ClientStatus.Playing, client.Status);
        return (server, client, root);
    }

    private static async Task<string> RunScriptAsync(Client client, string root, string name, string body)
    {
        string path = Path.Combine(root, name + ".mcc");
        await File.WriteAllTextAsync(path, body);
        CmdResult run = await client.Commands.DispatchAsync($"scripts run \"{path}\"");
        Assert.Equal(CmdStatus.Done, run.Status);
        return path;
    }

    private static async Task<string> WaitForServerTextAsync(
        FakeJavaServer server, string needle, TimeSpan budget, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(budget);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        while (true)
        {
            linked.Token.ThrowIfCancellationRequested();
            InboundFrame frame;
            try
            {
                frame = await server.NextFrameAsync(linked.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                Assert.Fail($"Timed out waiting for a serverbound frame containing '{needle}'.");
                throw new InvalidOperationException("Unreachable.");
            }

            string text = Encoding.UTF8.GetString(frame.Payload);
            if (text.Contains(needle, StringComparison.Ordinal))
                return text;
        }
    }

    private static async Task WaitForServerTextsAsync(
        FakeJavaServer server, IReadOnlyList<string> needles, TimeSpan budget, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(budget);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var remaining = new HashSet<string>(needles, StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            linked.Token.ThrowIfCancellationRequested();
            InboundFrame frame;
            try
            {
                frame = await server.NextFrameAsync(linked.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                Assert.Fail(
                    $"Timed out waiting for serverbound frames containing '{string.Join("', '", remaining)}'.");
                throw new InvalidOperationException("Unreachable.");
            }

            string text = Encoding.UTF8.GetString(frame.Payload);
            remaining.RemoveWhere(needle => text.Contains(needle, StringComparison.Ordinal));
        }
    }

    private static async Task<int> CountServerTextAsync(
        FakeJavaServer server, string needle, TimeSpan window, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(window);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        int count = 0;
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                InboundFrame frame = await server.NextFrameAsync(linked.Token);
                if (Encoding.UTF8.GetString(frame.Payload).Contains(needle, StringComparison.Ordinal))
                    count++;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                break;
            }
        }

        return count;
    }

    private static async Task SendAsync<TPacket>(
        FakeJavaServer server, TPacket packet, CancellationToken ct)
        where TPacket : class, Umpk.Protocol.Java.IPacket
        => await McPluginSessionHarness.SendAsync(server, ProtocolPhase.Play, packet, ct);

    [Fact]
    public async Task Chat_PacketToSay_RoundTrip()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;
        (FakeJavaServer server, Client client, string root) = await StartClientAsync(ct);
        await using (server)
        await using (client)
        {
            await RunScriptAsync(client, root, "echo",
                WithHeader("on chat\nsay \"back {message}\"\nend on\n"));
            await SendAsync(server, new ClientboundSystemChatPacket(Component.Text("<Alice> hello"), false), ct);
            string frame = await WaitForServerTextAsync(server, "back hello", TimeSpan.FromSeconds(10), ct);
            Assert.Contains("back hello", frame, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Whisper_PacketToSay_RoundTrip()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;
        (FakeJavaServer server, Client client, string root) = await StartClientAsync(ct);
        await using (server)
        await using (client)
        {
            await RunScriptAsync(client, root, "pm",
                WithHeader("on whisper\nsay \"w {player} {message}\"\nend on\n"));
            await SendAsync(
                server,
                new ClientboundSystemChatPacket(Component.Text("Alice whispers to you: psst"), false),
                ct);
            await WaitForServerTextAsync(server, "w Alice psst", TimeSpan.FromSeconds(10), ct);
        }
    }

    [Fact]
    public async Task ServerMessage_PacketToSay_RoundTrip()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;
        (FakeJavaServer server, Client client, string root) = await StartClientAsync(ct);
        await using (server)
        await using (client)
        {
            await RunScriptAsync(client, root, "sys",
                WithHeader("on server_message\nsay \"s {text}\"\nend on\n"));
            await SendAsync(
                server,
                new ClientboundSystemChatPacket(Component.Text("Server restarts soon"), false),
                ct);
            await WaitForServerTextAsync(server, "s Server restarts soon", TimeSpan.FromSeconds(10), ct);
        }
    }

    [Fact]
    public async Task ServerMessage_TranslationKey_Surfaced()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;
        (FakeJavaServer server, Client client, string root) = await StartClientAsync(ct);
        await using (server)
        await using (client)
        {
            await RunScriptAsync(client, root, "syskey",
                WithHeader("on server_message\nsay \"k {translation_key}\"\nend on\n"));
            await SendAsync(
                server,
                new ClientboundSystemChatPacket(
                    Component.Translatable("multiplayer.disconnect.server_shutdown"), false),
                ct);
            await WaitForServerTextAsync(
                server, "k multiplayer.disconnect.server_shutdown", TimeSpan.FromSeconds(10), ct);
        }
    }

    [Fact]
    public async Task Join_AddPacket_Fires()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;
        (FakeJavaServer server, Client client, string root) = await StartClientAsync(ct);
        await using (server)
        await using (client)
        {
            await RunScriptAsync(client, root, "greeter",
                WithHeader("on join\nsay \"hi {player}\"\nend on\n"));
            var id = Guid.NewGuid();
            await SendAsync(
                server,
                new ClientboundPlayerInfoUpdatePacket(
                    PlayerInfoActions.AddPlayer,
                    [new PlayerInfoEntry(id, "Alice", null, false, null, GameMode.Survival, true, 10, null, 0, true)]),
                ct);
            await WaitForServerTextAsync(server, "hi Alice", TimeSpan.FromSeconds(10), ct);
        }
    }

    [Fact]
    public async Task Leave_RemovePacket_Fires()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;
        (FakeJavaServer server, Client client, string root) = await StartClientAsync(ct);
        await using (server)
        await using (client)
        {
            await RunScriptAsync(client, root, "farewell",
                WithHeader("on join\nsay \"hi {player}\"\nend on\non leave\nsay \"bye {player}\"\nend on\n"));
            var id = Guid.NewGuid();
            await SendAsync(
                server,
                new ClientboundPlayerInfoUpdatePacket(
                    PlayerInfoActions.AddPlayer,
                    [new PlayerInfoEntry(id, "Bob", null, false, null, GameMode.Survival, true, 10, null, 0, true)]),
                ct);
            // An add and a remove inside one poll window collapse to no diff, so the remove only counts once the poll has seen the add: gate on the join frame first.
            await WaitForServerTextAsync(server, "hi Bob", TimeSpan.FromSeconds(10), ct);
            await SendAsync(server, new ClientboundPlayerInfoRemovePacket([id]), ct);
            await WaitForServerTextAsync(server, "bye Bob", TimeSpan.FromSeconds(10), ct);
        }
    }

    [Fact]
    public async Task PlayerList_HeaderPacket_Fires()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;
        (FakeJavaServer server, Client client, string root) = await StartClientAsync(ct);
        await using (server)
        await using (client)
        {
            await RunScriptAsync(client, root, "plist",
                WithHeader("on player_list\nsay \"pl {count}\"\nend on\n"));
            await SendAsync(
                server,
                new ClientboundTabListPacket(Component.Text("Queue: 3"), Component.Text("Have fun")),
                ct);
            await WaitForServerTextAsync(server, "pl 0", TimeSpan.FromSeconds(10), ct);
        }
    }

    [Fact]
    public async Task Vitals_Change_FiresOnce()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;
        (FakeJavaServer server, Client client, string root) = await StartClientAsync(ct);
        await using (server)
        await using (client)
        {
            await RunScriptAsync(client, root, "vitals",
                WithHeader("on health\nsay \"h {health} {change}\"\nend on\non hunger\nsay \"f {food} {change}\"\nend on\n"));
            await SendAsync(server, new ClientboundSetHealthPacket(15, 17, 5), ct);
            await WaitForServerTextsAsync(server, ["h 15 -5", "f 17 -3"], TimeSpan.FromSeconds(10), ct);
            int repeats = await CountServerTextAsync(server, "h 15 -5", TimeSpan.FromSeconds(2), ct);
            Assert.Equal(0, repeats);
        }
    }

    [Fact]
    public async Task Inventory_SlotPacket_FiresOnce()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;
        (FakeJavaServer server, Client client, string root) = await StartClientAsync(ct);
        await using (server)
        await using (client)
        {
            await RunScriptAsync(client, root, "watcher",
                WithHeader("on inventory\nsay \"inv {len(slots_changed)}\"\nend on\n"));
            var registries = JavaGameData.Registries(McPluginSessionHarness.Version.Version.Protocol);
            Assert.True(registries.Items.TryGet(
                Identifier.Parse("minecraft:stone"), out RegistryEntry<ItemDefinition> stone));
            await SendAsync(
                server,
                new ClientboundContainerSetSlotPacket(-2, 0, 10, new ItemStack(stone, 1)),
                ct);
            await WaitForServerTextAsync(server, "inv 1", TimeSpan.FromSeconds(10), ct);
            int repeats = await CountServerTextAsync(server, "inv 1", TimeSpan.FromSeconds(2), ct);
            Assert.Equal(0, repeats);
        }
    }

    [Fact]
    public async Task Death_AnnouncementPacket_FiresWithVictim()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;
        (FakeJavaServer server, Client client, string root) = await StartClientAsync(ct);
        await using (server)
        await using (client)
        {
            await RunScriptAsync(client, root, "obit",
                WithHeader("on death\nsay \"d {player}\"\nend on\n"));
            await SendAsync(
                server,
                new ClientboundSystemChatPacket(
                    Component.Translatable("death.fell.accident.generic", Component.Text("Alice")), false),
                ct);
            await WaitForServerTextAsync(server, "d Alice", TimeSpan.FromSeconds(10), ct);
        }
    }

    [Fact]
    public async Task Death_SelfPush_FiresForSelf()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;
        (FakeJavaServer server, Client client, string root) = await StartClientAsync(ct);
        await using (server)
        await using (client)
        {
            await RunScriptAsync(client, root, "selfdeath",
                WithHeader("on death\nsay \"died {player}\"\nend on\n"));
            await SendAsync(server, new ClientboundSetHealthPacket(0, 20, 5), ct);
            await WaitForServerTextAsync(server, "died you", TimeSpan.FromSeconds(10), ct);
        }
    }

    [Fact]
    public async Task Death_PushAndAnnouncement_DedupeToOneFire()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;
        (FakeJavaServer server, Client client, string root) = await StartClientAsync(ct);
        await using (server)
        await using (client)
        {
            await RunScriptAsync(client, root, "once",
                WithHeader("on death\nsay \"died {player}\"\nend on\n"));
            await SendAsync(server, new ClientboundSetHealthPacket(0, 20, 5), ct);
            await SendAsync(
                server,
                new ClientboundSystemChatPacket(
                    Component.Translatable("death.fell.accident.generic", Component.Text("you")), false),
                ct);
            await WaitForServerTextAsync(server, "died you", TimeSpan.FromSeconds(10), ct);
            int total = 1 + await CountServerTextAsync(server, "died you", TimeSpan.FromSeconds(3), ct);
            Assert.Equal(1, total);
        }
    }

    [Fact]
    public async Task Respawn_SelfPush_FiresForSelf()
    {
        using var cts = new CancellationTokenSource(McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;
        (FakeJavaServer server, Client client, string root) = await StartClientAsync(ct);
        await using (server)
        await using (client)
        {
            await RunScriptAsync(client, root, "back",
                WithHeader("on respawn\nsay \"re {player}\"\nend on\n"));
            await SendAsync(
                server,
                new ClientboundRespawnPacket(
                    new CommonPlayerSpawnInfo(0, "minecraft:overworld", 0, 1, -1, false, false, null, 0, 63),
                    0,
                    null),
                ct);
            await WaitForServerTextAsync(server, "re you", TimeSpan.FromSeconds(10), ct);
        }
    }

    [Fact]
    public async Task Tps_EstimateArrival_FiresWithNumberAndNoneMspt()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        CancellationToken ct = cts.Token;
        (FakeJavaServer server, Client client, string root) = await StartClientAsync(ct);
        await using (server)
        await using (client)
        {
            await RunScriptAsync(client, root, "tickwatch",
                WithHeader("on tps\nsay \"t {tps} {mspt}\"\nend on\n"));
            await SendAsync(
                server,
                new ClientboundSetTimePacket(GameTime: 400, DayTime: 400, TickDayTime: false, ClockUpdates: []),
                ct);
            string first = await WaitForServerTextAsync(server, "t ", TimeSpan.FromSeconds(10), ct);
            System.Text.RegularExpressions.MatchCollection hits =
                System.Text.RegularExpressions.Regex.Matches(first, @"t (\S+) none");
            Assert.NotEmpty(hits);
            string tpsText = hits[^1].Groups[1].Value;
            Assert.True(
                double.TryParse(
                    tpsText,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out _),
                $"TPS field is not a number: '{first}'.");

            // A second sample moves the estimate (at most one more fire); after that, with no new samples, the hook stays quiet: polling reports, it does not spam.
            await Task.Delay(TimeSpan.FromMilliseconds(1200), ct);
            await SendAsync(
                server,
                new ClientboundSetTimePacket(GameTime: 440, DayTime: 440, TickDayTime: false, ClockUpdates: []),
                ct);
            int before = await CountServerTextAsync(server, "t ", TimeSpan.FromSeconds(3), ct);
            int after = await CountServerTextAsync(server, "t ", TimeSpan.FromSeconds(2), ct);
            Assert.Equal(before, after);
        }
    }

    [Fact]
    public async Task Suppression_StopEvent_WithholdsMessageFromReads()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        CancellationToken ct = cts.Token;
        (FakeJavaServer server, Client client, string root) = await StartClientAsync(ct);
        await using (server)
        await using (client)
        {
            await RunScriptAsync(client, root, "moderator",
                WithBareHeader("on chat when message contains \"secret\"\nstop event\nend on\n"));
            await SendAsync(
                server, new ClientboundSystemChatPacket(Component.Text("<Alice> hello ordinary"), false), ct);
            await WaitForReplAsync(client, "last_from(\"Alice\")", "hello ordinary", TimeSpan.FromSeconds(10), ct);
            await SendAsync(
                server, new ClientboundSystemChatPacket(Component.Text("<Alice> the secret word"), false), ct);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            CmdResult repl = await client.Commands.DispatchAsync("scripts repl last_from(\"Alice\")");
            Assert.Equal(CmdStatus.Done, repl.Status);
            Assert.Contains("hello ordinary", repl.Message ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", repl.Message ?? string.Empty, StringComparison.Ordinal);
        }
    }

    private static async Task WaitForReplAsync(
        Client client, string line, string needle, TimeSpan budget, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(budget);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        while (true)
        {
            linked.Token.ThrowIfCancellationRequested();
            CmdResult repl = await client.Commands.DispatchAsync($"scripts repl {line}");
            if (repl.Status == CmdStatus.Done
                && (repl.Message ?? string.Empty).Contains(needle, StringComparison.Ordinal))
                return;

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), linked.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                Assert.Fail($"Timed out waiting for repl '{line}' to contain '{needle}'.");
                throw new InvalidOperationException("Unreachable.");
            }
        }
    }
}
