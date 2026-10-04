using DMCBK.Core.Commands;
using DMCBK.Core.Configuration;
using DMCBK.Core.Plugins;
using DMCBK.Core.Tests.Fakes;
using DMCBK.PluginSdk;
using Umpk.Client;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// Crash isolation.
/// Plugins run in-process with no sandbox, so an exception cannot be contained; what can be contained is a plugin that throws without end.
/// Exceptions are counted across the three surfaces a plugin has (events, commands, scheduled callbacks), and past the budget the plugin is disabled.
/// </summary>
public sealed class PluginCrashIsolationTests
{
    private const string ThrowingEventPluginSource = """
        using System;
        using System.Threading.Tasks;
        using DMCBK.PluginSdk;

        public sealed class ThrowingPlugin : IPlugin
        {
            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "throwing";

            public Task ActivateAsync(PluginContext context)
            {
                context.BeforeExit += (_, _) => throw new InvalidOperationException("no");
                return Task.CompletedTask;
            }
        }
        """;

    private const string ThrowingCommandPluginSource = """
        using System;
        using System.Threading.Tasks;
        using DMCBK.Core.Commands;
        using DMCBK.PluginSdk;
        using Umpk.Commands;

        public sealed class BoomCommand : CommandBase
        {
            public override string CmdName => "boom";
            public override string CmdDesc => "Throws.";
            public override string CmdUsage => "boom";

            public override void Register(CommandBuilder<CommandContext> builder)
            {
                RegisterHelp(builder, CmdName, ShowUsage);
                builder.Literal("boom", l => l.Executes(_ => Boom()));
            }

            private static int Boom() => throw new InvalidOperationException("no");
        }

        public sealed class CommandThrowerPlugin : IPlugin
        {
            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "thrower";

            public Task ActivateAsync(PluginContext context)
            {
                context.Commands.Register(new BoomCommand());
                return Task.CompletedTask;
            }
        }
        """;

    private const string TickThrowerPluginSource = """
        using System;
        using System.Threading.Tasks;
        using DMCBK.PluginSdk;

        public sealed class TickThrowerPlugin : IPlugin
        {
            public void Configure(PluginDescriptor descriptor) => descriptor.Id = "ticker";

            public Task ActivateAsync(PluginContext context)
            {
                context.Cron.Every(TimeSpan.FromMilliseconds(20), () => throw new InvalidOperationException("no"));
                return Task.CompletedTask;
            }
        }
        """;

    [Fact]
    public async Task AnExceptionOutOfAnEventHandlerIsCountedAndDoesNotEscape()
    {
        using var fixture = new PluginRootFixture(new PluginsConfig { CrashThreshold = 0 });
        fixture.WritePlugin("throwing", "Throwing.cs", ThrowingEventPluginSource);
        await fixture.Host.LoadAllAsync();

        PluginContext context = fixture.Host.GetContext("throwing")!;
        context.RaiseBeforeExit(new BeforeExitEventArgs(CancellationToken.None));
        context.RaiseBeforeExit(new BeforeExitEventArgs(CancellationToken.None));

        PluginInfo info = fixture.Host.List().Single();
        Assert.Equal(2, info.FaultCount);
        Assert.Contains("BeforeExit", info.LastError);
        Assert.Contains("InvalidOperationException", info.LastError);
        Assert.True(info.Loaded);
    }

    [Fact]
    public async Task PastTheThresholdInsideTheWindowThePluginIsDisabled()
    {
        using var fixture = new PluginRootFixture(
            new PluginsConfig { CrashThreshold = 3, CrashWindowSeconds = 60 });
        fixture.WritePlugin("throwing", "Throwing.cs", ThrowingEventPluginSource);
        await fixture.Host.LoadAllAsync();

        PluginContext context = fixture.Host.GetContext("throwing")!;
        for (int i = 0; i < 3; i++)
            context.RaiseBeforeExit(new BeforeExitEventArgs(CancellationToken.None));

        await fixture.Host.CrashDisablesAsync();

        PluginInfo info = fixture.Host.List().Single();
        Assert.False(info.Loaded);
        Assert.False(info.Enabled);
        Assert.Equal(3, info.FaultCount);
    }

