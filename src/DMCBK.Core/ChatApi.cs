using Umpk.Client;
using Umpk.Client.Events;
using Umpk.Commands;

namespace DMCBK.Core;

/// <summary>
/// A single server-side command-completion candidate surfaced by <see cref="ChatApi.CompleteAsync"/>.
/// Console-free: the host renders it.
/// Mirrors UMPK's <c>CompletionSuggestion</c> without leaking the wire type.
/// </summary>
/// <param name="Text">The replacement text to insert over the covered input span.</param>
/// <param name="Tooltip">An optional descriptive tooltip, or <c>null</c>.</param>
public readonly record struct ChatCompletion(string Text, string? Tooltip);

/// <summary>
/// The result of a <see cref="ChatApi.CompleteAsync"/> query: the shared replacement range in the input plus the ordered candidates.
/// <see cref="RangeStart"/>/<see cref="RangeEnd"/> index the original input string.
/// </summary>
/// <param name="RangeStart">The inclusive start index of the input range the suggestions replace.</param>
/// <param name="RangeEnd">The exclusive end index of the input range the suggestions replace.</param>
/// <param name="Suggestions">The ordered completion candidates.</param>
public sealed record ChatCompletionResult(int RangeStart, int RangeEnd, IReadOnlyList<ChatCompletion> Suggestions)
{
    /// <summary>An empty completion result covering the cursor position.</summary>
    public static ChatCompletionResult Empty { get; } = new(0, 0, []);
}

/// <summary>
/// The chat surface: send a line (a leading <c>/</c> routes to the command path) and observe inbound messages.
/// Inbound messages are surfaced with UMPK's structured <see cref="ChatMessageReceived"/> payload; the core never flattens the component to a string (the host chooses rendering).
/// </summary>
public sealed class ChatApi
{
    private readonly object _gate = new();
    private UmpkClient? _client;
    private IDisposable? _subscription;
    private IDisposable? _suppressedSubscription;
    private IDisposable? _gapSubscription;

    internal ChatApi()
    {
    }

    /// <summary>Raised for every inbound chat message while the session is live.</summary>
    public event EventHandler<ChatMessageReceived>? MessageReceived;

    /// <summary>
    /// Raised when the server delivered a chat message and told the client to display none of it (a fully filtered message).
    /// No content is carried, deliberately: the point is that a host can tell a suppressed message from a message that was never sent, without being handed text the server withheld.
    /// </summary>
    public event EventHandler<ChatMessageSuppressed>? MessageSuppressed;

    /// <summary>
    /// Raised when the server's chat stream was observed to be missing a message or delivered one out of order (1.21.5+, where every <c>player_chat</c> carries a per-connection global index).
    /// A host whose automation depends on seeing every chat line should treat this as the signal that it did not.
    /// </summary>
    public event EventHandler<ChatStreamGap>? StreamGap;

    /// <summary>
    /// Sends a chat line.
    /// A leading <c>/</c> routes to the command send path (the slash is preserved by UMPK's command action); anything else is sent as chat.
    /// </summary>
    public Task SendAsync(string message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        UmpkClient client = _client
            ?? throw new InvalidOperationException("Chat is unavailable: the client is not in a live session.");

        return client.Actions.Chat.SendAsync(message, ct);
    }

    /// <summary>
    /// Requests server-side command completion at <paramref name="cursor"/> over UMPK's merged completion entry point (local server-command-tree walk plus, when the tree marks the token as server-driven, the server tab-complete round trip).
    /// The returned <see cref="ChatCompletionResult"/> is console-free; the host merges it with the local internal-command tree and renders the suggestion UI.
    /// When no session is live, an empty result is returned rather than throwing (the host may complete internal commands offline).
    /// </summary>
    /// <param name="input">The full input line, including any leading command slash.</param>
    /// <param name="cursor">The cursor position, in [0, input.Length]. Defaults to the end of the input.</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task<ChatCompletionResult> CompleteAsync(string input, int cursor = -1, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        int at = cursor < 0 ? input.Length : cursor;
        if (at < 0 || at > input.Length)
            throw new ArgumentOutOfRangeException(nameof(cursor));

        UmpkClient? client;
        lock (_gate)
            client = _client;

        if (client is null)
            return ChatCompletionResult.Empty;

        CompletionResult result = await client.Actions.Chat.CompleteAsync(input, at, ct).ConfigureAwait(false);
        if (result.Suggestions.IsDefaultOrEmpty)
            return ChatCompletionResult.Empty;

        var mapped = new List<ChatCompletion>(result.Suggestions.Length);
        foreach (CompletionSuggestion suggestion in result.Suggestions)
            mapped.Add(new ChatCompletion(suggestion.Text, suggestion.Tooltip));

        return new ChatCompletionResult(result.RangeStart, result.RangeEnd, mapped);
    }

    internal void Bind(UmpkClient client)
    {
        lock (_gate)
        {
            _client = client;
            _subscription = client.Events.Subscribe<ChatMessageReceived>(OnReceived);
            _suppressedSubscription = client.Events.Subscribe<ChatMessageSuppressed>(OnSuppressed);
            _gapSubscription = client.Events.Subscribe<ChatStreamGap>(OnStreamGap);
        }
    }

    internal void Unbind()
    {
        lock (_gate)
        {
            _subscription?.Dispose();
            _subscription = null;
            _suppressedSubscription?.Dispose();
            _suppressedSubscription = null;
            _gapSubscription?.Dispose();
            _gapSubscription = null;
            _client = null;
        }
    }

    private void OnReceived(ChatMessageReceived message) => MessageReceived?.Invoke(this, message);

    private void OnSuppressed(ChatMessageSuppressed message) => MessageSuppressed?.Invoke(this, message);

    private void OnStreamGap(ChatStreamGap gap) => StreamGap?.Invoke(this, gap);
}
