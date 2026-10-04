using System.Globalization;

namespace DMCBK.Core.Beacon;

/// <summary>Which of the six Beacon value kinds a <see cref="BeaconValue"/> holds.</summary>
public enum BeaconValueKind
{
    /// <summary>Unicode text.</summary>
    Text,
    /// <summary>Double-precision number.</summary>
    Number,
    /// <summary>Yes or no.</summary>
    YesNo,
    /// <summary>Ordered values.</summary>
    List,
    /// <summary>String-keyed values.</summary>
    Map,
    /// <summary>Absence of a value.</summary>
    None,
}

/// <summary>
/// The six Beacon value kinds (text, number, yes/no, list, map, none).
/// Constructors and pretty-printing for errors and trace live here; explicit converters live in the interpreter.
/// </summary>
public abstract record BeaconValue
{
    /// <summary>Restricted to the six nested kinds below.</summary>
    protected BeaconValue()
    {
    }

    /// <summary>Builds a text value.</summary>
    public static BeaconTextValue Text(string value) => new(value);

    /// <summary>Builds a number value.</summary>
    public static BeaconNumberValue Number(double value) => new(value);

    /// <summary>Builds a yes/no value.</summary>
    public static BeaconYesNoValue YesNo(bool value) => new(value);

    /// <summary>Builds a list value.</summary>
    public static BeaconListValue List(IReadOnlyList<BeaconValue> items) => new(items);

    /// <summary>Builds a map value.</summary>
    public static BeaconMapValue Map(IReadOnlyDictionary<string, BeaconValue> entries) => new(entries);

    /// <summary>The single none value.</summary>
    public static BeaconNoneValue None { get; } = new();

    /// <summary>Which kind this value holds.</summary>
    public abstract BeaconValueKind Kind { get; }

    /// <summary>Renders the value for errors and trace output.</summary>
    public abstract string ToDisplayString();
}

/// <summary>Beacon text value.</summary>
public sealed record BeaconTextValue(string Value) : BeaconValue
{
    /// <inheritdoc />
    public override BeaconValueKind Kind => BeaconValueKind.Text;

    /// <inheritdoc />
    public override string ToDisplayString() => $"\"{Value}\"";
}

/// <summary>Beacon number value.</summary>
public sealed record BeaconNumberValue(double Value) : BeaconValue
{
    /// <inheritdoc />
    public override BeaconValueKind Kind => BeaconValueKind.Number;

    /// <inheritdoc />
    public override string ToDisplayString() => Value.ToString("G", CultureInfo.InvariantCulture);
}

/// <summary>Beacon yes/no value.</summary>
public sealed record BeaconYesNoValue(bool Value) : BeaconValue
{
    /// <inheritdoc />
    public override BeaconValueKind Kind => BeaconValueKind.YesNo;

    /// <inheritdoc />
    public override string ToDisplayString() => Value ? "yes" : "no";
}

/// <summary>Beacon list value.</summary>
public sealed record BeaconListValue(IReadOnlyList<BeaconValue> Items) : BeaconValue
{
    /// <inheritdoc />
    public override BeaconValueKind Kind => BeaconValueKind.List;

    /// <inheritdoc />
    public override string ToDisplayString() => $"[{string.Join(", ", Items.Select(i => i.ToDisplayString()))}]";
}

/// <summary>Beacon map value.</summary>
public sealed record BeaconMapValue(IReadOnlyDictionary<string, BeaconValue> Entries) : BeaconValue
{
    /// <inheritdoc />
    public override BeaconValueKind Kind => BeaconValueKind.Map;

    /// <inheritdoc />
    public override string ToDisplayString() =>
        $"{{{string.Join(", ", Entries.Select(e => $"{e.Key}: {e.Value.ToDisplayString()}"))}}}";
}

/// <summary>Beacon none value.</summary>
public sealed record BeaconNoneValue : BeaconValue
{
    /// <inheritdoc />
    public override BeaconValueKind Kind => BeaconValueKind.None;

    /// <inheritdoc />
    public override string ToDisplayString() => "none";
}