    [Fact]
    public async Task AThresholdOfZeroCountsAndNeverDisables()
    {
        using var fixture = new PluginRootFixture(new PluginsConfig { CrashThreshold = 0 });
        fixture.WritePlugin("throwing", "Throwing.cs", ThrowingEventPluginSource);
        await fixture.Host.LoadAllAsync();

        PluginContext context = fixture.Host.GetContext("throwing")!;
        for (int i = 0; i < 25; i++)
            context.RaiseBeforeExit(new BeforeExitEventArgs(CancellationToken.None));

        PluginInfo info = fixture.Host.List().Single();
        Assert.Equal(25, info.FaultCount);
        Assert.True(info.Loaded);
    }

    [Fact]
    public async Task AnExceptionOutOfAPluginsCommandIsCountedAgainstThatPlugin()
    {
        _ = typeof(Umpk.Commands.CommandBuilder<CommandContext>);
        _ = typeof(PluginContext);
        _ = typeof(Client);
        _ = typeof(UmpkClient);

        using var fixture = new PluginRootFixture(new PluginsConfig { CrashThreshold = 0 });
        fixture.WritePlugin("thrower", "Thrower.cs", ThrowingCommandPluginSource);
        PluginActionResult loaded = await fixture.Host.LoadAllAsync();
        Assert.True(fixture.Host.List().Single().Loaded, loaded.Message);

        CmdResult result = await fixture.Client.Commands.DispatchAsync("boom");

        Assert.Equal(CmdStatus.Fail, result.Status);
        PluginInfo info = fixture.Host.List().Single();
        Assert.Equal(1, info.FaultCount);
        Assert.Contains("boom", info.LastError);
    }

    [Fact]
    public async Task AnExceptionOutOfAScheduledCallbackIsCountedAgainstThatPlugin()
    {
        using var fixture = new PluginRootFixture(new PluginsConfig { CrashThreshold = 0 });
        fixture.WritePlugin("ticker", "Ticker.cs", TickThrowerPluginSource);
        await fixture.Host.LoadAllAsync();
        Assert.True(fixture.Host.List().Single().Loaded);

        await WaitUntil(() => fixture.Host.List().Single().FaultCount > 0);

        PluginInfo info = fixture.Host.List().Single();
        Assert.True(info.FaultCount > 0);
        Assert.Contains("scheduled callback", info.LastError);
    }

    [Fact]
    public void TheMonitorSlidesItsWindow()
    {
        var time = new StepTime(DateTimeOffset.UnixEpoch);
        var monitor = new PluginCrashMonitor(3, TimeSpan.FromSeconds(60), time);

        Assert.Equal((1, false), monitor.Record("x"));
        time.Advance(TimeSpan.FromSeconds(90));
        Assert.Equal((1, false), monitor.Record("x"));
        Assert.Equal((2, false), monitor.Record("x"));
        Assert.Equal((3, true), monitor.Record("x"));

        monitor.Forget("x");
        Assert.Equal((1, false), monitor.Record("x"));
    }

    [Fact]
    public void TheGuardedSchedulerReportsBeforeTheCallbackEscapes()
    {
        var inner = new ImmediateCron();
        Exception? seen = null;
        var guarded = new GuardedCronScheduler(inner, ex => seen = ex);

        guarded.Every(TimeSpan.FromSeconds(1), () => throw new InvalidOperationException("no"));

        Assert.IsType<InvalidOperationException>(seen);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
            await Task.Delay(25);
    }

    /// <summary>A clock the test moves by hand, so the sliding window is proved without waiting.</summary>
    private sealed class StepTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>A scheduler that runs the callback where it stands, so the decorator can be tested alone.</summary>
    private sealed class ImmediateCron : Umpk.Hosting.ICronScheduler
    {
        public IDisposable Every(TimeSpan interval, Action callback) => Run(callback);

        public IDisposable EveryWithJitter(TimeSpan min, TimeSpan max, Action callback) => Run(callback);

        public IDisposable DailyAt(TimeOnly timeOfDay, Action callback) => Run(callback);

        public IDisposable At(DateTimeOffset when, Action callback) => Run(callback);

        private static IDisposable Run(Action callback)
        {
            callback();
            return new Nothing();
        }

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
