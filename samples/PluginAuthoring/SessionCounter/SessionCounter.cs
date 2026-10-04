using System.Globalization;
using System.Threading.Tasks;
using DMCBK.PluginSdk;

public sealed class SessionCounter : IPlugin
{
    public void Configure(PluginDescriptor descriptor)
    {
        descriptor.Id = "session-counter";
        descriptor.Version = "1.0.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
    }

    public Task ActivateAsync(PluginContext context)
    {
        int count = 0;
        if (context.Storage.TryGet("sessions", out string? saved))
            int.TryParse(saved, NumberStyles.Integer, CultureInfo.InvariantCulture, out count);

        context.SessionStarted += (_, _) =>
        {
            count++;
            string value = count.ToString(CultureInfo.InvariantCulture);
            context.Storage.Set("sessions", value);
            context.Storage.Save();
            context.Variables.Set("session_counter_sessions", value);
        };
        return Task.CompletedTask;
    }
}
