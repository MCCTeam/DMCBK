using System.Globalization;
using System.Text;
using DMCBK.Core.Localization;
using Umpk.Commands;
using Umpk.Text;

namespace DMCBK.Core.Commands.Impl;

/// <summary>
/// The <c>debug</c> command: toggles debug messages or dumps the current session state.
/// Ported from the legacy <c>MinecraftClient/Commands/Debug.cs</c>, including its coloured <c>debug state</c> table.
/// </summary>
public sealed class DebugCommand : CommandBase
{
    /// <inheritdoc/>
    public override string CmdName => "debug";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.debug.desc");

    /// <inheritdoc/>
    public override string CmdUsage => "debug [on|off|state]";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Client;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("state", "dump the client state"),
        new("on", "turn debug messages on"),
        new("off", "turn them off"),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["debug state", "debug on"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["tps", "reload"];

    /// <inheritdoc/>
    public override string? ManTopic => "config";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);

        // Legacy grammar (Debug.cs:25-36): the bare command FLIPS the flag, on/off set it explicitly.
        builder.Literal(CmdName, l => l
            .Executes(ctx => SetDebugMode(ctx.Source, flip: true))
            .ThenLiteral("on", h => h.Executes(ctx => SetDebugMode(ctx.Source, flip: false, mode: true)))
            .ThenLiteral("off", h => h.Executes(ctx => SetDebugMode(ctx.Source, flip: false, mode: false)))
            .ThenLiteral("state", h => h.Executes(ctx => ShowState(ctx.Source)))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    /// <summary>
    /// Renders the server facts of a session snapshot: endpoint, version, the server brand, and the timing figures.
    /// This is the UMPK-era projection of what the client knows about the server; the legacy <c>debug state</c> table (see <see cref="ShowState"/>) has no row for the brand or the keep-alive figures, so this stays a separate, public projection rather than part of the table.
    /// It is the shape <c>DMCBK.Core.Tests.ServerFactsDisplayTests</c> pins.
    /// <para>
    /// Two rules this must never break.
    /// A null tick rate is UNKNOWN, not zero: a server paused by 1.21.2+ pause-when-empty or held by <c>/tick freeze</c> stops broadcasting game time, and the client cannot tell that apart from an unreachable server.
    /// And the two timing figures are labelled apart: the latency is SERVER-MEASURED (the tab-list figure, the only real round trip a Java client has), while the keep-alive turnaround is our own responder-side time and is never presented as ping.
    /// </para>
    /// </summary>
    /// <param name="info">The session snapshot.</param>
    /// <exception cref="ArgumentNullException"><paramref name="info"/> is null.</exception>
    public static string FormatServerFacts(SessionInfoSnapshot info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var sb = new StringBuilder();
        sb.Append("server=").Append(info.Host).Append(':').Append(info.Port)
            .Append(" version=").Append(info.VersionName).Append(" protocol=").Append(info.Protocol);
        sb.Append(info.Brand is { } brand ? CommandStrings.DebugStateBrand(brand) : CommandStrings.DebugStateBrandUnknown);
        sb.Append(info.ObservedLatencyMs is { } latency
            ? CommandStrings.DebugStateLatency(latency)
            : CommandStrings.DebugStateLatencyUnknown);
        sb.Append(info.TpsEstimate is { } tps ? CommandStrings.DebugStateTps(tps) : CommandStrings.DebugStateTpsUnknown);

        if (info.KeepAliveTurnaround is { } turnaround)
        {
            sb.Append(info.KeepAliveInterval is { } interval
                ? CommandStrings.DebugStateKeepAlive(turnaround.TotalMilliseconds, interval.TotalSeconds)
                : CommandStrings.DebugStateKeepAliveTurnaroundOnly(turnaround.TotalMilliseconds));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Legacy's <c>SetDebugMode</c> (Debug.cs:49-64): flip or set, ASSIGN, then report the resulting state.
    /// <para>
    /// The assignment is the point.
    /// Before this the command computed the new value, reported it, and set nothing, so "/debug on" printed "Debug messages are now ON" and debug logging stayed off.
    /// It is live because the CLI's level gate reads LoggingConfig.DebugMessages on every call through a closure over that record (Mcc.Cli/ConsoleLogger.cs:63-65).
    /// Not persisted to disk, matching legacy, which only wrote the in-memory settings object.
    /// </para>
    /// <para>
    /// Historical note on what used to block this: LoggingConfig.DebugMessages (<c>Settings.Config.Logging.DebugMessages = ...</c>) and pushed it into the live logger (<c>handler.Log.DebugEnabled = ...</c>).
    /// Neither is expressible here: <c>LoggingConfig.DebugMessages</c> is init-only (Configuration/ConfigurationSections.cs:273), <c>DmcbkConfiguration.Logging</c> is init-only (Configuration/DmcbkConfiguration.cs:31), <see cref="CommandContext.Config"/> is get-only, and no host seam (<see cref="IHostUi"/>, <see cref="IHostInterface"/>) exposes a runtime logging switch.
    /// The CLI's level gate already reads the flag through a live closure over that record (Mcc.Cli/ConsoleLogger.cs:65), so making <c>DebugMessages</c> settable is the whole central fix, and the assignment belongs on the next line once it is.
    /// </para>
    /// </summary>
    private int SetDebugMode(CommandContext ctx, bool flip, bool mode = false)
    {
        bool enabled = flip ? !(ctx.Config?.Logging.DebugMessages ?? false) : mode;

        if (ctx.Config is { } config)
            config.Logging.DebugMessages = enabled;

        return ctx.Result.Set(CmdStatus.Done, enabled
            ? McStrings.Get("cmd.debug.state_on")
            : McStrings.Get("cmd.debug.state_off"));
    }

    /// <summary>
    /// Legacy's <c>ShowState</c> (Debug.cs:66-102): the section-sign coloured state table, logged as one block, with an empty result message.
    /// </summary>
    private static CultureInfo Inv => CultureInfo.InvariantCulture;

    private int ShowState(CommandContext ctx)
    {
        if (!ctx.InSession)
            return ctx.Result.Fail(CommandText.NotConnected(ctx));

        SessionInfoSnapshot info = ctx.Run(ct => ctx.Game.Session.GetInfoAsync(ct));
        PlayerStatus status = ctx.Run(ct => ctx.Game.Player.GetStatusAsync(ct));
        TabListSnapshot players = ctx.Run(ct => ctx.Game.Player.GetTabListAsync(ct));

        var sb = new StringBuilder();
        sb.AppendLine($"§e=== {McStrings.Get("cmd.debug.state_header")} ===");
        sb.AppendLine($"§7{McStrings.Get("cmd.debug.state_server"),-10}§f{info.Host}:{info.Port}");
        sb.AppendLine($"§7{McStrings.Get("cmd.debug.state_username"),-10}§f{Username(ctx)}");
        sb.AppendLine($"§7{McStrings.Get("cmd.debug.state_protocol"),-10}§f{info.Protocol}");
        sb.AppendLine($"§7{McStrings.Get("cmd.debug.state_gamemode"),-10}§f{status.GameMode}");
        // InvariantCulture throughout this table.
        // On a comma-decimal locale the position read "0,00, 0,00, 0,00" -- a coordinate triple that parses to the eye as six numbers -- and the health read "20,0".
        // CommandStrings already formats invariantly; this table did not.
        sb.AppendLine(Inv, $"§7{McStrings.Get("cmd.debug.state_health"),-10}§f{status.Health:F1}");
        sb.AppendLine(Inv, $"§7{McStrings.Get("cmd.debug.state_food"),-10}§f{status.Food}");

        sb.AppendLine(Inv, $"§7{McStrings.Get("cmd.debug.state_location"),-10}"
            + $"§f{status.Position.X:F2}, {status.Position.Y:F2}, {status.Position.Z:F2}");

        // A null tick rate is NOT KNOWN, never 0.0: same rule TpsCommand.Format documents.
        // The full explanation is a two-sentence paragraph, and dropping it into a column-aligned table broke the alignment of every row after it, so the CELL says "unknown" and the paragraph goes in a footnote below the table.
        bool tpsKnown = info.TpsEstimate is not null;
        string tps = info.TpsEstimate is { } measured
            ? measured.ToString("F1", Inv)
            : CommandStrings.TpsUnknownShort;
        sb.AppendLine(Inv, $"§7{McStrings.Get("cmd.debug.state_tps"),-10}§f{tps}");

        // Legacy printed the console backend class (ConsoleIO.Backend).
        // The core owns no console; the host UI implementation is the observable successor, and null still reads "null" as it did.
        sb.AppendLine($"§7{McStrings.Get("cmd.debug.state_console"),-10}§f{ctx.Ui?.GetType().Name ?? "null"}");

        // All FIVE gates.
        // Physics and Pathfinding are as real as the other three -- they are client.toml [Gameplay] keys that commands guard on -- and listing only three made a disabled pathfinder invisible in the one command whose whole job is saying what is switched on.
        var features = new StringBuilder();
        features.Append(ctx.TerrainEnabled ? "§aTerrain " : "§8Terrain ");
        features.Append(ctx.InventoryEnabled ? "§aInventory " : "§8Inventory ");
        features.Append(ctx.EntityEnabled ? "§aEntity " : "§8Entity ");
        features.Append(ctx.PhysicsEnabled ? "§aPhysics " : "§8Physics ");
        features.Append(ctx.PathfindingEnabled ? "§aPathfinding " : "§8Pathfinding ");
        sb.AppendLine($"§7{McStrings.Get("cmd.debug.state_features"),-10}{features}");

        bool debugMessages = ctx.Config?.Logging.DebugMessages ?? false;
        sb.AppendLine($"§7{McStrings.Get("cmd.debug.state_debug"),-10}§f{(debugMessages ? "§aON" : "§cOFF")}");

        // Legacy listed the loaded ChatBots here; loaded plugins are their successor on this runtime.
        var loaded = new List<string>();
        if (ctx.Client.PluginHost is { } pluginHost)
        {
            foreach (Plugins.PluginInfo plugin in pluginHost.List())
            {
                if (plugin.Loaded)
                    loaded.Add(plugin.Id);
            }
        }

        sb.AppendLine($"§7{McStrings.Get("cmd.debug.state_bots")} ({loaded.Count}): §f{string.Join(", ", loaded)}");

        sb.AppendLine($"§7{McStrings.Get("cmd.debug.state_players"),-10}"
            + $"§f{McStrings.Format("cmd.debug.state_online", players.Entries.Count)}");

        if (!tpsKnown)
            sb.AppendLine($"§7{CommandStrings.TpsUnknown}");

        ctx.Output.WriteLine(sb.ToString().TrimEnd());
        return ctx.Result.Set(CmdStatus.Done);
    }

    /// <summary>
    /// The in-game name: the authenticated profile name when there is one, else the offline account's configured username.
    /// An ONLINE account's configured login is a mail hint, never printed here.
    /// </summary>
    private static string Username(CommandContext ctx)
    {
        if (ctx.Client.CurrentSession?.Profile.Name is { Length: > 0 } profileName)
            return profileName;

        return ctx.Config?.ResolvedAccount is { Kind: DmcbkAccountKind.Offline } account ? account.Login : string.Empty;
    }
}
