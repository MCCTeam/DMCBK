using DMCBK.Core.Beacon;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>Value model: six kinds, constructors, and pretty-printing for errors and trace.</summary>
public sealed class BeaconValueTests
{
    [Fact]
    public void Factories_ProduceAllSixKinds()
    {
        Assert.Equal(BeaconValueKind.Text, BeaconValue.Text("hi").Kind);
        Assert.Equal(BeaconValueKind.Number, BeaconValue.Number(1).Kind);
        Assert.Equal(BeaconValueKind.YesNo, BeaconValue.YesNo(true).Kind);
        Assert.Equal(BeaconValueKind.List, BeaconValue.List([]).Kind);
        Assert.Equal(BeaconValueKind.Map, BeaconValue.Map(new Dictionary<string, BeaconValue>()).Kind);
        Assert.Equal(BeaconValueKind.None, BeaconValue.None.Kind);
    }

    [Theory]
    [InlineData("hi", "\"hi\"")]
    [InlineData("", "\"\"")]
    public void Text_PrettyPrintsQuoted(string value, string expected)
    {
        Assert.Equal(expected, BeaconValue.Text(value).ToDisplayString());
    }

    [Theory]
    [InlineData(20, "20")]
    [InlineData(2.5, "2.5")]
    [InlineData(0, "0")]
    public void Number_PrettyPrintsInvariant(double value, string expected)
    {
        Assert.Equal(expected, BeaconValue.Number(value).ToDisplayString());
    }

    [Theory]
    [InlineData(true, "yes")]
    [InlineData(false, "no")]
    public void YesNo_PrettyPrintsAsWords(bool value, string expected)
    {
        Assert.Equal(expected, BeaconValue.YesNo(value).ToDisplayString());
    }

    [Fact]
    public void None_PrettyPrintsAsNone()
    {
        Assert.Equal("none", BeaconValue.None.ToDisplayString());
    }

    [Fact]
    public void List_PrettyPrintsElements()
    {
        BeaconValue value = BeaconValue.List([BeaconValue.Number(1), BeaconValue.Text("two")]);

        Assert.Equal("[1, \"two\"]", value.ToDisplayString());
        Assert.Equal("[]", BeaconValue.List([]).ToDisplayString());
    }

    [Fact]
    public void Map_PrettyPrintsEntries()
    {
        BeaconValue value = BeaconValue.Map(new Dictionary<string, BeaconValue>
        {
            ["a"] = BeaconValue.Number(1),
        });

        Assert.Equal("{a: 1}", value.ToDisplayString());
        Assert.Equal("{}", BeaconValue.Map(new Dictionary<string, BeaconValue>()).ToDisplayString());
    }

    [Fact]
    public void Values_RetainPayloads()
    {
        Assert.Equal("hi", Assert.IsType<BeaconTextValue>(BeaconValue.Text("hi")).Value);
        Assert.Equal(2.5, Assert.IsType<BeaconNumberValue>(BeaconValue.Number(2.5)).Value);
        Assert.True(Assert.IsType<BeaconYesNoValue>(BeaconValue.YesNo(true)).Value);
    }
}
