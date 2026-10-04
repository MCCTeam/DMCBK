using DMCBK.Core.Beacon;
using DMCBK.Core.Commands;
using DMCBK.Core.Configuration;
using Umpk.Commands;
using Xunit;

namespace DMCBK.Core.Tests;

public sealed class ScriptsApiCommandIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dmcbk-script-command-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TypedLoadBindsArgumentsAndReturnsLocalOutput()
    {
        await using Client client = BuildClient();
        Assert.True((await client.Scripts.RunAsync("price", Source("price", "show arg(\"item\")"))).Success);
        CmdResult result = await client.Commands.DispatchAsync("price \"red apple\"");
        Assert.True(result.IsSuccess);
        Assert.Equal("red apple", result.Message);
        Assert.Contains(client.Commands.ListCommands(), command => command.Name == "price");
        Assert.True(client.Scripts.Stop("price"));
        Assert.False((await client.Commands.DispatchAsync("price bread")).IsSuccess);
    }

    [Fact]
    public async Task FileReloadReplacesBodyAndStopAllWithdrawsCommands()
    {
        await using Client client = BuildClient();
        string directory = client.Scripts.ScriptsDirectory!;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "price.bcn");
        await File.WriteAllTextAsync(path, Source("price", "show \"first\""));
        Assert.True((await client.Scripts.RunFileAsync("price")).Success);
        Assert.Equal("first", (await client.Commands.DispatchAsync("price bread")).Message);
        await File.WriteAllTextAsync(path, Source("price", "show \"second\""));
        Assert.True((await client.Scripts.ReloadAsync("price")).Success);
        Assert.Equal("second", (await client.Commands.DispatchAsync("price bread")).Message);
        Assert.True((await client.Scripts.RunAsync("other", Source("other", "show \"other\""))).Success);
        Assert.Equal(2, client.Scripts.StopAll());
        Assert.DoesNotContain(client.Commands.ListCommands(), command => command.Name is "price" or "other");
    }

    [Fact]
    public async Task RuntimeDirectLoadAndDisposalOwnCommandScopes()
    {
        await using Client client = BuildClient();
        Assert.True((await client.Scripts.Runtime.Engine.RunScriptAsync("price", Source("price", "show \"direct\""))).Success);
        Assert.Equal("direct", (await client.Commands.DispatchAsync("price bread")).Message);
        client.Scripts.Dispose();
        Assert.False((await client.Commands.DispatchAsync("price bread")).IsSuccess);
    }

    [Fact]
    public async Task ExistingHostCommandIsPreservedOnScriptCollision()
    {
        await using Client client = BuildClient();
        client.Commands.RegisterHostCommand(new HostPriceCommand());
        BeaconRunResult loaded = await client.Scripts.RunAsync("collision", Source("price", "show \"script\""));
        Assert.True(loaded.Success);
        Assert.Contains(loaded.Diagnostics, diagnostic => diagnostic.Severity == BeaconSeverity.Warning);
        Assert.Equal("host", (await client.Commands.DispatchAsync("price bread")).Message);
        client.Scripts.Stop("collision");
        Assert.Equal("host", (await client.Commands.DispatchAsync("price bread")).Message);
    }

    [Fact]
    public async Task ExistingBuiltinAliasIsPreservedOnScriptCollision()
    {
        await using Client client = BuildClient();
        CmdResult before = await client.Commands.DispatchAsync("advancements list");
        CommandDescriptor descriptor = Assert.Single(client.Commands.DescribeCommands(),
            command => command.Name == "achievement");
        Assert.Contains("advancements", descriptor.Aliases);

        BeaconRunResult loaded = await client.Scripts.RunAsync(
            "collision", Source("advancements", "show \"script override\""));
        Assert.True(loaded.Success);
        Assert.Contains(loaded.Diagnostics, diagnostic => diagnostic.Severity == BeaconSeverity.Warning);
        CmdResult during = await client.Commands.DispatchAsync("advancements list");
        Assert.Equal(before.Status, during.Status);
        Assert.Equal(before.Message, during.Message);
        Assert.DoesNotContain(client.Commands.DescribeCommands(),
            command => command.Name == "advancements");

        client.Scripts.Stop("collision");
        CmdResult after = await client.Commands.DispatchAsync("advancements list");
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Message, after.Message);
        CommandDescriptor remaining = Assert.Single(client.Commands.DescribeCommands(),
            command => command.Name == "achievement");
        Assert.Equal(descriptor.Usage, remaining.Usage);
        Assert.Equal(descriptor.Aliases.ToArray(), remaining.Aliases.ToArray());
    }

    [Fact]
    public async Task FailedReloadWithdrawsOldCommand()
    {
        await using Client client = BuildClient();
        Assert.True((await client.Scripts.RunAsync("price", Source("price", "show \"old\""))).Success);
        Assert.False((await client.Scripts.RunAsync("price", "# beacon 1\nif yes\n")).Success);
        Assert.False((await client.Commands.DispatchAsync("price bread")).IsSuccess);
    }

    [Fact]
    public async Task CommandRuntimeFailureReturnsFailureAndOutput()
    {
        await using Client client = BuildClient();
        Assert.True((await client.Scripts.RunAsync("price", Source("price", "show \"before\"\nassert(no, \"expected failure\")"))).Success);
        CmdResult result = await client.Commands.DispatchAsync("price bread");
        Assert.False(result.IsSuccess);
        Assert.Contains("before", result.Message, StringComparison.Ordinal);
        Assert.Contains("expected failure", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScriptCommandCanDispatchAnotherInternalCommand()
    {
        await using Client client = BuildClient();
        string source = Source("nested", "set answer to mcc \"help\"\nshow \"nested done\"")
            .Replace("# beacon 1", "# beacon 1\n# needs: mcc.run");
        Assert.True((await client.Scripts.RunAsync("nested", source)).Success);
        CmdResult result = await client.Commands.DispatchAsync("nested bread")
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.IsSuccess);
        Assert.Equal("nested done", result.Message);
    }

    [Fact]
    public async Task FailedStartHookWithdrawsCommands()
    {
        await using Client client = BuildClient();
        string source = Source("price", "show \"not callable\"")
            + "on start\nassert(no, \"start failed\")\nend on\n";
        Assert.False((await client.Scripts.RunAsync("price", source)).Success);
        Assert.False((await client.Commands.DispatchAsync("price bread")).IsSuccess);
    }

    private Client BuildClient()
        => new ClientBuilder().UseCommands().UseBeacon()
            .UseConfiguration(new DmcbkConfiguration { SourceFolder = _root })
            .UseServer("localhost").UseUsername("Tester").Build();

    private static string Source(string name, string body)
        => $"# beacon 1\ncommand \"/{name} <item>\"\n{body}\nend command\n";

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class HostPriceCommand : CommandBase
    {
        public override string CmdName => "price";
        public override string CmdDesc => "Test host command.";
        public override string CmdUsage => "price <item>";
        public override void Register(CommandBuilder<CommandContext> builder)
            => builder.Literal("price", node => node.ThenArgument("item", Arguments.QuotableString(),
                argument => argument.Executes(call => call.Source.Result.Ok("host"))));
    }
}
