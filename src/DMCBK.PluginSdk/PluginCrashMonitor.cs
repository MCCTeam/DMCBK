namespace DMCBK.PluginSdk;

/// <summary>Which plugin surface an exception came out of, for the line the user reads.</summary>
public enum PluginFaultSurface
{
    /// <summary>One of the events on <see cref="PluginContext"/>.</summary>
    Event,

    /// <summary>A command the plugin registered.</summary>
    Command,

    /// <summary>A callback the plugin gave to <see cref="PluginContext.Cron"/>.</summary>
    Scheduler,
}

/// <summary>The last exception one plugin threw, kept for <c>plugins info</c> and <c>plugins doctor</c>.</summary>
/// <param name="Surface">Where it came from.</param>
/// <param name="Detail">The event or command name, when there is one.</param>
/// <param name="Message">The exception's type and message.</param>
/// <param name="At">When it happened.</param>
public sealed record PluginFault(
    PluginFaultSurface Surface, string? Detail, string Message, DateTimeOffset At)
{
    /// <summary>One line naming where it came from and what it said.</summary>
    public string Describe() => PluginStrings.LastError(
        Surface switch
        {
            PluginFaultSurface.Command => Detail is { Length: > 0 } command
                ? PluginStrings.CrashSurfaceCommandNamed(command)
                : PluginStrings.CrashSurfaceCommand,
            PluginFaultSurface.Scheduler => PluginStrings.CrashSurfaceScheduler,
            _ => PluginStrings.CrashSurfaceEvent(Detail ?? "?"),
        },
        Message);
}

