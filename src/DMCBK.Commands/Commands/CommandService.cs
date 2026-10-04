using System.Collections.Immutable;
using DMCBK.Core.Commands.Impl;
using DMCBK.Core.Configuration;
using DMCBK.Core.Localization;
using DMCBK.Core.Presentation;
using Microsoft.Extensions.Logging;
using Umpk.Client;
using Umpk.Client.Commands;
using Umpk.Commands;
using UmpkCommands = Umpk.Commands;

namespace DMCBK.Core.Commands;

/// <summary>
/// The per-instance internal-command system: UMPK's <see cref="UmpkCommands.CommandService{TSource}"/>, input routing (prefix char, <c>//</c> escape, <c>none</c> mode, <c>%var%</c> expansion, unknown-command fall-through to server chat), the help tree, and command re-dispatch.
/// Replaces the legacy static <c>McClient.dispatcher</c> + <c>CmdResult.currentHandler</c>; commands receive an injected <see cref="CommandContext"/>.
/// One instance per <see cref="Client"/>, safe to build many in one process.
/// <para>
/// Registration lives in three scopes on the shared <see cref="_service"/>: the builtins (one long-lived scope, registered once at construction), the host scope (one long-lived scope a host registers its own commands into, for example the CLI's clear-console/console-chat/exit), and one scope per scoped registration (a plugin's per-session command, taken back off the tree by disposing the handle).
/// UMPK owns all unregistration itself (dispose a scope, it rebuilds the dispatcher from the surviving scopes); this class does none of that bookkeeping anymore.
/// </para>
/// </summary>
public sealed class CommandService : ICommandDispatcher
{
    private readonly Client _client;
    private readonly ICommandOutput _output;
    private readonly IHostUi? _ui;
    private readonly ClientFeatures _features;
    private readonly VariableStore _variables;
    private readonly IRegistrySuggestionSource _registrySuggestions;
    private readonly ILogger _logger;
    private readonly UmpkCommands.CommandService<CommandContext> _service = new();
    private readonly ICommandRegistrationScope<CommandContext> _hostScope;
    private readonly object _gate = new();

    private DmcbkConfiguration? _config;
    private char _prefix;
    private bool _noPrefix;
    private GlyphSet _glyphs = GlyphSet.Ascii;

    // The dispatcher can enumerate GRAMMARS but knows nothing about categories, aliases or examples, so help needs the CommandBase instances themselves.
    // One list, every scope, in registration order.
    private readonly List<CommandBase> _registered = [];

    internal CommandService(
        Client client,
        ICommandOutput output,
        IHostUi? ui,
        DmcbkConfiguration? config,
        ClientFeatures features,
        VariableStore variables)
    {
        _client = client;
        _output = output;
        _ui = ui;
        _config = config;
        _features = features;
        _variables = variables;
        _registrySuggestions = new RegistrySuggestionSource(() => _client.GameSession.Current?.State.Registries);
        _logger = client.LoggerFactory.CreateLogger("DMCBK.Core.Commands");
        ApplyPrefix(config);
        RegisterBuiltins();
        _hostScope = _service.CreateScope("mcc:host");
    }

    /// <summary>
    /// Raised when a dispatched command threw, with the command the line named.
    /// The plugin host listens so an exception out of a plugin's command is counted against that plugin; the caller still gets the same failure result either way.
    /// </summary>
    public event EventHandler<CommandFaultedEventArgs>? CommandFaulted;

    /// <summary>The command prefix character in effect (a sentinel space in <c>none</c> mode).</summary>
    public char Prefix => _prefix;

    /// <summary>
    /// The text a host should place before an internal command.
    /// Empty means commands are configured with no prefix.
    /// Read from the live configuration, so a browser never has to guess or cache it.
    /// </summary>
    public string ActivePrefix
    {
        get { lock (_gate) return _noPrefix ? string.Empty : _prefix.ToString(); }
    }

    /// <summary>
    /// The current configuration snapshot used for command resolution.
    /// Unlike the client's construction snapshot, this follows explicit configuration reloads and saved-server changes.
    /// </summary>
    public DmcbkConfiguration? CurrentConfiguration
    {
        get { lock (_gate) return _config; }
    }

    /// <summary>Whether every line is treated as a command (<c>none</c> prefix mode).</summary>
    public bool NoPrefix => _noPrefix;

