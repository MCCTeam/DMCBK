using DMCBK.Core;
using DMCBK.Core.Commands;
using DMCBK.Core.Configuration;
using DMCBK.Core.Beacon;
using Umpk.Auth;
using Xunit;

namespace DMCBK.Core.Tests;

public sealed class ScriptsApiTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mcc-scripts-api-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveDocument_BlocksInvalidSourceWithoutChangingTheFile()
    {
        await using Client client = BuildClient();
        string path = WriteScript(client, "broken", "# beacon 1\nshow \"before\"\n");
        ScriptDocument document = await client.Scripts.LoadDocumentAsync("broken");

        ScriptSaveResult result = await client.Scripts.SaveDocumentAsync(
            document, "# beacon 1\nif yes\nshow \"missing end\"\n");

        Assert.Equal(ScriptSaveOutcome.ValidationErrors, result.Outcome);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == BeaconSeverity.Error);
        Assert.Equal("# beacon 1\nshow \"before\"\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task SaveDocument_DetectsExternalChanges_AndAllowsExplicitOverwrite()
    {
        await using Client client = BuildClient();
        string path = WriteScript(client, "conflict", "# beacon 1\nshow \"first\"\n");
        ScriptDocument document = await client.Scripts.LoadDocumentAsync("conflict");
        await File.WriteAllTextAsync(path, "# beacon 1\nshow \"external\"\n");

        ScriptSaveResult conflict = await client.Scripts.SaveDocumentAsync(
            document, "# beacon 1\nshow \"editor\"\n");
        Assert.Equal(ScriptSaveOutcome.ExternalConflict, conflict.Outcome);
        Assert.Contains("external", await File.ReadAllTextAsync(path), StringComparison.Ordinal);

        ScriptSaveResult saved = await client.Scripts.SaveDocumentAsync(
            document, "# beacon 1\nshow \"editor\"\n", overwriteConflict: true);
        Assert.Equal(ScriptSaveOutcome.Saved, saved.Outcome);
        Assert.Contains("editor", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public async Task SaveDocument_AllowsWarnings()
    {
        await using Client client = BuildClient();
        string path = WriteScript(client, "warning", "# beacon 1\nshow \"before\"\n");
        ScriptDocument document = await client.Scripts.LoadDocumentAsync("warning");
        const string source = "# beacon 1\n# wants: missing.capability\nshow \"saved\"\n";

        ScriptSaveResult result = await client.Scripts.SaveDocumentAsync(document, source);

        Assert.Equal(ScriptSaveOutcome.Saved, result.Outcome);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == BeaconSeverity.Warning);
        Assert.Equal(source, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Discover_ReturnsOnlyTopLevelScripts_AndReflectsRunningState()
    {
        await using Client client = BuildClient();
        WriteScript(client, "alpha", "# beacon 1\nshow \"alpha\"\n");
        WriteScript(client, "beta", "# beacon 1\nshow \"beta\"\n");
        string nested = Path.Combine(client.Scripts.ScriptsDirectory!, "nested");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "hidden.mcc"), "# beacon 1\n");

        Assert.True((await client.Scripts.RunFileAsync("alpha")).Success);
        IReadOnlyList<ScriptInfo> scripts = client.Scripts.Discover();

        Assert.Equal(["alpha", "beta"], scripts.Select(script => script.Id));
        Assert.True(Assert.Single(scripts, script => script.Id == "alpha").Running);
        Assert.False(Assert.Single(scripts, script => script.Id == "beta").Running);
    }

    [Fact]
    public async Task Delete_RemovesTheSourceAndStopsItsRunningInstance()
    {
        await using Client client = BuildClient();
        string path = WriteScript(client, "disposable", "# beacon 1\nshow \"running\"\n");
        Assert.True((await client.Scripts.RunFileAsync("disposable")).Success);

        ScriptDeleteResult result = client.Scripts.Delete("disposable");

        Assert.Equal(ScriptDeleteOutcome.Deleted, result.Outcome);
        Assert.True(result.StoppedRunningScript);
        Assert.False(File.Exists(path));
        Assert.DoesNotContain("disposable", client.Scripts.Runtime.Engine.ScriptIds);
        Assert.DoesNotContain(client.Scripts.Discover(), script => script.Id == "disposable");
    }

    [Fact]
    public async Task Delete_RejectsPathsOutsideTheTopLevelScriptsDirectory()
    {
        await using Client client = BuildClient();
        string outside = Path.Combine(Path.GetDirectoryName(client.Scripts.ScriptsDirectory!)!, "outside.mcc");
        await File.WriteAllTextAsync(outside, "# beacon 1\n");

        Assert.Throws<ArgumentException>(() => client.Scripts.Delete("../outside"));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task Delete_ReportsWhenTheScriptNoLongerExists()
    {
        await using Client client = BuildClient();

        ScriptDeleteResult result = client.Scripts.Delete("missing");

        Assert.Equal(ScriptDeleteOutcome.NotFound, result.Outcome);
        Assert.False(result.StoppedRunningScript);
    }

    [Fact]
    public async Task ClientDisposalStopsTheOwnedScriptWatcher()
    {
        Client client = BuildClient();
        Directory.CreateDirectory(client.Scripts.ScriptsDirectory!);
        Assert.True(client.Scripts.SetWatch(true));

        ScriptsApi scripts = client.Scripts;
        await client.DisposeAsync();
        Assert.False(scripts.WatchEnabled);
    }

    private Client BuildClient()
    {
        string configs = Path.Combine(_root, "configurations");
        Directory.CreateDirectory(configs);
        return new ClientBuilder().UseCommands().UseBeacon()
            .UseConfiguration(new MccConfiguration { SourceFolder = configs })
            .UseUsername("Tester")
            .UseServer("localhost")
            .UseHostInterface(new SilentHost())
            .Build();
    }

    private static string WriteScript(Client client, string id, string source)
    {
        string directory = client.Scripts.ScriptsDirectory!;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, id + ".mcc");
        File.WriteAllText(path, source);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class SilentHost : IHostInterface
    {
        public IUserPrompt? Prompt => null;

        public IAuthInteraction? AuthInteraction => null;

        public ICommandOutput? CommandOutput => null;

        public IHostUi? Ui => null;
    }
}
