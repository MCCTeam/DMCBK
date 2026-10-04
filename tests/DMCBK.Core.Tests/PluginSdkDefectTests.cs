using System.Collections.Concurrent;
using System.Reflection;
using DMCBK.Core;
using DMCBK.Core.Commands;
using DMCBK.Core.Diagnostics;
using DMCBK.Core.Plugins;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Umpk.Client;
using Umpk.Client.Events;
using Umpk.Client.Movement;
using Umpk.Client.Plugins;
using Umpk.Hosting;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The four plugin-API defects, each of which was a surface that existed and did nothing.
/// Every test here is written so that removing the fix makes it FAIL rather than merely stop being exercised:
/// <list type="number">
/// <item>
/// <see cref="IPlugin"/> had no teardown hook, so a plugin's own network clients and file handles survived unload.
/// Proved by a plugin that records its teardown, and by one that hangs in it.
/// </item>
/// <item>
/// <see cref="IPluginMessenger"/> keyed subscriptions by <see cref="Type"/>, so a plugin-defined contract never unified across the per-plugin collectible load contexts and every such message was dropped in silence.
/// Proved end to end with two real plugins in two real load contexts.
/// </item>
/// <item>
/// <c>ISessionScope.Commands</c> exposed a scope that drives completion only, so a command registered through it never ran.
/// Proved by dispatching one.
/// </item>
/// <item><c>logging.packetdebugmessages</c> was bound in five places and read by nobody.</item>
/// </list>
/// </summary>
public sealed class PluginSdkDefectTests
{
    // ---------------------------------------------------------------------------------------------------
    // 1. Teardown hook
    // ---------------------------------------------------------------------------------------------------

    // Records its own teardown into the storage sandbox.
    // Without the host calling DeactivateAsync the marker is never written, which is exactly the pre-fix behavior.
    private const string TeardownPluginSource = """
        using System.Threading;
        using System.Threading.Tasks;
        using DMCBK.PluginSdk;

        public sealed class TeardownPlugin : IPlugin
        {
            private PluginContext? _context;

            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "teardown-probe";

            public Task ActivateAsync(PluginContext context)
            {
                _context = context;
                return Task.CompletedTask;
            }

            public Task DeactivateAsync(CancellationToken ct)
            {
                _context!.Storage.Set("tornDown", "1");
                _context!.Storage.Save();
                return Task.CompletedTask;
            }
        }
        """;

    // Never returns from teardown.
    // The host must cancel it, warn, and finish the unload anyway.
    // The task completes only on CANCELLATION and involves no timer at all, so what the host has to do to get past it is the same whether the machine is idle or loaded.
    private const string HangingTeardownPluginSource = """
        using System.Threading;
        using System.Threading.Tasks;
        using DMCBK.PluginSdk;

        public sealed class HangingPlugin : IPlugin
        {
            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "hang-probe";

            public Task ActivateAsync(PluginContext context) => Task.CompletedTask;

            public Task DeactivateAsync(CancellationToken ct)
            {
                var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                ct.Register(() => never.TrySetCanceled(ct));
                return never.Task;
            }
        }
        """;

