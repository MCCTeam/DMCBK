using DMCBK.Core;
using DMCBK.Core.Commands;
using Umpk.Auth;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// An incomplete internal command is answered here, with its usage, instead of being handed to the server.
/// <para>
/// THE DEFECT.
/// Every parse failure became <see cref="CmdStatus.NotRun"/>, and <c>HandleInputAsync</c> forwards every <c>NotRun</c> in slash mode to the server as a server command.
/// So typing <c>/bed</c> did not produce an MCC message at all: MCC gave up, sent <c>/bed</c> to the server, and what the user read was the SERVER rejecting a command it has never heard of, complete with the server's own caret.
/// Every command whose root literal has no executable branch behaved that way, and the client that owns the command never got to say what it accepts.
/// </para>
/// <para>
/// Legacy did not do this.
/// It branched on whether the parse had consumed any node (Archive/MinecraftClient/McClient.cs:1107-1119): none meant "not mine, treat as chat", and one or more meant "mine, used wrongly", which it answered with the Brigadier message plus <c>GetAllUsageString</c>.
/// That is the behaviour restored here.
/// </para>
/// </summary>
public sealed class IncompleteCommandTests
{
    private static Client BuildClient()
        => new ClientBuilder().UseCommands().UseBeacon()
            .UseUsername("Tester")
            .UseServer("localhost", 25565)
            .UseHostInterface(new SilentHost())
            .Build();

    /// <summary>Strips legacy section codes so an assertion reads the text, not the colours.</summary>
    private static string Strip(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\u00a7' && i + 1 < text.Length)
            {
                i++;
                continue;
            }

            sb.Append(text[i]);
        }

        return sb.ToString();
    }

    /// <summary>A host that prompts for nothing and prints nowhere; these tests read the CmdResult itself.</summary>
    private sealed class SilentHost : IHostInterface
    {
        public IUserPrompt? Prompt => null;

        public IAuthInteraction? AuthInteraction => null;

        public ICommandOutput? CommandOutput => null;

        public IHostUi? Ui => null;
    }

    [Fact]
    public async Task ACommandNameOnItsOwn_IsAHelpRequest()
    {
        // Typing the name alone asks what the command does.
        // It is not a mistake, so it is answered with the help page and a success, not with a parse error: the same page `help bed` renders, header and all.
        await using Client client = BuildClient();
        CmdResult result = await client.Commands.DispatchAsync("bed");

        Assert.Equal(CmdStatus.Done, result.Status);
        Assert.NotNull(result.Message);
        Assert.DoesNotContain("<--[HERE]", result.Message, StringComparison.Ordinal);

        // The page states one row per idea.
        // The two ways of sleeping are still both on it.
        string message = Strip(result.Message!);
        Assert.Contains("bed leave", message, StringComparison.Ordinal);
        Assert.Contains("bed sleep <x> <y> <z>", message, StringComparison.Ordinal);
        Assert.Contains("bed sleep <radius>", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACommandNameOnItsOwn_RendersTheSamePageAsHelp()
    {
        await using Client client = BuildClient();

        CmdResult bare = await client.Commands.DispatchAsync("bed");
        CmdResult viaHelp = await client.Commands.DispatchAsync("help bed");

        Assert.Equal(viaHelp.Message, bare.Message);
    }

    [Fact]
    public async Task BareBed_DoesNotReachTheServer()
    {
        // The routing half: a known command must be answered here, not forwarded.
        // With no session attached, a forwarded line reports NotConnected, so that status IS the forwarding.
        await using Client client = BuildClient();
        InputRouting routing = await client.Commands.HandleInputAsync("/bed");

        Assert.Equal(InputAction.CommandExecuted, routing.Action);
        Assert.Equal(CmdStatus.Done, routing.Result?.Status);
    }

    [Theory]
    [InlineData("bed sleep")]
    [InlineData("bed bogus")]
    [InlineData("bed sleep notanumber")]
    public async Task EveryWayOfGettingBedWrong_AnswersWithTheUsage(string command)
    {
        await using Client client = BuildClient();
        CmdResult result = await client.Commands.DispatchAsync(command);

        Assert.Equal(CmdStatus.Fail, result.Status);

        // A wrong argument no longer answers with the whole subtree.
        // It marks the offending token, offers forms that work, and points at the page: recovery, not a wall.
        string message = Strip(result.Message!);
        Assert.Contains("bed", message, StringComparison.Ordinal);
        Assert.Contains("bed sleep 150 79 380", message, StringComparison.Ordinal);
        Assert.Contains("help bed", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheParseErrorIsKept_NotReplacedByTheUsage()
    {
        // A real misuse (not a bare name) keeps both halves, in legacy's order: what went wrong, then what the command accepts.
        await using Client client = BuildClient();
        CmdResult result = await client.Commands.DispatchAsync("bed bogus");

        string message = Strip(result.Message!);
        int usageAt = message.IndexOf("help bed", StringComparison.Ordinal);
        // Brigadier's own caret and position are gone: the caret sat under the COMMAND NAME rather than under the bad token, which is the opposite of useful.
        // What survives is the reason, under a caret pointing at the token the user actually got wrong.
        Assert.DoesNotContain("<--[HERE]", message, StringComparison.Ordinal);
        Assert.Contains("^", message, StringComparison.Ordinal);
        Assert.True(usageAt > 0, "and a route to the full syntax follows it");
    }

    [Fact]
    public async Task AnUnknownCommand_StillFallsThroughToTheServer()
    {
        // The other side of legacy's branch, and the one that must not regress: a line naming no internal command keeps NotRun so routing hands it over.
        // NotConnected is what forwarding reports with no session attached.
        await using Client client = BuildClient();

        CmdResult dispatched = await client.Commands.DispatchAsync("thisisnotacommand foo");
        Assert.Equal(CmdStatus.NotRun, dispatched.Status);

        InputRouting routing = await client.Commands.HandleInputAsync("/thisisnotacommand foo");
        Assert.Equal(InputAction.NotConnected, routing.Action);
    }

    [Fact]
    public async Task ServerCommandsThatShareNoNameWithUs_AreUntouched()
    {
        // A real server command MCC has no command for: still forwarded, exactly as before.
        await using Client client = BuildClient();

        foreach (string command in new[] { "gamemode creative", "time set day", "say hello" })
        {
            InputRouting routing = await client.Commands.HandleInputAsync($"/{command}");
            Assert.Equal(InputAction.NotConnected, routing.Action);
        }
    }

    [Theory]
    [InlineData("useblock")]
    [InlineData("enchant")]
    [InlineData("recipebook")]
    public async Task OtherRootlessCommands_GetTheSameTreatment(string name)
    {
        // bed was the reported one; the behaviour is general, and these are the other commands whose root literal carries no executable branch of its own.
        // (book is not among them any more: bare `book` now reports the book in your hand.)
        await using Client client = BuildClient();
        CmdResult result = await client.Commands.DispatchAsync(name);

        Assert.Equal(CmdStatus.Done, result.Status);
        Assert.DoesNotContain("<--[HERE]", result.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AValidCommand_IsUnaffected()
    {
        // The usage is appended only on a parse failure; a command that parses answers exactly as it did.
        await using Client client = BuildClient();
        CmdResult result = await client.Commands.DispatchAsync("set greeting=hi");

        Assert.NotEqual(CmdStatus.Fail, result.Status);
        Assert.DoesNotContain("<--[HERE]", result.Message ?? string.Empty, StringComparison.Ordinal);
    }
}
