namespace DMCBK.Core.Commands;

/// <summary>
/// The host-provided sink a command writes multi-line body output to (player listings, help text, block dumps).
/// The core never touches the console; the host renders.
/// A command's final one-line status/message travels back in its <see cref="CmdResult"/>; longer bodies stream through this sink as they are produced.
/// A host that provides none gets <see cref="NullCommandOutput"/> (output is dropped).
/// </summary>
public interface ICommandOutput
{
    /// <summary>Writes one line of already-localized command output.</summary>
    void WriteLine(string text);
}

/// <summary>An <see cref="ICommandOutput"/> that discards everything (used when the host supplies none).</summary>
public sealed class NullCommandOutput : ICommandOutput
{
    /// <summary>The shared instance.</summary>
    public static NullCommandOutput Instance { get; } = new();

    private NullCommandOutput()
    {
    }

    /// <inheritdoc/>
    public void WriteLine(string text)
    {
    }
}

/// <summary>An <see cref="ICommandOutput"/> that captures every line (used by tests and buffered hosts).</summary>
public sealed class BufferedCommandOutput : ICommandOutput
{
    private readonly List<string> _lines = [];

    /// <summary>The captured lines in order.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>The captured lines joined by newlines.</summary>
    public string Text => string.Join('\n', _lines);

    /// <inheritdoc/>
    public void WriteLine(string text) => _lines.Add(text);

    /// <summary>Clears the buffer.</summary>
    public void Clear() => _lines.Clear();
}
