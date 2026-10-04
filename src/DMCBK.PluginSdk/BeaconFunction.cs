using DMCBK.Core.Beacon;

namespace DMCBK.PluginSdk;

/// <summary>
/// One extension function a plugin offers to scripts (the C# side of <c>extern</c>).
/// The description is not decoration: it becomes the help text shown by lint and REPL autocomplete, the same way doc comments feed <c>/help</c> for commands.
/// </summary>
/// <param name="Name">The script-side name.</param>
/// <param name="Capability">The manifest capability scripts declare to use it.</param>
/// <param name="Description">Help text for lint and autocomplete.</param>
/// <param name="Parameters">Parameter names (arity and help).</param>
/// <param name="ParameterTypes">
/// Optional per-parameter CLR types for registration-time validation (same length as <paramref name="Parameters"/>).
/// Null skips static validation; call-time marshaling still enforces the six kinds on whatever <paramref name="Invoke"/> returns.
/// </param>
/// <param name="ReturnType">Optional CLR return type for registration-time validation.</param>
/// <param name="Invoke">
/// Runs on the Beacon scheduler, never the session loop.
/// Receives caller cancellation (fires on reconnect and unload); a function that blocks past the event budget is aborted and counted against the owning plugin.
/// Returns one of the six kinds (or Task of one); a private type fails the call naming the plugin and the function.
/// </param>
public sealed record BeaconFunction(
    string Name,
    string Capability,
    string Description,
    IReadOnlyList<string> Parameters,
    IReadOnlyList<Type>? ParameterTypes,
    Type? ReturnType,
    Func<BeaconCallContext, Task<object?>> Invoke)
{
    /// <summary>
    /// Validates the six-kind mapping for one function at registration time: text/string, number/double (ints widen), yes-no/bool, list/read-only list, map/read-only string-keyed dict, none/null (void, null, or nullable returns).
    /// Anything else fails fast naming the plugin plus the offending signature, never at first call at night.
    /// </summary>
    /// <exception cref="InvalidOperationException">A parameter or return type cannot cross.</exception>
    public static void ValidateSignature(string pluginId, IReadOnlyList<Type> parameterTypes, Type? returnType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(parameterTypes);
        for (int i = 0; i < parameterTypes.Count; i++)
        {
            Type type = Unwrap(parameterTypes[i]);
            if (!IsCrossable(type, forReturn: false))
            {
                throw new InvalidOperationException(
                    $"Plugin '{pluginId}' cannot offer a Beacon function with parameter {i} " +
                    $"of type '{Describe(parameterTypes[i])}': only string, double (ints widen), bool, " +
                    $"read-only lists, read-only string-keyed dictionaries, and nullable/none cross the boundary.");
            }
        }

        if (returnType is not null)
        {
            Type type = Unwrap(returnType);
            if (!IsCrossable(type, forReturn: true))
            {
                throw new InvalidOperationException(
                    $"Plugin '{pluginId}' cannot offer a Beacon function returning " +
                    $"'{Describe(returnType)}': only string, double (ints widen), bool, read-only lists, " +
                    $"read-only string-keyed dictionaries, void, and null cross the boundary.");
            }
        }
    }

    internal static Type Unwrap(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        Type current = type;
        while (true)
        {
            Type? underlying = Nullable.GetUnderlyingType(current);
            if (underlying is null)
                return current;

            current = underlying;
        }
    }

    internal static bool IsCrossable(Type type, bool forReturn)
    {
        if (type == typeof(void) || type == typeof(Task) || type == typeof(ValueTask))
            return forReturn;

        if (type == typeof(string) || type == typeof(bool))
            return true;

        if (type == typeof(double) || type == typeof(float)
            || type == typeof(int) || type == typeof(long)
            || type == typeof(short) || type == typeof(byte)
            || type == typeof(decimal))
            return true;

        if (type.IsGenericType)
        {
            Type generic = type.GetGenericTypeDefinition();
            Type[] arguments = type.GetGenericArguments();
            if ((generic == typeof(Task<>) || generic == typeof(ValueTask<>)) && arguments.Length == 1)
                return IsCrossable(Unwrap(arguments[0]), forReturn: true);

            if (generic == typeof(Nullable<>) && arguments.Length == 1)
                return IsCrossable(arguments[0], forReturn);

            if (arguments.Length == 1
                && (generic == typeof(IReadOnlyList<>) || generic == typeof(IList<>)
                    || generic == typeof(List<>) || generic == typeof(IEnumerable<>)
                    || generic == typeof(IReadOnlyCollection<>) || generic == typeof(ICollection<>)))
                return arguments[0] == typeof(object);

            if (arguments.Length == 2
                && (generic == typeof(IReadOnlyDictionary<,>) || generic == typeof(IDictionary<,>)
                    || generic == typeof(Dictionary<,>))
                && arguments[0] == typeof(string))
                return arguments[1] == typeof(object);
        }

        if (type.IsArray && type.GetArrayRank() == 1 && type.GetElementType() == typeof(object))
            return true;

        if (type == typeof(object))
            return forReturn;

        return false;
    }

    internal static string Describe(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
            return Describe(type.GetGenericArguments()[0]) + "?";

        return type.FullName ?? type.Name;
    }
}
