using DMCBK.Core.Commands;
using DMCBK.Core.Commands.Impl;
using DMCBK.Core.Presentation;
using Umpk.Auth;
using Umpk.Client;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// Unit tests for the <c>scoreboard</c> command's pure rendering: the objective index, the per-objective score table, truncation, and the unknown-name guidance.
/// No session.
/// </summary>
public sealed class ScoreboardCommandTests
{
    private static ObjectiveInfo Objective(
        string name, string display, string renderType, IReadOnlyDictionary<string, int> scores)
        => new(name, display, renderType, scores);

    private static ScoreboardSnapshot Board(params ObjectiveInfo[] objectives)
        => new(objectives, []);

    [Fact]
    public void RenderList_EmptyBoard_AnswersEmptyState()
        => Assert.Equal(
            "· No scoreboard objectives.",
            ScoreboardCommand.RenderList(Board(), GlyphSet.Ascii));

    [Fact]
    public void RenderList_SortsByName_AndCountsEntries()
    {
        var board = Board(
            Objective("kills", "Kills", "Integer", new Dictionary<string, int> { ["Alice"] = 3, ["Bob"] = 1 }),
            Objective("deaths", "deaths", "Integer", new Dictionary<string, int> { ["Alice"] = 1 }));

        string rendered = ScoreboardCommand.RenderList(board, GlyphSet.Ascii);

        Assert.Equal(
            "§eScoreboard objectives (2):§r\n"
            + "  deaths - 1 entry\n"
            + "  Kills §7(kills)§r - 2 entries",
            rendered);
    }

    [Fact]
    public void RenderList_HeartsObjective_CarriesTag()
    {
        var board = Board(Objective("health", "Health", "Hearts", new Dictionary<string, int>()));

        string rendered = ScoreboardCommand.RenderList(board, GlyphSet.Ascii);

        Assert.Contains("[hearts]", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderObjective_SortsHighestFirst_TiesByName()
    {
        var board = Board(Objective("kills", "Kills", "Integer", new Dictionary<string, int>
        {
            ["bob"] = 5,
            ["Alice"] = 10,
            ["cara"] = 5,
        }));

        string rendered = ScoreboardCommand.RenderObjective(board, "kills", all: true, GlyphSet.Ascii);

        Assert.Equal(
            "§eKills §7(kills)§r\n"
            + "  1. Alice - 10\n"
            + "  2. bob - 5\n"
            + "  3. cara - 5",
            rendered);
    }

    [Fact]
    public void RenderObjective_TruncatesToTop15_WithMoreNote()
    {
        var scores = new Dictionary<string, int>();
        for (int i = 1; i <= 17; i++)
            scores[$"p{i:00}"] = 100 - i;

        var board = Board(Objective("kills", "Kills", "Integer", scores));
        string rendered = ScoreboardCommand.RenderObjective(board, "kills", all: false, GlyphSet.Ascii);
        string[] lines = rendered.Split('\n');

        Assert.Equal(17, lines.Length);
        Assert.Contains("p01 - 99", lines[1], StringComparison.Ordinal);
        Assert.Equal("  §7and 2 more (--all shows all)§r", lines[^1]);
    }

    [Fact]
    public void RenderObjective_All_ShowsEveryScore()
    {
        var scores = new Dictionary<string, int>();
        for (int i = 1; i <= 17; i++)
            scores[$"p{i:00}"] = i;

        var board = Board(Objective("kills", "Kills", "Integer", scores));
        string rendered = ScoreboardCommand.RenderObjective(board, "kills", all: true, GlyphSet.Ascii);

        Assert.Equal(18, rendered.Split('\n').Length);
        Assert.DoesNotContain("more", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderObjective_MatchesNameCaseInsensitively()
    {
        var board = Board(Objective("Kills", "Kills", "Integer", new Dictionary<string, int> { ["Alice"] = 1 }));

        string rendered = ScoreboardCommand.RenderObjective(board, "kills", all: true, GlyphSet.Ascii);

        Assert.StartsWith("§eKills", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderObjective_UnknownName_NamesTheKnownOnes()
    {
        var board = Board(
            Objective("kills", "Kills", "Integer", new Dictionary<string, int>()),
            Objective("deaths", "Deaths", "Integer", new Dictionary<string, int>()));

        string rendered = ScoreboardCommand.RenderObjective(board, "bogus", all: false, GlyphSet.Ascii);

        Assert.Equal(
            "Unknown scoreboard objective 'bogus'.\nKnown objectives: deaths, kills.",
            rendered);
    }

    [Fact]
    public void RenderObjective_UnknownName_EmptyBoard_HasNoKnownLine()
    {
        string rendered = ScoreboardCommand.RenderObjective(Board(), "bogus", all: false, GlyphSet.Ascii);

        Assert.Equal("Unknown scoreboard objective 'bogus'.", rendered);
    }

    [Fact]
    public void RenderObjective_NoScores_SaysSo()
    {
        var board = Board(Objective("kills", "Kills", "Integer", new Dictionary<string, int>()));

        string rendered = ScoreboardCommand.RenderObjective(board, "kills", all: false, GlyphSet.Ascii);

        Assert.Equal("Objective 'kills' has no scores yet.", rendered);
    }

    [Fact]
    public async Task Scoreboard_WhenOffline_FailsInsteadOfReading()
    {
        await using Client client = new ClientBuilder().UseCommands().UseBeacon()
            .UseUsername("Tester")
            .UseServer("localhost")
            .UseHostInterface(new OfflineHost())
            .Build();

        CmdResult bare = await client.Commands.DispatchAsync("scoreboard");
        CmdResult detail = await client.Commands.DispatchAsync("scoreboard kills");

        Assert.Equal(CmdStatus.Fail, bare.Status);
        Assert.Equal(CmdStatus.Fail, detail.Status);
    }

    private sealed class OfflineHost : IHostInterface
    {
        public IUserPrompt? Prompt => null;

        public IAuthInteraction? AuthInteraction => null;

        public ICommandOutput? CommandOutput => null;

        public IHostUi? Ui => null;
    }
}
