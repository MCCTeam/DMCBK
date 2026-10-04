using System.Globalization;
using System.Text;
using DMCBK.Core.Beacon;
using DMCBK.Core.Configuration;
using Umpk.Client;
using Umpk.Client.Events;
using Umpk.Commands;
using Umpk.Geometry;
using Umpk.Text;

namespace DMCBK.Core.Commands.Impl;

/// <summary>
/// The <c>scripts</c> command: daily driving for Beacon scripts (<c>list</c> / <c>run</c> / <c>stop</c> / <c>reload</c> / <c>lint</c> / <c>new</c> / <c>mute</c> / <c>repl</c> / <c>format</c>), with tab completion over script names.
/// </summary>
/// <remarks>
/// <para>
/// Runtime state lives in one <see cref="ScriptsRuntime"/> per <see cref="Client"/> (keyed by client identity, because <see cref="CommandBase"/> instances are constructed per <see cref="CommandService"/> but nothing stops a host from sharing one): exactly ONE <see cref="BeaconEngine"/> per client, so the global chat bucket stays global, and mute plus the held-chat count are owned by that runtime through the <see cref="BeaconClientHost"/> callbacks.
/// Lint never reimplements anything: both text and JSON modes go through <see cref="BeaconLintBatch"/>, the same engine the headless frontend calls, so parity with the headless <c>lint</c> is structural.
/// </para>
/// <para>
/// Runtime user-facing strings live in <c>CommandStrings</c> (Beacon has no legacy corpus entry);
/// command metadata literals stay inline per the <c>PluginsCommand</c> pattern.
/// </para>
/// </remarks>
public sealed class ScriptsCommand : CommandBase
{
    private static readonly string[] HelpTopics =
    [
        "ui", "list", "run", "stop", "reload", "lint", "new", "mute", "repl", "watch", "config", "format",
    ];

    /// <inheritdoc/>
    public override string CmdName => "scripts";

    /// <inheritdoc/>
    public override string CmdDesc => CommandStrings.ScriptsDesc;

