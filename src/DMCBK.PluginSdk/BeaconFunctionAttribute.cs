using System.Reflection;
using DMCBK.Core.Beacon;

namespace DMCBK.PluginSdk;

/// <summary>
/// The short form for single-file plugins: an attribute over a static method for the common case of a pure function.
/// The host scans for it at load and registers the same record as explicit registration (one spelling taught, one forgiven, same rule as the language).
/// Parameters and returns follow the six-kind mapping (validated at scan time, fail fast naming plugin plus signature); an optional trailing <see cref="CancellationToken"/> binds caller cancellation; sync and Task/ValueTask returns both work.
/// </summary>
/// <example>
/// <code>
/// [BeaconFunction("price_of", Capability = "econ.read",
///     Description = "Today's buy price for an item id, or none when unlisted.")]
/// public static double? PriceOf(string item) => PriceList.Today(item);
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class BeaconFunctionAttribute : Attribute
{
    /// <summary>Builds the attribute with the script-side name.</summary>
    public BeaconFunctionAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>The script-side name.</summary>
    public string Name { get; }

    /// <summary>The manifest capability scripts declare to use it. Required.</summary>
    public string Capability { get; set; } = string.Empty;

    /// <summary>Help text for lint and autocomplete. Required.</summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>Scans one plugin assembly for <see cref="BeaconFunctionAttribute"/> methods.</summary>
public static class BeaconFunctionScanner
{
    /// <summary>
    /// Scans <paramref name="assembly"/> for attributed static methods and builds their records.
    /// A bad signature fails fast naming the plugin plus the signature (never at first call).
    /// </summary>
    public static IReadOnlyList<BeaconFunction> Scan(Assembly assembly, string pluginId)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        var found = new List<BeaconFunction>();
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t is not null).ToArray()!;
        }

        foreach (Type type in types)
        {
            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                continue;
            }

            foreach (MethodInfo method in methods)
            {
                BeaconFunctionAttribute? attribute = method.GetCustomAttribute<BeaconFunctionAttribute>(inherit: false);
                if (attribute is null)
                    continue;

                found.Add(Build(pluginId, method, attribute));
            }
        }

        return found;
    }

    private static BeaconFunction Build(string pluginId, MethodInfo method, BeaconFunctionAttribute attribute)
    {
        if (string.IsNullOrWhiteSpace(attribute.Capability))
        {
            throw new InvalidOperationException(
                $"Plugin '{pluginId}' marks '{method.DeclaringType?.FullName}.{method.Name}' " +
                $"as Beacon function '{attribute.Name}' with no Capability: set Capability = \"...\" on the attribute.");
        }

        if (string.IsNullOrWhiteSpace(attribute.Description))
        {
            throw new InvalidOperationException(
                $"Plugin '{pluginId}' marks '{method.DeclaringType?.FullName}.{method.Name}' " +
                $"as Beacon function '{attribute.Name}' with no Description: the description becomes the help text, so it is required.");
        }

        ParameterInfo[] declared = method.GetParameters();
        bool takesToken = declared.Length > 0 && declared[^1].ParameterType == typeof(CancellationToken);
        ParameterInfo[] scriptParams = takesToken ? declared[..^1] : declared;

        var parameterTypes = scriptParams.Select(p => p.ParameterType).ToList<Type>();
        try
        {
            BeaconFunction.ValidateSignature(pluginId, parameterTypes, method.ReturnType);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"Plugin '{pluginId}' cannot offer Beacon function '{attribute.Name}' " +
                $"({method.DeclaringType?.FullName}.{method.Name}): {ex.Message}", ex);
        }

        var names = scriptParams.Select(p => p.Name ?? $"arg{p.Position}").ToList<string>();
        Func<BeaconCallContext, Task<object?>> invoke = async call =>
        {
            object?[] arguments = new object?[scriptParams.Length + (takesToken ? 1 : 0)];
            for (int i = 0; i < scriptParams.Length; i++)
                arguments[i] = Coerce(call.Args[i], scriptParams[i].ParameterType, attribute.Name, pluginId, i);

            if (takesToken)
                arguments[^1] = call.Cancellation;

            object? raw;
            try
            {
                raw = method.Invoke(null, arguments);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }

            object? awaited = await UnwrapAsync(raw).ConfigureAwait(false);
            try
            {
                return BeaconMarshal.ToBeacon(awaited);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(
                    $"Beacon function '{attribute.Name}' from plugin '{pluginId}' returned " +
                    $"a value that cannot cross the boundary: {ex.Message}", ex);
            }
        };

        return new BeaconFunction(
            attribute.Name, attribute.Capability, attribute.Description,
            names, parameterTypes, method.ReturnType, invoke);
    }

    private static object? Coerce(BeaconValue value, Type target, string function, string pluginId, int index)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(target);
        Type unwrapped = BeaconFunction.Unwrap(target);
        object? converted = (BeaconMarshal.FromBeacon(value), unwrapped) switch
        {
            (string text, _) when unwrapped == typeof(string) => text,
            (double number, _) when unwrapped == typeof(double) => number,
            (double number, _) when unwrapped == typeof(float) => (float)number,
            (double number, _) when unwrapped == typeof(int) => (int)number,
            (double number, _) when unwrapped == typeof(long) => (long)number,
            (double number, _) when unwrapped == typeof(short) => (short)number,
            (double number, _) when unwrapped == typeof(byte) => (byte)number,
            (double number, _) when unwrapped == typeof(decimal) => (decimal)number,
            (bool yesNo, _) when unwrapped == typeof(bool) => yesNo,
            (IReadOnlyList<object?> list, _) when IsList(unwrapped) => list,
            (IReadOnlyDictionary<string, object?> map, _) when IsMap(unwrapped) => map,
            (null, _) when !unwrapped.IsValueType || Nullable.GetUnderlyingType(target) is not null => null,
            _ => throw new InvalidOperationException(
                $"Beacon function '{function}' from plugin '{pluginId}' cannot take argument {index} " +
                $"of type '{BeaconFunction.Describe(target)}' from {DescribeValue(value)}."),
        };
        return converted;
    }

    private static bool IsList(Type type)
        => type.IsGenericType
            && (type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
                || type.GetGenericTypeDefinition() == typeof(IList<>)
                || type.GetGenericTypeDefinition() == typeof(List<>)
                || type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                || type.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>)
                || type.GetGenericTypeDefinition() == typeof(ICollection<>))
            && type.GetGenericArguments()[0] == typeof(object);

    private static bool IsMap(Type type)
        => type.IsGenericType
            && (type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)
                || type.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                || type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            && type.GetGenericArguments()[0] == typeof(string)
            && type.GetGenericArguments()[1] == typeof(object);

    private static string DescribeValue(BeaconValue value) => value switch
    {
        BeaconTextValue => "text",
        BeaconNumberValue => "a number",
        BeaconYesNoValue => "yes/no",
        BeaconListValue => "a list",
        BeaconMapValue => "a map",
        _ => "none",
    };

    private static async Task<object?> UnwrapAsync(object? raw)
    {
        switch (raw)
        {
            case null:
                return null;
            case Task<object?> typed:
                return await typed.ConfigureAwait(false);
            case Task task:
                await task.ConfigureAwait(false);
                return task.GetType().IsGenericType && task.GetType().GetGenericTypeDefinition() == typeof(Task<>)
                    ? task.GetType().GetProperty("Result")?.GetValue(task)
                    : null;
            case ValueTask<object?> vtyped:
                return await vtyped.ConfigureAwait(false);
            case ValueTask vtask:
                await vtask.ConfigureAwait(false);
                return null;
            default:
                return raw;
        }
    }
}
