using System;
using System.Globalization;
using System.Threading.Tasks;
using DMCBK.Core.Commands;
using DMCBK.PluginSdk;
using Umpk.Commands;

public sealed class JournalSettings : IValidatablePluginSettings
{
    public int Increment { get; set; } = 1;
    public void Validate() => Increment = Math.Clamp(Increment, 1, 100);
}

public sealed class SessionJournal : IPlugin
{
    private readonly object _gate = new();
    private int _count;
    private JournalSettings _settings = new();

    public void Configure(PluginDescriptor descriptor)
    {
        descriptor.Id = "session-journal";
        descriptor.Version = "1.0.0";
        descriptor.WithSettings<JournalSettings>();
    }

    public Task ActivateAsync(PluginContext context)
    {
        _settings = context.Settings.Load<JournalSettings>();
        if (context.Storage.TryGet("sessions", out string? saved))
            int.TryParse(saved, NumberStyles.Integer, CultureInfo.InvariantCulture, out _count);

        context.Commands.Register(new JournalCommand(context.Strings, ReadCount));
        context.Beacon.Functions.Register(new BeaconFunction(
            Name: "journal_count", Capability: "journal.read",
            Description: context.Strings.Get("journal.function_help"),
            Parameters: [], ParameterTypes: [], ReturnType: typeof(double),
            Invoke: call => Task.FromResult<object?>((double)ReadCount())));

        context.SessionStarted += (_, _) =>
        {
            lock (_gate)
            {
                _count += _settings.Increment;
                string value = _count.ToString(CultureInfo.InvariantCulture);
                context.Storage.Set("sessions", value);
                context.Storage.Save();
                context.Variables.Set("session_journal_sessions", value);
            }
        };
        return Task.CompletedTask;
    }

    private int ReadCount()
    {
        lock (_gate) return _count;
    }

    private sealed class JournalCommand(IPluginLocalization strings, Func<int> count) : CommandBase
    {
        public override string CmdName => "journal-count";
        public override string CmdDesc => strings.Get("journal.command_help");
        public override string CmdUsage => "journal-count";
        public override void Register(CommandBuilder<CommandContext> builder)
            => builder.Literal(CmdName, command => command.Executes(
                call => call.Source.Result.Ok(strings.Format("journal.count", count()))));
    }
}
