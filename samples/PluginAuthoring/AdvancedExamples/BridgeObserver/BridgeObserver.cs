using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DMCBK.Core.Commands;
using DMCBK.PluginSdk;
using Umpk.Commands;
using Umpk.Protocol.Java;

public sealed class BridgeObserver : IPlugin
{
    private double _lastTotal;
    private Task _worker = Task.CompletedTask;

    public void Configure(PluginDescriptor descriptor)
    {
        descriptor.Id = "guide-bridge";
        descriptor.Version = "1.0.0";
    }

    public Task ActivateAsync(PluginContext context)
    {
        context.Beacon.Variables.Register(new BeaconVariable(
            "workflow", "workflow.read", context.Strings.Get("snapshot_help"),
            _ => new Dictionary<string, object?> { ["total"] = Volatile.Read(ref _lastTotal) }));
        context.Beacon.RegisterEvent("workflow_ready", ["total"],
            context.Strings.Get("event_help"), capability: "workflow.read");
        context.Commands.Register(new WorkflowCommand(context, total => Volatile.Write(ref _lastTotal, total)));

        context.SessionStarted += (_, args) =>
        {
            ISessionScope session = args.Session;
            int ticks = 0;
            session.Scheduler.OnTick(() =>
                context.Variables.Set("guide_bridge_ticks", (++ticks).ToString(CultureInfo.InvariantCulture)));
            session.Scheduler.Delay(2, () => context.Variables.Set("guide_bridge_delayed", "yes"));
            session.ObservePackets((in PacketFrame frame) =>
            {
                if (!frame.IsClientbound) return;
                byte[] payload = frame.CopyPayload();
                session.Scheduler.Post(() =>
                    context.Variables.Set("guide_bridge_packet_bytes", payload.Length.ToString(CultureInfo.InvariantCulture)));
            });
            _worker = session.Scheduler.RunOffLoop(async () =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, session.Detached).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (session.Detached.IsCancellationRequested)
                {
                    context.Variables.Set("guide_bridge_canceled", "yes");
                }
            }, session.Detached);
        };
        return Task.CompletedTask;
    }

    public async Task DeactivateAsync(CancellationToken ct)
        => await _worker.WaitAsync(ct).ConfigureAwait(false);

    private sealed class WorkflowCommand(PluginContext context, Action<double> remember) : CommandBase
    {
        public override string CmdName => "guide-workflow";
        public override string CmdDesc => context.Strings.Get("command_help");
        public override string CmdUsage => "guide-workflow";
        public override void Register(CommandBuilder<CommandContext> builder)
            => builder.Literal(CmdName, command => command.Executes(async call =>
            {
                object? value = await context.Beacon.CallFunctionAsync(
                    "workflow", "subtotal", [3.0, 4.0], call.Source.Cancellation).ConfigureAwait(false);
                double total = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                remember(total);
                var dispatched = await context.Beacon.FireEventAsync("workflow_ready",
                    new Dictionary<string, object?> { ["total"] = total }, call.Source.Cancellation).ConfigureAwait(false);
                if (dispatched.Handlers.Any(handler => handler.Result is { Success: false }))
                    throw new InvalidOperationException(context.Strings.Get("event_failed"));
                return call.Source.Result.Ok(context.Strings.Format("result", total));
            }));
    }
}