    /// <inheritdoc/>
    public override string CmdUsage =>
        "scripts [list|run <id|file> [--trace]|stop <id|all>|reload [id]|lint <file> [--format text|json]"
        + "|new <template> <id>|mute [on|off]|repl [--seed <n>] <line>|watch [on|off]|config <id> [key value]"
        + "|format <file> [--check]|ui]";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Client;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("list", "running scripts and the mute state"),
        new("run <id|file> [--trace]", "load and run a script from the scripts folder"),
        new("stop <id|all>", "stop one script, or every script at once"),
        new("reload [id]", "re-run one script, or every running script, from disk"),
        new("lint <file> [--format text|json]", "check a file with the shared lint engine"),
        new("new <template> <id>", "scaffold a script from a working template"),
        new("mute [on|off]", "gag script chat while logic runs, or show the held count"),
        new("repl [--seed <n>] [line]", "evaluate one line against the live session"),
        new("watch [on|off]", "opt-in hot-reload when a running script file changes"),
        new("config <id> [key value]", "show a script settings file, or set one key"),
        new("format <file> [--check]", "normalize layout and print the diff"),
        new("ui", CommandStrings.ScriptsUiUsage),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<UsageFlag> Flags =>
    [
        new("--format text|json", "lint output: the human view, or the agent-scriptable JSON document"),
        new("--trace", "print the structured per-line trace after run"),
        new("--seed <n>", "replay one repl line with a fixed RNG seed"),
        new("--check", "format dry run: print the diff without writing"),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples =>
    [
        "scripts list",
        "scripts run welcome",
        "scripts run welcome --trace",
        "scripts lint welcome.mcc --format json",
        "scripts new welcome mybot",
        "scripts mute on",
        "scripts repl show 1 + 2",
        "scripts stop all",
        "scripts format welcome",
        "scripts ui",
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["plugins"];

    /// <inheritdoc/>
    public override string? ManTopic => "scripts";

    /// <inheritdoc/>
    public override CommandFeature RequiredFeatures => CommandFeature.None;

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);

        foreach (string topic in HelpTopics)
            help.ThenLiteral(topic, h => h.Executes(ctx => ShowUsage(ctx.Source)));

        builder.Literal(CmdName, l => l
            .Executes(ctx => List(ctx.Source))
            .ThenLiteral("ui", h => h.Executes(ctx => Ui(ctx.Source)))
            .ThenLiteral("list", h => h
                .Executes(ctx => List(ctx.Source)))
            .ThenLiteral("run", h => h.ThenArgument("args", Arguments.GreedyString(), a => a
                .Suggests(SuggestScriptIds)
                .Executes(ctx => Run(ctx.Source, ctx.GetArgument<string>("args")))))
            .ThenLiteral("stop", h => h
                .Executes(ctx => Stop(ctx.Source, null))
                .ThenArgument("id", Arguments.QuotableString(), a => a
                    .Suggests(SuggestScriptIdsOrAll)
                    .Executes(ctx => Stop(ctx.Source, ctx.GetArgument<string>("id")))))
            .ThenLiteral("reload", h => h
                .Executes(ctx => Reload(ctx.Source, null))
                .ThenArgument("id", Arguments.QuotableString(), a => a
                    .Suggests(SuggestRunningIds)
                    .Executes(ctx => Reload(ctx.Source, ctx.GetArgument<string>("id")))))
            .ThenLiteral("lint", h => h
                .Executes(ctx => Lint(ctx.Source, null))
                .ThenArgument("args", Arguments.GreedyString(), a => a
                    .Executes(ctx => Lint(ctx.Source, ctx.GetArgument<string>("args")))))
            .ThenLiteral("new", h => h
                .Executes(ctx => New(ctx.Source, null, null))
                .ThenArgument("template", Arguments.QuotableString(), a => a
                    .Suggests(SuggestTemplates)
                    .ThenArgument("id", Arguments.QuotableString(), b => b
                        .Executes(ctx => New(
                            ctx.Source,
                            ctx.GetArgument<string>("template"),
                            ctx.GetArgument<string>("id"))))))
            .ThenLiteral("mute", h => h
                .Executes(ctx => Mute(ctx.Source, null))
                .ThenLiteral("on", o => o.Executes(ctx => Mute(ctx.Source, "on")))
                .ThenLiteral("off", o => o.Executes(ctx => Mute(ctx.Source, "off"))))
            .ThenLiteral("repl", h => h
                .Executes(ctx => Repl(ctx.Source, null))
                .ThenArgument("line", Arguments.GreedyString(), a => a
                    .Executes(ctx => Repl(ctx.Source, ctx.GetArgument<string>("line")))))
            .ThenLiteral("watch", h => h
                .Executes(ctx => Watch(ctx.Source, null))
                .ThenLiteral("on", o => o.Executes(ctx => Watch(ctx.Source, "on")))
                .ThenLiteral("off", o => o.Executes(ctx => Watch(ctx.Source, "off"))))
            .ThenLiteral("config", h => h
                .Executes(ctx => Config(ctx.Source, null))
                .ThenArgument("args", Arguments.GreedyString(), a => a
                    .Suggests(SuggestRunningIds)
                    .Executes(ctx => Config(ctx.Source, ctx.GetArgument<string>("args")))))
            .ThenLiteral("format", h => h
                .Executes(ctx => Format(ctx.Source, null))
                .ThenArgument("args", Arguments.GreedyString(), a => a
                    .Suggests(SuggestScriptIds)
                    .Executes(ctx => Format(ctx.Source, ctx.GetArgument<string>("args")))))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private static int Ui(CommandContext ctx)
        => ctx.Ui?.TryOpenScriptManager() == true
            ? ctx.Result.Ok()
            : ctx.Result.Fail(CommandStrings.UiRequiresTui);

    private static ScriptsRuntime GetRuntime(Client client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.Scripts.Runtime;
    }

    private int List(CommandContext ctx)
    {
        ScriptsRuntime runtime = GetRuntime(ctx.Client);
        List<string> ids = runtime.Engine.ScriptIds
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        if (ids.Count == 0)
            sb.Append(CommandStrings.ScriptsListEmpty);
        else
        {
            sb.Append(CommandStrings.ScriptsListHeader(ids.Count));
            foreach (string id in ids)
                sb.Append('\n').Append(id);
        }

        if (runtime.IsMuted)
            sb.Append(CommandStrings.ScriptsListMuted(runtime.Held));

        return ctx.Result.Ok(sb.ToString());
    }

    private int Run(CommandContext ctx, string rawArgs)
    {
        List<string> tokens = Tokenize(rawArgs);
        bool trace = false;
        var operands = new List<string>();
        foreach (string token in tokens)
        {
            if (string.Equals(token, "--trace", StringComparison.Ordinal))
                trace = true;
            else if (token.StartsWith("--", StringComparison.Ordinal))
                return ctx.Result.Fail(CommandStrings.ScriptsUnknownFlag(token));
            else
                operands.Add(token);
        }

        if (operands.Count == 0)
            return ctx.Result.Fail(CommandStrings.ScriptsRunUsage);

        if (!TryResolveScriptFile(ctx, operands[0], out string scriptId, out string path, out string error))
            return ctx.Result.Fail(error);

        string source;
        try
        {
            source = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ctx.Result.Fail(CommandStrings.ScriptsCouldNotRead(path, ex.Message));
        }

        ScriptsRuntime runtime = GetRuntime(ctx.Client);
        BeaconTraceCollector? collector = trace ? new BeaconTraceCollector() : null;
        BeaconRunResult result;
        try
        {
            if (collector is not null)
                runtime.Engine.Tracer = collector.Sink;

            result = ctx.Run(ct => ctx.Client.Scripts.RunAsync(
                scriptId, source, Path.GetFileName(path), ct));
        }
        finally
        {
            if (collector is not null)
                runtime.Engine.Tracer = null;
        }

        if (result.Success)
            ArmPollBaselines(ctx, runtime);

        return RenderRunResult(ctx, scriptId, result, collector);
    }

    /// <summary>
    /// Snapshots the poll baselines synchronously after a load, so the pump's first tick diffs against the world the script started watching instead of silently adopting whatever joined in the half second after <c>run</c> returned.
    /// Best-effort: offline, mid-reconnect, or a dispatch from the session loop leaves the baselines for the pump to arm later.
    /// </summary>
    private static void ArmPollBaselines(CommandContext ctx, ScriptsRuntime runtime)
    {
        try
        {
            ctx.Run(async _ =>
            {
                await runtime.PollSessionAsync(ctx.Client).ConfigureAwait(false);
                return 0;
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    private static int RenderRunResult(
        CommandContext ctx, string scriptId, BeaconRunResult result, BeaconTraceCollector? collector)
    {
        string? traceText = collector is not null && collector.Lines.Count > 0
            ? collector.RenderText()
            : null;

        List<BeaconDiagnostic> errors = result.Diagnostics
            .Where(d => d.Severity == BeaconSeverity.Error)
            .ToList();
        if (!result.Success || errors.Count > 0)
        {
            BeaconDiagnostic first = result.Error ?? (errors.Count > 0 ? errors[0] : FallbackError(scriptId));
            var sb = new StringBuilder();
            sb.Append(FormatDiagnostic(first));
            if (errors.Count > 1)
                sb.Append(CommandStrings.ScriptsMoreErrors(errors.Count - 1));

            if (traceText is not null)
                sb.Append(CommandStrings.ScriptsTraceSeparator).Append(traceText);

            return ctx.Result.Fail(sb.ToString());
        }

        var ok = new StringBuilder();
        if (result.LocalOutput.Count > 0)
            ok.Append(string.Join("\n", result.LocalOutput));
        else
            ok.Append(CommandStrings.ScriptsRan(scriptId));

        if (traceText is not null)
            ok.Append(CommandStrings.ScriptsTraceSeparator).Append(traceText);

        return ctx.Result.Ok(ok.ToString());
    }

    private static BeaconDiagnostic FallbackError(string scriptId)
        => new(
            BeaconDiagnosticCodes.Parse,
            BeaconSeverity.Error,
            CommandStrings.ScriptsFailedNoDiagnostic(scriptId),
            new SourceSpan(scriptId + BeaconScriptDiscovery.ScriptExtension, 1, 1, 0),
            null);

    private static string FormatDiagnostic(BeaconDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        string text = CommandStrings.ScriptsDiagnosticLine(diagnostic.Code, diagnostic.Message);
        if (!string.IsNullOrWhiteSpace(diagnostic.Suggestion))
            text += CommandStrings.ScriptsTryThis(diagnostic.Suggestion);

        return text;
    }

    private int Stop(CommandContext ctx, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return ctx.Result.Fail(CommandStrings.ScriptsStopUsage);

        ScriptsRuntime runtime = GetRuntime(ctx.Client);
        if (string.Equals(id.Trim(), "all", StringComparison.OrdinalIgnoreCase))
        {
            List<string> running = [.. runtime.Engine.ScriptIds];
            int count = 0;
            foreach (string scriptId in running)
            {
                if (ctx.Client.Scripts.Stop(scriptId))
                    count++;
            }

            return ctx.Result.Ok(CommandStrings.ScriptsStopped(count));
        }

        string target = id.Trim();
        if (ctx.Client.Scripts.Stop(target))
            return ctx.Result.Ok(CommandStrings.ScriptsStoppedOne(target));

        return ctx.Result.Fail(CommandStrings.ScriptsUnknownScript(target));
    }

    private int Reload(CommandContext ctx, string? id)
    {
        ScriptsRuntime runtime = GetRuntime(ctx.Client);
        List<string> targets = string.IsNullOrWhiteSpace(id)
            ? runtime.Engine.ScriptIds.OrderBy(s => s, StringComparer.Ordinal).ToList()
            : [id.Trim()];

        if (targets.Count == 0)
            return ctx.Result.Ok(CommandStrings.ScriptsReloadNothing);

        int ok = 0;
        var failures = new List<string>();
        foreach (string target in targets)
        {
            if (!TryResolveScriptFile(ctx, target, out _, out string path, out _))
            {
                failures.Add(CommandStrings.ScriptsReloadNotFound(target));
                continue;
            }

            string source;
            try
            {
                source = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(CommandStrings.ScriptsReloadUnreadable(target, path, ex.Message));
                continue;
            }

            BeaconRunResult result = ctx.Run(ct => ctx.Client.Scripts.RunAsync(
                target, source, Path.GetFileName(path), ct));

            BeaconDiagnostic? firstError = result.Error
                ?? result.Diagnostics.FirstOrDefault(d => d.Severity == BeaconSeverity.Error);
            if (result.Success && firstError is null)
                ok++;
            else
            {
                failures.Add(firstError is null
                    ? CommandStrings.ScriptsReloadNoDiagnostic(target)
                    : CommandStrings.ScriptsReloadFailure(target, firstError.Code, firstError.Message));
            }
        }

        if (failures.Count == 0)
        {
            ArmPollBaselines(ctx, runtime);
            return ctx.Result.Ok(CommandStrings.ScriptsReloaded(ok, targets.Count));
        }

        return ctx.Result.Fail(
            CommandStrings.ScriptsReloadedWithFailures(ok, targets.Count, string.Join("; ", failures)));
    }

    private static int Lint(CommandContext ctx, string? rawArgs)
    {
        if (string.IsNullOrWhiteSpace(rawArgs))
            return ctx.Result.Fail(CommandStrings.ScriptsLintUsage);

        List<string> tokens = Tokenize(rawArgs);
        if (!ScriptsLintArguments.TryParse([.. tokens], out BeaconLintRequest? cliArgs, out string parseError)
            || cliArgs is null)
            return ctx.Result.Fail(CommandStrings.ScriptsLintFailure(parseError));

        // Like run/format, lint accepts a bare script id: resolve operands that name no file on disk and carry no path separators through the scripts folder, so `/scripts lint welcome` checks what `/scripts run welcome` would run.
        // Anything
        // else (flags already parsed out, real paths, missing files) passes through
        // untouched for the engine to report.
        if (!cliArgs.UseStdin && cliArgs.Files.Count > 0)
        {
            var resolved = new List<string>(cliArgs.Files.Count);
            foreach (string file in cliArgs.Files)
            {
                if (!File.Exists(file)
                    && file.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0
                    && TryResolveScriptFile(ctx, file, out _, out string scriptPath, out _))
                    resolved.Add(scriptPath);
                else
                    resolved.Add(file);
            }

            cliArgs = cliArgs with { Files = resolved };
        }

        int exit = BeaconLintBatch.Execute(cliArgs, stdinText: null, out string stdout, out string stderr);
        if (exit == BeaconLint.ExitCodes.Usage)
            return ctx.Result.Fail(string.IsNullOrWhiteSpace(stderr) ? CommandStrings.ScriptsLintUsageError : stderr);

        // JSON mode always succeeds with the document as the message (even when it reports errors): the exit-code contract is headless-only, and parity compares the payload.
        if (string.Equals(cliArgs.Format, "json", StringComparison.OrdinalIgnoreCase))
            return ctx.Result.Ok(stdout);

        return exit == BeaconLint.ExitCodes.Clean
            ? ctx.Result.Ok(stdout)
            : ctx.Result.Fail(stdout);
    }

    private static int Format(CommandContext ctx, string? rawArgs)
    {
        if (string.IsNullOrWhiteSpace(rawArgs))
            return ctx.Result.Fail(CommandStrings.ScriptsFormatUsage);

        bool check = false;
        var operands = new List<string>();
        foreach (string token in Tokenize(rawArgs))
        {
            if (string.Equals(token, "--check", StringComparison.Ordinal))
                check = true;
            else if (token.StartsWith("--", StringComparison.Ordinal))
                return ctx.Result.Fail(CommandStrings.ScriptsFormatUnknownFlag(token));
            else
                operands.Add(token);
        }

        if (operands.Count != 1)
            return ctx.Result.Fail(CommandStrings.ScriptsFormatUsage);

        if (!TryResolveScriptFile(ctx, operands[0], out _, out string path, out string error))
            return ctx.Result.Fail(error);

        string source;
        try
        {
            source = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ctx.Result.Fail(CommandStrings.ScriptsCouldNotRead(path, ex.Message));
        }

        BeaconFormatResult result = ctx.Client.Scripts.Format(Path.GetFileName(path), source);
        var sb = new StringBuilder();
        if (!result.Changed)
            sb.Append(CommandStrings.ScriptsFormatClean(path));
        else
        {
            sb.Append(result.Diff);
            if (check)
                sb.Append('\n').Append(CommandStrings.ScriptsFormatWouldChange(path));
            else
            {
                try
                {
                    File.WriteAllText(path, result.Formatted);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return ctx.Result.Fail(CommandStrings.ScriptsFormatWriteFailed(path, ex.Message));
                }

                sb.Append('\n').Append(CommandStrings.ScriptsFormatted(path));
            }
        }

        if (result.HadErrors)
            sb.Append('\n').Append(CommandStrings.ScriptsFormatHasErrors);

        return ctx.Result.Ok(sb.ToString());
    }

    private static int New(CommandContext ctx, string? template, string? id)
    {
        if (string.IsNullOrWhiteSpace(template) || string.IsNullOrWhiteSpace(id))
        {
            return ctx.Result.Fail(
                CommandStrings.ScriptsNewUsage(string.Join(", ", BeaconTemplates.Names)));
        }

        if (ctx.Config?.SourceFolder is not { } folder)
            return ctx.Result.Fail(CommandStrings.ScriptsNewNoFolder);

        ScriptCreateResult created = ctx.Client.Scripts.Create(template.Trim(), id.Trim());
        if (!created.Created)
            return ctx.Result.Fail(created.Error);

        return ctx.Result.Ok(CommandStrings.ScriptsCreated(created.Path!, template.Trim()));
    }

    private int Mute(CommandContext ctx, string? mode)
    {
        ScriptsRuntime runtime = GetRuntime(ctx.Client);
        if (string.Equals(mode, "on", StringComparison.OrdinalIgnoreCase))
            ctx.Client.Scripts.SetMuted(true);
        else if (string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase))
            ctx.Client.Scripts.SetMuted(false);

        return ctx.Result.Ok(runtime.MuteStatus());
    }

    private int Repl(CommandContext ctx, string? rawLine)
    {
        ScriptsRuntime runtime = GetRuntime(ctx.Client);
        if (string.IsNullOrWhiteSpace(rawLine))
            return ctx.Result.Ok(runtime.Repl.LocalsText());

        string text = rawLine.Trim();
        int? seed = null;
        if (text.StartsWith("--seed ", StringComparison.Ordinal))
        {
            string rest = text["--seed ".Length..].TrimStart();
            int space = rest.IndexOfAny([' ', '\t']);
            string number = space < 0 ? rest : rest[..space];
            if (!int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                return ctx.Result.Fail(CommandStrings.ScriptsReplUsage);

            seed = parsed;
            text = space < 0 ? string.Empty : rest[(space + 1)..];
        }

        if (string.IsNullOrWhiteSpace(text))
            return ctx.Result.Ok(runtime.Repl.LocalsText());

        BeaconReplResult result = ctx.Run(ct => ctx.Client.Scripts.EvaluateAsync(text, seed, ct));
        return result.Success
            ? ctx.Result.Ok(result.Output)
            : ctx.Result.Fail(result.Error);
    }

    private int Watch(CommandContext ctx, string? mode)
    {
        ScriptsRuntime runtime = GetRuntime(ctx.Client);
        if (string.Equals(mode, "on", StringComparison.OrdinalIgnoreCase))
        {
            if (ctx.Config?.SourceFolder is not { } folder)
                return ctx.Result.Fail(CommandStrings.ScriptsWatchNoFolder);

            ctx.Client.Scripts.SetWatch(true);
            return ctx.Result.Ok(runtime.WatchStatus());
        }

        if (string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Client.Scripts.SetWatch(false);
            return ctx.Result.Ok(runtime.WatchStatus());
        }

        return ctx.Result.Ok(runtime.WatchStatus());
    }

    private int Config(CommandContext ctx, string? rawArgs)
    {
        List<string> tokens = string.IsNullOrWhiteSpace(rawArgs)
            ? []
            : Tokenize(rawArgs);
        if (tokens.Count == 0)
            return ctx.Result.Fail(CommandStrings.ScriptsConfigUsage);

        ScriptsRuntime runtime = GetRuntime(ctx.Client);
        string id = tokens[0];
        if (!TryResolveScriptFile(ctx, id, out string scriptId, out string path, out string error))
            return ctx.Result.Fail(error);

        string source;
        try
        {
            source = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ctx.Result.Fail(CommandStrings.ScriptsCouldNotRead(path, ex.Message));
        }

        BeaconHeaderResult header = BeaconHeader.Parse(Path.GetFileName(path), source);
        if (!header.Ok)
        {
            BeaconDiagnostic first = header.Diagnostics.FirstOrDefault(d => d.Severity == BeaconSeverity.Error)
                ?? new BeaconDiagnostic(
                    BeaconDiagnosticCodes.Parse, BeaconSeverity.Error,
                    CommandStrings.ScriptsConfigNoSchema(scriptId),
                    new SourceSpan(Path.GetFileName(path), 1, 1, 0), null);
            return ctx.Result.Fail(FormatDiagnostic(first));
        }

        if (header.Settings.Count == 0)
            return ctx.Result.Ok(CommandStrings.ScriptsConfigNoSchema(scriptId));

        if (tokens.Count == 1)
        {
            IReadOnlyDictionary<string, BeaconValue> resolved = BeaconScriptSettings.Resolve(
                scriptId, header.Settings, ctx.Config?.SourceFolder);
            var sb = new StringBuilder();
            sb.Append(CommandStrings.ScriptsConfigHeader(scriptId));
            foreach (BeaconSettingDecl schemaDecl in header.Settings)
            {
                resolved.TryGetValue(schemaDecl.Name, out BeaconValue? shown);
                sb.Append('\n').Append(CommandStrings.ScriptsConfigRow(
                    schemaDecl.Name,
                    shown is null ? "none" : BeaconInterpreter.ToDisplayText(shown),
                    schemaDecl.Comment));
            }

            return ctx.Result.Ok(sb.ToString());
        }

        if (tokens.Count != 3)
            return ctx.Result.Fail(CommandStrings.ScriptsConfigUsage);

        string key = tokens[1];
        BeaconSettingDecl? match = header.Settings.FirstOrDefault(s => string.Equals(s.Name, key, StringComparison.Ordinal));
        if (match is null)
            return ctx.Result.Fail(CommandStrings.ScriptsConfigUnknownKey(scriptId, key));

        BeaconValue newValue = ParseSettingScalar(tokens[2]);
        string? writeError = WriteSettingValue(ctx.Config?.SourceFolder, scriptId, header.Settings, key, newValue);
        if (writeError is not null)
            return ctx.Result.Fail(writeError);

        bool live = runtime.Engine.SetRuntimeSetting(scriptId, key, newValue);
        return ctx.Result.Ok(CommandStrings.ScriptsConfigSet(scriptId, key, BeaconInterpreter.ToDisplayText(newValue), live));
    }

    private static BeaconValue ParseSettingScalar(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length >= 2 && trimmed.StartsWith('"') && trimmed.EndsWith('"'))
            return BeaconValue.Text(trimmed[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\"));

        if (string.Equals(trimmed, "yes", StringComparison.OrdinalIgnoreCase))
            return BeaconValue.YesNo(true);

        if (string.Equals(trimmed, "no", StringComparison.OrdinalIgnoreCase))
            return BeaconValue.YesNo(false);

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            && !double.IsNaN(number) && !double.IsInfinity(number))
            return BeaconValue.Number(number);

        return BeaconValue.Text(trimmed);
    }

    private static string? WriteSettingValue(
        string? configFolder, string scriptId, IReadOnlyList<BeaconSettingDecl> schema, string key, BeaconValue value)
    {
        if (configFolder is null)
            return CommandStrings.ScriptsConfigNoFolder;

        string path;
        try
        {
            path = Path.GetFullPath(ConfigurationPaths.BeaconSettingsFile(configFolder, scriptId));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return CommandStrings.ScriptsConfigWriteFailed(path: scriptId, reason: ex.Message);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
                File.WriteAllText(path, BeaconScriptSettings.RenderDefaults(scriptId, schema));

            string[] lines = File.ReadAllText(path).Split(["\r\n", "\n"], StringSplitOptions.None);
            string rendered = key + " = " + RenderSettingScalar(value);
            bool replaced = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string stripped = lines[i].TrimStart();
                if (stripped.StartsWith('#'))
                    continue;

                int equals = stripped.IndexOf('=');
                if (equals < 0)
                    continue;

                if (string.Equals(stripped[..equals].Trim(), key, StringComparison.Ordinal))
                {
                    lines[i] = rendered;
                    replaced = true;
                    break;
                }
            }

            string text = replaced ? string.Join("\n", lines) : string.Join("\n", lines) + "\n" + rendered + "\n";
            File.WriteAllText(path, text);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CommandStrings.ScriptsConfigWriteFailed(path, ex.Message);
        }
    }

    private static string RenderSettingScalar(BeaconValue value) => value switch
    {
        BeaconTextValue text => "\"" + text.Value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
        BeaconNumberValue number => number.Value.ToString("G", CultureInfo.InvariantCulture),
        BeaconYesNoValue yesNo => yesNo.Value ? "true" : "false",
        _ => "\"\"",
    };

    private static bool TryResolveScriptFile(
        CommandContext ctx, string operand, out string scriptId, out string path, out string error)
    {
        scriptId = string.Empty;
        path = string.Empty;
        error = string.Empty;

        string given = operand.Trim();
        if (given.Length == 0)
        {
            error = CommandStrings.ScriptsRunUsage;
            return false;
        }

        if (File.Exists(given))
        {
            path = Path.GetFullPath(given);
            scriptId = Path.GetFileNameWithoutExtension(path);
            if (scriptId.Length == 0)
                scriptId = given;

            return true;
        }

        // A path with separators that does not exist is a missing file, never a scripts-dir probe: combining it would let ".." escape the scripts folder.
        if (given.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            error = CommandStrings.ScriptsFileNotFound(given);
            return false;
        }

        if (ctx.Config?.SourceFolder is { } folder)
        {
            string dir = ConfigurationPaths.ScriptsDir(folder);
            string file = given.EndsWith(
                BeaconScriptDiscovery.ScriptExtension, StringComparison.OrdinalIgnoreCase)
                ? given
                : given + BeaconScriptDiscovery.ScriptExtension;
            string candidate = Path.Combine(dir, file);
            if (File.Exists(candidate))
            {
                path = Path.GetFullPath(candidate);
                scriptId = Path.GetFileNameWithoutExtension(path);
                return true;
            }

            error = CommandStrings.ScriptsIdNotFound(given, candidate);
            return false;
        }

        error = CommandStrings.ScriptsIdNotFoundNoFolder(given);
        return false;
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        bool inToken = false;
        bool inDouble = false;
        bool inSingle = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inDouble)
            {
                if (c == '\\' && i + 1 < text.Length && (text[i + 1] == '"' || text[i + 1] == '\\'))
                {
                    current.Append(text[i + 1]);
                    i++;
                }
                else if (c == '"')
                    inDouble = false;
                else
                    current.Append(c);

                continue;
            }

            if (inSingle)
            {
                if (c == '\'')
                    inSingle = false;
                else
                    current.Append(c);

                continue;
            }

            if (c == '"')
            {
                inDouble = true;
                inToken = true;
            }
            else if (c == '\'')
            {
                inSingle = true;
                inToken = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }
            }
            else
            {
                current.Append(c);
                inToken = true;
            }
        }

        if (inToken)
            tokens.Add(current.ToString());

        return tokens;
    }

    private static ValueTask SuggestScriptIds(
        ICommandContext<CommandContext> ctx, ISuggestionSink sink)
    {
        try
        {
            foreach (string id in DiscoverIds(ctx))
            {
                if (id.StartsWith(sink.Remaining, StringComparison.OrdinalIgnoreCase))
                    sink.Suggest(id);
            }
        }
        catch
        {
            // Completion never fails a line; no candidates is a valid answer.
        }

        return ValueTask.CompletedTask;
    }

    private static ValueTask SuggestScriptIdsOrAll(
        ICommandContext<CommandContext> ctx, ISuggestionSink sink)
    {
        try
        {
            if ("all".StartsWith(sink.Remaining, StringComparison.OrdinalIgnoreCase))
                sink.Suggest("all");

            foreach (string id in DiscoverIds(ctx))
            {
                if (id.StartsWith(sink.Remaining, StringComparison.OrdinalIgnoreCase))
                    sink.Suggest(id);
            }
        }
        catch
        {
            // Completion never fails a line; no candidates is a valid answer.
        }

        return ValueTask.CompletedTask;
    }

    private static ValueTask SuggestRunningIds(
        ICommandContext<CommandContext> ctx, ISuggestionSink sink)
    {
        try
        {
            IReadOnlyCollection<string> running = ctx.Source.Client.Scripts.Runtime.Engine.ScriptIds;
            if (running.Count > 0)
            {
                foreach (string id in running)
                {
                    if (id.StartsWith(sink.Remaining, StringComparison.OrdinalIgnoreCase))
                        sink.Suggest(id);
                }
            }
            else
            {
                foreach (string id in DiscoverIds(ctx))
                {
                    if (id.StartsWith(sink.Remaining, StringComparison.OrdinalIgnoreCase))
                        sink.Suggest(id);
                }
            }
        }
        catch
        {
            // Completion never fails a line; no candidates is a valid answer.
        }

        return ValueTask.CompletedTask;
    }

    private static ValueTask SuggestTemplates(
        ICommandContext<CommandContext> ctx, ISuggestionSink sink)
    {
        _ = ctx;
        foreach (string name in BeaconTemplates.Names)
        {
            if (name.StartsWith(sink.Remaining, StringComparison.OrdinalIgnoreCase))
                sink.Suggest(name);
        }

        return ValueTask.CompletedTask;
    }

    private static IEnumerable<string> DiscoverIds(ICommandContext<CommandContext> ctx)
    {
        if (ctx.Source.Config?.SourceFolder is not { } folder)
            yield break;

        IReadOnlyList<BeaconScriptFile> found;
        try
        {
            found = BeaconScriptDiscovery.Discover(folder);
        }
        catch
        {
            yield break;
        }

        foreach (BeaconScriptFile script in found)
            yield return script.ScriptId;
    }

    /// <summary>
    /// One client's Beacon runtime: the single engine (global chat bucket), the persistent REPL, and the mute gag with its held-chat count, all owned here and observed by the live-session adapter's callbacks.
    /// Live movement binds per script after each load: the runner keeps newest-wins preemption across scripts while the executor steers through the session navigator; reconnects cancel through the engine session propagation below.
    /// Live session events reach the event bus here: chat lines arrive off the stable <c>ChatApi</c> facade (survives reconnects, no rebind), death and respawn pushes bind per session scope (rebound on every play entry, detached on disconnect, never doubled), dialogs bind per session client (one subscription per connect attempt, so configuration-phase dialogs are seen as well as play-phase ones), and vitals, tab-list, tick-rate, and inventory snapshots poll in the timer pump with change baselines so handlers fire once per change instead of every tick.
    /// </summary>
    internal sealed class ScriptsRuntime
    {
        private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan DisconnectGrace = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan DeathDedupeWindow = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan FailureNoticeWindow = TimeSpan.FromSeconds(60);
        private const double MaxHealthDefault = 20;
        private const float SaturationThreshold = 1.0f;

        private readonly object _gate = new();
        private readonly Client _client;
        private bool _muted;
        private int _held;
        private FileSystemWatcher? _watcher;
        private string? _watchDir;
        private readonly Dictionary<string, DateTimeOffset> _pendingReloads = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTimeOffset> _failureNotices = new(StringComparer.Ordinal);
        private IDisposable? _diedSubscription;
        private IDisposable? _respawnedSubscription;
        private int _statusGeneration;
        private bool _hasVitalsBaseline;
        private double _lastHealth;
        private int _lastFood;
        private double _lastSaturation;
        private bool _hasTabBaseline;
        private List<string> _lastTabNames = [];
        private List<string> _lastTabSigs = [];
        private string? _lastTabHeader;
        private string? _lastTabFooter;
        private bool _hasTpsBaseline;
        private double? _lastTps;
        private bool _hasInvBaseline;
        private List<string> _lastInvPrints = [];
        private int? _lastInvWindow;
        private List<string>? _lastWindowPrints;
        private bool _hasEntityBaseline;
        private Dictionary<int, BeaconEntityInfo> _lastEntities = new();
        private string? _lastPushDeathWho;
        private DateTimeOffset _lastPushDeathAt = DateTimeOffset.MinValue;
        private string? _lastChatDeathWho;
        private DateTimeOffset _lastChatDeathAt = DateTimeOffset.MinValue;

        public ScriptsRuntime(Client client)
        {
            ArgumentNullException.ThrowIfNull(client);
            _client = client;
            Host = new BeaconClientHost(client, () => IsMuted, NoteHeld);
            Engine = new BeaconEngine(Host);
            Engine.Variables = client.Variables;
            BeaconEngineWiring.Bind(client, Engine);
            Repl = new BeaconRepl(Host);
            client.Game.Chat.MessageReceived += OnChatMessage;
            client.Game.Inventory.ContainerOpened += OnContainerOpened;
            client.Game.Inventory.ContainerClosed += OnContainerClosed;
            client.StatusChanged += OnStatusChanged;
            // One dialog subscription per session client, bound before the dial: the UMPK bus on that client publishes configuration-phase dialogs as well as play-phase ones, and a play-entry subscription would miss the former.
            client.ObserveSessionClients(OnSessionClient);
            if (client.BoundSessionClient is { } current)
                // Scripts usually load after the login already happened, so the pre-dial hook above never fires for this session: bind it now.
                OnSessionClient(current);
            Engine.MovementBinder = BindLiveMovement;
            if (client.Status == ClientStatus.Playing)
                // Scripts usually load after the login already happened, so the play-entry transition below never fires for this session: bind the pushes now.
                AttachSessionSubscriptions();

            StartTimerPump();
        }

        /// <summary>
        /// Builds one load's live movement binding: runner preemption plus the session navigator executor with sneak/sprint and tolerance options.
        /// Headless runs keep the free scope with immediate arrival; this replaces it per script load, before the load fires, so start-hook movement already steers for real.
        /// </summary>
        private BeaconMovementBinding BindLiveMovement(string scriptId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
            Client client = _client;
            BeaconGameplayGates gates = client.Configuration?.Gameplay is { } gameplay
                ? BeaconGameplayGates.FromMcc(gameplay)
                : BeaconGameplayGates.AllOn;
            return new BeaconMovementBinding(
                new BeaconFreeMovementScope(),
                async (target, ct) =>
                {
                    if (target.Kind == BeaconMovementKind.Follow)
                    {
                        await FollowLiveAsync(client, target.Player ?? string.Empty, ct).ConfigureAwait(false);
                        return new BeaconMoveArrival(true, "follow ended");
                    }

                    if (target.Kind == BeaconMovementKind.FollowEntity)
                    {
                        await FollowEntityLiveAsync(
                            client, target.EntityId ?? -1, target.EntityDesc ?? "entity", ct).ConfigureAwait(false);
                        return new BeaconMoveArrival(true, "follow ended");
                    }

                    BeaconMoveOptions options = target.Options ?? new BeaconMoveOptions();
                    if (options.Sneak)
                        await client.Game.Movement.SetSneakingAsync(true, ct).ConfigureAwait(false);

                    if (options.Sprint)
                        await client.Game.Movement.SetSprintingAsync(true, ct).ConfigureAwait(false);

                    try
                    {
                        await NavigateWithSessionPreemptionAsync(
                            client,
                            linkedCt => client.Game.Movement.NavigateToAsync(
                                BlockPos.Containing(target.X, target.Y, target.Z),
                                Math.Max(1, (int)Math.Ceiling(options.Tolerance)),
                                linkedCt),
                            ct).ConfigureAwait(false);
                        return new BeaconMoveArrival(true, $"arrived within {options.Tolerance} blocks");
                    }
                    finally
                    {
                        if (options.Sneak)
                        {
                            try
                            {
                                await client.Game.Movement.SetSneakingAsync(false, CancellationToken.None).ConfigureAwait(false);
                            }
                            catch (Exception ex) when (ex is not OutOfMemoryException)
                            {
                            }
                        }

                        if (options.Sprint)
                        {
                            try
                            {
                                await client.Game.Movement.SetSprintingAsync(false, CancellationToken.None).ConfigureAwait(false);
                            }
                            catch (Exception ex) when (ex is not OutOfMemoryException)
                            {
                            }
                        }
                    }
                },
                gates);
        }

        /// <summary>
        /// Pumps <c>every</c> and one-shot timers while the session plays.
        /// The headless runner pumps explicitly per tick, but a live client needs this background loop or timer work silently never fires.
        /// Weak references on both ends so an abandoned runtime (and its client) still collects; per-tick failures never kill the loop.
        /// </summary>
        private void StartTimerPump()
        {
            var weakClient = new WeakReference<Client>(_client);
            WeakReference<BeaconEngine> weakEngine = new(Engine);
            _ = Task.Run(async () =>
            {
                while (weakClient.TryGetTarget(out Client? client)
                    && weakEngine.TryGetTarget(out BeaconEngine? engine))
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                        if (client.Status != ClientStatus.Playing)
                            continue;

                        await engine.TickEveryAsync().ConfigureAwait(false);
                        await engine.TickOnceAsync().ConfigureAwait(false);
                        await PollSessionAsync(client).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                    }
                }
            });
        }

        public BeaconClientHost Host { get; }

        public BeaconEngine Engine { get; }

        public void Dispose()
        {
            SetWatch(false, null);
            DetachSessionSubscriptions();
            _client.Game.Chat.MessageReceived -= OnChatMessage;
            _client.Game.Inventory.ContainerOpened -= OnContainerOpened;
            _client.Game.Inventory.ContainerClosed -= OnContainerClosed;
            _client.StatusChanged -= OnStatusChanged;
            foreach (string scriptId in Engine.ScriptIds.ToArray())
                Engine.RemoveScript(scriptId);
        }

        public BeaconRepl Repl { get; }

        public bool IsMuted
        {
            get { lock (_gate) return _muted; }
        }

        public int Held
        {
            get { lock (_gate) return _held; }
        }

        public void SetMuted(bool muted)
        {
            lock (_gate)
                _muted = muted;
        }

        public void NoteHeld()
        {
            lock (_gate)
                _held++;
        }

        public string MuteStatus()
        {
            lock (_gate)
                return CommandStrings.ScriptsMuteStatus(_muted, _held);
        }

        private static async Task FollowLiveAsync(Client client, string player, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                Vec3d? position = null;
                try
                {
                    IReadOnlyList<EntitySnapshot> nearby =
                        await client.Game.Entities.NearbyAsync(64, ct).ConfigureAwait(false);
                    foreach (EntitySnapshot entity in nearby)
                    {
                        if (string.Equals(entity.PlayerName, player, StringComparison.OrdinalIgnoreCase))
                        {
                            position = entity.Position;
                            break;
                        }
                    }
                }
                catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
                    or OperationCanceledException or DmcbkFeatureDisabledException)
                {
                }

                if (position is null)
                    throw new InvalidOperationException($"player '{player}' is not tracked nearby");

                await NavigateWithSessionPreemptionAsync(
                    client,
                    linkedCt => client.Game.Movement.NavigateToAsync(
                        BlockPos.Containing(position.Value), 2, linkedCt),
                    ct).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
        }