    [Fact]
    public async Task Teardown_Runs_OnUnload()
    {
        using var fixture = new HostFixture();
        fixture.WritePlugin("teardown-probe", "Teardown.cs", TeardownPluginSource);
        await fixture.Host.LoadAllAsync();

        PluginActionResult unload = await fixture.Host.UnloadAsync("teardown-probe");

        Assert.True(unload.Success);
        string storage = Path.Combine(fixture.Root, "userdata", "teardown-probe", "data", "storage.toml");
        Assert.True(File.Exists(storage));
        Assert.Contains("tornDown", File.ReadAllText(storage), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Teardown_Runs_OnReload_Too()
    {
        using var fixture = new HostFixture();
        fixture.WritePlugin("teardown-probe", "Teardown.cs", TeardownPluginSource);
        await fixture.Host.LoadAllAsync();

        PluginActionResult reload = await fixture.Host.ReloadAsync("teardown-probe");

        Assert.True(reload.Success);
        string storage = Path.Combine(fixture.Root, "userdata", "teardown-probe", "data", "storage.toml");
        Assert.Contains("tornDown", File.ReadAllText(storage), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Teardown_ThatNeverReturns_CannotWedgeTheUnload()
    {
        using var fixture = new HostFixture();
        fixture.Host.TeardownTimeout = TimeSpan.FromMilliseconds(250);
        fixture.WritePlugin("hang-probe", "Hang.cs", HangingTeardownPluginSource);
        await fixture.Host.LoadAllAsync();

        // "Did not wedge" is proved by the unload COMPLETING, which the WaitAsync below is the detector for: a wedged unload never completes and the await fails the test.
        // The old form also measured elapsed milliseconds against a 10s ceiling, which is a second, load-sensitive assertion of the same thing and the one that could fail on a busy machine while the behaviour was perfectly correct.
        PluginActionResult unload = await fixture.Host.UnloadAsync("hang-probe").WaitAsync(HangGuard);

        Assert.True(unload.Success);

        // And it got there by CANCELLING the teardown and saying so, not by silently skipping it.
        Assert.Contains(
            fixture.Logs,
            line => line.Contains("DeactivateAsync", StringComparison.Ordinal)
                && line.Contains("hang-probe", StringComparison.Ordinal));
    }

    [Fact]
    public void Teardown_IsOptional_SoPluginsWrittenBeforeItStillCompileAndRun()
    {
        // A default interface member, not a required one: the 23 shipped plugins predate the hook and none of them had to change.
        // Calling it on a plugin that does not implement it must be a no-op, not a throw.
        IPlugin plugin = new LegacyShapedPlugin();

        Assert.True(plugin.DeactivateAsync(CancellationToken.None).IsCompletedSuccessfully);
    }

    private sealed class LegacyShapedPlugin : IPlugin
    {
        public void Configure(PluginDescriptor descriptor) => descriptor.Id = "legacy";

        public Task ActivateAsync(PluginContext context) => Task.CompletedTask;
    }

    // ---------------------------------------------------------------------------------------------------
    // 2. Messenger contract identity
    // ---------------------------------------------------------------------------------------------------

    // Two plugins that each declare their OWN copy of the same contract type.
    // Loaded into two collectible contexts they are two different runtime types, which is precisely the case that used to vanish.
    private const string ContractSubscriberSource = """
        using System.Threading.Tasks;
        using DMCBK.PluginSdk;

        namespace SharedContracts { public sealed class Ping { public string Text = ""; } }

        public sealed class SubPlugin : IPlugin
        {
            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "contract-sub";

            public Task ActivateAsync(PluginContext context)
            {
                context.Messenger.Subscribe<SharedContracts.Ping>(_ => { });
                return Task.CompletedTask;
            }
        }
        """;

    private const string ContractPublisherSource = """
        using System;
        using System.Threading.Tasks;
        using DMCBK.PluginSdk;

        namespace SharedContracts { public sealed class Ping { public string Text = ""; } }

        public sealed class PubPlugin : IPlugin
        {
            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "contract-pub";

            public Task ActivateAsync(PluginContext context)
            {
                try
                {
                    context.Messenger.Publish(new SharedContracts.Ping { Text = "hello" });
                    context.Storage.Set("outcome", "silently-dropped");
                }
                catch (PluginContractMismatchException ex)
                {
                    context.Storage.Set("outcome", "diagnosed");
                    context.Storage.Set("contract", ex.Contract);
                    context.Storage.Set("other", ex.SubscriberOwner);
                }

                context.Storage.Save();
                return Task.CompletedTask;
            }
        }
        """;

    // Both sides use a BCL type, which loads in the shared default context and therefore unifies.
    private const string SharedContractSubscriberSource = """
        using System.Threading.Tasks;
        using DMCBK.PluginSdk;

        public sealed class SharedSubPlugin : IPlugin
        {
            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "shared-sub";

            public Task ActivateAsync(PluginContext context)
            {
                context.Messenger.Subscribe<string[]>(message =>
                {
                    context.Storage.Set("received", message[0]);
                    context.Storage.Save();
                });
                return Task.CompletedTask;
            }
        }
        """;

    private const string SharedContractPublisherSource = """
        using System.Threading.Tasks;
        using DMCBK.PluginSdk;

        public sealed class SharedPubPlugin : IPlugin
        {
            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "shared-pub";

            public Task ActivateAsync(PluginContext context)
            {
                context.Messenger.Publish(new[] { "delivered" });
                return Task.CompletedTask;
            }
        }
        """;

    [Fact]
    public async Task Messenger_PluginDefinedContract_IsDiagnosed_NotDropped()
    {
        using var fixture = new HostFixture();
        string subscriber = fixture.WritePlugin("contract-sub", "Sub.cs", ContractSubscriberSource);
        string publisher = fixture.WritePlugin("contract-pub", "Pub.cs", ContractPublisherSource);

        // Deterministic order: the subscriber has to be listening before the publisher activates.
        Assert.True((await fixture.Host.LoadAsync(subscriber)).Success);
        Assert.True((await fixture.Host.LoadAsync(publisher)).Success);

        string storage = File.ReadAllText(Path.Combine(fixture.Root, "userdata", "contract-pub", "data", "storage.toml"));
        Assert.Contains("diagnosed", storage, StringComparison.Ordinal);
        Assert.DoesNotContain("silently-dropped", storage, StringComparison.Ordinal);

        // The diagnostic names the contract and the plugin on the other side, which is what makes it fixable.
        Assert.Contains("SharedContracts.Ping", storage, StringComparison.Ordinal);
        Assert.Contains("contract-sub", storage, StringComparison.Ordinal);

        // And the host warned at REGISTRATION time, before any message was even published.
        Assert.Contains(
            fixture.Logs,
            line => line.Contains("SharedContracts.Ping", StringComparison.Ordinal)
                && line.Contains("does not unify", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Messenger_SharedContract_StillDelivers()
    {
        using var fixture = new HostFixture();
        string subscriber = fixture.WritePlugin("shared-sub", "SharedSub.cs", SharedContractSubscriberSource);
        string publisher = fixture.WritePlugin("shared-pub", "SharedPub.cs", SharedContractPublisherSource);

        Assert.True((await fixture.Host.LoadAsync(subscriber)).Success);
        Assert.True((await fixture.Host.LoadAsync(publisher)).Success);

        string storage = File.ReadAllText(Path.Combine(fixture.Root, "userdata", "shared-sub", "data", "storage.toml"));
        Assert.Contains("delivered", storage, StringComparison.Ordinal);
    }

    [Fact]
    public void Contract_Name_IsAssemblyAgnostic_SoTwoPrivateCopiesCollide()
    {
        // The whole detection rests on this: two copies of one declaration must produce ONE name.
        // Keying by Type put them in separate buckets, which is why nothing ever noticed.
        Assert.Equal("System.String[]", PluginContract.NameOf(typeof(string[])));
        Assert.Equal(
            "System.Collections.Generic.List`1[System.String]",
            PluginContract.NameOf(typeof(List<string>)));
        Assert.DoesNotContain("Version=", PluginContract.NameOf(typeof(List<CmdResult>)), StringComparison.Ordinal);
    }

    [Fact]
    public void Contract_Shared_IsTrueForBclAndSdkTypes()
    {
        Assert.True(PluginContract.IsShared(typeof(string[])));
        Assert.True(PluginContract.IsShared(typeof(PluginContext)));
        Assert.True(PluginContract.IsShared(typeof(Dictionary<string, int>)));
        Assert.Equal("default", PluginContract.LoadContextNameOf(typeof(string)));
    }

    // ---------------------------------------------------------------------------------------------------
    // 3. Session/plugin command scopes
    // ---------------------------------------------------------------------------------------------------

    private const string SessionCommandPluginSource = """
        using System.Threading.Tasks;
        using DMCBK.Core.Commands;
        using DMCBK.PluginSdk;
        using Umpk.Commands;

        public sealed class ProbeCommand : CommandBase
        {
            public override string CmdName => "sessionprobe";
            public override string CmdDesc => "session probe";
            public override string CmdUsage => "sessionprobe";

            public override void Register(CommandBuilder<CommandContext> builder)
            {
                RegisterHelp(builder, CmdName, ShowUsage);
                builder.Literal("sessionprobe", l => l.Executes(ctx => ctx.Source.Result.Ok("session-ran")));
            }
        }

        public sealed class PluginCommand : CommandBase
        {
            public override string CmdName => "pluginprobe";
            public override string CmdDesc => "plugin probe";
            public override string CmdUsage => "pluginprobe";

            public override void Register(CommandBuilder<CommandContext> builder)
            {
                RegisterHelp(builder, CmdName, ShowUsage);
                builder.Literal("pluginprobe", l => l.Executes(ctx => ctx.Source.Result.Ok("plugin-ran")));
            }
        }

        public sealed class CommandProbePlugin : IPlugin
        {
            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "command-probe";

            public Task ActivateAsync(PluginContext context)
            {
                context.Commands.Register(new PluginCommand());
                context.SessionStarted += (_, e) => e.Session.Commands.Register(new ProbeCommand());
                return Task.CompletedTask;
            }
        }
        """;

    /// <summary>
    /// Loads the command probe and reports success.
    /// The Roslyn plugin compile only references the assemblies that happen to be LOADED, so a probe deriving from <see cref="CommandBase"/> needs Umpk.Commands touched first; without that the compile fails and the dispatch assertions below would be testing nothing.
    /// </summary>
    private static async Task<bool> LoadCommandProbeAsync(HostFixture fixture)
    {
        _ = typeof(Umpk.Commands.CommandBuilder<CommandContext>);
        _ = typeof(PluginContext);
        _ = typeof(Client);
        _ = typeof(UmpkClient);

        PluginActionResult result = await fixture.Host.LoadAllAsync();
        Assert.True(fixture.Host.List().Single().Loaded, string.Join("\n", fixture.Logs));
        return result.Success;
    }

    [Fact]
    public async Task SessionScopeCommand_Executes_AndIsUnregisteredAtSessionEnd()
    {
        using var fixture = new HostFixture();
        fixture.WritePlugin("command-probe", "CommandProbe.cs", SessionCommandPluginSource);
        Assert.True(await LoadCommandProbeAsync(fixture));

        // Before a session: the session-scoped command does not exist yet.
        Assert.Equal(CmdStatus.NotRun, (await fixture.Client.Commands.DispatchAsync("sessionprobe")).Status);

        PluginContext context = fixture.Host.GetContext("command-probe")!;
        var scope = new FakeSessionScope(fixture.Client.Commands);
        context.AttachSession(scope);

        CmdResult ran = await fixture.Client.Commands.DispatchAsync("sessionprobe");
        Assert.Equal(CmdStatus.Done, ran.Status);
        Assert.Equal("session-ran", ran.Message);

        context.DetachSession();
        scope.SimulateDetached();

        Assert.Equal(CmdStatus.NotRun, (await fixture.Client.Commands.DispatchAsync("sessionprobe")).Status);
    }

    [Fact]
    public async Task SessionScopeCommand_ComesBack_OnReconnect()
    {
        using var fixture = new HostFixture();
        fixture.WritePlugin("command-probe", "CommandProbe.cs", SessionCommandPluginSource);
        Assert.True(await LoadCommandProbeAsync(fixture));

        PluginContext context = fixture.Host.GetContext("command-probe")!;
        var first = new FakeSessionScope(fixture.Client.Commands);
        context.AttachSession(first);
        context.DetachSession();
        first.SimulateDetached();
        context.AttachSession(new FakeSessionScope(fixture.Client.Commands));

        Assert.Equal(CmdStatus.Done, (await fixture.Client.Commands.DispatchAsync("sessionprobe")).Status);
    }

    [Fact]
    public async Task PluginScopeCommand_Executes_AndIsUnregisteredOnUnload()
    {
        using var fixture = new HostFixture();
        fixture.WritePlugin("command-probe", "CommandProbe.cs", SessionCommandPluginSource);
        Assert.True(await LoadCommandProbeAsync(fixture));

        CmdResult ran = await fixture.Client.Commands.DispatchAsync("pluginprobe");
        Assert.Equal(CmdStatus.Done, ran.Status);
        Assert.Equal("plugin-ran", ran.Message);

        Assert.True((await fixture.Host.UnloadAsync("command-probe")).Success);

        // RegisterHostCommand had no unregister, so this used to keep working after unload, and with it the command kept the plugin instance and its whole collectible load context alive.
        Assert.Equal(CmdStatus.NotRun, (await fixture.Client.Commands.DispatchAsync("pluginprobe")).Status);
    }

    [Fact]
    public async Task BuiltinCommands_SurviveAScopedUnregister()
    {
        // The unregister rebuilds the dispatcher; the builtins and host commands must come back with it.
        using var fixture = new HostFixture();
        fixture.WritePlugin("command-probe", "CommandProbe.cs", SessionCommandPluginSource);
        Assert.True(await LoadCommandProbeAsync(fixture));
        await fixture.Host.UnloadAsync("command-probe");

        Assert.NotEqual(CmdStatus.NotRun, (await fixture.Client.Commands.DispatchAsync("help")).Status);
    }

    #region the adjacent hazard: dispatching inline from a session event handler

    [Fact]
    public async Task CommandWork_ThatBlocksTheSessionLoop_ThrowsInsteadOfHanging()
    {
        var scheduler = new ChannelSessionScheduler();
        var never = new TaskCompletionSource();
        Exception? captured = null;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Runs ON the loop, exactly like a plugin's session event handler, and blocks on async work the loop itself would have to run.
        // Before the guard this never returned; it was seen live as a server timeout.
        //
        // This one is timing-INDEPENDENT even though it carries a budget: the work never completes and the probe cannot run, because the loop it would run on is the loop this item is blocking.
        // Neither task can complete on any machine at any load, so the wait has exactly one possible outcome; load moves only how long it takes to reach it.
        bool sawItselfOnTheLoop = false;
        scheduler.Post(() =>
        {
            // The realism half of this file's WaitForWork coverage: a real ChannelSessionScheduler really does report IsCurrent inside a posted item, which is what the guard branches on.
            // The two tests below drive the branches from a stand-in scheduler so their outcome is decided by an event rather than by a clock; this is where the branch condition itself is checked for real.
            sawItselfOnTheLoop = scheduler.IsCurrent;
            try
            {
                Client.WaitForWork(never.Task, scheduler, BlockedProbeWindow);
            }
            catch (Exception ex)
            {
                captured = ex;
            }

            done.SetResult();
        });

        try
        {
            await done.Task.WaitAsync(HangGuard);
        }
        finally
        {
            // Release the loop item unconditionally.
            // Without the guard this test's subject genuinely hangs, and a hang must surface as a failed assertion, never as a wedged test host.
            never.TrySetResult();
            await scheduler.DisposeAsync();
        }

        Assert.True(sawItselfOnTheLoop, "A real posted work item must report IsCurrent, or the guard never arms.");
        Assert.IsType<DmcbkSessionLoopBlockedException>(captured);
        Assert.Contains("session event handler", captured!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommandWork_OffTheLoop_IsNeverCutShort()
    {
        // A legitimately slow command from the console reader thread must still be allowed to take its time.
        //
        // The old form slept 600ms against a 100ms budget and called that a proof, which is a stopwatch standing in for a synchronisation primitive: it only ever said "600 is more than 100".
        // This one uses a budget of ZERO, which no elapsed time can beat, and finishes the work only once the caller is demonstrably blocked in the wait.
        // So the guard has to skip the budget outright for an off-loop caller, and it must not even post a probe.
        await using var real = new ChannelSessionScheduler();
        Assert.False(real.IsCurrent, "The test thread is not the session loop; the off-loop branch is the one under test.");

        var scheduler = new RecordingScheduler(isCurrent: false);
        var work = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new ManualResetEventSlim(false);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? captured = null;

        var waiter = new Thread(() =>
        {
            entered.Set();
            try
            {
                Client.WaitForWork(work.Task, scheduler, TimeSpan.Zero);
            }
            catch (Exception ex)
            {
                captured = ex;
            }

            returned.SetResult();
        })
        {
            IsBackground = true,
            Name = "off-loop-waiter",
        };

        waiter.Start();
        Assert.True(entered.Wait(HangGuard));

        // Wait for the OBSERVABLE state the test is about: the caller is parked inside the wait.
        // Capped, and if the guard wrongly threw instead the thread will have returned, which the first branch catches and the assertions below then fail on.
        // No branch here waits out a fixed delay.
        SpinWait.SpinUntil(
            () => returned.Task.IsCompleted
                || (waiter.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
            HangGuard);

        work.SetResult();
        await returned.Task.WaitAsync(HangGuard);

        Assert.Null(captured);
        Assert.True(work.Task.IsCompletedSuccessfully);
        Assert.Equal(0, scheduler.PostCount);
    }

    [Fact]
    public async Task CommandWork_OnTheLoop_ThatFinishes_IsNotFlaggedAsBlocked()
    {
        // The work finishes because the TEST finishes it, not because a 50ms timer beat a 5s budget.
        //
        // The synchronisation is a real happens-before rather than a delay: the guard posts its probe only AFTER it has decided the work is still pending, so a test that waits for that post knows the wait is under way before it completes the work.
        // The stand-in scheduler never runs the probe, which is exactly what a blocked loop does, so the only thing that can end the wait is the work completing.
        // The budget is then irrelevant, which is the property being pinned.
        var probePosted = new ManualResetEventSlim(false);
        var scheduler = new RecordingScheduler(isCurrent: true, onPost: probePosted.Set);
        var work = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? captured = null;

        var waiter = new Thread(() =>
        {
            try
            {
                Client.WaitForWork(work.Task, scheduler, GenerousProbeWindow);
            }
            catch (Exception ex)
            {
                captured = ex;
            }

            returned.SetResult();
        })
        {
            IsBackground = true,
            Name = "on-loop-waiter",
        };

        waiter.Start();
        Assert.True(
            probePosted.Wait(HangGuard),
            "The guard never posted its probe, so it never reached the wait this test is about.");

        work.SetResult();

        await returned.Task.WaitAsync(HangGuard);
        Assert.Null(captured);
        Assert.Equal(1, scheduler.PostCount);
    }

    /// <summary>
    /// How long a test waits for something that should already have happened before calling it a hang.
    /// Only ever reached on a real failure, so it is set well past any scheduling delay rather than tuned.
    /// </summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The probe window for the genuinely-blocked case, where neither the work nor the probe can ever complete, so this only decides how fast the single possible outcome is reached.
    /// </summary>
    private static readonly TimeSpan BlockedProbeWindow = TimeSpan.FromMilliseconds(400);

    /// <summary>The probe window for cases where the work is completed by the test and the budget must not matter.</summary>
    private static readonly TimeSpan GenerousProbeWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A scheduler that answers a fixed <see cref="ISessionScheduler.IsCurrent"/> and records posts without ever running them.
    /// It makes "an off-loop caller is not probed and not budgeted" assertable directly, instead of inferring it from the fact that a long enough sleep happened to survive.
    /// </summary>
    private sealed class RecordingScheduler(bool isCurrent, Action? onPost = null) : ISessionScheduler
    {
        private int _posts;

        public bool IsCurrent { get; } = isCurrent;

        public int PostCount => Volatile.Read(ref _posts);

        /// <summary>
        /// Records the post and signals the caller, but never RUNS the work: that is what a session loop blocked by the very item doing the waiting looks like, and it is the case the guard exists for.
        /// </summary>
        public void Post(Action work)
        {
            Interlocked.Increment(ref _posts);
            onPost?.Invoke();
        }

        public Task InvokeAsync(Action work, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TResult> InvokeAsync<TResult>(Func<TResult> work, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task InvokeAsync(Func<ValueTask> work, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TResult> InvokeAsync<TResult>(Func<ValueTask<TResult>> work, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ---------------------------------------------------------------------------------------------------
    // 4. logging.packetdebugmessages
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void PacketDebug_Line_SaysNotAvailable_RatherThanPrintingAFakeWireId()
    {
        // UMPK publishes PacketReceived with WireId = -1 today.
        // Printing "id=-1" would be a lie; the line says so, and starts printing the real id the moment the feed carries one.
        Assert.Equal("packet Play ClientboundChatPacket id=n/a", PacketDebugLogger.Format("Play", -1, "ClientboundChatPacket", -1));
        Assert.Equal("packet Play ClientboundChatPacket id=0x21", PacketDebugLogger.Format("Play", 0x21, "ClientboundChatPacket", -1));
    }

    [Fact]
    public void PacketDebug_Line_CarriesThePayloadSize_WhenTheFeedHasOne()
    {
        Assert.Equal(
            "packet Configuration RegistryDataPacket id=0x07 bytes=4096",
            PacketDebugLogger.Format("Configuration", 7, "RegistryDataPacket", 4096));
    }

    [Fact]
    public void PacketDebug_Exclusions_MatchTheConfiguredNamesCaseInsensitively()
    {
        string[] exclusions = ["KeepAlive", " Ping "];

        Assert.True(PacketDebugLogger.IsExcluded("ClientboundKeepAlivePacket", exclusions));
        Assert.True(PacketDebugLogger.IsExcluded("ClientboundPingPacket", exclusions));
        Assert.True(PacketDebugLogger.IsExcluded("clientboundkeepalivepacket", exclusions));
        Assert.False(PacketDebugLogger.IsExcluded("ClientboundChatPacket", exclusions));
        Assert.False(PacketDebugLogger.IsExcluded("ClientboundChatPacket", []));
    }

    [Fact]
    public void PacketDebug_HasAConsumer_WiredIntoTheSessionSetup()
    {
        // The defect was not a wrong line, it was NO CALLER: five bindings and zero consumers.
        // A formatter test cannot see that, so assert the call site exists by looking for the token in the session builder's IL.
        // False negatives are impossible here; that is the property that matters.
        // DmcbkSessionFactory.CreateAsync, not Client, since the run-EstablishSessionAsync-equivalent session-build logic (including this wiring) moved there when Client started running the session on UmpkClientSupervisor.
        MethodInfo target = typeof(PacketDebugLogger)
            .GetMethod(nameof(PacketDebugLogger.TryAttach), BindingFlags.Static | BindingFlags.NonPublic)!;

        Assert.True(
            CallsMethod(typeof(DmcbkSessionFactory), target),
            "DmcbkSessionFactory no longer calls PacketDebugLogger.TryAttach, so logging.packetdebugmessages has no consumer again.");
    }

    /// <summary>
    /// True when any method on <paramref name="declaring"/> carries a metadata token that resolves to <paramref name="target"/>.
    /// Scans the IL for 4-byte windows and asks the module to resolve them, which can in principle over-report but can never miss a real call.
    /// </summary>
    private static bool CallsMethod(Type declaring, MethodInfo target)
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        // Async methods put their body in a compiler-generated nested state machine, so the nested types are where the interesting call sites actually are.
        var bodies = new List<MethodInfo>(declaring.GetMethods(All));
        foreach (Type nested in declaring.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            bodies.AddRange(nested.GetMethods(All));

        foreach (MethodInfo method in bodies)
        {
            byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
            if (il is null)
                continue;

            for (int i = 0; i + 4 <= il.Length; i++)
            {
                int token = BitConverter.ToInt32(il, i);
                try
                {
                    if (declaring.Module.ResolveMethod(token) is MethodInfo resolved && resolved == target)
                        return true;
                }
                catch (Exception ex) when (ex is ArgumentException or BadImageFormatException)
                {
                    // Not a method token at this offset; keep scanning.
                }
            }
        }

        return false;
    }

    // --------------------------------------------------------------------------------------------------- Fixtures ---------------------------------------------------------------------------------------------------

    /// <summary>A plugins root, a non-started client, a host, and a captured log so diagnostics are assertable.</summary>
    private sealed class HostFixture : IDisposable
    {
        private readonly CapturingLoggerProvider _provider = new();

        public HostFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "mcc-m5a-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Client = new ClientBuilder().UseCommands().UseBeacon().UseUsername("Tester").UseServer("localhost").Build();

            Host = new PluginHost(Client, Root, _provider, Client.Translations, Client.Variables);
            Host.InstallationSource = _ => Task.FromResult(TestPackages.Read(Root));
        }

        public string Root { get; }

        public Client Client { get; }

        public PluginHost Host { get; }

        public IReadOnlyCollection<string> Logs => _provider.Lines;

        /// <summary>Writes a single-file plugin and returns its folder.</summary>
        public string WritePlugin(string id, string entry, string source)
        {
            string folder = Path.Combine(Root, id);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, entry), source);
            File.WriteAllText(Path.Combine(folder, PluginManifest.FileName), TestManifest.Upgrade($"id = \"{id}\"\nversion = \"1.0.0\"\nentry = \"{entry}\"\napi-version = \"1\"\nenabled = true\n"));
            return folder;
        }

        public void Dispose()
        {
            Client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>An ILoggerFactory that keeps every formatted line, so a diagnostic can be asserted on.</summary>
    private sealed class CapturingLoggerProvider : ILoggerFactory
    {
        public ConcurrentBag<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capturing(Lines);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class Capturing(ConcurrentBag<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => sink.Add(formatter(state, exception));
        }
    }

    /// <summary>
    /// A stand-in session scope; only <see cref="Commands"/> and <see cref="Detached"/> are ever touched.
    /// It mirrors the real <see cref="SessionScope"/>'s shape (a real, self-closing <see cref="PluginCommandScope"/> billed to its own <see cref="Detached"/> token) rather than reaching for the host's plugin-lifetime command scope: production hands a plugin session-scoped commands through exactly this mechanism, and unregistering them at session end is the behavior under test.
    /// <see cref="SimulateDetached"/> stands in for the underlying <see cref="Umpk.Client.Plugins.ClientPluginContext.Detached"/> firing.
    /// </summary>
    private sealed class FakeSessionScope : ISessionScope
    {
        private readonly CancellationTokenSource _detached = new();
        private readonly PluginCommandScope _commands;

        internal FakeSessionScope(ICommandDispatcher commands)
        {
            _commands = new PluginCommandScope(commands);
            _detached.Token.Register(_commands.DisposeAll);
        }

        /// <summary>Simulates the underlying ClientPluginContext.Detached token firing (test seam).</summary>
        internal void SimulateDetached() => _detached.Cancel();

        public UmpkClient Client => throw new NotSupportedException();

        public ClientState State => throw new NotSupportedException();

        public ClientEvents Events => throw new NotSupportedException();

        public ClientActions Actions => throw new NotSupportedException();

        public PluginScheduler Scheduler => throw new NotSupportedException();

        public IPluginCommandScope Commands => _commands;

        public CancellationToken Detached => _detached.Token;

        public ISessionChannels Channels => throw new NotSupportedException();

        public Umpk.Client.ClientActionCapabilities Capabilities => throw new NotSupportedException();

        public IMovementLease? TryAcquireMovement(string reason) => null;

        public string? MovementOwner => null;

        public PluginChannelRegistration RegisterPluginChannel(Umpk.Identifier channel, Action<ReadOnlyMemory<byte>> onMessage)
            => throw new NotSupportedException();

        public ValueTask SendPluginMessageAsync(Umpk.Identifier channel, ReadOnlyMemory<byte> data, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IDisposable ObservePackets(Umpk.Protocol.Java.PacketFrameHandler handler) => throw new NotSupportedException();
    }
    #endregion
}
