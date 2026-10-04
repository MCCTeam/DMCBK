using DMCBK.Core.Commands;
using DMCBK.Core.Commands.Impl;
using Umpk.Geometry;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The <c>/entity</c> listing.
/// <para>
/// It used to be one line per entity with the field names repeated on each ("#24817: Type: Squid, Location: X:127,25, Y:56,92, Z:287,66"), in tracker order, with the whole usage block appended underneath.
/// Sixty of those is a realistic count on an ordinary server, and it answers none of the questions someone runs the command to ask: what is around me, and what is near me.
/// </para>
/// </summary>
public sealed class EntityListingTests
{
    private static EntityCommand.ListingRow Row(
        int id, string type, double distance, string name = "")
        => new(id, type, name, distance, new Vec3d(1.2, 2.5, 3.75));

    /// <summary>The listing is a table, so it can be rendered as one by every host.</summary>
    [Fact]
    public void IsAMarkdownTable()
    {
        string md = EntityCommand.BuildListingMarkdown([Row(1, "Cow", 3.0), Row(2, "Bat", 9.0)]);

        Assert.Contains("| 1 | Cow |", md, StringComparison.Ordinal);
        Assert.Contains("| 2 | Bat |", md, StringComparison.Ordinal);
        Assert.Contains("| --- |", md, StringComparison.Ordinal);
        Assert.StartsWith("# ", md, StringComparison.Ordinal);
    }

    /// <summary>
    /// The name column costs width on every row, so it is only there when something has a name.
    /// On a normal server nothing does, and an empty column is a stripe of wasted width down the table.
    /// </summary>
    [Fact]
    public void NameColumn_IsOmittedWhenNothingIsNamed()
    {
        string md = EntityCommand.BuildListingMarkdown([Row(1, "Cow", 3.0), Row(2, "Bat", 9.0)]);

        Assert.DoesNotContain("Nickname", md, StringComparison.Ordinal);

        // Four columns: id, type, distance, location.
        string header = md.Split('\n').First(l => l.StartsWith("| ", StringComparison.Ordinal));
        Assert.Equal(4, header.Split('|', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    /// <summary>And it IS there as soon as one entity has one.</summary>
    [Fact]
    public void NameColumn_AppearsWhenSomethingIsNamed()
    {
        string md = EntityCommand.BuildListingMarkdown(
            [Row(1, "Cow", 3.0), Row(2, "Villager", 9.0, "Bob the Trader")]);

        Assert.Contains("Nickname", md, StringComparison.Ordinal);
        Assert.Contains("Bob the Trader", md, StringComparison.Ordinal);

        string header = md.Split('\n').First(l => l.StartsWith("| ", StringComparison.Ordinal));
        Assert.Equal(5, header.Split('|', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    /// <summary>The tally is what answers "what is around me" without reading every row.</summary>
    [Fact]
    public void CountsByType_MostCommonFirst()
    {
        string md = EntityCommand.BuildListingMarkdown(
        [
            Row(1, "Bat", 1), Row(2, "Bat", 2), Row(3, "Bat", 3),
            Row(4, "Cow", 4), Row(5, "Cow", 5),
            Row(6, "Pig", 6),
        ]);

        Assert.Contains("3 Bat, 2 Cow, 1 Pig", md, StringComparison.Ordinal);
        Assert.Contains("6 nearby", md, StringComparison.Ordinal);
    }

    /// <summary>
    /// Coordinates are invariant.
    /// The old form used the current culture, so on a comma-decimal locale a position read "X:127,25, Y:56,92, Z:287,66": three numbers that parse to the eye as six.
    /// </summary>
    [Fact]
    public void CoordinatesAreInvariant()
    {
        System.Globalization.CultureInfo original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            string md = EntityCommand.BuildListingMarkdown([Row(1, "Cow", 3.42)]);

            Assert.Contains("1.2 2.5 3.8", md, StringComparison.Ordinal);
            Assert.Contains("| 3.4 |", md, StringComparison.Ordinal);
            Assert.DoesNotContain(",5", md, StringComparison.Ordinal);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>
    /// A custom name is server-controlled text.
    /// A pipe in it would split the row into extra columns and silently shift every cell after it, which is a table a player could corrupt by renaming a mob.
    /// </summary>
    [Fact]
    public void ServerSuppliedNamesCannotBreakTheTable()
    {
        string md = EntityCommand.BuildListingMarkdown(
            [Row(1, "Villager", 3.0, "evil | name | here")]);

        string row = md.Split('\n').First(l => l.Contains("evil", StringComparison.Ordinal));

        // Five cells, not seven: the pipes the "player" typed did not become column separators.
        Assert.Equal(5, row.Split(" | ", StringSplitOptions.None).Length);
        Assert.DoesNotContain("evil | name", row, StringComparison.Ordinal);
    }

    /// <summary>
    /// The plain-text fallback aligns the table rather than leaving the pipes on screen, so a host with no Markdown renderer still gets columns.
    /// </summary>
    [Fact]
    public void PlainTextFallback_AlignsTheColumns()
    {
        string md = EntityCommand.BuildListingMarkdown(
            [Row(1, "Cow", 3.0), Row(22222, "Nautilus", 41.5)]);

        string plain = ManCommand.PlainText(md);

        Assert.DoesNotContain('|', plain);

        // Everything below the rule under the header is a data row.
        // The tally line above the table names every type too, so matching on a type name alone would find the wrong line.
        string[] lines = [.. plain.Split('\n')];
        int rule = Array.FindIndex(lines, l => l.Trim().Length > 3 && l.Trim().All(c => c == '-'));
        Assert.True(rule > 0, "the table has no rule under its header");

        string[] rows = [.. lines.Skip(rule + 1).Where(l => l.Trim().Length > 0)];
        string first = rows.First(l => l.Contains("Cow", StringComparison.Ordinal));
        string second = rows.First(l => l.Contains("Nautilus", StringComparison.Ordinal));

        // The type column starts at the same offset on both rows, which is the whole point of aligning.
        Assert.Equal(first.IndexOf("Cow", StringComparison.Ordinal),
                     second.IndexOf("Nautilus", StringComparison.Ordinal));
    }
}
