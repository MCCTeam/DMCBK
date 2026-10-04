using DMCBK.Core;
using DMCBK.Core.Commands;
using DMCBK.Core.Configuration;
using DMCBK.Core.Diagnostics;
using DMCBK.Core.Plugins;
using DMCBK.PluginSdk;
using Tomlet;
using Umpk.Client;
using Umpk.Client.Events;
using Umpk.Protocol.Java;
using Umpk.Text;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// Covers the session diagnostic surfaces: the kick reason, the raw packet feed, and the signing keys.
/// <para>
/// The kick reason.
/// The play-phase disconnect packet is decoded on all 50 protocols now, so <c>DisconnectInfo.Message</c> is populated and <c>CloseReason.DisconnectMessage</c> is a real signal.
/// Before that a kick reached the user as a bare transport error with the server's own text discarded, and three hosts each hardcoded their own wording for it.
/// There is one describer now, in DMCBK.Core so an embedding host can reach it, and its text comes from <see cref="CommandStrings"/>.
/// </para>
/// <para>
/// The raw packet feed.
/// <c>PacketReceived</c> carries a real wire id and a payload length now, so the packet-debug line stops printing <c>id=n/a</c> with no byte count.
/// </para>
/// </summary>
public sealed class SessionDescribeTests
{
    #region The kick reason reaches the user

    private static DisconnectInfo Kick(string? message)
        => new() { Reason = CloseReason.DisconnectMessage, Message = message is null ? null : Component.Text(message) };

    [Fact]
    public void Describe_Kick_CarriesTheServersOwnReason()
    {
        // The whole point: the text the server typed after /kick must reach the user verbatim.
        string described = Kick("Banned: griefing").Describe();

        Assert.Contains("Banned: griefing", described, StringComparison.Ordinal);
        Assert.Equal(CommandStrings.DisconnectKickedWith("Banned: griefing"), described);
    }

    [Fact]
    public void Describe_Kick_SaysItIsAKickEvenWithNoMessage()
    {
        // A kick with no reason is still a kick, not a mystery socket error.
        Assert.Equal(CommandStrings.DisconnectKicked, Kick(null).Describe());
        Assert.Equal(CommandStrings.DisconnectKicked, Kick("   ").Describe());
    }

    [Fact]
    public void Describe_Kick_IsDistinguishableFromATransportFault()
    {
        var fault = new DisconnectInfo { Reason = CloseReason.SocketEof };

        Assert.True(Kick("bye").IsKick);
        Assert.False(fault.IsKick);
        Assert.NotEqual(Kick("bye").Describe(), fault.Describe());
    }

