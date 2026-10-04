using System.Threading.Tasks;
using DMCBK.PluginSdk;

public sealed class SessionJournal : IPlugin
{
    public void Configure(PluginDescriptor descriptor)
    {
        descriptor.Id = "session-journal";
        descriptor.Version = "1.0.0";
    }

    public Task ActivateAsync(PluginContext context) => Task.CompletedTask;
}
