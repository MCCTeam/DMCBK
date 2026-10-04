namespace DMCBK.PluginSdk;

/// <summary>
/// A call from C# into a script failed: missing script, missing export, arity mismatch, or an unmarshable argument.
/// Names the script, the function, the arity, and the caller line (0 when called from C# rather than from a script line), loud like the messenger contract mismatch, never a null that reads as "no sales today".
/// </summary>
public sealed class BeaconCallException : Exception
{
    /// <summary>Builds a call failure.</summary>
    public BeaconCallException(
        string scriptId,
        string function,
        string message,
        int expectedArity = -1,
        int actualArity = -1,
        int callerLine = 0,
        Exception? inner = null)
        : base(message, inner)
    {
        ScriptId = scriptId;
        Function = function;
        ExpectedArity = expectedArity;
        ActualArity = actualArity;
        CallerLine = callerLine;
    }

    /// <summary>The script id (file name without extension).</summary>
    public string ScriptId { get; }

    /// <summary>The exported function name.</summary>
    public string Function { get; }

    /// <summary>Expected argument count, or -1 when not an arity failure.</summary>
    public int ExpectedArity { get; }

    /// <summary>Actual argument count, or -1 when not an arity failure.</summary>
    public int ActualArity { get; }

    /// <summary>The caller line, or 0 when called from C#.</summary>
    public int CallerLine { get; }
}
