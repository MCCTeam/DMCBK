namespace DMCBK.Core.Beacon;

/// <summary>
/// The six outside-world verbs against <see cref="IBeaconHostServices"/> doubles.
/// Real <c>CommandService</c>/<c>ChatApi</c> binding lives in the live-session wiring; this class programs only to the seam so fakes and real adapters stay interchangeable (see the seam-conformance test).
/// </summary>
/// <remarks>
/// <para>FROZEN verb result shapes for the scheduler, event, and state runtimes:</para>
/// <list type="bullet">
/// <item>
/// <c>say</c>/<c>whisper</c>/<c>server</c>/<c>show</c>/<c>disconnect</c> are statements: no value, success means sent (or locally shown, or disconnected).
/// <c>say</c> with a leading slash warns (<c>B3004</c>) and sends nothing; <c>server</c> without a leading slash errors (<c>B3004</c>) and sends nothing.
/// </item>
/// <item>
/// <c>mcc</c> is an expression returning text.
/// Host failures raise a catchable <see cref="BeaconRuntimeException"/> whose catch value is a map with at least <c>message</c> text; outside <c>try</c> the handler stops with that readable message.
/// </item>
/// <item>
/// <c>show</c> appends to local output only, needs no capability, and never touches the fake transport.
/// It is the REPL default output.
/// </item>
/// <item>
/// Every <c>say</c>/<c>whisper</c>/<c>server</c>/<c>mcc</c> passthrough is debug-logged with script id and line.
/// Local echo stamps the script id (<c>[id] text</c>) while the public chat payload sent to <see cref="IBeaconChatSink.SayAsync"/> stays unprefixed.
/// </item>
/// </list>
/// </remarks>
public sealed class BeaconVerbs
{
    private readonly IBeaconHostServices _host;
    private readonly string _scriptId;
    private readonly List<BeaconPassthroughLog> _passthrough;
    private readonly List<string> _localEcho;
    private readonly List<string> _localOutput;
    private readonly List<BeaconDiagnostic> _warnings;
    private readonly BeaconChatBucket? _chatBucket;
    private readonly Action? _yielded;

    /// <summary>Builds a verb runner over shared per-dispatch sinks.</summary>
    public BeaconVerbs(
        IBeaconHostServices host,
        string scriptId,
        List<BeaconPassthroughLog> passthrough,
        List<string> localEcho,
        List<string> localOutput,
        List<BeaconDiagnostic> warnings,
        BeaconChatBucket? chatBucket = null,
        Action? yielded = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ArgumentNullException.ThrowIfNull(passthrough);
        ArgumentNullException.ThrowIfNull(localEcho);
        ArgumentNullException.ThrowIfNull(localOutput);
        ArgumentNullException.ThrowIfNull(warnings);
        _host = host;
        _scriptId = scriptId;
        _passthrough = passthrough;
        _localEcho = localEcho;
        _localOutput = localOutput;
        _warnings = warnings;
        _chatBucket = chatBucket;
        _yielded = yielded;
    }

    private int LineOf(SourceSpan span) => span.Origin.Line;

