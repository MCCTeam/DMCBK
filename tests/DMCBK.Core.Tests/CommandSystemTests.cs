using DMCBK.Core;
using DMCBK.Core.Commands;
using DMCBK.Core.Commands.Impl;
using DMCBK.Core.Configuration;
using Umpk.Auth;
using Umpk.Client;
using Umpk.Commands;
using Umpk.Text;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// Exercises the command system without a live server: input routing, feature-gate results, the help tree, per-command parsing, and the four catalogued bug fixes.
/// Commands are dispatched against an unstarted <see cref="Client"/> (so <c>InSession</c> is false); feature-gated commands short-circuit before any session read, which is exactly the behavior under test.
/// </summary>
public sealed class CommandSystemTests
{
    private static Client BuildClient(
        ClientFeatures? features = null, ICommandOutput? output = null)
    {
        var builder = new ClientBuilder().UseCommands().UseBeacon()
            .UseUsername("Tester")
            .UseServer("localhost")
            .UseHostInterface(new TestHost(output));
        if (features is not null)
            builder.UseFeatures(features);

        return builder.Build();
    }

    private static Client BuildClientWithConfig(MccConfiguration config, ICommandOutput? output = null)
        => new ClientBuilder().UseCommands().UseBeacon()
            .UseConfiguration(config)
            .UseHostInterface(new TestHost(output))
            .Build();

    #region Feature gates (FailNeed* preserved)

    [Fact]
    public async Task ChangeSlot_WhenInventoryDisabled_FailsNeedInventory()
    {
        await using Client client = BuildClient(new ClientFeatures { Inventory = false });
        CmdResult result = await client.Commands.DispatchAsync("changeslot 3");
        Assert.Equal(CmdStatus.FailNeedInventory, result.Status);
    }

    [Fact]
    public async Task Effects_WhenEntitiesDisabled_FailsNeedEntity()
    {
        await using Client client = BuildClient(new ClientFeatures { Entities = false });
        CmdResult result = await client.Commands.DispatchAsync("effects");
        Assert.Equal(CmdStatus.FailNeedEntity, result.Status);
    }

    [Theory]
    [InlineData(86399, false)]
    [InlineData(86400, true)]
    [InlineData(200000, true)]
    public void Effects_DayPlusDurations_ReadAsUnlimited(int seconds, bool unlimited)
    {
        bool readsUnlimited =
            EffectsCommand.FormatShortDuration(seconds) == EffectsCommand.FormatShortDuration(-1);
        Assert.Equal(unlimited, readsUnlimited);
    }

    [Fact]
    public async Task BlockInfo_WhenTerrainDisabled_FailsNeedTerrain()
    {
        // A coherent composition: terrain off derives physics and pathfinding off too (the config-loaded path always produces this shape now; see ConfigurationValidation.ValidateGameplay).
        await using Client client = BuildClient(new ClientFeatures { Terrain = false, Physics = false, Pathfinding = false });
        CmdResult result = await client.Commands.DispatchAsync("blockinfo");
        Assert.Equal(CmdStatus.FailNeedTerrain, result.Status);
    }

    #endregion
    #region The four catalogued bug fixes

    [Fact]
    public void ChunkDebugState_SetLoaded_MarksChunkLoaded()
    {
        // Bug 1 (Chunk.cs:266): marking a chunk loaded must set the flag TRUE (legacy set it false).
        var debug = new ChunkDebugState();
        debug.SetLoaded(3, 7);
        Assert.True(debug.TryGet(3, 7, out bool loaded));
        Assert.True(loaded);

        debug.SetLoading(3, 7);
        Assert.True(debug.TryGet(3, 7, out bool loading));
        Assert.False(loading);

        debug.Delete(3, 7);
        Assert.False(debug.TryGet(3, 7, out _));
    }

    [Fact]
    public async Task DropItem_WhenInventoryDisabled_FailsNeedInventory_NotTerrain()
    {
        // Bug 2 (DropItem.cs:48): the inventory gate must yield FailNeedInventory, not FailNeedTerrain.
        await using Client client = BuildClient(new ClientFeatures { Inventory = false });
        CmdResult result = await client.Commands.DispatchAsync("dropitem stone");
        Assert.Equal(CmdStatus.FailNeedInventory, result.Status);
        Assert.NotEqual(CmdStatus.FailNeedTerrain, result.Status);
    }