        private static async Task FollowEntityLiveAsync(Client client, int entityId, string desc, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                Vec3d? position = null;
                try
                {
                    EntitySnapshot? tracked = await client.Game.Entities.ByIdAsync(entityId, ct).ConfigureAwait(false);
                    position = tracked?.Position;
                }
                catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
                    or OperationCanceledException or DmcbkFeatureDisabledException)
                {
                }

                if (position is null)
                    throw new InvalidOperationException($"entity '{desc}' (id {entityId}) is no longer tracked nearby");

                await NavigateWithSessionPreemptionAsync(
                    client,
                    linkedCt => client.Game.Movement.NavigateToAsync(
                        BlockPos.Containing(position.Value), 2, linkedCt),
                    ct).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Runs one session navigation, tolerating the handoff window that newest-wins preemption creates: the runner cancels the superseded Beacon request first, but its in-flight navigator call releases the exclusive session lease a beat later.
        /// A newcomer that arrives in that window retries (bounded, cancellation-aware) instead of failing outright, so the newest request actually wins.
        /// Anything else, or a lease that stays held, still throws to the caller.
        /// </summary>
        private static async Task NavigateWithSessionPreemptionAsync(
            Client client,
            Func<CancellationToken, Task> navigate,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(navigate);
            const int maxAttempts = 10;
            var backoff = TimeSpan.FromMilliseconds(200);
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await navigate(ct).ConfigureAwait(false);
                    return;
                }
                catch (InvalidOperationException ex)
                    when (attempt < maxAttempts
                        && ex.Message.Contains("Movement is held by", StringComparison.Ordinal))
                {
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                }
            }
        }

        private void OnContainerOpened(object? sender, ContainerOpenedEventArgs args)
        {
            // Off-loop by construction: container notifications arrive on the session loop, so the fire-and-forget hop keeps the handler cheap and the bus dispatch serialized.
            _ = Task.Run(async () =>
            {
                try
                {
                    await Engine.FireEventAsync(
                        "container_open",
                        BeaconEventFields.ContainerOpen(args.WindowId, args.Title)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                }
            });
        }

        private void OnContainerClosed(object? sender, ContainerClosedEventArgs args)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Engine.FireEventAsync(
                        "container_close",
                        BeaconEventFields.ContainerClose(args.WindowId)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                }
            });
        }

        private void OnSessionClient(UmpkClient umpk)
        {
            ArgumentNullException.ThrowIfNull(umpk);
            // No lifetime tracking: a session client is built fresh per attempt and goes away with it, so a subscription here dies with its session.
            // The handler itself hops off-loop before touching the engine.
            umpk.Events.Subscribe<DialogShown>(shown => OnDialogShown(umpk, shown));
            umpk.Events.Subscribe<DialogCleared>(_ => Host.ClearStagedDialogInputs());
        }

        private void OnDialogShown(UmpkClient umpk, DialogShown shown)
        {
            ArgumentNullException.ThrowIfNull(umpk);
            ArgumentNullException.ThrowIfNull(shown);
            // Off-loop by construction: configuration-phase dialogs arrive inline on the connect path, so the snapshot is read here (same thread that just wrote the state) and only the engine dispatch hops to the pool.
            IReadOnlyDictionary<string, BeaconValue> fields;
            try
            {
                fields = SnapshotDialog(umpk, shown.RegistryId);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return;
            }

            Host.ClearStagedDialogInputs();
            _ = Task.Run(async () =>
            {
                try
                {
                    await Engine.FireEventAsync("dialog", fields).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                }
            });
        }

        private static IReadOnlyDictionary<string, BeaconValue> SnapshotDialog(
            UmpkClient umpk, int? registryId)
        {
            Umpk.Game.Dialogs.Dialog? dialog = umpk.State.Dialogs.Current;
            if (dialog is null)
                return BeaconEventFields.Dialog(string.Empty, [], [], [], registryId);

            string title;
            try
            {
                title = dialog.Title.ToPlainText(null);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                title = string.Empty;
            }

            var body = new List<string>(dialog.Body.Count);
            foreach (Umpk.Game.Dialogs.DialogBodyElement element in dialog.Body)
            {
                string text;
                try
                {
                    text = element.Contents?.ToPlainText(null) ?? element.Type.ToString();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    text = element.Type.ToString();
                }

                if (!string.IsNullOrWhiteSpace(text))
                    body.Add(text);
            }

            var inputs = new List<(string Key, string? Label, string Kind, string Value)>(dialog.Inputs.Count);
            foreach (Umpk.Game.Dialogs.DialogInput input in dialog.Inputs)
            {
                string? label;
                try
                {
                    label = input.Label?.ToPlainText(null);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    label = null;
                }

                inputs.Add((input.Key, label, input.Kind.ToString(), input.InitialValue));
            }

            var buttons = new List<(int Index, string Label)>(dialog.Buttons.Count);
            for (int i = 0; i < dialog.Buttons.Count; i++)
            {
                string label;
                try
                {
                    label = dialog.Buttons[i].Label.ToPlainText(null);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    label = string.Empty;
                }

                buttons.Add((i + 1, label));
            }

            return BeaconEventFields.Dialog(title, body, inputs, buttons, registryId: null);
        }

        private void OnStatusChanged(object? sender, ClientStatusChangedEventArgs args)
        {
            ArgumentNullException.ThrowIfNull(args);
            if (args.Current == ClientStatus.Disconnected)
            {
                DetachSessionSubscriptions();
                ResetPollBaselines();
                int generation = Interlocked.Increment(ref _statusGeneration);
                string reason = DisconnectDescription.Describe(args.Disconnect);
                bool isKick = args.Disconnect?.IsKick == true;
                bool cleanLogout = args.Disconnect is { WasLocal: true };
                _ = Task.Run(async () =>
                {
                    // Lifecycle hooks fire BEFORE the engine drops the sessions: after the drop the bus short-circuits to empty and no handler would run.
                    // The grace window bounds a stalling handler, and the generation guard keeps a stale drop from landing on a session that already reconnected behind it.
                    try
                    {
                        using var grace = new CancellationTokenSource(DisconnectGrace);
                        CancellationToken ct = grace.Token;
                        if (cleanLogout)
                        {
                            await Engine.FireEventAsync(
                                "logout",
                                new Dictionary<string, BeaconValue>(StringComparer.Ordinal),
                                ct).ConfigureAwait(false);
                        }

                        await Engine.FireEventAsync(
                            "disconnect", BeaconEventFields.Disconnect(reason), ct).ConfigureAwait(false);
                        if (isKick)
                        {
                            await Engine.FireEventAsync(
                                "kick", BeaconEventFields.Kick(reason), ct).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                    }

                    try
                    {
                        if (Volatile.Read(ref _statusGeneration) == generation)
                            Engine.HandleDisconnect(reason);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                    }
                });
            }
            else if (args.Current == ClientStatus.Playing && args.Previous != ClientStatus.Playing)
            {
                Interlocked.Increment(ref _statusGeneration);
                AttachSessionSubscriptions();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Engine.HandleReconnectAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                    }
                });
            }
        }

        /// <summary>
        /// Binds the per-session pushes (death, respawn) to the live session bus.
        /// Runs on every play entry, initial and reconnect alike: the bus belongs to the session, so the previous one dies with it.
        /// Detach-then-attach under one lock, so this never double-subscribes.
        /// </summary>
        private void AttachSessionSubscriptions()
        {
            lock (_gate)
            {
                DetachSessionSubscriptionsLocked();
                ClientEvents events;
                try
                {
                    events = _client.Game.Events;
                }
                catch (Exception ex) when (ex is DmcbkNotInSessionException or InvalidOperationException)
                {
                    return;
                }

                try
                {
                    _diedSubscription = events.Subscribe<Died>(OnDied);
                    _respawnedSubscription = events.Subscribe<Respawned>(OnRespawned);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    DetachSessionSubscriptionsLocked();
                }
            }
        }

        private void DetachSessionSubscriptions()
        {
            lock (_gate)
                DetachSessionSubscriptionsLocked();
        }

        private void DetachSessionSubscriptionsLocked()
        {
            _diedSubscription?.Dispose();
            _diedSubscription = null;
            _respawnedSubscription?.Dispose();
            _respawnedSubscription = null;
        }

        private void ResetPollBaselines()
        {
            lock (_gate)
            {
                _hasVitalsBaseline = false;
                _hasTabBaseline = false;
                _lastTabNames = [];
                _lastTabSigs = [];
                _lastTabHeader = null;
                _lastTabFooter = null;
                _hasTpsBaseline = false;
                _lastTps = null;
                _hasInvBaseline = false;
                _lastInvPrints = [];
                _lastInvWindow = null;
                _lastWindowPrints = null;
                _hasEntityBaseline = false;
                _lastEntities = new();
            }
        }

        /// <summary>
        /// Forwards one inbound chat line to the bus.
        /// Runs on the session loop, so it only captures the immutable record and hops off-loop; rendering and classification never block the loop.
        /// Overlay (action-bar) text never fires a hook.
        /// </summary>
        private void OnChatMessage(object? sender, ChatMessageReceived message)
        {
            if (message is null || message.IsOverlay)
                return;

            Client client = _client;
            BeaconClientHost host = Host;
            BeaconEngine engine = Engine;
            _ = Task.Run(async () =>
            {
                try
                {
                    var translations = client.Translations;
                    string raw = SafePlain(message.Message, translations);
                    if (string.IsNullOrWhiteSpace(raw))
                        return;

                    string? senderName = SafePlain(message.SenderName, translations);
                    if (string.IsNullOrWhiteSpace(senderName))
                        senderName = null;

                    string? translationKey = message.Message.Content is TranslatableContent translatable
                        ? translatable.Key
                        : null;

                    // A death announcement doubles as the death hook (the only notice other players deaths ever produce); the line still fires its chat hook below.
                    if (translationKey is not null
                        && translationKey.StartsWith("death.", StringComparison.Ordinal))
                        FireDeathFromChat(raw, message, translations);

                    (string Hook, IReadOnlyDictionary<string, BeaconValue> Fields) classified =
                        ClassifyChatMessage(raw.Trim(), senderName, message.Category, translationKey);
                    BeaconFireResult fire = await engine
                        .FireEventAsync(classified.Hook, classified.Fields).ConfigureAwait(false);
                    if (fire.Suppressed)
                    {
                        List<string> scripts = fire.Handlers
                            .Where(h => h.Result?.EventSuppressed == true)
                            .Select(h => h.ScriptId)
                            .Distinct(StringComparer.Ordinal)
                            .ToList();
                        host.NoteSuppressedChat(message, classified.Hook, scripts);
                    }

                    ReportFireFailures(classified.Hook, fire);

                    IReadOnlyDictionary<string, BeaconValue> rawChatFields =
                        BuildRawChatFields(message, translations, raw, senderName, translationKey);
                    BeaconFireResult rawFire = await engine
                        .FireEventAsync("raw_chat", rawChatFields).ConfigureAwait(false);
                    ReportFireFailures("raw_chat", rawFire);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                }
            });
        }

        private static string SafePlain(Component? component, ITranslationSource? translations)
        {
            if (component is null)
                return string.Empty;

            try
            {
                return component.ToPlainText(translations) ?? string.Empty;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Builds the <c>raw_chat</c> field map 1:1 from <see cref="ChatMessageReceived"/>: raw from Message, category from Category, sender from SenderName, sender_id from SenderId, chat_type from ChatTypeId, target from TargetName, body from Body, translation key from the message content, verified from Verification.
        /// </summary>
        private static IReadOnlyDictionary<string, BeaconValue> BuildRawChatFields(
            ChatMessageReceived message,
            ITranslationSource? translations,
            string raw,
            string? senderName,
            string? translationKey)
        {
            string? targetName = SafePlain(message.TargetName, translations);
            if (string.IsNullOrWhiteSpace(targetName))
                targetName = null;

            string body = SafePlain(message.Body, translations);
            string? senderId = message.SenderId?.ToString();
            if (string.IsNullOrWhiteSpace(senderId))
                senderId = null;

            string? sender = string.IsNullOrWhiteSpace(senderName) ? null : senderName.Trim();
            string category = message.Category.ToString().ToLowerInvariant();
            bool verified = message.Verification == ChatVerification.Verified;
            return BeaconEventFields.RawChat(
                raw, category, sender, senderId, message.ChatTypeId, targetName, body, translationKey, verified);
        }

        /// <summary>
        /// Maps one chat line onto its hook and documented fields, mirroring the plugin SDK classifier: category first, then text shape (whispers and teleport requests even when the era relays them as system text), then the angle-bracket chat form.
        /// A line with no sender and no chat shape is a server message.
        /// </summary>
        private static (string Hook, IReadOnlyDictionary<string, BeaconValue> Fields) ClassifyChatMessage(
            string trimmed, string? sender, ChatCategory category, string? translationKey)
        {
            int tpaIndex = trimmed.IndexOf("teleport", StringComparison.OrdinalIgnoreCase);
            if (tpaIndex > 0
                && (trimmed.Contains("request", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains("wants to", StringComparison.OrdinalIgnoreCase)))
            {
                string? who = sender ?? FirstWord(trimmed);
                return string.IsNullOrWhiteSpace(who)
                    ? ("server_message", BeaconEventFields.ServerMessage(trimmed, translationKey))
                    : ("whisper", BeaconEventFields.Whisper(who, trimmed, trimmed));
            }

            int whisperIndex = trimmed.IndexOf("whisper", StringComparison.OrdinalIgnoreCase);
            if (whisperIndex > 0 || trimmed.Contains("-> me", StringComparison.OrdinalIgnoreCase))
            {
                string body = trimmed;
                string? who = sender;
                if (whisperIndex > 0)
                {
                    int colon = trimmed.IndexOf(':', whisperIndex);
                    if (colon >= 0)
                        body = trimmed[(colon + 1)..].Trim();

                    who ??= trimmed[..whisperIndex].Trim();
                }

                return string.IsNullOrWhiteSpace(who)
                    ? ("server_message", BeaconEventFields.ServerMessage(trimmed, translationKey))
                    : ("whisper", BeaconEventFields.Whisper(who, body, trimmed));
            }

            if (sender is not null)
            {
                return ("chat", BeaconEventFields.Chat(
                    sender, StripSenderPrefix(trimmed, sender), trimmed, isPrivate: false));
            }

            if (trimmed.StartsWith('<'))
            {
                int close = trimmed.IndexOf('>');
                if (close > 1)
                {
                    string name = trimmed[1..close];
                    return ("chat", BeaconEventFields.Chat(
                        name, trimmed[(close + 1)..].Trim(), trimmed, isPrivate: false));
                }
            }

            return ("server_message", BeaconEventFields.ServerMessage(trimmed, translationKey));
        }

        private static string StripSenderPrefix(string raw, string sender)
        {
            string angle = $"<{sender}>";
            int angleIndex = raw.IndexOf(angle, StringComparison.Ordinal);
            if (angleIndex >= 0)
                return raw[(angleIndex + angle.Length)..].Trim();

            string colon = $"{sender}:";
            int colonIndex = raw.IndexOf(colon, StringComparison.Ordinal);
            if (colonIndex >= 0)
                return raw[(colonIndex + colon.Length)..].Trim();

            return raw;
        }

        private static string FirstWord(string text)
        {
            int space = text.IndexOf(' ');
            return space > 0 ? text[..space] : text;
        }

        /// <summary>
        /// Fires the death hook for a death announcement, unless the self-death push already claimed this victim moments ago (one death, one fire, whichever source wins the race).
        /// </summary>
        private void FireDeathFromChat(
            string raw, ChatMessageReceived message, ITranslationSource? translations)
        {
            string victim = string.Empty;
            try
            {
                if (message.Message.Content is TranslatableContent death && death.Args.Count > 0)
                    victim = (death.Args[0].ToPlainText(translations) ?? string.Empty).Trim();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
            }

            if (string.IsNullOrWhiteSpace(victim))
                return;

            lock (_gate)
            {
                if (string.Equals(_lastPushDeathWho, victim, StringComparison.Ordinal)
                    && DateTimeOffset.UtcNow - _lastPushDeathAt < DeathDedupeWindow)
                    return;

                _lastChatDeathWho = victim;
                _lastChatDeathAt = DateTimeOffset.UtcNow;
            }

            FireLive("death", BeaconEventFields.Death(victim, raw));
        }

        /// <summary>
        /// Fires the death hook for our own death push.
        /// Self-only (no cause rides the push), skipped when the matching death announcement already fired it.
        /// </summary>
        private void OnDied(Died _)
        {
            if (_client.Status != ClientStatus.Playing)
                return;

            string self = Host.SelfName ?? CommandStrings.ScriptsSelfNameFallback;
            lock (_gate)
            {
                if (string.Equals(_lastChatDeathWho, self, StringComparison.Ordinal)
                    && DateTimeOffset.UtcNow - _lastChatDeathAt < DeathDedupeWindow)
                    return;

                _lastPushDeathWho = self;
                _lastPushDeathAt = DateTimeOffset.UtcNow;
            }

            FireLive("death", BeaconEventFields.Death(self, CommandStrings.ScriptsUnknownDeathCause));
        }

        private void OnRespawned(Respawned _)
        {
            if (_client.Status != ClientStatus.Playing)
                return;

            FireLive(
                "respawn",
                BeaconEventFields.Respawn(Host.SelfName ?? CommandStrings.ScriptsSelfNameFallback));
        }

        /// <summary>
        /// Fire-and-forget bus hop for poll and push sources: translate and forward only, never block the caller, never hand live objects to handlers (fields are already snapshots).
        /// </summary>
        private void FireLive(string hook, IReadOnlyDictionary<string, BeaconValue> fields)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    BeaconFireResult fire =
                        await Engine.FireEventAsync(hook, fields).ConfigureAwait(false);
                    ReportFireFailures(hook, fire);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                }
            });
        }

        /// <summary>
        /// Names failed event handlers on the host console instead of letting them die silently: a 24/7 bot that breaks overnight should leave a note its owner can act on.
        /// Throttled per script, hook, and code so a flapping handler cannot flood.
        /// </summary>
        private void ReportFireFailures(string hook, BeaconFireResult fire)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(hook);
            ArgumentNullException.ThrowIfNull(fire);
            foreach (BeaconHandlerFire handler in fire.Handlers)
            {
                if (handler.Result is not { Success: false })
                    continue;

                BeaconDiagnostic? error = handler.Result.Error
                    ?? handler.Result.Diagnostics.FirstOrDefault(
                        d => d.Severity == BeaconSeverity.Error);
                if (error is null)
                    continue;

                string key = handler.ScriptId + "\0" + hook + "\0" + error.Code;
                DateTimeOffset now = DateTimeOffset.UtcNow;
                lock (_gate)
                {
                    if (_failureNotices.TryGetValue(key, out DateTimeOffset last)
                        && now - last < FailureNoticeWindow)
                        continue;

                    _failureNotices[key] = now;
                }

                _client.CommandOutput.WriteLine(CommandStrings.ScriptsEventHandlerFailed(
                    handler.ScriptId, hook, error.Message, error.Code,
                    error.Span.File, error.Span.Line));
            }
        }

        /// <summary>
        /// Polls the snapshot facades while playing and fires on change only.
        /// One bounded budget per cycle so a wedged session delays the pump instead of deadlocking it; one section failing never skips the rest.
        /// </summary>
        internal async Task PollSessionAsync(Client client)
        {
            using var budget = new CancellationTokenSource(PollTimeout);
            CancellationToken ct = budget.Token;
            try
            {
                await PollVitalsAsync(client, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
            }

            try
            {
                await PollTabListAsync(client, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
            }

            try
            {
                await PollTpsAsync(client, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
            }

            try
            {
                await PollInventoryAsync(client, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
            }

            try
            {
                await PollEntitiesAsync(client, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
            }
        }

        private async Task PollVitalsAsync(Client client, CancellationToken ct)
        {
            var status = await client.Game.Player.GetStatusAsync(ct).ConfigureAwait(false);
            double health = status.Health;
            int food = status.Food;
            double saturation = status.Saturation;
            bool fireHealth = false;
            bool fireHunger = false;
            double healthChange = 0;
            int foodChange = 0;
            lock (_gate)
            {
                if (!_hasVitalsBaseline)
                {
                    _lastHealth = health;
                    _lastFood = food;
                    _lastSaturation = saturation;
                    _hasVitalsBaseline = true;
                }
                else
                {
                    // Health and food move discretely, so any change fires; saturation drifts on nearly every tick, so it needs a full point before it earns a fire.
                    if (health != _lastHealth)
                    {
                        healthChange = health - _lastHealth;
                        _lastHealth = health;
                        fireHealth = true;
                    }

                    if (food != _lastFood || Math.Abs(saturation - _lastSaturation) >= SaturationThreshold)
                    {
                        foodChange = food - _lastFood;
                        _lastFood = food;
                        _lastSaturation = saturation;
                        fireHunger = true;
                    }
                }
            }

            if (fireHealth)
                FireLive("health", BeaconEventFields.Health(health, MaxHealthDefault, healthChange));

            if (fireHunger)
                FireLive("hunger", BeaconEventFields.Hunger(food, saturation, foodChange));
        }

        private async Task PollTabListAsync(Client client, CancellationToken ct)
        {
            var tab = await client.Game.Player.GetTabListAsync(ct).ConfigureAwait(false);
            List<string> names = tab.Entries.Select(e => e.Name).ToList();
            List<string> sigs = tab.Entries
                .Select(e => e.Name + "\u0001" + e.Latency + "\u0001" + e.GameMode).ToList();
            string? header = string.IsNullOrEmpty(tab.Header) ? null : tab.Header;
            string? footer = string.IsNullOrEmpty(tab.Footer) ? null : tab.Footer;
            List<string> added = [];
            List<string> removed = [];
            bool listChanged = false;
            lock (_gate)
            {
                if (!_hasTabBaseline)
                {
                    _lastTabNames = names;
                    _lastTabSigs = sigs;
                    _lastTabHeader = header;
                    _lastTabFooter = footer;
                    _hasTabBaseline = true;
                    return;
                }

                var before = new HashSet<string>(_lastTabNames, StringComparer.Ordinal);
                var now = new HashSet<string>(names, StringComparer.Ordinal);
                added = names.Where(n => !before.Contains(n)).ToList();
                removed = _lastTabNames.Where(n => !now.Contains(n)).ToList();
                listChanged = added.Count > 0 || removed.Count > 0
                    || !sigs.SequenceEqual(_lastTabSigs)
                    || !string.Equals(header, _lastTabHeader, StringComparison.Ordinal)
                    || !string.Equals(footer, _lastTabFooter, StringComparison.Ordinal);
                _lastTabNames = names;
                _lastTabSigs = sigs;
                _lastTabHeader = header;
                _lastTabFooter = footer;
            }

            foreach (string player in added)
                FireLive("join", BeaconEventFields.Join(player));

            foreach (string player in removed)
                FireLive("leave", BeaconEventFields.Leave(player));

            if (listChanged)
                FireLive("player_list", BeaconEventFields.PlayerList(names, header, footer));
        }

        private async Task PollTpsAsync(Client client, CancellationToken ct)
        {
            var info = await client.Game.Session.GetInfoAsync(ct).ConfigureAwait(false);
            double? tps = info.TpsEstimate;
            bool changed = false;
            lock (_gate)
            {
                if (!_hasTpsBaseline)
                {
                    _lastTps = tps;
                    _hasTpsBaseline = true;
                }
                else if (_lastTps != tps)
                {
                    _lastTps = tps;
                    changed = true;
                }
            }

            // Null means unknown (no samples yet, or the estimate expired), surfaced as none, never zero; mspt has no session API behind it yet, so it stays none.
            if (changed)
                FireLive("tps", BeaconEventFields.Tps(tps, null));
        }

        private async Task PollInventoryAsync(Client client, CancellationToken ct)
        {
            var player = await client.Game.Inventory.GetPlayerInventoryAsync(ct).ConfigureAwait(false);
            var open = await client.Game.Inventory.GetOpenContainerAsync(ct).ConfigureAwait(false);
            List<string> prints = player.Slots.Select(SlotFingerprint).ToList();
            int? windowId = open?.WindowId;
            List<string>? windowPrints = open?.Slots.Select(SlotFingerprint).ToList();
            List<int> windowChanged = [];
            List<int> playerChanged = [];
            lock (_gate)
            {
                if (!_hasInvBaseline)
                {
                    _lastInvPrints = prints;
                    _lastInvWindow = windowId;
                    _lastWindowPrints = windowPrints;
                    _hasInvBaseline = true;
                    return;
                }

                if (_lastInvPrints.Count == prints.Count)
                {
                    for (int i = 0; i < prints.Count; i++)
                    {
                        if (!string.Equals(prints[i], _lastInvPrints[i], StringComparison.Ordinal))
                            playerChanged.Add(i);
                    }
                }

                _lastInvPrints = prints;

                if (windowId is null)
                {
                    _lastInvWindow = null;
                    _lastWindowPrints = null;
                }
                else if (_lastInvWindow != windowId
                    || _lastWindowPrints is null
                    || _lastWindowPrints.Count != windowPrints!.Count)
                {
                    _lastInvWindow = windowId;
                    _lastWindowPrints = windowPrints;
                }
                else
                {
                    for (int i = 0; i < windowPrints.Count; i++)
                    {
                        if (!string.Equals(windowPrints[i], _lastWindowPrints[i], StringComparison.Ordinal))
                            windowChanged.Add(i);
                    }

                    _lastWindowPrints = windowPrints;
                }
            }

            // One batch per poll: open-window slots first, then player slots.
            // Scripts that only need the count use len(slots_changed); index consumers read the catalog note.
            if (windowChanged.Count > 0 || playerChanged.Count > 0)
            {
                windowChanged.AddRange(playerChanged);
                FireLive("inventory", BeaconEventFields.Inventory(windowChanged));
            }
        }

        private static string SlotFingerprint(ItemStackInfo stack)
            => stack.IsEmpty
                ? "empty"
                : stack.ItemId + "|" + stack.Count + "|" + stack.Damage + "|" + stack.CustomName;

        /// <summary>
        /// Polls the entity tracker while playing and fires <c>entity_add</c>/<c>entity_remove</c> on id-set change only.
        /// Skipped entirely when no script listens (the snapshot walk is the most expensive poll here); the first poll only baselines, like every other poll.
        /// </summary>
        private async Task PollEntitiesAsync(Client client, CancellationToken ct)
        {
            bool wantAdd;
            bool wantRemove;
            try
            {
                wantAdd = Engine.EventBus.HasHandlers("entity_add");
                wantRemove = Engine.EventBus.HasHandlers("entity_remove");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return;
            }

            if (!wantAdd && !wantRemove)
                return;

            IReadOnlyList<EntitySnapshot> all;
            Vec3d? self = null;
            try
            {
                all = await client.Game.Entities.AllAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
                or OperationCanceledException or DmcbkFeatureDisabledException)
            {
                return;
            }

            try
            {
                self = (await client.Game.Movement.GetPoseAsync(ct).ConfigureAwait(false)).Position;
            }
            catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException
                or OperationCanceledException or DmcbkFeatureDisabledException)
            {
            }

            var now = new Dictionary<int, BeaconEntityInfo>(all.Count);
            foreach (EntitySnapshot entity in all)
                now[entity.Id] = ProjectEntitySnapshot(entity);

            List<BeaconEntityInfo> added = [];
            List<BeaconEntityInfo> removed = [];
            lock (_gate)
            {
                if (!_hasEntityBaseline)
                {
                    _lastEntities = now;
                    _hasEntityBaseline = true;
                    return;
                }

                BeaconEntityWatch.Diff(
                    [.. _lastEntities.Values], [.. now.Values], out added, out removed);
                _lastEntities = now;
            }

            var selfPos = self is Vec3d known
                ? new BeaconPosition(known.X, known.Y, known.Z, null, null)
                : null;
            if (wantAdd)
            {
                foreach (BeaconEntityInfo entity in added)
                {
                    FireLive("entity_add", BeaconEventFields.EntityAdd(
                        entity, BeaconEventFields.EntityDistance(entity, selfPos)));
                }
            }

            if (wantRemove)
            {
                foreach (BeaconEntityInfo entity in removed)
                {
                    FireLive("entity_remove", BeaconEventFields.EntityRemove(
                        entity, BeaconEventFields.EntityDistance(entity, selfPos)));
                }
            }
        }

        private static BeaconEntityInfo ProjectEntitySnapshot(EntitySnapshot entity)
        {
            ArgumentNullException.ThrowIfNull(entity);
            return new BeaconEntityInfo(
                entity.Id,
                entity.Uuid.ToString(),
                entity.TypeId,
                entity.Position.X,
                entity.Position.Y,
                entity.Position.Z,
                entity.Yaw,
                entity.Pitch,
                entity.Pose,
                entity.OnGround,
                entity.PlayerName,
                entity.CustomName,
                IsPlayer: !string.IsNullOrEmpty(entity.PlayerName)
                    || string.Equals(entity.TypeId, "minecraft:player", StringComparison.Ordinal));
        }

        public string SetWatch(bool on, string? scriptsDir)
        {
            lock (_gate)
            {
                if (!on)
                {
                    _watcher?.Dispose();
                    _watcher = null;
                    _watchDir = null;
                    _pendingReloads.Clear();
                    return CommandStrings.ScriptsWatchOff;
                }

                if (string.IsNullOrWhiteSpace(scriptsDir))
                    return CommandStrings.ScriptsWatchNoFolder;

                if (_watcher is not null && string.Equals(_watchDir, scriptsDir, StringComparison.Ordinal))
                    return CommandStrings.ScriptsWatchOn(scriptsDir!);

                _watcher?.Dispose();
                _pendingReloads.Clear();
                var watcher = new FileSystemWatcher(scriptsDir!)
                {
                    Filter = "*.mcc",
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };
                watcher.Changed += OnScriptFileChanged;
                watcher.Created += OnScriptFileChanged;
                watcher.Renamed += OnScriptFileRenamed;
                _watcher = watcher;
                _watchDir = scriptsDir;
                return CommandStrings.ScriptsWatchOn(scriptsDir!);
            }
        }

        public string WatchStatus()
        {
            lock (_gate)
            {
                return _watchDir is null
                    ? CommandStrings.ScriptsWatchStatusOff
                    : CommandStrings.ScriptsWatchStatusOn(_watchDir);
            }
        }

        public bool WatchEnabled
        {
            get { lock (_gate) return _watchDir is not null; }
        }

        private void OnScriptFileRenamed(object? sender, RenamedEventArgs args)
            => OnScriptFileChanged(sender, new FileSystemEventArgs(
                WatcherChangeTypes.Changed, Path.GetDirectoryName(args.FullPath) ?? string.Empty,
                Path.GetFileName(args.FullPath)));

        private void OnScriptFileChanged(object? sender, FileSystemEventArgs args)
        {
            string id;
            try
            {
                id = Path.GetFileNameWithoutExtension(args.Name ?? string.Empty);
            }
            catch (Exception ex) when (ex is ArgumentException)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(id))
                return;

            bool running;
            lock (_gate)
            {
                running = Engine.ScriptIds.Contains(id);
                if (running)
                    _pendingReloads[id] = DateTimeOffset.UtcNow;
            }

            if (!running)
                return;

            // Debounced: rapid editor saves collapse into one reload issued as an ordinary command dispatch, so loop affinity and manifest gates behave like manual reload.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                DateTimeOffset? latest;
                lock (_gate)
                {
                    if (!_pendingReloads.TryGetValue(id, out DateTimeOffset stamp))
                        return;

                    if (DateTimeOffset.UtcNow - stamp < TimeSpan.FromMilliseconds(400))
                        return;

                    latest = stamp;
                    _pendingReloads.Remove(id);
                }

                _ = latest;
                try
                {
                    await _client.Commands.DispatchAsync($"scripts reload \"{id}\"").ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                }
            });
        }
    }
}
