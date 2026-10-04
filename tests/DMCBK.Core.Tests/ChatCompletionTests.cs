using DMCBK.Core;
using DMCBK.Core.Commands;
using DMCBK.Core.Configuration;
using Umpk.Auth;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// Completion seam: the core <see cref="ChatApi.CompleteAsync"/> surface (server command completion over UMPK's merged entry point) and the local internal-command-tree completion the host merges it with.
/// The interactive suggestion UI is manual-smoke-only (file input cannot drive it); these prove the candidate sources the host feeds into that UI behave as expected.
/// </summary>
public sealed class ChatCompletionTests
{
    private static Client BuildClient()
        => new ClientBuilder().UseCommands().UseBeacon()
            .UseUsername("Tester")
            .UseServer("localhost")
            .UseHostInterface(new StubHost())
            .Build();

    [Fact]
    public async Task Chat_CompleteAsync_WhenNotConnected_ReturnsEmpty()
    {
        await using Client client = BuildClient();
        ChatCompletionResult result = await client.Game.Chat.CompleteAsync("/gamemode ");

        Assert.Empty(result.Suggestions);
        Assert.Same(ChatCompletionResult.Empty, result);
    }

    [Fact]
    public async Task Chat_CompleteAsync_NullInput_Throws()
    {
        await using Client client = BuildClient();
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.Game.Chat.CompleteAsync(null!));
    }

    [Fact]
    public async Task Chat_CompleteAsync_CursorOutOfRange_Throws()
    {
        await using Client client = BuildClient();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.Game.Chat.CompleteAsync("hi", 99));
    }

    [Fact]
    public async Task LocalCommandTree_Completes_InternalCommandNames()
    {
        await using Client client = BuildClient();
        IReadOnlyList<string> candidates = await client.Commands.CompleteAsync("he");

        Assert.Contains("health", candidates);
        Assert.Contains("help", candidates);
    }

    [Fact]
    public async Task LocalCommandTree_StripsPrefix_BeforeCompleting()
    {
        await using Client client = BuildClient();
        IReadOnlyList<string> candidates = await client.Commands.CompleteAsync("/he");

        Assert.Contains("health", candidates);
    }

    // ------------------------------------------------------------------------------------------------- B3: the "//" server-command escape in the completion path.
    //
    // CommandService.HandleInputAsync has always implemented the escape: a line starting with the internal prefix followed by a second '/' loses the prefix character and the rest goes to the server.
    // The completion path did not.
    // Both hosts (RichConsole and TuiCompletion) handed the RAW buffer to ChatApi.CompleteAsync, so "//tel" reached UMPK, which strips a command slash per vanilla and then looked for a command literal named "/tel".
    // Nothing matched, and the symptom read as "server-side completion returns nothing at all" (the live sweep's session.command_tree_* rows on protocol 775).
    //
    // Both halves are pinned below: the text/cursor the library is asked about, AND the range that comes back, which has to be shifted by the number of characters the escape stripped or the popup replaces the wrong span.
    // FakeServerCommands mirrors Umpk.Client's ChatActions.CompleteAsync + ServerCommandTree.CompleteLocally closely enough to reproduce the original failure by itself, which the positive control below asserts. -------------------------------------------------------------------------------------------------

    /// <summary>Applies a candidate exactly as the hosts do (MainTuiView.AcceptSuggestion / ConsoleBuffer.Replace).</summary>
    private static string Splice(string buffer, InputCompletion completion, string candidate)
        => buffer[..completion.Start] + candidate + buffer[completion.End..];

    [Fact]
    public async Task Fake_ReproducesTheOriginalDefect_WhenHandedTheRawEscapedLine()
    {
        // Positive control on the fake itself: it must fail the way the library failed, or the tests below prove nothing.
        // Unescaped works; the raw "//tel" the hosts used to pass returns nothing.
        var server = new FakeServerCommands();

        ChatCompletionResult good = await server.CompleteAsync("/tel", 4, CancellationToken.None);
        ChatCompletionResult raw = await server.CompleteAsync("//tel", 5, CancellationToken.None);

        Assert.Contains(good.Suggestions, s => s.Text == "tell");
        Assert.Empty(raw.Suggestions);
    }

    [Fact]
    public async Task Escape_AsksTheServerAboutTheEscapedText_WithTheCursorMovedWithIt()
    {
        await using Client client = BuildClient();
        var server = new FakeServerCommands();

        await ((CommandService)client.Commands).CompleteInputAsync("//tel", 5, server.CompleteAsync, CancellationToken.None);

        Assert.Equal("/tel", server.LastInput);
        Assert.Equal(4, server.LastCursor);
    }

    [Fact]
    public async Task Escape_CompletesThroughToTheServerSuggestion()
    {
        await using Client client = BuildClient();
        var server = new FakeServerCommands();

        InputCompletion completion =
            await ((CommandService)client.Commands).CompleteInputAsync("//tel", 5, server.CompleteAsync, CancellationToken.None);

        Assert.Contains(completion.Suggestions, s => s.Text == "tell");
    }

    [Fact]
    public async Task Escape_ReturnsTheRangeInTheRawBuffersOwnIndices()
    {
        await using Client client = BuildClient();
        var server = new FakeServerCommands();

        InputCompletion completion =
            await ((CommandService)client.Commands).CompleteInputAsync("//tel", 5, server.CompleteAsync, CancellationToken.None);

        // The library answered against "/tel", so its range was (1,4).
        // Un-shifted that would splice over the second slash and produce "/tell": the suggestion text would look right and the buffer would be corrupted.
        // Against the raw "//tel" the span covering "tel" is (2,5).
        Assert.Equal(2, completion.Start);
        Assert.Equal(5, completion.End);
        Assert.Equal("//tell", Splice("//tel", completion, "tell"));
    }

    [Fact]
    public async Task UnescapedServerCommand_IsUnaffected()
    {
        await using Client client = BuildClient();
        var server = new FakeServerCommands();

        InputCompletion completion =
            await ((CommandService)client.Commands).CompleteInputAsync("/tel", 4, server.CompleteAsync, CancellationToken.None);

        Assert.Equal("/tel", server.LastInput);
        Assert.Equal(4, server.LastCursor);
        Assert.Contains(completion.Suggestions, s => s.Text == "tell");
        Assert.Equal(1, completion.Start);
        Assert.Equal(4, completion.End);
        Assert.Equal("/tell", Splice("/tel", completion, "tell"));
    }

    [Fact]
    public async Task InternalCommandName_KeepsThePrefixCharacterOutOfTheReplacedSpan()
    {
        // Adjacent defect the same fix closes: both hosts used the whitespace-delimited token as the span, so the first token's span started at 0 and applying "health" to "/he" produced "health", losing the prefix that makes it an internal command at all.
        await using Client client = BuildClient();
        var server = new FakeServerCommands();

        InputCompletion completion =
            await ((CommandService)client.Commands).CompleteInputAsync("/he", 3, server.CompleteAsync, CancellationToken.None);

        Assert.Contains(completion.Suggestions, s => s.Text == "health");
        Assert.Equal(1, completion.Start);
        Assert.Equal(3, completion.End);
        Assert.Equal("/health", Splice("/he", completion, "health"));
    }

    [Fact]
    public async Task ArgumentToken_ReplacesOnlyThatToken()
    {
        await using Client client = BuildClient();
        var server = new FakeServerCommands();

        InputCompletion completion =
            await ((CommandService)client.Commands).CompleteInputAsync("//gamemode cre", 14, server.CompleteAsync, CancellationToken.None);

        Assert.Equal("/gamemode cre", server.LastInput);
        Assert.Equal(13, server.LastCursor);
        Assert.Contains(completion.Suggestions, s => s.Text == "creative");
        Assert.Equal(11, completion.Start);
        Assert.Equal(14, completion.End);
        Assert.Equal("//gamemode creative", Splice("//gamemode cre", completion, "creative"));
    }

    [Fact]
    public async Task CursorInsideTheEscape_CompletesNothing()
    {
        await using Client client = BuildClient();
        var server = new FakeServerCommands();

        InputCompletion completion =
            await ((CommandService)client.Commands).CompleteInputAsync("//tel", 1, server.CompleteAsync, CancellationToken.None);

        Assert.Empty(completion.Suggestions);
        Assert.Equal(1, completion.Start);
        Assert.Equal(1, completion.End);
    }

    [Fact]
    public async Task BackslashPrefix_CompletesInternalCommandsAndLeavesThePrefixAlone()
    {
        await using Client client = BuildClientWithPrefix(InternalCommandPrefix.Backslash);
        var server = new FakeServerCommands();

        InputCompletion completion =
            await ((CommandService)client.Commands).CompleteInputAsync("\\he", 3, server.CompleteAsync, CancellationToken.None);

        // The hosts used to gate on a literal leading '/', so a configured backslash prefix got no suggestions at all; now the configured prefix decides.
        Assert.Contains(completion.Suggestions, s => s.Text == "health");
        Assert.Equal(1, completion.Start);
        Assert.Equal("\\health", Splice("\\he", completion, "health"));
        Assert.Null(server.LastInput);
    }

    [Fact]
    public async Task BackslashPrefix_SlashLineStillReachesTheServerUnstripped()
    {
        await using Client client = BuildClientWithPrefix(InternalCommandPrefix.Backslash);
        var server = new FakeServerCommands();

        InputCompletion completion =
            await ((CommandService)client.Commands).CompleteInputAsync("/tel", 4, server.CompleteAsync, CancellationToken.None);

        Assert.Equal("/tel", server.LastInput);
        Assert.Equal(4, server.LastCursor);
        Assert.Equal("/tell", Splice("/tel", completion, "tell"));
    }

    [Fact]
    public async Task NonePrefix_CompletesEveryLineAsAnInternalCommand()
    {
        await using Client client = BuildClientWithPrefix(InternalCommandPrefix.None);
        var server = new FakeServerCommands();

        InputCompletion completion =
            await ((CommandService)client.Commands).CompleteInputAsync("he", 2, server.CompleteAsync, CancellationToken.None);

        Assert.Contains(completion.Suggestions, s => s.Text == "health");
        Assert.Equal(0, completion.Start);
        Assert.Equal("health", Splice("he", completion, "health"));
    }

    [Fact]
    public async Task NonePrefix_SlashLineIsAServerCommandWithNoStripping()
    {
        await using Client client = BuildClientWithPrefix(InternalCommandPrefix.None);
        var server = new FakeServerCommands();

        InputCompletion completion =
            await ((CommandService)client.Commands).CompleteInputAsync("/tel", 4, server.CompleteAsync, CancellationToken.None);

        Assert.Equal("/tel", server.LastInput);
        Assert.Equal(4, server.LastCursor);
        Assert.Equal(1, completion.Start);
        Assert.Equal("/tell", Splice("/tel", completion, "tell"));
    }

    [Fact]
    public async Task PlainChat_IsNotCompleted()
    {
        await using Client client = BuildClient();
        var server = new FakeServerCommands();

        InputCompletion completion =
            await ((CommandService)client.Commands).CompleteInputAsync("hello there", 11, server.CompleteAsync, CancellationToken.None);

        Assert.Empty(completion.Suggestions);
        Assert.Null(server.LastInput);
    }

    // ------------------------------------------------------------------------------------------------- CompleteInputAsync used to complete the internal tree at the END of the classified text no matter where the cursor actually sat, because it went through CompleteAsync(string, ct), which always completes at text.Length. Typing "healthx" and moving the cursor back to right after "he" should still surface "health"/"help": completing at the CURSOR sees "he" and matches; completing at the end (the bug) sees "healthx", which matches no root command and returns nothing. -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Completion_AtAMidLineCursor_CompletesTheTokenUnderTheCursor()
    {
        await using Client client = BuildClient();
        var server = new FakeServerCommands();

        InputCompletion completion =
            await ((CommandService)client.Commands).CompleteInputAsync("/healthx", 3, server.CompleteAsync, CancellationToken.None);

        Assert.Contains(completion.Suggestions, s => s.Text == "health");
    }

    private static Client BuildClientWithPrefix(InternalCommandPrefix prefix)
        => new ClientBuilder().UseCommands().UseBeacon()
            .UseConfiguration(new DmcbkConfiguration
            {
                ResolvedHost = "localhost",
                ResolvedPort = 25565,
                ResolvedVersion = "auto",
                ResolvedAccount = new ConfiguredAccount { Name = "Tester", Kind = DmcbkAccountKind.Offline },
                Permissions = new PermissionsConfig { CommandPrefix = prefix },
            })
            .UseHostInterface(new StubHost())
            .Build();

    /// <summary>
    /// A stand-in for the server-side completion entry point that mirrors the real one closely enough to reproduce the defect: <c>Umpk.Client.Actions.ChatActions.CompleteAsync</c> walks the command tree on the body (the input WITHOUT its leading slash) and shifts the resulting range back by one so it indexes the input it was handed.
    /// A tiny root-literal tree is enough; only the first two tokens are modelled.
    /// </summary>
    private sealed class FakeServerCommands
    {
        private static readonly string[] RootLiterals = ["tell", "teleport", "time", "gamemode"];
        private static readonly string[] GamemodeLiterals = ["creative", "survival", "spectator", "adventure"];

        public string? LastInput { get; private set; }

        public int LastCursor { get; private set; } = -1;

        public Task<ChatCompletionResult> CompleteAsync(string input, int cursor, CancellationToken ct)
        {
            LastInput = input;
            LastCursor = cursor;

            if (!input.StartsWith('/'))
                return Task.FromResult(ChatCompletionResult.Empty);

            string body = input[1..];
            int bodyCursor = Math.Clamp(cursor - 1, 0, body.Length);
            string typed = body[..bodyCursor];

            int space = typed.LastIndexOf(' ');
            string token = space < 0 ? typed : typed[(space + 1)..];
            string[] candidates = space < 0
                ? RootLiterals
                : typed.StartsWith("gamemode ", StringComparison.Ordinal) ? GamemodeLiterals : [];

            var matched = new List<ChatCompletion>();
            foreach (string candidate in candidates)
            {
                if (candidate.StartsWith(token, StringComparison.Ordinal))
                    matched.Add(new ChatCompletion(candidate, null));
            }

            if (matched.Count == 0)
                return Task.FromResult(ChatCompletionResult.Empty);

            // Body coordinates (tokenStart, bodyCursor), shifted by the leading slash the walk dropped.
            int tokenStart = space < 0 ? 0 : space + 1;
            return Task.FromResult(new ChatCompletionResult(tokenStart + 1, bodyCursor + 1, matched));
        }
    }

    private sealed class StubHost : IHostInterface
    {
        public IUserPrompt? Prompt => null;

        public IAuthInteraction? AuthInteraction => null;

        public ICommandOutput? CommandOutput => null;

        public IHostUi? Ui => null;
    }
}
