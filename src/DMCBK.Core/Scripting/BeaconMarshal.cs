namespace DMCBK.Core.Beacon;
/// <summary>CLR/Boundary value marshaling for the fixed six-kind mapping (fail fast, never coerce quietly).</summary>
public static class BeaconMarshal
{
    /// <summary>Converts a plugin-side value to a Beacon value (six kinds only).</summary>
    public static BeaconValue ToBeacon(object? value) => value switch
    {
        null => BeaconValue.None,
        string text => BeaconValue.Text(text),
        bool yesNo => BeaconValue.YesNo(yesNo),
        double number => BeaconValue.Number(number),
        float f => BeaconValue.Number(f),
        int i => BeaconValue.Number(i),
        long l => BeaconValue.Number(l),
        short s => BeaconValue.Number(s),
        byte b => BeaconValue.Number(b),
        decimal d => BeaconValue.Number((double)d),
        IReadOnlyList<object?> list => BeaconValue.List(list.Select(ToBeacon).ToList<BeaconValue>()),
        IReadOnlyDictionary<string, object?> map => BeaconValue.Map(
            map.ToDictionary(kv => kv.Key, kv => ToBeacon(kv.Value), StringComparer.Ordinal)),
        IReadOnlyDictionary<string, string> stringMap => BeaconValue.Map(
            stringMap.ToDictionary(kv => kv.Key, kv => (BeaconValue)BeaconValue.Text(kv.Value), StringComparer.Ordinal)),
        _ => throw new InvalidOperationException(
            $"Cannot marshal '{value.GetType().FullName}' across the Beacon boundary: " +
            "only text/string, number/double (ints widen), yes-no/bool, list/read-only list, " +
            "map/read-only string-keyed dict, and none/null cross. Anything else fails fast at registration."),
    };

    /// <summary>Converts a Beacon value to a read-only plugin-side value (deep, snapshots only).</summary>
    public static object? FromBeacon(BeaconValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value switch
        {
            BeaconTextValue text => text.Value,
            BeaconNumberValue number => number.Value,
            BeaconYesNoValue yesNo => yesNo.Value,
            BeaconNoneValue => null,
            BeaconListValue list => list.Items.Select(FromBeacon).ToList<object?>().AsReadOnly(),
            BeaconMapValue map => map.Entries
                .ToDictionary(kv => kv.Key, kv => FromBeacon(kv.Value), StringComparer.Ordinal),
            _ => null,
        };
    }
}

