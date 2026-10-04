using DMCBK.Core.Configuration;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// Loader pipeline coverage: first-run generation (files carry expanded comments; parse-back equals defaults), round-trip, unknown-key tolerance, reload snapshot semantics, write-back only-on-request, and secrets/gitignore emission.
/// Each test uses an isolated temp folder.
/// </summary>
public sealed class ConfigurationLoaderTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "mcc-cfg-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public void Load_FirstRun_GeneratesAllFilesWithComments()
    {
        var loader = new MccConfigurationLoader(_folder);
        ConfigurationLoadResult result = loader.Load(generateMissing: true);

        Assert.True(result.Generated);
        Assert.True(File.Exists(ConfigurationPaths.ClientFile(_folder)));
        Assert.True(File.Exists(ConfigurationPaths.AccountsFile(_folder)));
        Assert.True(File.Exists(ConfigurationPaths.ServersFile(_folder)));
        Assert.True(File.Exists(ConfigurationPaths.GitIgnoreFile(_folder)));

        string clientText = File.ReadAllText(ConfigurationPaths.ClientFile(_folder));
        // Comments are emitted as `#` lines carried over from the ConfigComments corpus.
        Assert.Contains("# Uses more ram, cpu, bandwidth but allows you to move around.", clientText);
        Assert.Contains("# Controls the minimum interval (in seconds) between sending each message", clientText);
        // No unexpanded placeholder tokens survive.
        Assert.DoesNotContain("$Main.Advanced", clientText);
        Assert.DoesNotContain("$Signature", clientText);
    }

    [Fact]
    public void GeneratedAccounts_CarriesACommentedOutExample()
    {
        // The bare `Account = [ ]` is cryptic without TOML experience, so generation includes a commented-out entry.
        // Commented out on purpose: a live example would become the active account and skip the first-run login prompt.
        var loader = new MccConfigurationLoader(_folder);
        loader.Load(generateMissing: true);

        string accountsText = File.ReadAllText(ConfigurationPaths.AccountsFile(_folder));
        Assert.Contains("# Account = [ { Name = ", accountsText, StringComparison.Ordinal);
        Assert.Contains("Account = [ ]", accountsText, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedDefaults_ParseBack_EqualDefaults()
    {
        var loader = new MccConfigurationLoader(_folder);
        MccConfiguration generated = loader.Load(generateMissing: true).Config;

        // A second loader over the now-populated folder must not regenerate and must produce an equal snapshot.
        var reader = new MccConfigurationLoader(_folder);
        ConfigurationLoadResult reread = reader.Load(generateMissing: true);

        Assert.False(reread.Generated);
        Assert.Equal(generated.Gameplay, reread.Config.Gameplay);
        Assert.Equal(generated.Chat, reread.Config.Chat);
        Assert.Equal(generated.ClientSettings, reread.Config.ClientSettings);
        Assert.Equal(generated.Logging, reread.Config.Logging);
        Assert.Equal(generated.Connection, reread.Config.Connection);
        Assert.Empty(reread.Warnings);
    }

    [Fact]
    public void Reconnect_DefaultsAreGenerated_AndParseBack()
    {
        var loader = new MccConfigurationLoader(_folder);
        MccConfiguration generated = loader.Load(generateMissing: true).Config;

        // Legacy default: auto-reconnect off (0 attempts), 5s fixed delay.
        Assert.Equal(0, generated.Connection.Reconnect.MaxAttempts);
        Assert.Equal(5.0, generated.Connection.Reconnect.DelaySeconds);
        Assert.Equal(1.0, generated.Connection.Reconnect.BackoffFactor);

        string clientText = File.ReadAllText(ConfigurationPaths.ClientFile(_folder));
        Assert.Contains("Reconnect", clientText);
        Assert.Contains("MaxAttempts", clientText);
        Assert.DoesNotContain("$Mcc.connection.reconnect", clientText);

        // Enabling it on disk parses back to a live policy.
        string clientPath = ConfigurationPaths.ClientFile(_folder);
        File.WriteAllText(clientPath, File.ReadAllText(clientPath).Replace("MaxAttempts = 0", "MaxAttempts = 4"));
        MccConfiguration reread = new MccConfigurationLoader(_folder).Load(generateMissing: false).Config;
        Assert.Equal(4, reread.Connection.Reconnect.MaxAttempts);
        Assert.NotNull(new ConfigReconnectPolicyProvider(reread.Connection.Reconnect).GetReconnectPolicy());
    }

    [Fact]
    public void Load_ExistingFiles_DoesNotRewriteThem()
    {
        var loader = new MccConfigurationLoader(_folder);
        loader.Load(generateMissing: true);

        string clientPath = ConfigurationPaths.ClientFile(_folder);
        DateTime before = File.GetLastWriteTimeUtc(clientPath);

        // A plain load (and a reload) must never write back.
        var second = new MccConfigurationLoader(_folder);
        second.Load(generateMissing: true);
        second.Reload();

        Assert.Equal(before, File.GetLastWriteTimeUtc(clientPath));
    }

    [Fact]
    public void GitIgnore_IgnoresSecretsAndCache()
    {
        var loader = new MccConfigurationLoader(_folder);
        loader.Load(generateMissing: true);

        string gitignore = File.ReadAllText(ConfigurationPaths.GitIgnoreFile(_folder));
        Assert.Contains("accounts.toml", gitignore);
        Assert.Contains("cache/", gitignore);
    }

    [Fact]
    public void Secrets_LiveInAccountsFile_NotClientFile()
    {
        var loader = new MccConfigurationLoader(_folder);
        loader.Load(generateMissing: true);

        string clientText = File.ReadAllText(ConfigurationPaths.ClientFile(_folder));
        string accountsText = File.ReadAllText(ConfigurationPaths.AccountsFile(_folder));

        // Proxy credentials and the account list belong to the secrets file only.
        Assert.Contains("Proxy", accountsText);
        Assert.Contains("Account", accountsText);
        Assert.DoesNotContain("Proxy", clientText);
        Assert.DoesNotContain("[[Account", clientText);
    }

    [Fact]
    public void Reload_ProducesNewSnapshot_AndFiresEvent()
    {
        var loader = new MccConfigurationLoader(_folder);
        MccConfiguration first = loader.Load(generateMissing: true).Config;

        // Mutate the file on disk (turn a feature gate off, since terrain now defaults on) and reload.
        string clientPath = ConfigurationPaths.ClientFile(_folder);
        File.WriteAllText(clientPath, File.ReadAllText(clientPath).Replace("Terrain = true", "Terrain = false"));

        ConfigurationReloadedEventArgs? captured = null;
        loader.Reloaded += (_, e) => captured = e;
        MccConfiguration second = loader.Reload().Config;

        Assert.NotSame(first, second);
        Assert.True(first.Gameplay.Terrain);
        Assert.False(second.Gameplay.Terrain);
        Assert.NotNull(captured);
        Assert.Same(first, captured!.Previous);
        Assert.Same(second, captured.Current);
    }

    [Fact]
    public void UnknownKey_IsWarnedAndIgnored()
    {
        var loader = new MccConfigurationLoader(_folder);
        loader.Load(generateMissing: true);

        string clientPath = ConfigurationPaths.ClientFile(_folder);
        // A valid-but-unknown key at the document root (before any table header) is warn-and-ignored.
        File.WriteAllText(clientPath, "NotARealSetting = 42\n" + File.ReadAllText(clientPath));

        ConfigurationLoadResult reread = new MccConfigurationLoader(_folder).Load(generateMissing: false);

        Assert.Contains(reread.Warnings, w => w.Message.Contains("NotARealSetting"));
        // Still usable: the known values survive.
        Assert.Equal(25565, reread.Config.ResolvedPort);
    }

    [Fact]
    public void SaveAccount_WritesBackOnlyOnRequest()
    {
        var loader = new MccConfigurationLoader(_folder);
        loader.Load(generateMissing: true);

        loader.SaveAccount(
            new ConfiguredAccount { Name = "alt", Kind = MccAccountKind.Offline, Login = "AltHero" },
            makeActive: true);

        MccConfiguration reread = new MccConfigurationLoader(_folder).Load(generateMissing: false).Config;
        Assert.Equal("alt", reread.Accounts.ActiveAccount);
        Assert.Equal("AltHero", reread.ResolvedAccount.Login);
    }

    [Fact]
    public void SaveServer_UpsertsAndActivates()
    {
        var loader = new MccConfigurationLoader(_folder);
        loader.Load(generateMissing: true);

        loader.SaveServer(
            new ConfiguredServer { Name = "smp", Host = "play.example.net", Port = 25566, Version = "1.21.5" },
            makeActive: true);

        MccConfiguration reread = new MccConfigurationLoader(_folder).Load(generateMissing: false).Config;
        Assert.Equal("smp", reread.Servers.ActiveServer);
        Assert.Equal("play.example.net", reread.ResolvedHost);
        Assert.Equal(25566, reread.ResolvedPort);
        Assert.Equal("1.21.5", reread.ResolvedVersion);
    }
}
