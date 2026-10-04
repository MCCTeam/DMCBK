using Umpk.Client;
namespace DMCBK.Core.Commands;

/// <summary>What the input router did with a line.</summary>
public enum InputAction
{
    /// <summary>The line was empty and ignored.</summary>
    Empty,

    /// <summary>An internal command executed; <see cref="InputRouting.Result"/> carries the outcome.</summary>
    CommandExecuted,

    /// <summary>The line was forwarded to the server as chat/command.</summary>
    SentToServer,

    /// <summary>The line would have gone to the server but no session is active.</summary>
    NotConnected,
}

/// <summary>The outcome of routing one input line.</summary>
/// <param name="Action">What the router did.</param>
/// <param name="Result">The command result when an internal command ran (or a diagnostic), else null.</param>
public sealed record InputRouting(InputAction Action, CmdResult? Result);

/// <summary>What the prefix rules make of one raw input line.</summary>
internal enum InputKind
{
    /// <summary>The line was empty.</summary>
    Empty,

    /// <summary>An internal MCC command, to be dispatched against the internal command tree.</summary>
    InternalCommand,

    /// <summary>A server command: it reaches the server with its own command slash intact.</summary>
    ServerCommand,

    /// <summary>Ordinary chat (which the chat API still routes to the command wire if it starts with '/').</summary>
    Chat,
}

/// <summary>
/// One raw input line seen through the prefix rules.
/// <paramref name="Text"/> is the line with the internal prefix character removed when the rules remove one, and <paramref name="Offset"/> is how many leading characters that removed, so any index into <paramref name="Text"/> maps back onto the raw line by adding <paramref name="Offset"/>.
/// </summary>
/// <param name="Kind">What the line is.</param>
/// <param name="Text">The line after prefix handling.</param>
/// <param name="Offset">Leading characters dropped from the raw line to produce <paramref name="Text"/>.</param>
internal readonly record struct InputClassification(InputKind Kind, string Text, int Offset);

/// <summary>
/// One suggestion query answered for a raw host input line: the ordered candidates and the span of THAT LINE they replace.
/// <see cref="Start"/> and <see cref="End"/> index the raw buffer the host holds, never a prefix-stripped form of it, so a host can apply a candidate with a plain substring splice.
/// </summary>
/// <param name="Suggestions">The ordered, de-duplicated candidates (internal tree first, then the server).</param>
/// <param name="Start">The inclusive start index, in the raw line, of the span a candidate replaces.</param>
/// <param name="End">The exclusive end index, in the raw line, of that span (the cursor).</param>
public sealed record InputCompletion(IReadOnlyList<ChatCompletion> Suggestions, int Start, int End);
