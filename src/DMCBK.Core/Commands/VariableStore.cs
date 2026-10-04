using System.Text;

namespace DMCBK.Core.Commands;

/// <summary>
/// The scoped <c>%var%</c> service: a per-client store of user variables for <c>set</c>, input routing, and command arguments.
/// Variable names are sanitized to leading letters/digits/underscores and compared case-insensitively; values are stored as text.
/// Thread-safe; one instance per <see cref="Client"/>, no statics.
/// </summary>
public sealed class VariableStore
{
    private readonly Dictionary<string, string> _vars = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Creates an empty store.</summary>
    public VariableStore()
    {
    }

    /// <summary>Creates a store seeded from configured variables.</summary>
    public VariableStore(IReadOnlyDictionary<string, string> initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        foreach (KeyValuePair<string, string> entry in initial)
            Set(entry.Key, entry.Value);
    }

    /// <summary>The number of variables currently stored.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _vars.Count;
        }
    }

    /// <summary>
    /// Sets a variable, sanitizing the name to its leading letters/digits/underscores.
    /// Returns false when the name has no usable characters or the value is null.
    /// </summary>
    public bool Set(string name, string value)
    {
        string sanitized = Sanitize(name);
        if (sanitized.Length == 0 || value is null)
            return false;

        lock (_gate)
            _vars[sanitized] = value;

        return true;
    }

    /// <summary>Reads a variable value, or null when it is not set.</summary>
    public string? Get(string name)
    {
        string sanitized = Sanitize(name);
        lock (_gate)
        {
            return _vars.TryGetValue(sanitized, out string? value) ? value : null;
        }
    }

    /// <summary>Tries to read a variable value.</summary>
    public bool TryGet(string name, out string? value)
    {
        string sanitized = Sanitize(name);
        lock (_gate)
            return _vars.TryGetValue(sanitized, out value);
    }

    /// <summary>Removes a variable; returns true when one was removed.</summary>
    public bool Remove(string name)
    {
        string sanitized = Sanitize(name);
        lock (_gate)
            return _vars.Remove(sanitized);
    }

    /// <summary>An immutable snapshot of all variables.</summary>
    public IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (_gate)
            return new Dictionary<string, string>(_vars, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Expands every <c>%name%</c> occurrence (name = letters/digits/underscore) to its value; an unset variable is left literal, matching the legacy <c>ExpandVars</c> behavior.
    /// A lone or unmatched <c>%</c> is emitted verbatim.
    /// </summary>
    public string Expand(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.IndexOf('%') < 0)
            return input;

        var sb = new StringBuilder(input.Length);
        int i = 0;
        while (i < input.Length)
        {
            char c = input[i];
            if (c != '%')
            {
                sb.Append(c);
                i++;
                continue;
            }

            int end = -1;
            for (int j = i + 1; j < input.Length; j++)
            {
                char n = input[j];
                if (n == '%')
                {
                    end = j;
                    break;
                }

                if (!char.IsLetterOrDigit(n) && n != '_')
                    break;
            }

            if (end < 0)
            {
                sb.Append(c);
                i++;
                continue;
            }

            string name = input.Substring(i + 1, end - i - 1);
            if (name.Length > 0 && TryGet(name, out string? value) && value is not null)
                sb.Append(value);
            else
                sb.Append('%').Append(name).Append('%');

            i = end + 1;
        }

        return sb.ToString();
    }

    private static string Sanitize(string name)
    {
        if (string.IsNullOrEmpty(name))
            return string.Empty;

        int count = 0;
        while (count < name.Length && (char.IsLetterOrDigit(name[count]) || name[count] == '_'))
            count++;

        return name[..count];
    }
}
