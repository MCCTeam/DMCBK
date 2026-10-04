using System;
using System.Globalization;
using System.Threading.Tasks;
using DMCBK.PluginSdk;

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

}
