using System.Globalization;
using System.Text;
using DMCBK.Core.Commands.ArgumentTypes;
using DMCBK.Core.Configuration;
using DMCBK.Core.Localization;
using DMCBK.Core.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Umpk.Commands;
using Umpk.Data.Java;
using Umpk.Protocol.Java;

namespace DMCBK.Core.Commands.Impl;

/// <summary>
/// The <c>reload</c> command: reload the configuration from disk (and every loaded plugin).
/// Mirrors legacy <c>Commands/Reload.cs</c>: it announces <c>cmd.reload.started</c>, reloads, prints the legacy warnings, and returns <c>cmd.reload.finished</c>.
/// </summary>
public sealed class ReloadCommand : CommandBase
{
    /// <summary>
    /// The legacy post-reload warnings (Reload.cs:44-47).
    /// <c>cmd.reload.warning4</c> is deliberately not printed: it warns that the Replay chat bot is not hard reloaded, and this client reloads every plugin by unloading and reactivating it (<c>PluginHost.ReloadAllAsync</c>), so the warning would be false.
    /// </summary>
    private static readonly string[] WarningKeys =
    [
        "cmd.reload.warning1",
        "cmd.reload.warning2",
        "cmd.reload.warning3",
    ];

    /// <inheritdoc/>
    public override string CmdName => "reload";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.reload.desc");

    /// <inheritdoc/>
    public override string CmdUsage => "reload";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Session;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new(string.Empty, "re-read client.toml without restarting"),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["reload"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["set"];

    /// <inheritdoc/>
    public override string? ManTopic => "config";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .Executes(ctx => Run(ctx.Source))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private int Run(CommandContext ctx)
    {
        if (ctx.Config?.SourceFolder is not { } folder)
            return ctx.Result.Fail(CommandStrings.ReloadNoConfig);

        ctx.Output.WriteLine(McStrings.Get("cmd.reload.started"));
        try
        {
            var loader = ctx.Client.GetModule<IConfigurationStorage>();
            ConfigurationLoadResult result = loader.Load();
            ctx.Commands.ReloadConfiguration(result.Config);

            // The language is read from the same file, so a change to it lands with the rest of the reload rather than waiting for a restart.
            CultureInfo culture = UiCulture.Apply(result.Config.Localization.Language);
            ctx.Client.UiCulture = culture;
            loader.UseCulture(culture);
            foreach (KeyValuePair<string, string> variable in result.Config.Variables)
                ctx.Variables.Set(variable.Key, variable.Value);

            // Reload plugins too (config + settings pipeline re-run) when a plugin host is attached.
            if (ctx.Client.PluginHost is { } host)
            {
                host.UseCulture(culture);
                ctx.Run(reloadCt => host.ReloadAllAsync(reloadCt));
            }

            // Announced after the plugins come back, so the subscribers that hear it are the ones now loaded rather than the instances the reload above has just replaced.
            ctx.Client.RaiseConfigurationReloaded(result.Config);

            // Legacy printed these through Log.Warn, which prefixed "§6[WARN] "; the command sink is not a logger, so only the gold colour is carried over.
            foreach (string key in WarningKeys)
                ctx.Output.WriteLine("§6" + McStrings.Get(key));

            return ctx.Result.Ok(McStrings.Get("cmd.reload.finished"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ctx.Result.Fail(CommandStrings.ReloadFailed(ex.Message));
        }
    }
}
