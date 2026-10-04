using DMCBK.Core.Beacon;
using DMCBK.Core.Configuration;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Configuration: <c>beacon.toml</c> host-allowlist schema (generate-with-commented-defaults on first run, never rewrite on plain load, warn-and-ignore unknown keys like the core loader) plus <c>scripts/</c> discovery beside the configurations folder.
/// </summary>
public sealed class ConfigTests : IDisposable
{
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (string root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort: temp cleanup must never fail a test.
            }
        }
    }

    private string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "mcc-beacon-p7cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    private string NewConfigurationsFolder()
    {
        string folder = Path.Combine(NewRoot(), "configurations");
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void MissingBeaconToml_GeneratedWithCommentedDefaults()
    {
        string folder = NewConfigurationsFolder();
        var loader = new BeaconConfigLoader(folder);

        BeaconConfigLoadResult result = loader.Load();

        Assert.True(result.Generated);
        Assert.Empty(result.Config.Net.AllowedHosts);
        string path = ConfigurationPaths.BeaconFile(folder);
        Assert.True(File.Exists(path));
        string text = File.ReadAllText(path);
        Assert.Contains("[Net]", text, StringComparison.Ordinal);
        Assert.Contains("AllowedHosts", text, StringComparison.Ordinal);
        Assert.Contains("#", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainLoad_NeverRewrites()
    {
        string folder = NewConfigurationsFolder();
        var loader = new BeaconConfigLoader(folder);
        loader.Load();
        string path = ConfigurationPaths.BeaconFile(folder);
        byte[] before = File.ReadAllBytes(path);

        BeaconConfigLoadResult reread = loader.Load();

        Assert.False(reread.Generated);
        Assert.Empty(reread.Warnings);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Same(loader.Current, reread.Config);
    }

    [Fact]
    public void UnknownKeys_WarnAndIgnored()
    {
        string folder = NewConfigurationsFolder();
        File.WriteAllText(
            ConfigurationPaths.BeaconFile(folder),
            "bogus_root = 1\n[Net]\nAllowedHosts = []\nmystery = \"x\"\n");

        BeaconConfigLoadResult result = new BeaconConfigLoader(folder).Load(generateMissing: false);

        Assert.False(result.Generated);
        Assert.Empty(result.Config.Net.AllowedHosts);
        Assert.Contains(result.Warnings, w => w.Message.Contains("bogus_root", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Message.Contains("Net.mystery", StringComparison.Ordinal));
    }

    [Fact]
    public void AllowedHosts_Validated_NormalizedWarned()
    {
        string folder = NewConfigurationsFolder();
        File.WriteAllText(
            ConfigurationPaths.BeaconFile(folder),
            "[Net]\nAllowedHosts = [\"example.com\", \"Shop.EU-1.example.org\", "
            + "\"https://evil.example/\", \"bad host\", \"\", \"example.com:443\", \"a/b\", "
            + "\"  padded.example  \", \"example.com\"]\n");

        BeaconConfigLoadResult result = new BeaconConfigLoader(folder).Load(generateMissing: false);

        Assert.Equal(
            new[] { "example.com", "shop.eu-1.example.org", "padded.example" },
            result.Config.Net.AllowedHosts);
        Assert.Equal(5, result.Warnings.Count);
        Assert.Contains(result.Warnings, w => w.Message.Contains("https://evil.example/", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Message.Contains("example.com:443", StringComparison.Ordinal));
    }

    [Fact]
    public void Discovery_OneFileOneScript()
    {
        string root = NewRoot();
        string folder = Path.Combine(root, "configurations");
        Directory.CreateDirectory(folder);
        string scripts = Path.Combine(root, "scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "shop.bcn"), "# beacon 1\n");
        File.WriteAllText(Path.Combine(scripts, "greeter.bcn"), "# beacon 1\n");
        File.WriteAllText(Path.Combine(scripts, "README.txt"), "not a script\n");
        Directory.CreateDirectory(Path.Combine(scripts, "lib"));
        File.WriteAllText(Path.Combine(scripts, "lib", "nested.bcn"), "# beacon 1\n");

        IReadOnlyList<BeaconScriptFile> found = BeaconScriptDiscovery.Discover(folder);

        Assert.Equal(["greeter", "shop"], found.Select(f => f.ScriptId));
        Assert.All(found, f => Assert.StartsWith(scripts, f.Path, StringComparison.Ordinal));
    }

    [Fact]
    public void Discovery_MissingScriptsDir_Empty()
    {
        string folder = NewConfigurationsFolder();
        Assert.Empty(BeaconScriptDiscovery.Discover(folder));
    }

    [Fact]
    public void SecretsProof_AccountsUntouchedByConfigAndDiscovery()
    {
        string root = NewRoot();
        string folder = Path.Combine(root, "configurations");
        Directory.CreateDirectory(folder);
        string accounts = Path.Combine(folder, "accounts.toml");
        byte[] sentinel = "config-secrets-sentinel"u8.ToArray();
        File.WriteAllBytes(accounts, sentinel);

        var loader = new BeaconConfigLoader(folder);
        loader.Load();
        loader.Load();
        BeaconScriptDiscovery.Discover(folder);

        Assert.Equal(sentinel, File.ReadAllBytes(accounts));
    }

    [Fact]
    public void BeaconPaths_ResolverShape()
    {
        string folder = NewConfigurationsFolder();
        Assert.Equal(
            Path.Combine(folder, "beacon.toml"),
            ConfigurationPaths.BeaconFile(folder));
        Assert.Equal(
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(folder))!, "scripts"),
            ConfigurationPaths.ScriptsDir(folder));
        Assert.Equal(
            Path.Combine(folder, "beacon"),
            ConfigurationPaths.BeaconDir(folder));
    }
}