    private static void RequireSendableText(string text, string verb, string what, SourceSpan span)
    {
        int bad = text.IndexOfAny(['\r', '\n', '§']);
        if (bad >= 0)
        {
            char found = text[bad];
            string name = found == '§' ? "the section sign '§'" : "a line break";
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.StrictChatText,
                $"I expected {what} in '{verb}' to be one chat-safe line, but found {name}. " +
                "The server kicks for these instead of delivering the message.",
                span.Origin,
                $"Send one line at a time (split on newlines first) and drop '§' codes; " +
                $"read {what} with len() or slice() before sending it.");
        }
    }

    private static string RequireText(BeaconValue value, string verb, SourceSpan span, string what)
    {
        if (value is BeaconTextValue text)
            return text.Value;

        throw new BeaconRuntimeException(
            BeaconDiagnosticCodes.StrictMixedOperands,
            $"I expected text for {what} in '{verb}', but found {BeaconInterpreter.DescribeValueKind(value)}. " +
            $"Write {verb} \"...\" with text, or convert with text(value) or interpolation \"{{value}}\".",
            span.Origin,
            $"Write {verb} \"...\" (text only; use text(value) to convert).");
    }

    /// <summary>
    /// <c>say text</c>: public chat.
    /// A leading slash warns (naming <c>server</c>) and sends nothing.
    /// Chat-bucket throttling (or the mute gag) applies before the transport.
    /// </summary>
    public async Task SayAsync(BeaconValue message, SourceSpan span, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(span);
        string text = RequireText(message, "say", span, "the message");
        SourceSpan origin = span.Origin;
        RequireSendableText(text, "say", "the message", span);
        if (text.StartsWith("/", StringComparison.Ordinal))
        {
            _warnings.Add(new BeaconDiagnostic(
                BeaconDiagnosticCodes.StrictLeadingSlash,
                BeaconSeverity.Warning,
                $"say with a leading slash sends nothing. Did you mean `server \"{text}\"`?",
                origin,
                $"Write server \"{text}\"."));
            _passthrough.Add(new BeaconPassthroughLog(_scriptId, origin.Line, "say", $"(refused leading slash) {text}"));
            return;
        }

        if (_chatBucket is not null && _chatBucket.Muted)
        {
            _passthrough.Add(new BeaconPassthroughLog(_scriptId, origin.Line, "say", $"(muted) {text}"));
            _localEcho.Add($"[{_scriptId}] (muted) {text}");
            return;
        }

        if (_chatBucket is not null)
        {
            bool waited = await _chatBucket.WaitForSlotAsync(_scriptId, origin, "say", _warnings, ct).ConfigureAwait(false);
            if (waited)
                _yielded?.Invoke();
        }

        _passthrough.Add(new BeaconPassthroughLog(_scriptId, origin.Line, "say", text));
        await _host.SayAsync(text, ct).ConfigureAwait(false);
        _localEcho.Add($"[{_scriptId}] {text}");
    }

    /// <summary><c>whisper player text</c>: private message to one player (bucketed like <c>say</c>).</summary>
    public async Task WhisperAsync(BeaconValue player, BeaconValue message, SourceSpan span, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(span);
        string who = RequireText(player, "whisper", span, "the player name");
        string text = RequireText(message, "whisper", span, "the message");
        SourceSpan origin = span.Origin;
        RequireSendableText(who, "whisper", "the player name", span);
        RequireSendableText(text, "whisper", "the message", span);
        if (_chatBucket is not null && _chatBucket.Muted)
        {
            _passthrough.Add(new BeaconPassthroughLog(_scriptId, origin.Line, "whisper", $"(muted) {who} {text}"));
            _localEcho.Add($"[{_scriptId}] (muted whisper {who}) {text}");
            return;
        }

        if (_chatBucket is not null)
        {
            bool waited = await _chatBucket.WaitForSlotAsync(_scriptId, origin, "whisper", _warnings, ct).ConfigureAwait(false);
            if (waited)
                _yielded?.Invoke();
        }

        _passthrough.Add(new BeaconPassthroughLog(_scriptId, origin.Line, "whisper", $"{who} {text}"));
        await _host.WhisperAsync(who, text, ct).ConfigureAwait(false);
        _localEcho.Add($"[{_scriptId}] (whisper {who}) {text}");
    }

    /// <summary><c>server command</c>: slash required, down the server-command path.</summary>
    public async Task ServerAsync(BeaconValue command, SourceSpan span, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(span);
        string line = RequireText(command, "server", span, "the command");
        SourceSpan origin = span.Origin;
        RequireSendableText(line, "server", "the command", span);
        if (!line.StartsWith("/", StringComparison.Ordinal))
        {
            throw new BeaconRuntimeException(
                BeaconDiagnosticCodes.StrictLeadingSlash,
                $"I expected a leading slash after 'server', but found \"{line}\". Server commands start with '/'.",
                origin,
                $"Write server \"/{line.TrimStart('/')}\".");
        }

        _passthrough.Add(new BeaconPassthroughLog(_scriptId, origin.Line, "server", line));
        try
        {
            await _host.SendServerAsync(line, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BeaconRuntimeException(
                "B4002",
                $"server \"{line}\" failed: {ex.Message}",
                origin,
                $"Wrap it in try/catch err and read err.message.",
                ex);
        }

        _localEcho.Add($"[{_scriptId}] {line}");
    }

    /// <summary>
    /// <c>disconnect [reason]</c>: leave the server cleanly and stay out (auto-reconnect stops).
    /// The reason is local-only (a note in the audit log and console echo); the wire carries no reason.
    /// </summary>
    public async Task DisconnectAsync(BeaconValue? reason, SourceSpan span, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(span);
        SourceSpan origin = span.Origin;
        string? text = reason is null || reason is BeaconNoneValue
            ? null
            : RequireText(reason, "disconnect", span, "the reason");
        string detail = text is null ? "disconnect" : $"disconnect ({text})";
        _passthrough.Add(new BeaconPassthroughLog(_scriptId, origin.Line, "disconnect", detail));
        try
        {
            await _host.DisconnectAsync(text, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BeaconRuntimeException(
                "B4002",
                $"disconnect failed: {ex.Message}",
                origin,
                "Wrap it in try/catch err and read err.message.",
                ex);
        }

        _localEcho.Add($"[{_scriptId}] {detail}");
    }

    /// <summary>
    /// <c>mcc command</c>: runs an MCC internal command, returns output text, raises catchably on failure.
    /// </summary>
    public async Task<BeaconValue> MccAsync(BeaconValue command, SourceSpan span, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(span);
        string line = RequireText(command, "mcc", span, "the command");
        SourceSpan origin = span.Origin;
        _passthrough.Add(new BeaconPassthroughLog(_scriptId, origin.Line, "mcc", line));
        try
        {
            string output = await _host.RunMccAsync(line, ct).ConfigureAwait(false);
            return BeaconValue.Text(output ?? string.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BeaconRuntimeException(
                "B4001",
                $"mcc \"{line}\" failed: {ex.Message}",
                origin,
                "Wrap it in try/catch err and read err.message.",
                ex);
        }
    }

    /// <summary>Merges a settled task's sinks into this dispatch (audit continuity on <c>await</c>).</summary>
    public void AbsorbTaskSinks(
        IEnumerable<BeaconPassthroughLog> passthrough,
        IEnumerable<string> echo,
        IEnumerable<string> output,
        IEnumerable<BeaconDiagnostic> warnings)
    {
        ArgumentNullException.ThrowIfNull(passthrough);
        ArgumentNullException.ThrowIfNull(echo);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(warnings);
        _passthrough.AddRange(passthrough);
        _localEcho.AddRange(echo);
        _localOutput.AddRange(output);
        _warnings.AddRange(warnings);
    }

    /// <summary><c>show expr</c>: local-only, capability-free, never touches the transport.</summary>
    public void Show(BeaconValue message, SourceSpan span)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(span);
        _localOutput.Add(BeaconInterpreter.ToDisplayText(message));
    }
}
