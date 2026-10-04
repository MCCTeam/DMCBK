using System.Reflection;

namespace DMCBK.Core.Configuration;

/// <summary>
/// Warn-and-ignore unknown-key policy: Tomlet silently ignores table keys that have no matching model property, so this scans the parsed document against the model type and emits a warning for each unknown key (leaving deserialization to ignore it).
/// Dictionary-typed sections (e.g. Variables) accept any key.
/// </summary>
internal static class ConfigurationKeyScanner
{
    public static void Scan<T>(IEnumerable<KeyValuePair<string, T>> entries, Type modelType, string prefix, List<ConfigurationWarning> warnings,
        Func<T, IEnumerable<KeyValuePair<string, T>>?> getTable, Func<T, IEnumerable<T>?> getArray)
    {
        if (IsDictionary(modelType))
            return;

        Dictionary<string, PropertyInfo> props = modelType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

        foreach ((string key, T value) in entries)
        {
            string path = prefix.Length == 0 ? key : $"{prefix}.{key}";
            if (!props.TryGetValue(key, out PropertyInfo? prop))
            {
                warnings.Add(new ConfigurationWarning(Localization.McStrings.Format("configuration.validation.unknown_key", path)));
                continue;
            }

            Type propType = prop.PropertyType;
            if (getTable(value) is { } sub && IsComplex(propType))
                Scan(sub, propType, path, warnings, getTable, getArray);
            else if (getArray(value) is { } array && TryGetElementType(propType, out Type? element) && IsComplex(element))
            {
                foreach (T item in array)
                {
                    if (getTable(item) is { } itemTable)
                        Scan(itemTable, element, path, warnings, getTable, getArray);
                }
            }
        }
    }

    private static bool IsComplex(Type type)
        => type is { IsClass: true, IsArray: false }
            && type != typeof(string)
            && !IsDictionary(type)
            && !IsList(type);

    private static bool IsDictionary(Type type)
        => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>);

    private static bool IsList(Type type)
        => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>);

    private static bool TryGetElementType(Type type, out Type element)
    {
        if (IsList(type))
        {
            element = type.GetGenericArguments()[0];
            return true;
        }

        if (type.IsArray && type.GetElementType() is { } arrayElement)
        {
            element = arrayElement;
            return true;
        }

        element = typeof(object);
        return false;
    }
}