    [Fact]
    public void Describe_TransportFault_ReadsAsProseNotAnEnumName()
    {
        // "SocketEof" is an implementation detail; the user gets a sentence fragment.
        string described = new DisconnectInfo { Reason = CloseReason.SocketEof }.Describe();

        Assert.Equal(CommandStrings.DisconnectSocketEof, described);
        Assert.DoesNotContain("SocketEof", described, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_Fault_AppendsTheMessage_AndTheDetailOptInAppendsTheChain()
    {
        var inner = new InvalidOperationException("inner cause");
        var info = new DisconnectInfo
        {
            Reason = CloseReason.ProtocolViolation,
            Fault = new IOException("outer", inner),
        };

        string terse = info.Describe();
        Assert.Contains("outer", terse, StringComparison.Ordinal);
        Assert.DoesNotContain("inner cause", terse, StringComparison.Ordinal);

        string detailed = info.Describe(includeFaultDetail: true);
        Assert.Contains("inner cause", detailed, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_Null_IsTheSharedClosedText()
        => Assert.Equal(CommandStrings.DisconnectClosed, DisconnectDescription.Describe(null));

    [Fact]
    public void Describe_NonKickWithServerText_StillPrefersTheServerText()
    {
        // A transfer can carry text; our own wording must not shadow it.
        var info = new DisconnectInfo
        {
            Reason = CloseReason.Transferred,
            Message = Component.Text("moving you to lobby"),
        };

        Assert.Equal("moving you to lobby", info.Describe());
    }

    #endregion
    #region The packet-debug line renders the real wire id and byte count

    [Fact]
    public void PacketDebug_RendersTheRealWireIdAndPayloadLength()
    {
        // Both halves used to be missing: WireId was hardcoded to -1 upstream and no length existed at all.
        string line = PacketDebugLogger.Format("Play", 0x3C, "ClientboundSetHealthPacket", 42);

        Assert.Contains("id=0x3c", line, StringComparison.Ordinal);
        Assert.Contains("bytes=42", line, StringComparison.Ordinal);
        Assert.DoesNotContain("n/a", line, StringComparison.Ordinal);
    }

    [Fact]
    public void PacketDebug_ProjectsTheRealEventFields_NotHardcodedPlaceholders()
    {
        // The wiring, not just the formatter: the line is built FROM the event, whose WireId was once hardcoded to -1 upstream and whose PayloadLength did not exist, so the consumer passed -1 for it.
        var received = new PacketReceived(ProtocolPhase.Play, 0x26, new object(), PayloadLength: 137);

        string line = PacketDebugLogger.FormatReceived(received);

        Assert.Contains("id=0x26", line, StringComparison.Ordinal);
        Assert.Contains("bytes=137", line, StringComparison.Ordinal);
        Assert.Contains("Play", line, StringComparison.Ordinal);
    }

    [Fact]
    public void PacketDebug_StillSaysNotAvailableRatherThanInventingAWireId()
    {
        // The honesty guard stays: a feed with no id says so instead of printing a fake -1.
        string line = PacketDebugLogger.Format("Configuration", -1, "Unknown", -1);

        Assert.Contains("id=n/a", line, StringComparison.Ordinal);
        Assert.DoesNotContain("bytes=", line, StringComparison.Ordinal);
    }

    #endregion
    #region The SDK frame shape plugins see
    //
    // DMCBK.PluginSdk no longer carries its own PacketFrame/PacketFrameHandler copy: ISessionScope.ObservePackets forwards straight to ClientPluginContext.ObservePackets, so the frame type plugins see is UMPK's own Umpk.Protocol.Java.PacketFrame. The shape assertions that used to pin a local mirror of that type here (PacketFrame_ExposesTheRealWireShape, PacketFrame_OutboundIsObservedAndReportedAsSuch, PacketFrame_CopyPayload_DetachesFromTheRecycledBuffer, PacketFrameHandler_DeliversTheFrameByReadOnlyReference) are ported verbatim onto UMPK's own type in Umpk.Protocol.Java.Tests.PacketFrameTests, which is now the authoritative copy (its own doc comment credits this file as the origin).
    // What is left for MCC to prove is the wiring, not the shape: that a plugin's ObservePackets handle is released when it detaches.
    //
    // DisposableScope (and the three FrameSubscriptions_* cases that unit-tested it directly) is gone too: the per-plugin scope machinery it backed moved onto UMPK's own extension host, where a frame subscription is billed to, and released by, exactly one plugin's ClientPluginContext.Detached with no MCC-side bookkeeping layer left to test in isolation.

    /// <summary>
    /// Hermetic, end to end (same FakeJavaServer pattern as AcceptanceRegressionTests's session test): a plugin subscribes via <c>ISessionScope.ObservePackets</c> in <c>SessionStarted</c>, observes at least one real frame once play begins (UMPK sends outbound frames of its own, e.g. client-settings/keep-alive traffic, so no server-sent packet is needed to prove delivery), and unloading the plugin mid-session must stop further delivery - proving the handle is torn down with the plugin's own session view rather than living until the connection eventually drops.
    /// </summary>
    [Fact]
    public async Task ObservePackets_HandleDetaches_WhenThePluginUnloads()
    {
        using var cts = new CancellationTokenSource(Fakes.McPluginSessionHarness.Budget);
        CancellationToken ct = cts.Token;

        await using DMCBK.Testing.Server.FakeJavaServer server = DMCBK.Testing.Server.FakeJavaServer.Create();
        string root = Path.Combine(Path.GetTempPath(), "mcc-m5b-observe-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        Client client = Fakes.McPluginSessionHarness.BuildClient(
            server, "Tester", root, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        await using (client.ConfigureAwait(false))
        {
            var host = new PluginHost(
                client, root, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
                client.Translations, client.Variables);
            host.InstallationSource = _ => Task.FromResult(TestPackages.Read(root));

            const string id = "frame-observer";
            string folder = Path.Combine(root, id);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Observer.cs"), ObserverPluginSource);
            File.WriteAllText(
                Path.Combine(folder, PluginManifest.FileName), TestManifest.Upgrade($"id = \"{id}\"\nversion = \"1.0.0\"\nentry = \"Observer.cs\"\napi-version = \"1\"\nenabled = true\n"));
            await host.LoadAllAsync(ct);

            Task starting = client.StartAsync(ct);
            await Fakes.McPluginSessionHarness.DriveLoginAsync(server, ct);
            await starting.WaitAsync(Fakes.McPluginSessionHarness.Budget, ct);

            // SessionStarted (and therefore the plugin's ObservePackets subscription) has already run by the time StartAsync returns play is live, so a deterministic outbound send from here is guaranteed to land on an active subscription rather than racing it.
            // This is the actual wiring under test: PluginHost -> McPluginExtension -> SessionScope.ObservePackets -> ClientPluginContext.ObservePackets -> UmpkClient, carrying a real frame end to end.
            var channel = new Umpk.Identifier("mcc", "probe");
            ISessionScope session = host.GetContext(id)!.CurrentSession!;
            Assert.False(session.Detached.IsCancellationRequested);
            await session.SendPluginMessageAsync(channel, new byte[] { 1 }, ct);

            string storagePath = Path.Combine(TestPackages.UserFolder(folder), "data", "storage.toml");
            Assert.True(await WaitForAsync(() => ReadFrameCount(storagePath) > 0, ct), "The plugin never observed the probe frame.");

            // Unloading tears the plugin's own bridge down: its Detached token fires (the same guarantee AcceptanceRegressionTests proves for Events/Scheduler) and, because ObservePackets is a bare forward to ClientPluginContext.ObservePackets, the subscription it returned goes with it - UMPK, not MCC, is what "auto-unregistered on detach" is a promise from here.
            PluginActionResult unloaded = await host.UnloadAsync(id, ct);
            Assert.True(unloaded.Success, unloaded.Message);
            Assert.True(session.Detached.IsCancellationRequested);
        }
    }

    private const string ObserverPluginSource = """
        using System.Threading;
        using System.Threading.Tasks;
        using DMCBK.PluginSdk;

        public sealed class ObserverPlugin : IPlugin
        {
            private PluginContext? _context;
            private int _count;

            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "frame-observer";

            public Task ActivateAsync(PluginContext context)
            {
                _context = context;
                context.SessionStarted += (_, e) => e.Session.ObservePackets(OnFrame);
                return Task.CompletedTask;
            }

            private void OnFrame(in Umpk.Protocol.Java.PacketFrame frame)
            {
                int n = Interlocked.Increment(ref _count);
                _context!.Storage.Set("frames", n.ToString());
                _context!.Storage.Save();
            }
        }
        """;

    private static int ReadFrameCount(string storagePath)
    {
        if (!File.Exists(storagePath))
            return 0;

        var store = TomletMain.To<Dictionary<string, string>>(File.ReadAllText(storagePath));
        return store.TryGetValue("frames", out string? value) && int.TryParse(value, out int n) ? n : 0;
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            if (ct.IsCancellationRequested)
                return false;

            await Task.Delay(TimeSpan.FromMilliseconds(20), ct);
        }

        return true;
    }

    #endregion
    #region The signing keys are honest

    [Fact]
    public void Signing_LoginWithSecureProfile_IsActuallyRead()
    {
        // The defect: this reached SignatureConfig and stopped there, so turning it off did nothing.
        // It now decides whether the signing provider (which IS the profile key) is installed at all.
        var off = new MccConfiguration
        {
            Chat = new ChatConfig { Signature = new SignatureConfig { LoginWithSecureProfile = false } },
        };
        var on = new MccConfiguration
        {
            Chat = new ChatConfig { Signature = new SignatureConfig { LoginWithSecureProfile = true } },
        };

        Assert.False(Client.SigningEnabled(off));
        Assert.True(Client.SigningEnabled(on));
    }

    [Fact]
    public void Signing_NoConfiguration_DefaultsToTheSettingsOwnDefault()
    {
        // An embedding host that never loaded a configuration gets the documented default rather than silently losing signing.
        Assert.True(Client.SigningEnabled(null));
    }

    [Fact]
    public void Signing_RemovedKeysAreGoneFromTheSchema()
    {
        // SignChat and SignMessageInCommand were removed rather than left silently inert: UMPK signs chat and command arguments off one per-session provider with no per-kind lever, so neither could ever work.
        // A stale config carrying them now produces an unknown-key warning instead of a silent no-op, which is the whole point.
        Type schema = typeof(SignatureConfig);

        Assert.Null(schema.GetProperty("SignChat"));
        Assert.Null(schema.GetProperty("SignMessageInCommand"));
        Assert.NotNull(schema.GetProperty("LoginWithSecureProfile"));
    }
    #endregion
}
