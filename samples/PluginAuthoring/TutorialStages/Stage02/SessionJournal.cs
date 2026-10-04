using System.Globalization;
using System.Threading.Tasks;
using DMCBK.PluginSdk;

public sealed class SessionJournal : IPlugin
{
    private int _count;

    public void Configure(PluginDescriptor descriptor)
    {
        descriptor.Id = "session-journal";
        descriptor.Version = "1.0.0";
    }

    public Task ActivateAsync(PluginContext context)
    {
        context.SessionStarted += (_, _) =>
        {
            _count++;
            context.Variables.Set("session_journal_sessions",
                _count.ToString(CultureInfo.InvariantCulture));
        };
        return Task.CompletedTask;
    }
}