    [Fact]
    public void Bed_IsBed_RecognizesBedIds()
    {
        // Bug 4 backing: the bed command awaits MoveToAsync (no busy-wait); its bed predicate is testable.
        Assert.True(BedCommand.IsBed("minecraft:red_bed"));
        Assert.True(BedCommand.IsBed("minecraft:white_bed"));
        Assert.False(BedCommand.IsBed("minecraft:stone"));
    }

    #endregion
    #region Input routing

    [Fact]
    public async Task Routing_SlashCommand_Executes()
    {
        await using Client client = BuildClient();
        InputRouting routing = await client.Commands.HandleInputAsync("/set greeting=hi");
        Assert.Equal(InputAction.CommandExecuted, routing.Action);
        Assert.Equal(CmdStatus.Done, routing.Result!.Status);
        Assert.Equal("hi", client.Variables.Get("greeting"));
    }

    [Fact]
    public async Task Routing_DoubleSlashEscape_GoesToServer()
    {
        await using Client client = BuildClient();
        // "//hello" forces server chat; with no live session this reports NotConnected rather than dispatching.
        InputRouting routing = await client.Commands.HandleInputAsync("//hello");
        Assert.Equal(InputAction.NotConnected, routing.Action);
    }

    [Fact]
    public async Task Routing_PlainText_GoesToServer()
    {
        await using Client client = BuildClient();
        InputRouting routing = await client.Commands.HandleInputAsync("just chatting");
        Assert.Equal(InputAction.NotConnected, routing.Action);
    }

    [Fact]
    public async Task Routing_UnknownSlashCommand_FallsThroughToServer()
    {
        await using Client client = BuildClient();
        InputRouting routing = await client.Commands.HandleInputAsync("/thisisnotacommand foo");
        Assert.Equal(InputAction.NotConnected, routing.Action); // fell through to chat, no session
    }

    [Fact]
    public async Task Routing_ExpandsVariablesBeforeDispatch()
    {
        await using Client client = BuildClient();
        await client.Commands.DispatchAsync("set who=World");
        InputRouting routing = await client.Commands.HandleInputAsync("/log hello %who%");
        Assert.Equal(InputAction.CommandExecuted, routing.Action);
        Assert.Equal("hello World", routing.Result!.Message);
    }

    [Fact]
    public async Task Routing_NoneMode_TreatsEveryLineAsCommand()
    {
        MccConfiguration config = BuildConfig(InternalCommandPrefix.None);
        await using Client client = BuildClientWithConfig(config);
        InputRouting routing = await client.Commands.HandleInputAsync("set flag=on");
        Assert.Equal(InputAction.CommandExecuted, routing.Action);
        Assert.Equal("on", client.Variables.Get("flag"));
    }

    #endregion
    #region Help tree

    [Fact]
    public async Task Help_ListsRegisteredCommands()
    {
        await using Client client = BuildClient();
        CmdResult result = await client.Commands.DispatchAsync("help");
        Assert.Equal(CmdStatus.Done, result.Status);
        Assert.Contains("health", result.Message);
        Assert.Contains("inventory", result.Message);
    }

    [Fact]
    public async Task HelpForCommand_RendersUsage()
    {
        await using Client client = BuildClient();
        CmdResult result = await client.Commands.DispatchAsync("help changeslot");
        Assert.Equal(CmdStatus.Done, result.Status);
        Assert.Contains("changeslot", result.Message);
    }

    #endregion
    #region Captured dispatch (buffered hosts)
    [Fact]
    public async Task DispatchCaptured_ReturnsThePrintedBody_AndLeavesHostOutputAlone()
    {
        // man prints its index through the output sink and returns a bare Done: the exact shape that used to reach Discord as just "Done".
        var hostOutput = new BufferedCommandOutput();
        await using Client client = BuildClient(output: hostOutput);

        (CmdResult result, string captured) = await client.Commands.DispatchCapturedAsync("man");

        Assert.Equal(CmdStatus.Done, result.Status);
        Assert.Contains("getting-started", captured);
        Assert.Equal(string.Empty, hostOutput.Text);
    }

