using DMCBK.Core.Configuration;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// CLI-binding coverage: positional/flag overrides beat config, dotted <c>--section.setting=value</c> overrides beat positional (precedence config &lt; positional &lt; dotted), the offline <c>-</c> sentinel, and dotted path binding + unknown-path tolerance.
/// </summary>
public sealed class ConfigurationOverrideTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "mcc-ovr-tests", Guid.NewGuid().ToString("N"));

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

    private MccConfiguration Load(ConfigurationOverrides overrides)
        => new MccConfigurationLoader(_folder).Load(overrides).Config;

    private MccConfiguration Load(ConfigurationOverrides overrides, List<ConfigurationWarning> warnings)
    {
        ConfigurationLoadResult result = new MccConfigurationLoader(_folder).Load(overrides);
        warnings.AddRange(result.Warnings);
        return result.Config;
    }

    [Fact]
    public void Positional_OverridesConfigVersion()
    {
        // Config default version is "auto"; a positional -v pins it.
        MccConfiguration config = Load(new ConfigurationOverrides { Version = "1.8" });
        Assert.Equal("1.8", config.ResolvedVersion);
    }

    /// <summary>
    /// The setting is spelled AutoConnect in the file and auto-connect on the command line, and the documented spelling has to be the one that binds.
    /// </summary>
    [Theory]
    [InlineData("connection.auto-connect")]
    [InlineData("connection.autoconnect")]
    [InlineData("Connection.Auto-Connect")]
    public void Dotted_AutoConnect_BindsWithOrWithoutTheHyphen(string path)
    {
        var warnings = new List<ConfigurationWarning>();
        MccConfiguration config = Load(
            new ConfigurationOverrides { Dotted = [new KeyValuePair<string, string>(path, "false")] }, warnings);

        Assert.False(config.Connection.AutoConnect);
        Assert.DoesNotContain(warnings, w => w.Message.Contains("Unknown override", StringComparison.Ordinal));
    }

    [Fact]
    public void AutoConnect_DefaultsToOn()
        => Assert.True(Load(new ConfigurationOverrides()).Connection.AutoConnect);

    [Fact]
    public void Dotted_Beats_Positional()
    {
        MccConfiguration config = Load(new ConfigurationOverrides
        {
            Version = "1.12.2",
            Dotted = [new KeyValuePair<string, string>("connection.version", "1.16.5")],
        });

        Assert.Equal("1.16.5", config.ResolvedVersion);
    }

    [Fact]
    public void Positional_OverridesAddressAndUsername()
    {
        MccConfiguration config = Load(new ConfigurationOverrides
        {
            Username = "SteveCLI",
            Address = "play.example.net:25599",
        });

        Assert.Equal("play.example.net", config.ResolvedHost);
        Assert.Equal(25599, config.ResolvedPort);
        Assert.Equal("SteveCLI", config.ResolvedAccount.Login);
    }

    [Fact]
    public void DashPassword_ForcesOffline_WhenNoAuthModeGiven()
    {
        // "<user> -" means offline.
        // Still honored.
        MccConfiguration config = Load(new ConfigurationOverrides
        {
            Username = "Steve",
            Password = "-",
        });

        Assert.Equal(MccAccountKind.Offline, config.ResolvedAccount.Kind);
    }

    [Fact]
    public void ExplicitAuthMode_WinsOverDashPassword_AndWarns()
    {
        // The sentinel used to silently downgrade an explicit --auth to offline, which then failed against an online-mode server as an opaque connect error.
        // The explicit flag now wins.
        var warnings = new List<ConfigurationWarning>();
        MccConfiguration config = Load(
            new ConfigurationOverrides
            {
                Username = "steve@example.com",
                AuthMode = "microsoft",
                Password = "-",
            },
            warnings);

        Assert.Equal(MccAccountKind.MicrosoftDeviceCode, config.ResolvedAccount.Kind);
        Assert.Contains(warnings, w => w.Message.Contains("offline password sentinel", StringComparison.Ordinal));
    }

    [Fact]
    public void AuthMode_WithoutDashPassword_SetsOnlineKind()
    {
        MccConfiguration config = Load(new ConfigurationOverrides
        {
            Username = "steve@example.com",
            AuthMode = "microsoft",
        });

        Assert.Equal(MccAccountKind.MicrosoftDeviceCode, config.ResolvedAccount.Kind);
    }

    [Fact]
    public void Dotted_BindsFeatureGate()
    {
        MccConfiguration config = Load(new ConfigurationOverrides
        {
            Dotted = [new KeyValuePair<string, string>("gameplay.terrain", "true")],
        });

        Assert.True(config.Gameplay.Terrain);
    }

    [Fact]
    public void Dotted_UnknownPath_WarnsAndIgnores()
    {
        ConfigurationLoadResult result = new MccConfigurationLoader(_folder).Load(new ConfigurationOverrides
        {
            Dotted = [new KeyValuePair<string, string>("connection.nonsense", "x")],
        });

        Assert.Contains(result.Warnings, w => w.Message.Contains("nonsense"));
    }

    #region The log-file override paths the file sink already honoured

    [Fact]
    public void Dotted_BindsLoggingSaveColorCodes()
    {
        // The field was honoured by the file sink but had no CLI override path, so it could only ever be set by editing the file.
        ConfigurationLoadResult result = new MccConfigurationLoader(_folder).Load(new ConfigurationOverrides
        {
            Dotted = [new KeyValuePair<string, string>("logging.savecolorcodes", "true")],
        });

        Assert.True(result.Config.Logging.SaveColorCodes);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Dotted_BindsLoggingLogFile()
    {
        ConfigurationLoadResult result = new MccConfigurationLoader(_folder).Load(new ConfigurationOverrides
        {
            Dotted =
            [
                new KeyValuePair<string, string>("logging.logtofile", "true"),
                new KeyValuePair<string, string>("logging.logfile", "m4-evidence.txt"),
            ],
        });

        Assert.True(result.Config.Logging.LogToFile);
        Assert.Equal("m4-evidence.txt", result.Config.Logging.LogFile);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void BindablePaths_CoverEveryLoggingFileSinkKnob()
    {
        // Guards the removed unreachable field from coming back: every field the file sink reads must be reachable from the CLI, not only from the config file.
        foreach (string path in new[]
                 {
                     "logging.logtofile", "logging.logfile", "logging.prependtimestamp", "logging.savecolorcodes",
                 })
            Assert.Contains(path, ClientTomlOverrideBinder.BindablePaths);
    }

    #endregion
    #region The chat signature-standing toggles

    [Fact]
    public void SignatureStandingToggles_HaveHonestDefaults()
    {
        MccConfiguration config = Load(new ConfigurationOverrides());

        // Signed-and-checked standings are marked by default, matching the legacy client.
        Assert.True(config.Chat.Signature.MarkLegallySignedMsg);
        Assert.True(config.Chat.Signature.MarkIllegallySignedMsg);

        // Uncheckable chat is its own standing and is marked by default.
        Assert.True(config.Chat.Signature.MarkUnverifiedMsg);

        // Unsigned chat is NOT marked by default: on an offline-mode server every player message is insecure, so marking them all would put a marker on every line.
        Assert.False(config.Chat.Signature.MarkInsecureMsg);
    }
    #endregion
}