    /// <summary>The variable store the commands read and write.</summary>
    public VariableStore Variables => _variables;

    /// <summary>
    /// The resolved glyph vocabulary every command draws status glyphs from, and the host decorates its results with.
    /// Emoji until a host says otherwise: the classic host resolves <c>console.toml [General] Glyphs</c> and sets this during startup.
    /// </summary>
    public GlyphSet Glyphs
    {
        get { lock (_gate) return _glyphs; }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate) _glyphs = value;
        }
    }

    /// <summary>
    /// Whether an executed command is echoed into the console log before it runs.
    /// <para>
    /// The line editor clears the input line on Enter, so without this the scrollback holds a block of output with nothing saying what produced it, and a transcript of a session cannot be read back at all.
    /// Chat is deliberately NOT echoed: the server broadcasts it back, and echoing locally would print every message twice.
    /// </para>
    /// </summary>
    public bool EchoCommands { get; set; }

    /// <summary>
    /// The cached registry suggestion source every dispatched command's context resolves through <see cref="CommandContext.GetService{T}"/>.
    /// One instance per <see cref="CommandService"/>, reading the live session's registries fresh on every query (see <see cref="RegistrySuggestionSource"/>).
    /// </summary>
    public IRegistrySuggestionSource RegistrySuggestions => _registrySuggestions;

    /// <summary>
    /// The diagnostic logger every dispatched command reaches through <see cref="CommandContext.Logger"/>.
    /// One instance per <see cref="CommandService"/>, on the host's own factory, so a command can record WHY something failed without putting that detail in the user-facing result.
    /// Nothing here is a substitute for the localized result text; this is the channel for the technical cause behind it.
    /// </summary>
    public ILogger Logger => _logger;

    /// <summary>
    /// Registers a host-supplied command (for example the CLI's clear-console/console-chat/exit) for the lifetime of the client, onto the single long-lived host scope.
    /// Use <see cref="RegisterScopedCommand"/> for a registration that has to come back off the tree again.
    /// </summary>
    public void RegisterHostCommand(CommandBase command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _hostScope.Register(command.Register);
        lock (_gate)
            _registered.Add(command);
    }

    /// <summary>
    /// Registers a command with a bounded lifetime, in its own scope, and returns the handle that removes it again.
    /// This is the path a plugin's per-session command registration takes: it dispatches real user input exactly like a host command does, and disposing the handle takes it back off the tree (UMPK rebuilds the dispatcher from the surviving scopes; see <see cref="UmpkCommands.CommandService{TSource}.CreateScope"/>).
    /// </summary>
    public IDisposable RegisterScopedCommand(CommandBase command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ICommandRegistrationScope<CommandContext> scope = _service.CreateScope($"mcc:scoped:{command.CmdName}");
        scope.Register(command.Register);
        lock (_gate)
            _registered.Add(command);

        // The registry must shrink with the scope, or an unloaded plugin's command keeps a row in the help index forever.
        return new ScopedRegistration(this, command, scope);
    }

    private void Unregister(CommandBase command)
    {
        lock (_gate)
            _registered.Remove(command);
    }

    /// <summary>Disposes the dispatcher scope and drops the command from the help registry together.</summary>
    private sealed class ScopedRegistration(CommandService owner, CommandBase command, IDisposable scope) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            scope.Dispose();
            owner.Unregister(command);
        }
    }

    /// <summary>Re-applies configuration after a reload: refreshes the prefix and the config the commands read.</summary>
    public void ReloadConfiguration(DmcbkConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_gate)
        {
            _config = config;
            ApplyPrefix(config);
        }
    }

    /// <summary>
    /// Routes a raw input line: applies the prefix rules and <c>%var%</c> expansion, dispatches an internal command, or forwards the line to the server as chat.
    /// The host renders the returned result.
    /// </summary>
    public async Task<InputRouting> HandleInputAsync(string line, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(line);

        char prefix;
        bool noPrefix;
        lock (_gate)
        {
            prefix = _prefix;
            noPrefix = _noPrefix;
        }

        InputClassification classified = Classify(line, prefix, noPrefix);
        if (classified.Kind == InputKind.Empty)
            return new InputRouting(InputAction.Empty, null);

        Echo(line, classified);

        if (classified.Kind is InputKind.ServerCommand or InputKind.Chat)
            return await SendToServerAsync(classified.Text, ct).ConfigureAwait(false);

        string command = _variables.Expand(classified.Text);

        CmdResult result = await DispatchAsync(command, ct).ConfigureAwait(false);

        // Unknown internal command in slash mode falls through to the server (a server-side /command).
        if (result.Status == CmdStatus.NotRun && prefix == '/' && !noPrefix)
        {
            // A near-miss notice is written BEFORE forwarding, because the forward is still the right thing to do (that is how a server-side /tell reaches the server) but the user typed something one edit away from an MCC command and deserves to know.
            if (result.Message is { Length: > 0 } notice)
                _output.WriteLine(notice);

            return await SendToServerAsync(line, ct).ConfigureAwait(false);
        }

        return new InputRouting(InputAction.CommandExecuted, result);
    }

    /// <summary>
    /// Writes the command that is about to run into the console log, so the output below it has a heading.
    /// </summary>
    /// <remarks>
    /// Commands only.
    /// Chat comes back from the server as a broadcast, so echoing it here would double every message a user sends.
    /// A command that repaints the screen opts out through <see cref="CommandBase.EchoWhenRun"/>.
    /// </remarks>
    private void Echo(string line, InputClassification classified)
    {
        if (!EchoCommands || classified.Kind is not (InputKind.InternalCommand or InputKind.ServerCommand))
            return;

        if (classified.Kind == InputKind.InternalCommand && !AllowsEcho(FirstToken(classified.Text)))
            return;

        // §7, not §8.
        // The marker is what makes the echo scannable as a heading over its output, and §8 is the vanilla dark grey (85,85,85), close enough to a dark background to disappear.
        _output.WriteLine($"§7> §f{line}§r");
    }

    /// <summary>Whether the command with this name wants to be echoed. Unknown names do.</summary>
    private bool AllowsEcho(string name)
    {
        if (name.Length == 0)
            return true;

        foreach (CommandBase command in RegisteredCommands)
        {
            if (string.Equals(command.CmdName, name, StringComparison.OrdinalIgnoreCase)
                || command.Aliases.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)))
                return command.EchoWhenRun;
        }

        return true;
    }

    /// <summary>
    /// Applies the prefix rules to one raw input line, without running or sending anything.
    /// This is the ONE copy of those rules; routing (<see cref="HandleInputAsync"/>), internal-tree completion (<see cref="CompleteAsync(string, CancellationToken)"/>) and the host suggestion pipeline (<see cref="CompleteInputAsync(string, int, CancellationToken)"/>) all go through it.
    /// </summary>
    /// <remarks>
    /// The rules used to be reimplemented per call site, and they drifted: the <c>//</c> escape existed in routing only, so the completion path handed the raw <c>//tell</c> to the server-completion entry point, which strips a command slash per vanilla and then looked for a command literal named <c>/tell</c>.
    /// Nothing matched, and the symptom read as "the server returns no suggestions at all".
    /// </remarks>
    internal InputClassification ClassifyInput(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        char prefix;
        bool noPrefix;
        lock (_gate)
        {
            prefix = _prefix;
            noPrefix = _noPrefix;
        }

        return Classify(line, prefix, noPrefix);
    }

    private static InputClassification Classify(string line, char prefix, bool noPrefix)
    {
        if (line.Length == 0)
            return new InputClassification(InputKind.Empty, line, 0);

        // none mode + leading '/': send straight to the server as a server command.
        if (noPrefix && line[0] == '/')
            return new InputClassification(InputKind.ServerCommand, line, 0);

        // prefix mode + "//" escape: strip the prefix char and forward the rest as a server command.
        if (!noPrefix && line.Length > 1 && line[0] == prefix && line[1] == '/')
            return new InputClassification(InputKind.ServerCommand, line[1..], 1);

        // prefix mode + anything else in first position: ordinary chat (which the chat API still routes to the command wire when it starts with a slash, so a '/' line is a server command in backslash mode).
        if (!noPrefix && line[0] != prefix)
            return new InputClassification(InputKind.Chat, line, 0);

        return noPrefix
            ? new InputClassification(InputKind.InternalCommand, line, 0)
            : new InputClassification(InputKind.InternalCommand, line[1..], 1);
    }

    /// <summary>
    /// Dispatches a single already-unprefixed, already-expanded internal command and returns its result.
    /// <para>
    /// A parse failure splits two ways, which is the distinction legacy drew and this client had lost.
    /// A line whose first word names NO registered command yields <see cref="CmdStatus.NotRun"/>, so routing hands it to the server; a line that names a real command and uses it wrongly yields <see cref="CmdStatus.Fail"/> carrying the parse error AND the command's usage listing, so it is answered here instead of being forwarded.
    /// An execution fault yields <see cref="CmdStatus.Fail"/>.
    /// Used directly by any caller that needs to run one command without going through the input router, which is what a plugin's own dispatch does.
    /// </para>
    /// </summary>
    public async Task<CmdResult> DispatchAsync(string command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var ctx = NewContext(ct);

        await DispatchCoreAsync(command, ctx, ct).ConfigureAwait(false);

        return ctx.Result;
    }

    /// <summary>
    /// Dispatches one internal command exactly like <see cref="DispatchAsync"/>, but with a private capturing sink instead of the host output, and returns both the result and everything the command printed.
    /// Buffered hosts (Discord, file drivers) answer from the capture, so a command that prints its body and returns a bare <c>Done</c> reads the same off-host as it does on the console.
    /// </summary>
    public async Task<(CmdResult Result, string Output)> DispatchCapturedAsync(
        string command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var capture = new BufferedCommandOutput();
        var ctx = NewContext(ct, capture);

        await DispatchCoreAsync(command, ctx, ct).ConfigureAwait(false);

        return (ctx.Result, capture.Text);
    }

    /// <summary>
    /// The sequence both dispatch paths share: execute, then bare-command help, then parse-failure reporting, with cancellation rethrown and any other exception reported as a single <see cref="CommandFaulted"/> followed by the localized failure.
    /// Context creation and capture ownership stay in the public wrappers, so this takes the already-built context.
    /// </summary>
    private async Task DispatchCoreAsync(string command, CommandContext ctx, CancellationToken ct)
    {
        using IDisposable culture = DMCBK.Core.Localization.UiCulture.Enter(_client.UiCulture);
        try
        {
            CommandResult r = await _service.ExecuteAsync(command, ctx, ct).ConfigureAwait(false);
            if (!r.Success && !await TryShowHelpForBareCommandAsync(command, ctx, ct).ConfigureAwait(false))
                ReportParseFailure(command, r.ErrorMessage, ctx);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RaiseCommandFaulted(command, ex);
            ctx.Result.Fail(CommandStrings.Error(ex.Message));
        }
    }

    private void RaiseCommandFaulted(string line, Exception ex)
    {
        if (CommandFaulted is not { } handler)
            return;

        string name = FirstToken(line);
        CommandBase? target = RegisteredCommands
            .FirstOrDefault(c => string.Equals(c.CmdName, name, StringComparison.OrdinalIgnoreCase)
                || c.Aliases.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)));

        handler(this, new CommandFaultedEventArgs(target, line, ex));
    }

    /// <summary>
    /// Answers a line that is nothing but a command's own name with that command's help page, by running <c>help &lt;name&gt;</c>.
    /// Returns false when the line is not that, leaving the caller to report a parse failure.
    /// <para>
    /// Typing <c>book</c> is a request to be told what <c>book</c> does; it is not a mistake worth an error and a caret.
    /// Commands whose root literal happens to have an executable branch (<c>inventory</c>, <c>move</c>) already answered that way, and the ones without (<c>book</c>, <c>bed</c>) answered with a parse error instead, which is a difference in how the command tree happens to be shaped rather than a difference the user should ever see.
    /// Routing it through the real <c>help</c> node means the bare form and <c>help book</c> render the same page from the same code, including the description header that a usage listing alone does not carry.
    /// </para>
    /// </summary>
    private async Task<bool> TryShowHelpForBareCommandAsync(string command, CommandContext ctx, CancellationToken ct)
    {
        string trimmed = command.Trim();
        if (trimmed.Length == 0 || trimmed.AsSpan().IndexOfAny(' ', '\t') >= 0)
            return false;

        if (_service.GetUsage(trimmed, ctx).IsDefaultOrEmpty)
            return false;

        CommandResult help = await _service.ExecuteAsync($"help {trimmed}", ctx, ct).ConfigureAwait(false);
        return help.Success;
    }

    /// <summary>
    /// Records a parse failure as either "not one of ours" or "ours, used wrongly", which is the distinction legacy drew and this client had lost.
    /// <para>
    /// Legacy caught the syntax exception around its dispatch and branched on whether the parse had consumed any node at all: none meant the line was not an internal command and the caller went on to treat it as chat, and one or more meant it WAS one, so legacy printed the Brigadier message and <c>dispatcher.GetAllUsageString(parse.Context.Nodes[0].Node.Name, false)</c> and stopped (Archive/MinecraftClient/McClient.cs:1107-1119).
    /// </para>
    /// <para>
    /// This client collapsed both into <c>NotRun</c>, and <see cref="HandleInputAsync"/> forwards every <c>NotRun</c> in slash mode to the server.
    /// So an incomplete but perfectly real internal command was sent to the SERVER, and what came back was the server's rejection of it: typing <c>/bed</c> answered "Unknown or incomplete command" with a caret under <c>bed</c> and no hint that <c>bed leave</c> and <c>bed sleep</c> exist, because the client that owns <c>bed</c> never got to say anything.
    /// Every command whose root has no executable branch behaved that way.
    /// </para>
    /// <para>
    /// A registered top-level command is exactly what <c>GetUsage</c> answers non-empty for, so that is the test, and a line naming no command keeps its old status and still falls through untouched.
    /// The <c>//</c> escape remains the way to reach a server command that shares a name with an internal one.
    /// </para>
    /// </summary>
    private void ReportParseFailure(string command, string? error, CommandContext ctx)
    {
        char prefix;
        bool noPrefix;
        lock (_gate)
        {
            prefix = _prefix;
            noPrefix = _noPrefix;
        }

        string name = FirstToken(command);
        bool known = name.Length != 0 && !_service.GetUsage(name, ctx).IsDefaultOrEmpty;
        string prefixText = noPrefix ? string.Empty : prefix.ToString();

        if (!known)
        {
            // Not one of ours.
            // The caller forwards it to the server in slash mode, which is right, but a TYPO then produced no local output at all.
            // Say so first when something is close.
            string? nearMiss = CommandDiagnostics.NearMiss(command, RegisteredCommands, prefixText, Glyphs);
            ctx.Result.Set(CmdStatus.NotRun, nearMiss ?? error);
            return;
        }

        // Ours, used wrongly.
        // Mark the offending token, offer two or three forms that work, and point at the page -- instead of the raw Brigadier caret followed by every branch of the subtree.
        CommandBase? target = RegisteredCommands
            .FirstOrDefault(c => string.Equals(c.CmdName, name, StringComparison.OrdinalIgnoreCase)
                || c.Aliases.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)));

        ctx.Result.Set(
            CmdStatus.Fail,
            CommandDiagnostics.BadUsage(command, error, target, prefixText, Glyphs));
    }

    private static string FirstToken(string command)
    {
        ReadOnlySpan<char> span = command.AsSpan().TrimStart();
        int end = span.IndexOfAny(' ', '\t');
        return (end < 0 ? span : span[..end]).ToString();
    }

    /// <summary>
    /// Returns completion candidates for a partial input (basic; no ANSI/suggestion polish).
    /// The prefix char is stripped before completing so command names complete naturally.
    /// Completes at the end of <paramref name="input"/>; <see cref="CompleteInputAsync(string, int, CancellationToken)"/> is the cursor-aware entry point a host actually drives.
    /// </summary>
    public Task<IReadOnlyList<string>> CompleteAsync(string input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        InputClassification classified = ClassifyInput(input);

        // Already-unprefixed text (what plugins and the merged pipeline pass) classifies as chat; complete it as-is.
        // A prefixed internal command is completed on its body.
        string command = classified.Kind == InputKind.InternalCommand ? classified.Text : input;
        return CompleteInternalTreeAsync(command, command.Length, ct);
    }

    /// <summary>
    /// Answers one suggestion query for a RAW host input line: applies the prefix rules once, merges the internal-command tree's candidates with the server's, and reports the span of that raw line the candidates replace.
    /// Every host uses this, so the prefix rules, the <c>//</c> escape and the index mapping between the raw line and whatever the server was actually asked about all have exactly one implementation.
    /// </summary>
    /// <param name="line">The raw input line exactly as the user's buffer holds it.</param>
    /// <param name="cursor">The cursor position within <paramref name="line"/>.</param>
    /// <param name="ct">A cancellation token.</param>
    public Task<InputCompletion> CompleteInputAsync(string line, int cursor, CancellationToken ct = default)
        => CompleteInputAsync(line, cursor, _client.Game.Chat.CompleteAsync, ct);

    /// <summary>
    /// The body of <see cref="CompleteInputAsync(string, int, CancellationToken)"/> with the server-side completion source injected, so the merge, the prefix rules and the index mapping are testable without a live session.
    /// The public entry point supplies <see cref="ChatApi.CompleteAsync"/>.
    /// </summary>
    internal async Task<InputCompletion> CompleteInputAsync(
        string line,
        int cursor,
        Func<string, int, CancellationToken, Task<ChatCompletionResult>> serverCompletion,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(serverCompletion);
        int at = Math.Clamp(cursor, 0, line.Length);

        char prefix;
        bool noPrefix;
        lock (_gate)
        {
            prefix = _prefix;
            noPrefix = _noPrefix;
        }

        InputClassification classified = Classify(line, prefix, noPrefix);

        // What (if anything) the server should be asked about, how many leading characters were dropped to get there, and the first index of the raw line a candidate may replace.
        string? serverText;
        int serverOffset;
        bool completeInternal;
        int bodyStart;
        switch (classified.Kind)
        {
            case InputKind.InternalCommand:
                completeInternal = true;

                // In slash mode an unknown internal command falls through to the server, so the server sees the WHOLE line, slash included.
                // In the other prefix modes it never reaches the server.
                bool fallsThroughToServer = !noPrefix && prefix == '/';
                serverText = fallsThroughToServer ? line : null;
                serverOffset = 0;
                bodyStart = classified.Offset;
                break;

            case InputKind.ServerCommand:
                // classified.Text always keeps its command slash, so the completable body starts one past it.
                serverText = classified.Text;
                serverOffset = classified.Offset;
                bodyStart = classified.Offset + 1;
                completeInternal = false;
                break;

            case InputKind.Chat:
                // A chat line is completable only when it is a server command in its own right, which is how a '/' line reaches the server under the backslash prefix.
                serverText = line.StartsWith('/') ? line : null;
                serverOffset = 0;
                bodyStart = 1;
                completeInternal = false;
                break;

            default:
                return new InputCompletion([], at, at);
        }

        // A cursor still inside the prefix (or before the command slash) has no body to complete.
        if (at < bodyStart)
            return new InputCompletion([], at, at);

        var ordered = new List<ChatCompletion>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string candidate, string? tooltip)
        {
            if (candidate.Length > 0 && seen.Add(candidate))
                ordered.Add(new ChatCompletion(candidate, tooltip));
        }

        if (completeInternal)
        {
            // Fix: complete AT THE CURSOR, not always at the end of classified.Text. The cursor is mapped from the raw line into classified.Text's coordinate space by undoing the prefix strip.
            int internalCursor = Math.Clamp(at - classified.Offset, 0, classified.Text.Length);
            foreach (string candidate in await CompleteInternalTreeAsync(classified.Text, internalCursor, ct).ConfigureAwait(false))
                Add(candidate, null);
        }

        int serverStart = -1;
        if (serverText is not null)
        {
            ChatCompletionResult server = await serverCompletion(serverText, at - serverOffset, ct)
                .ConfigureAwait(false);
            if (server.Suggestions.Count > 0)
            {
                foreach (ChatCompletion candidate in server.Suggestions)
                    Add(candidate.Text, candidate.Tooltip);

                // The server's range indexes the string IT was handed, which is the raw line minus the characters the prefix rules stripped.
                // Map it back onto the caller's buffer.
                serverStart = server.RangeStart + serverOffset;
            }
        }

        if (ordered.Count == 0)
            return new InputCompletion([], at, at);

        // The server's own range is authoritative when it answered; otherwise fall back to the current whitespace-delimited token, which is what an internal literal/argument suggestion replaces.
        int start = serverStart >= 0 ? serverStart : TokenStart(line, at);
        return new InputCompletion(ordered, Math.Clamp(start, bodyStart, at), at);
    }

    /// <summary>The start of the whitespace-delimited token that <paramref name="cursor"/> sits in.</summary>
    private static int TokenStart(string line, int cursor)
    {
        if (line.Length == 0)
            return 0;

        int start = line.LastIndexOf(' ', Math.Max(0, cursor - 1)) + 1;
        return start > cursor ? cursor : start;
    }

    /// <summary>Completes <paramref name="command"/> at <paramref name="cursor"/> against the internal tree.</summary>
    private async Task<IReadOnlyList<string>> CompleteInternalTreeAsync(string command, int cursor, CancellationToken ct)
    {
        var ctx = NewContext(ct);
        try
        {
            CompletionResult result = await _service.CompleteAsync(command, cursor, ctx, ct).ConfigureAwait(false);
            var results = new List<string>(result.Suggestions.Length);
            foreach (CompletionSuggestion suggestion in result.Suggestions)
                results.Add(suggestion.Text);

            return results;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>Renders the smart-usage listing of every registered command (for the <c>help</c> command).</summary>
    public string BuildHelpListing()
    {
        List<CommandBase> commands;
        GlyphSet glyphs;
        char prefix;
        bool noPrefix;
        lock (_gate)
        {
            commands = [.. _registered];
            glyphs = _glyphs;
            prefix = _prefix;
            noPrefix = _noPrefix;
        }

        return _ui?.FormatCommandIndex(commands, noPrefix ? string.Empty : prefix.ToString(), glyphs)
            ?? HelpRendering.Index(
            commands,
            noPrefix ? string.Empty : prefix.ToString(),
            new HelpRendering.GlyphContext(glyphs.Ok, glyphs.Fail));
    }

    /// <summary>The registered command objects, for the help index and for command lookup by name.</summary>
    internal IReadOnlyList<CommandBase> RegisteredCommands
    {
        get { lock (_gate) return [.. _registered]; }
    }

    /// <summary>
    /// Lists every registered internal command as name/usage/description snapshots.
    /// Shaped for bridges (the MCP server's <c>mcc_internal_commands_list</c> above all) that must describe the tree without holding live command objects or parsing rendered help text.
    /// </summary>
    public IReadOnlyList<CommandSummary> ListCommands()
    {
        lock (_gate)
        {
            var rows = new List<CommandSummary>(_registered.Count);
            foreach (CommandBase command in _registered)
                rows.Add(new CommandSummary(command.CmdName, command.CmdUsage, command.CmdDesc));

            return rows;
        }
    }

    /// <summary>
    /// Describes every command registered at the instant this method is called.
    /// The snapshot includes dynamically registered plugin and Beacon commands and owns copies of every metadata collection, so registrations may be removed without invalidating a host's current view.
    /// </summary>
    public IReadOnlyList<CommandDescriptor> DescribeCommands()
    {
        lock (_gate)
        {
            var rows = new List<CommandDescriptor>(_registered.Count);
            foreach (CommandBase command in _registered)
            {
                rows.Add(new CommandDescriptor(
                    command.CmdName,
                    command.CmdDesc,
                    command.CmdUsage,
                    command.Category,
                    command.Aliases.ToImmutableArray(),
                    command.UsageLines.ToImmutableArray(),
                    command.Flags.ToImmutableArray(),
                    command.Examples.ToImmutableArray(),
                    command.RequiredFeatures,
                    command.SeeAlso.ToImmutableArray(),
                    command.ManTopic,
                    command.ShowInIndex));
            }

            return rows;
        }
    }

    /// <summary>Every usage line for one command's whole subtree (for that command's <c>help</c> node).</summary>
    public ImmutableArray<string> GetCommandUsage(string commandName, CommandContext ctx)
    {
        ArgumentNullException.ThrowIfNull(commandName);
        ArgumentNullException.ThrowIfNull(ctx);
        return _service.GetUsage(commandName, ctx);
    }

    /// <inheritdoc/>
    public string RenderUsage(CommandBase command, CommandContext ctx)
        => ctx.Ui?.FormatCommandHelp(new CommandHelpPresentation(command, GetCommandUsage(command.CmdName, ctx), Prefix, NoPrefix,
            ctx.TerrainEnabled, ctx.InventoryEnabled, ctx.EntityEnabled, ctx.PhysicsEnabled, ctx.PathfindingEnabled), ctx.Glyphs)
            ?? HelpRendering.Page(command, GetCommandUsage(command.CmdName, ctx), Prefix, NoPrefix,
            new HelpRendering.GateStates(ctx.TerrainEnabled, ctx.InventoryEnabled, ctx.EntityEnabled,
                ctx.PhysicsEnabled, ctx.PathfindingEnabled),
            new HelpRendering.GlyphContext(ctx.Glyphs.Ok, ctx.Glyphs.Fail));

    private async Task<InputRouting> SendToServerAsync(string text, CancellationToken ct)
    {
        if (_client.Status != ClientStatus.Playing)
        {
            var notConnected = new CmdResult();

            // Configuring is a LIVE session that is mid-hop, not a session that is gone: 1.20.2+ servers re-enter the configuration phase to move a client somewhere else, which is how every proxy sends a player to another backend.
            // Answering "not connected to any server" there would be false, and the connection is about to be usable again.
            notConnected.Fail(_client.Status == ClientStatus.Configuring
                ? McStrings.Get("mcc.switching_servers_busy")
                : CommandText.NotConnected(this));
            return new InputRouting(InputAction.NotConnected, notConnected);
        }

        try
        {
            await _client.Game.Chat.SendAsync(text, ct).ConfigureAwait(false);
            return new InputRouting(InputAction.SentToServer, null);
        }
        catch (Umpk.Protocol.Java.ProtocolViolationException)
        {
            // The check above closes the window it can, but the hop can begin between it and the write: the phase is resolved where the frame is encoded, not where the send was called, so the result is "Packet ServerboundChatCommandPacket is not valid to send in phase Configuration"
            // - a protocol-layer sentence about a situation the user can only wait out. Same event as
            // above, so the same wording.
            var switching = new CmdResult();
            switching.Fail(McStrings.Get("mcc.switching_servers_busy"));
            return new InputRouting(InputAction.NotConnected, switching);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A send that failed because the socket is gone is a lost connection, not a failed command.
            // DescribeSendFailure says so and ends the session, so the next status read cannot answer from a snapshot the connection can no longer refresh.
            var failed = new CmdResult();
            failed.Fail(_client.DescribeSendFailure(ex));
            return new InputRouting(InputAction.CommandExecuted, failed);
        }
    }

    private CommandContext NewContext(CancellationToken ct, ICommandOutput? output = null)
    {
        DmcbkConfiguration? config;
        lock (_gate)
            config = _config;

        return new CommandContext(_client, output ?? _output, _ui, config, _variables, _features, this, ct);
    }

    private void ApplyPrefix(DmcbkConfiguration? config)
    {
        InternalCommandPrefix prefix = config?.Permissions.CommandPrefix ?? InternalCommandPrefix.Slash;
        switch (prefix)
        {
            case InternalCommandPrefix.None:
                _noPrefix = true;
                _prefix = ' ';
                break;
            case InternalCommandPrefix.Backslash:
                _noPrefix = false;
                _prefix = '\\';
                break;
            default:
                _noPrefix = false;
                _prefix = '/';
                break;
        }
    }

    private void RegisterBuiltins()
    {
        CommandBase[] commands =
        [
            new HelpCommand(),
            new ManCommand(),
            new HealthCommand(),
            new ListCommand(),
            new TpsCommand(),
            new EffectsCommand(),
            new TeamsCommand(),
            new ScoreboardCommand(),
            new RespawnCommand(),
            new SneakCommand(),
            new AnimationCommand(),
            new MoveCommand(),
            new LookCommand(),
            new BlockInfoCommand(),
            new DigCommand(),
            new UseBlockCommand(),
            new UseItemCommand(),
            new ChunkCommand(),
            new ChangeSlotCommand(),
            new DropItemCommand(),
            new InventoryCommand(),
            new EnchantCommand(),
            new NameItemCommand(),
            new RecipeBookCommand(),
            new EntityCommand(),
            new BookCommand(),
            new BedCommand(),
            new DialogCommand(),
            new LogCommand(),
            new SendCommand(),
            new DebugCommand(),
            new AchievementCommand(),
            new TabCommand(),
            new ConnectCommand(),
            new RecoCommand(),
            new ReloadCommand(),
            new SetCommand(),
            new PluginsCommand(),
            new ServersCommand(),
            new LangCommand(),
            RemovedCommand.Upgrade(),
            RemovedCommand.Script(),
        ];

        ICommandRegistrationScope<CommandContext> scope = _service.CreateScope("mcc:builtin");
        scope.Register(builder =>
        {
            foreach (CommandBase command in commands)
                command.Register(builder);
        });

        _registered.AddRange(commands);
    }
}
