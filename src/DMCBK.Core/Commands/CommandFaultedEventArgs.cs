namespace DMCBK.Core.Commands;

/// <summary>
/// One dispatched command that threw.
/// <see cref="Command"/> is resolved from the line's first token, so it is null when the exception came out of a line that named no registered command.
/// </summary>
public sealed class CommandFaultedEventArgs : EventArgs
{
    internal CommandFaultedEventArgs(CommandBase? command, string line, Exception exception)
    {
        Command = command;
        Line = line;
        Exception = exception;
    }

    /// <summary>The command the line named, or null when nothing registered matches it.</summary>
    public CommandBase? Command { get; }

    /// <summary>The dispatched line, already unprefixed and expanded.</summary>
    public string Line { get; }

    /// <summary>What it threw.</summary>
    public Exception Exception { get; }
}