    [Fact]
    public async Task DispatchCaptured_MatchesPlainDispatch_OnResult()
    {
        await using Client plain = BuildClient();
        await using Client captured = BuildClient();

        CmdResult expected = await plain.Commands.DispatchAsync("help health");
        (CmdResult actual, _) = await captured.Commands.DispatchCapturedAsync("help health");

        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Message, actual.Message);
    }
    #endregion
    #region Per-command parsing and gutted commands

    [Fact]
    public async Task Set_ParsesNameValue()
    {
        await using Client client = BuildClient();
        CmdResult result = await client.Commands.DispatchAsync("set alpha=beta");
        Assert.Equal(CmdStatus.Done, result.Status);
        Assert.Equal("beta", client.Variables.Get("alpha"));
    }

    [Fact]
    public async Task Set_WithoutEquals_ReportsFormat()
    {
        await using Client client = BuildClient();
        CmdResult result = await client.Commands.DispatchAsync("set noequals");
        Assert.Equal(CmdStatus.Fail, result.Status);
    }

    [Fact]
    public async Task Log_EchoesText()
    {
        await using Client client = BuildClient();
        CmdResult result = await client.Commands.DispatchAsync("log hello world");
        Assert.Equal(CmdStatus.Done, result.Status);
        Assert.Equal("hello world", result.Message);
    }

    /// <summary>
    /// setrnd, execmulti and execif are gone, by request, and gone means GONE: not a tombstone that answers "this was removed", which is what execif used to be.
    /// Nothing owns the names any more, so the input router treats them like any other line it does not recognise and hands them to the server, which is what NotRun means here.
    /// If one is ever re-registered this fails, which is the point.
    /// </summary>
    [Theory]
    [InlineData("setrnd roll 1 to 5")]
    [InlineData("setrnd pick a b c")]
    [InlineData("execmulti log one -> log two")]
    [InlineData("execif true log x")]
    public async Task RemovedCommands_AreNotRegisteredAtAll(string command)
    {
        await using Client client = BuildClient();
        CmdResult result = await client.Commands.DispatchAsync(command);

        Assert.Equal(CmdStatus.NotRun, result.Status);
    }

    [Theory]
    [InlineData("setrnd")]
    [InlineData("execmulti")]
    [InlineData("execif")]
    public async Task RemovedCommands_AreAbsentFromHelp(string name)
    {
        // The bare name would render a help page for anything still registered (see IncompleteCommandTests), so an empty usage listing is the check that the name is not in the tree.
        await using Client client = BuildClient();
        CmdResult result = await client.Commands.DispatchAsync($"help {name}");

        Assert.NotEqual(CmdStatus.Done, result.Status);
    }

    [Fact]
    public async Task TheVariableStoreSurvivesSetRndsRemoval()
    {
        // set and %var% expansion were never setrnd's; only the random-value writer went.
        await using Client client = BuildClient();

        await client.Commands.DispatchAsync("set greeting=hi");

        Assert.Equal("hi", client.Variables.Get("greeting"));
    }

    [Fact]
    public async Task Plugins_ReportsNoneLoaded()
    {
        await using Client client = BuildClient();

        // Attach a host over an empty plugins root; the list command reports no plugins (Done).
        string root = Path.Combine(Path.GetTempPath(), "mcc-plugins-cmd-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _ = new DMCBK.PluginSdk.PluginHost(
            client, root, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            client.Translations, client.Variables);

        CmdResult result = await client.Commands.DispatchAsync("plugins");
        Assert.Equal(CmdStatus.Done, result.Status);

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Plugins_WithoutHost_Fails()
    {
        await using Client client = BuildClient();
        CmdResult result = await client.Commands.DispatchAsync("plugins");
        Assert.Equal(CmdStatus.Fail, result.Status);
    }

    /// <summary>
    /// Bare <c>plugins reload</c> reloads everything.
    /// The literal used to exist with only an id branch behind it, so it parsed and then failed with "Unknown or incomplete command" pointing at the word itself, while IPluginHost had had ReloadAllAsync all along.
    /// </summary>
    [Fact]
    public async Task PluginsReload_WithoutAnId_IsAValidCommand()
    {
        await using Client client = BuildClient();
        string root = Path.Combine(Path.GetTempPath(), "mcc-plugins-reload-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _ = new DMCBK.PluginSdk.PluginHost(
            client, root, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            client.Translations, client.Variables);

        CmdResult result = await client.Commands.DispatchAsync("plugins reload");

        // NotRun is what an unparseable command yields; anything else means the grammar accepted it.
        Assert.NotEqual(CmdStatus.NotRun, result.Status);

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// <c>plugins load</c> absorbed the removed <c>plugin load</c> command, and <c>plugin</c> itself is gone rather than left as a second way in.
    /// </summary>
    [Fact]
    public async Task PluginsLoad_Parses_AndTheOldPluginCommandIsGone()
    {
        await using Client client = BuildClient();
        string root = Path.Combine(Path.GetTempPath(), "mcc-plugins-load-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _ = new DMCBK.PluginSdk.PluginHost(
            client, root, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            client.Translations, client.Variables);

        CmdResult loaded = await client.Commands.DispatchAsync("plugins load /nonexistent/path");
        Assert.NotEqual(CmdStatus.NotRun, loaded.Status);

        CmdResult old = await client.Commands.DispatchAsync("plugin load /nonexistent/path");
        Assert.Equal(CmdStatus.NotRun, old.Status);

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task UnknownCommand_YieldsNotRun()
    {
        await using Client client = BuildClient();
        CmdResult result = await client.Commands.DispatchAsync("definitelynotacommand");
        Assert.Equal(CmdStatus.NotRun, result.Status);
    }

    #endregion
    #region CommandContext as a UMPK ICommandSource

    private static CommandContext BuildContext(Client client, ICommandOutput? output = null)
        => new(
            client,
            output ?? NullCommandOutput.Instance,
            ui: null,
            config: client.Configuration,
            variables: client.Variables,
            features: new ClientFeatures(),
            commands: client.Commands,
            cancellation: CancellationToken.None);

    /// <summary>
    /// The line editor clears the input line on Enter, so without an echo the scrollback holds a block of output with nothing saying what produced it, and a session transcript cannot be read back.
    /// </summary>
    [Fact]
    public async Task ExecutedCommands_AreEchoedIntoTheLog()
    {
        var output = new BufferedCommandOutput();
        await using Client client = BuildClient(output: output);
        client.Commands.EchoCommands = true;

        await client.Commands.HandleInputAsync("/health");

        Assert.Contains(output.Lines, l => Strip(l).Contains("> /health", StringComparison.Ordinal));
    }

    /// <summary>Off by default, so a host that says nothing gets the old behaviour.</summary>
    [Fact]
    public async Task NoEcho_WhenTheHostDidNotAskForIt()
    {
        var output = new BufferedCommandOutput();
        await using Client client = BuildClient(output: output);

        await client.Commands.HandleInputAsync("/health");

        Assert.DoesNotContain(output.Lines, l => Strip(l).Contains("> /health", StringComparison.Ordinal));
    }

    /// <summary>
    /// Chat is never echoed.
    /// The server broadcasts it back, so echoing locally would print every message a user sends twice.
    /// </summary>
    [Fact]
    public async Task Chat_IsNotEchoed()
    {
        var output = new BufferedCommandOutput();
        await using Client client = BuildClient(output: output);
        client.Commands.EchoCommands = true;

        await client.Commands.HandleInputAsync("hello everyone");

        Assert.DoesNotContain(output.Lines, l => Strip(l).Contains("hello everyone", StringComparison.Ordinal));
    }

    /// <summary>
    /// A command that wipes the screen opts out: "> /cc" at the top of a display the user just asked to be empty is one stray line, and the opposite of what they asked for.
    /// </summary>
    [Fact]
    public async Task ScreenClearingCommands_AreNotEchoed()
    {
        var output = new BufferedCommandOutput();
        await using Client client = BuildClient(output: output);
        client.Commands.EchoCommands = true;
        client.Commands.RegisterHostCommand(new SilentEchoTestCommand());

        await client.Commands.HandleInputAsync("/quiettest");

        Assert.DoesNotContain(output.Lines, l => Strip(l).Contains("> /quiettest", StringComparison.Ordinal));
    }

    /// <summary>A command that declares EchoWhenRun false, standing in for the hosts' clear commands.</summary>
    private sealed class SilentEchoTestCommand : CommandBase
    {
        public override string CmdName => "quiettest";

        public override string CmdDesc => "Test command.";

        public override string CmdUsage => "quiettest";

        public override bool EchoWhenRun => false;

        public override void Register(CommandBuilder<CommandContext> builder)
            => builder.Literal(CmdName, l => l.Executes(ctx => ctx.Source.Result.Ok("done")));
    }

    [Fact]
    public async Task CommandContext_ReplyAsync_WritesOneLineToTheHostOutput()
    {
        var output = new BufferedCommandOutput();
        await using Client client = BuildClient(output: output);
        CommandContext ctx = BuildContext(client, output);

        await ctx.ReplyAsync(Component.Text("hello there"));

        Assert.Single(output.Lines);
        Assert.Equal("hello there", output.Lines[0]);
    }

    [Fact]
    public async Task CommandContext_GetService_ResolvesTheClientAndTheVariableStore()
    {
        await using Client client = BuildClient();
        CommandContext ctx = BuildContext(client);

        Assert.Same(client, ctx.GetService<Client>());
        Assert.Same(client.Variables, ctx.GetService<VariableStore>());
        Assert.Same(client.Commands, ctx.GetService<CommandService>());
        Assert.Same(client.Translations, ctx.GetService<ITranslationSource>());
    }

    [Fact]
    public async Task CommandContext_GetService_ResolvesARegistrySuggestionSource()
    {
        await using Client client = BuildClient();
        CommandContext ctx = BuildContext(client);

        IRegistrySuggestionSource? source = ctx.GetService<IRegistrySuggestionSource>();

        Assert.NotNull(source);
        Assert.Same(client.Commands.RegistrySuggestions, source);
        Assert.Empty(source.GetEntries(Umpk.Game.Registries.RegistryIds.Item)); // no live session yet
    }

    [Fact]
    public async Task CommandContext_GetService_ReturnsNullForAnUnknownService()
    {
        await using Client client = BuildClient();
        CommandContext ctx = BuildContext(client);

        Assert.Null(ctx.GetService<string>());
    }

    #endregion
    #region The atomic swap onto UMPK's CommandService

    [Fact]
    public async Task HelpListing_ListsEveryRegisteredCommandOnce()
    {
        // The builtins array has ~40 entries, EVERY ONE of which contributes its own top-level "help" node (via RegisterHelp) alongside its own root command node; UMPK's dispatcher merges same-named top-level literals when the scope's roots are added to the tree.
        // This proves that merge produced one listing row per distinct command, not a duplicate per RegisterHelp call.
        await using Client client = BuildClient();
        CmdResult result = await client.Commands.DispatchAsync("help");
        Assert.Equal(CmdStatus.Done, result.Status);

        // The listing is grouped now, so a row is a two-space-indented line; headings, blank lines and the footer are not rows.
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in result.Message!.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!raw.StartsWith("  ", StringComparison.Ordinal))
                continue;

            string line = Strip(raw).Trim();
            int space = line.IndexOf(' ', StringComparison.Ordinal);
            string name = space < 0 ? line : line[..space];
            Assert.True(names.Add(name), $"'{name}' appeared more than once in the help listing.");
        }

        Assert.True(names.Count >= 35, $"Expected at least 35 distinct commands, saw {names.Count}.");
    }

    [Fact]
    public async Task HelpForCommand_RendersEveryUsageLineOfThatCommand()
    {
        await using Client client = BuildClient();
        CmdResult result = await client.Commands.DispatchAsync("help move");
        Assert.Equal(CmdStatus.Done, result.Status);

        string message = Strip(result.Message!);

        // One row per IDEA, not one per Brigadier branch: the six directions fold into <direction>, and the -f suffix that used to double the row count is stated once under FLAGS.
        Assert.Contains("/move get", message, StringComparison.Ordinal);
        Assert.Contains("/move center", message, StringComparison.Ordinal);
        Assert.Contains("/move <direction>", message, StringComparison.Ordinal);
        Assert.Contains("/move <x> <y> <z>", message, StringComparison.Ordinal);
        Assert.Contains("-f", message, StringComparison.Ordinal);

        // Worked examples and the gate status are the point of the page.
        Assert.Contains("/move 150 80 380", message, StringComparison.Ordinal);
        Assert.Contains("Terrain", message, StringComparison.Ordinal);
        Assert.Contains("Pathfinding", message, StringComparison.Ordinal);

        // _help is an internal redirect node.
        // It is in the tree, and it is not on the page.
        Assert.DoesNotContain("_help", message, StringComparison.Ordinal);
    }

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

    [Fact]
    public async Task HelpForCommand_OmitsTheInternalHelpRedirect()
    {
        await using Client client = BuildClient();
        CmdResult result = await client.Commands.DispatchAsync("help move");
        Assert.Equal(CmdStatus.Done, result.Status);

        // `_help` is the internal node every command mirrors so `<cmd> _help` reaches the same page as `help <cmd>`.
        // It used to be RENDERED, putting two lines of plumbing on all 44 pages: 120 of the help corpus's 460 lines.
        // The redirect still works; it is just not documentation.
        Assert.DoesNotContain("_help", Strip(result.Message!), StringComparison.Ordinal);

        CmdResult viaRedirect = await client.Commands.DispatchAsync("move _help");
        Assert.Equal(CmdStatus.Done, viaRedirect.Status);
    }

    [Fact]
    public async Task ScopedCommand_Disposal_RemovesItFromTheTree()
    {
        await using Client client = BuildClient();
        IDisposable scope = client.Commands.RegisterScopedCommand(new PingTestCommand());

        CmdResult before = await client.Commands.DispatchAsync("pingtest");
        Assert.Equal(CmdStatus.Done, before.Status);

        scope.Dispose();

        CmdResult after = await client.Commands.DispatchAsync("pingtest");
        Assert.Equal(CmdStatus.NotRun, after.Status);
    }

    [Fact]
    public async Task ScopedCommand_Disposal_LeavesTheBuiltinsRegistered()
    {
        await using Client client = BuildClient();
        IDisposable scope = client.Commands.RegisterScopedCommand(new PingTestCommand());
        scope.Dispose();

        CmdResult result = await client.Commands.DispatchAsync("help");
        Assert.Equal(CmdStatus.Done, result.Status);
        Assert.Contains("health", result.Message);
    }

    [Fact]
    public async Task DescribeCommands_CapturesCompleteDynamicMetadata_AndTracksDisposal()
    {
        await using Client client = BuildClient();
        using IDisposable scope = client.Commands.RegisterScopedCommand(new DescriptorTestCommand());

        CommandDescriptor descriptor = Assert.Single(
            client.Commands.DescribeCommands(), row => row.Name == "descriptortest");
        Assert.Equal("Descriptor test.", descriptor.Description);
        Assert.Equal("descriptortest [value]", descriptor.Usage);
        Assert.Equal(CommandCategory.Client, descriptor.Category);
        Assert.Equal(["dt"], descriptor.Aliases.ToArray());
        Assert.Equal(new UsageLine("[value]", "Sets a value."), Assert.Single(descriptor.UsageLines));
        Assert.Equal(new UsageFlag("-f", "Forces it."), Assert.Single(descriptor.Flags));
        Assert.Equal(["descriptortest value"], descriptor.Examples.ToArray());
        Assert.Equal(CommandFeature.Inventory | CommandFeature.Entity, descriptor.Requirements);
        Assert.Equal(["help", "man"], descriptor.RelatedCommands.ToArray());
        Assert.Equal("tui", descriptor.ManualTopic);
        Assert.True(descriptor.ShowInIndex);

        scope.Dispose();
        Assert.DoesNotContain(client.Commands.DescribeCommands(), row => row.Name == "descriptortest");
    }

    [Theory]
    [InlineData(InternalCommandPrefix.Slash, "/")]
    [InlineData(InternalCommandPrefix.Backslash, "\\")]
    [InlineData(InternalCommandPrefix.None, "")]
    public async Task ActivePrefix_ReflectsTheLiveConfiguration(
        InternalCommandPrefix prefix, string expected)
    {
        await using Client client = BuildClientWithConfig(BuildConfig(prefix));
        Assert.Equal(expected, client.Commands.ActivePrefix);
    }

    [Fact]
    public async Task Completion_HonoursARequiresPredicate()
    {
        await using Client client = BuildClient();
        using IDisposable scope = client.Commands.RegisterScopedCommand(new GatedTestCommand());

        IReadOnlyList<string> candidates = await client.Commands.CompleteAsync("gatedt");

        Assert.DoesNotContain("gatedtest", candidates);
    }

    #region Dispatch parity (both dispatch paths)

    [Theory]
    [InlineData("bed bogus", CmdStatus.Fail)]
    [InlineData("bed sleep notanumber", CmdStatus.Fail)]
    [InlineData("set noequals", CmdStatus.Fail)]
    [InlineData("definitelynotacommand", CmdStatus.NotRun)]
    [InlineData("definitelynotacommand foo", CmdStatus.NotRun)]
    [InlineData("bed", CmdStatus.Done)]
    public async Task DispatchParity_Matrix_MatchesAcrossPaths(string command, CmdStatus expected)
    {
        var plainHost = new BufferedCommandOutput();
        await using Client plain = BuildClient(output: plainHost);
        var capturedHost = new BufferedCommandOutput();
        await using Client capturedClient = BuildClient(output: capturedHost);

        CmdResult plainResult = await plain.Commands.DispatchAsync(command);
        (CmdResult capturedResult, string capturedText) =
            await capturedClient.Commands.DispatchCapturedAsync(command);

        Assert.Equal(expected, plainResult.Status);
        Assert.Equal(plainResult.Status, capturedResult.Status);
        Assert.Equal(plainResult.Message, capturedResult.Message);
        Assert.Empty(plainHost.Lines);
        Assert.Empty(capturedHost.Lines);
        Assert.Equal(string.Empty, capturedText);
        if (expected == CmdStatus.Done)
            Assert.DoesNotContain("<--[HERE]", plainResult.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DispatchParity_PrintedBody_RoutingIsolation()
    {
        var plainHost = new BufferedCommandOutput();
        await using Client plain = BuildClient(output: plainHost);
        var capturedHost = new BufferedCommandOutput();
        await using Client capturedClient = BuildClient(output: capturedHost);

        CmdResult plainResult = await plain.Commands.DispatchAsync("man");
        (CmdResult capturedResult, string capturedText) =
            await capturedClient.Commands.DispatchCapturedAsync("man");

        Assert.Equal(CmdStatus.Done, plainResult.Status);
        Assert.Equal(plainResult.Status, capturedResult.Status);
        Assert.Equal(plainResult.Message, capturedResult.Message);
        Assert.Contains("getting-started", plainHost.Text, StringComparison.Ordinal);
        Assert.Equal(plainHost.Text, capturedText);
        Assert.Equal(string.Empty, capturedHost.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatchFault_RaisesOnce_BeforeLocalizedFailure(bool captured)
    {
        var host = new BufferedCommandOutput();
        await using Client client = BuildClient(output: host);
        var probeEx = new InvalidOperationException("boom");
        CommandContext? seen = null;
        string? seenResultAtFault = null;
        using IDisposable scope = client.Commands.RegisterScopedCommand(new ProbeCommand("faultprobe", ctx =>
        {
            seen = ctx;
            ctx.Output.WriteLine("partial-body");
            ctx.Result.Ok("pre-failure-marker");
            throw probeEx;
        }));
        int faults = 0;
        CommandFaultedEventArgs? fault = null;
        client.Commands.CommandFaulted += (sender, args) =>
        {
            faults++;
            fault = args;
            seenResultAtFault = seen?.Result.Message;
        };

        CmdResult result;
        string captureText = string.Empty;
        if (captured)
            (result, captureText) = await client.Commands.DispatchCapturedAsync("faultprobe");
        else
            result = await client.Commands.DispatchAsync("faultprobe");

        Assert.Equal(1, faults);
        Assert.NotNull(fault);
        Assert.NotNull(seen);
        Assert.Same(probeEx, fault!.Exception);
        Assert.Equal("faultprobe", fault!.Line);
        Assert.Equal("faultprobe", fault!.Command?.CmdName);
        Assert.Equal("pre-failure-marker", seenResultAtFault);
        Assert.Equal(CmdStatus.Fail, result.Status);
        Assert.Equal(CommandStrings.Error("boom"), result.Message);
        if (captured)
        {
            Assert.Equal("partial-body", captureText);
            Assert.Equal(string.Empty, host.Text);
        }
        else
            Assert.Equal("partial-body", host.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatchCancellation_PreCancelledToken_PropagatesWithoutFault(bool captured)
    {
        var host = new BufferedCommandOutput();
        await using Client client = BuildClient(output: host);
        using IDisposable scope = client.Commands.RegisterScopedCommand(new ProbeCommand("cancelcheckprobe", ctx =>
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            return ctx.Result.Ok("ok");
        }));
        int faults = 0;
        client.Commands.CommandFaulted += (sender, args) => { faults++; };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        OperationCanceledException thrown;
        if (captured)
        {
            thrown = await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await client.Commands.DispatchCapturedAsync("cancelcheckprobe", cts.Token));
        }
        else
        {
            thrown = await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await client.Commands.DispatchAsync("cancelcheckprobe", cts.Token));
        }

        Assert.Equal(cts.Token, thrown.CancellationToken);
        Assert.Equal(0, faults);
        Assert.Equal(string.Empty, host.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatchCancellation_ThrowingBody_PropagatesWithoutFault(bool captured)
    {
        var host = new BufferedCommandOutput();
        await using Client client = BuildClient(output: host);
        var probeEx = new OperationCanceledException("cancel-boom");
        using IDisposable scope = client.Commands.RegisterScopedCommand(
            new ProbeCommand("cancelthrowprobe", ctx => throw probeEx));
        int faults = 0;
        client.Commands.CommandFaulted += (sender, args) => { faults++; };

        OperationCanceledException thrown;
        if (captured)
        {
            thrown = await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await client.Commands.DispatchCapturedAsync("cancelthrowprobe"));
        }
        else
        {
            thrown = await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await client.Commands.DispatchAsync("cancelthrowprobe"));
        }

        Assert.Same(probeEx, thrown);
        Assert.Equal(0, faults);
        Assert.Equal(string.Empty, host.Text);
    }

    [Fact]
    public async Task Dispatch_NullCommand_ThrowsArgumentNull_OnBothPaths()
    {
        await using Client plain = BuildClient();
        await using Client capturedClient = BuildClient();

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await plain.Commands.DispatchAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await capturedClient.Commands.DispatchCapturedAsync(null!));
    }

    #endregion

    /// <summary>A minimal scoped command for the disposal tests: registers, runs, and comes back off.</summary>
    private sealed class PingTestCommand : CommandBase
    {
        public override string CmdName => "pingtest";

        public override string CmdDesc => "test";

        public override string CmdUsage => "pingtest";

        public override void Register(Umpk.Commands.CommandBuilder<CommandContext> builder)
            => builder.Literal(CmdName, l => l.Executes(ctx => ctx.Source.Result.Ok("pong")));
    }

    /// <summary>A command hidden by a <c>Requires</c> predicate that always refuses, for the completion test.</summary>
    private sealed class GatedTestCommand : CommandBase
    {
        public override string CmdName => "gatedtest";

        public override string CmdDesc => "test";

        public override string CmdUsage => "gatedtest";

        public override void Register(Umpk.Commands.CommandBuilder<CommandContext> builder)
            => builder.Literal(CmdName, l => l.Requires(_ => false).Executes(ctx => ctx.Source.Result.Ok("ran")));
    }

    /// <summary>A single scoped probe whose body is supplied per test, for fault and cancellation characterization.</summary>
    private sealed class ProbeCommand(string name, Func<CommandContext, int> body) : CommandBase
    {
        public override string CmdName => name;

        public override string CmdDesc => "test";

        public override string CmdUsage => name;

        public override void Register(CommandBuilder<CommandContext> builder)
            => builder.Literal(name, l => l.Executes(ctx => body(ctx.Source)));
    }

    private sealed class DescriptorTestCommand : CommandBase
    {
        public override string CmdName => "descriptortest";

        public override string CmdDesc => "Descriptor test.";

        public override string CmdUsage => "descriptortest [value]";

        public override CommandCategory Category => CommandCategory.Client;

        public override IReadOnlyList<string> Aliases => ["dt"];

        public override IReadOnlyList<UsageLine> UsageLines => [new("[value]", "Sets a value.")];

        public override IReadOnlyList<UsageFlag> Flags => [new("-f", "Forces it.")];

        public override IReadOnlyList<string> Examples => ["descriptortest value"];

        public override CommandFeature RequiredFeatures => CommandFeature.Inventory | CommandFeature.Entity;

        public override IReadOnlyList<string> SeeAlso => ["help", "man"];

        public override string? ManTopic => "tui";

        public override void Register(Umpk.Commands.CommandBuilder<CommandContext> builder)
            => builder.Literal(CmdName, literal => literal.Executes(ctx => ctx.Source.Result.Ok("ok")));
    }

    private static MccConfiguration BuildConfig(InternalCommandPrefix prefix)
        => new()
        {
            ResolvedHost = "localhost",
            ResolvedPort = 25565,
            ResolvedVersion = "auto",
            ResolvedAccount = new ConfiguredAccount { Name = "Tester", Kind = MccAccountKind.Offline },
            Permissions = new PermissionsConfig { CommandPrefix = prefix },
        };

    private sealed class TestHost(ICommandOutput? output) : IHostInterface
    {
        public IUserPrompt? Prompt => null;

        public IAuthInteraction? AuthInteraction => null;

        public ICommandOutput? CommandOutput { get; } = output;

        public IHostUi? Ui => null;
    }
    #endregion
}
