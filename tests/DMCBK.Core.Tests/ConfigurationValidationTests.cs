using DMCBK.Core.Configuration;
using DMCBK.Core.Configuration.Toml;
using DMCBK.Core.Localization;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// Pure-validator coverage: the re-homed OnSettingUpdate clamps and derivations (cooldown/timeout/movement clamps, language normalization, badpacket-forces-terrain), enum tolerance, and account/server resolution.
/// Runs directly against the internal validator with constructed file models (no disk, no session).
/// </summary>
public sealed class ConfigurationValidationTests
{
    private static ConfigurationValidationResult Validate(
        ClientTomlFile? client = null, AccountsTomlFile? accounts = null, ServersTomlFile? servers = null)
        => ConfigurationValidation.Validate(client ?? new(), accounts ?? new(), servers ?? new(), sourceFolder: null);

    [Fact]
    public void MessageCooldown_Negative_ClampsToZero()
    {
        var client = new ClientTomlFile();
        client.Chat.MessageCooldown = -5;
        ConfigurationValidationResult result = Validate(client);

        Assert.Equal(0, result.Config.Chat.MessageCooldownSeconds);
        Assert.Contains(result.Warnings, w => w.Message.Contains("MessageCooldown"));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(40000, 32767)]
    [InlineData(200, 200)]
    public void MaxChatMessageLength_ClampsToRange(int input, int expected)
    {
        var client = new ClientTomlFile();
        client.Chat.MaxChatMessageLength = input;
        Assert.Equal(expected, Validate(client).Config.Chat.MaxChatMessageLength);
    }

    [Fact]
    public void TcpTimeout_BelowOne_ClampsToOne()
    {
        var client = new ClientTomlFile();
        client.Connection.TcpTimeout = 0;
        Assert.Equal(1, Validate(client).Config.Connection.TcpTimeoutSeconds);
    }

    [Fact]
    public void MovementSpeed_BelowOne_ClampsToOne()
    {
        var client = new ClientTomlFile();
        client.Gameplay.MovementSpeed = 0;
        Assert.Equal(1, Validate(client).Config.Gameplay.MovementSpeed);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(3, 3)]
    [InlineData(6, 5)]
    public void Diagnostics_KeepSessions_ClampsToOneThroughFive(int input, int expected)
    {
        var client = new ClientTomlFile();
        client.Diagnostics.KeepSessions = input;

        Assert.Equal(expected, Validate(client).Config.Diagnostics.KeepSessions);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(3, 3)]
    [InlineData(21, 20)]
    public void Diagnostics_MaxCaptureMegabytes_ClampsToOneThroughTwenty(int input, int expected)
    {
        var client = new ClientTomlFile();
        client.Diagnostics.MaxCaptureMegabytes = input;

        Assert.Equal(expected, Validate(client).Config.Diagnostics.MaxCaptureMegabytes);
    }

    [Fact]
    public void Plugins_CrashBudget_DefaultsToTenInSixtySeconds()
    {
        PluginsConfig plugins = Validate(new ClientTomlFile()).Config.Plugins;

        Assert.Equal(10, plugins.CrashThreshold);
        Assert.Equal(60, plugins.CrashWindowSeconds);
    }

    /// <summary>
    /// Unlike its neighbours the crash budget is NOT clamped: zero is the documented way to count exceptions and never disable a plugin, so there is no floor to clamp to.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(5000, 5000)]
    public void Plugins_CrashThreshold_IsTakenAsWritten(int input, int expected)
    {
        var client = new ClientTomlFile();
        client.Plugins.CrashThreshold = input;

        Assert.Equal(expected, Validate(client).Config.Plugins.CrashThreshold);
    }

    [Theory]
    [InlineData("en-US", "en_us")]
    [InlineData("fr@@!", "fr")]
    [InlineData("zh_CN", "zh_cn")]
    public void Language_IsNormalized(string input, string expected)
    {
        var client = new ClientTomlFile();
        client.Localization.Language = input;
        Assert.Equal(expected, Validate(client).Config.Localization.Language);
    }

    [Fact]
    public void Language_EmptyAfterNormalization_FallsBackToDefault()
    {
        var client = new ClientTomlFile();
        client.Localization.Language = "@@@";
        ConfigurationValidationResult result = Validate(client);

        Assert.Equal(ConfigurationValidation.DefaultLanguage, result.Config.Localization.Language);
        Assert.Contains(result.Warnings, w => w.Message.Contains("Language"));
    }

    [Fact]
    public void Language_DefaultsToAuto()
    {
        ConfigurationValidationResult result = Validate();

        Assert.Equal(UiCulture.Auto, result.Config.Localization.Language);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("AUTO")]
    public void Language_Auto_IsAccepted(string input)
    {
        var client = new ClientTomlFile();
        client.Localization.Language = input;
        ConfigurationValidationResult result = Validate(client);

        Assert.Equal(UiCulture.Auto, result.Config.Localization.Language);
        Assert.DoesNotContain(result.Warnings, w => w.Message.Contains("Language", StringComparison.Ordinal));
    }

    [Fact]
    public void Language_UnknownTag_WarnsAndFallsBackToAuto()
    {
        // Normalization alone would let "qq_zz" through as a well-formed tag naming no language at all.
        var client = new ClientTomlFile();
        client.Localization.Language = "qq_zz";
        ConfigurationValidationResult result = Validate(client);

        Assert.Equal(UiCulture.Auto, result.Config.Localization.Language);
        Assert.Contains(result.Warnings, w => w.Message.Contains("no known language", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadMccTranslation_False_IsReadAsEnglish_WithAnObsoleteWarning()
    {
        var client = new ClientTomlFile();
        client.Localization.LoadMccTranslation = false;
        ConfigurationValidationResult result = Validate(client);

        Assert.Equal("en", result.Config.Localization.Language);
        Assert.Contains(result.Warnings, w => w.Message.Contains("obsolete", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadMccTranslation_False_DoesNotOverrideAnExplicitLanguage()
    {
        // A file that has already moved on to a language keeps it; the retired key only fills a gap.
        var client = new ClientTomlFile();
        client.Localization.Language = "pt_br";
        client.Localization.LoadMccTranslation = false;
        ConfigurationValidationResult result = Validate(client);

        Assert.Equal("pt_br", result.Config.Localization.Language);
        Assert.Contains(result.Warnings, w => w.Message.Contains("obsolete", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadMccTranslation_True_IsObsoleteButChangesNothing()
    {
        var client = new ClientTomlFile();
        client.Localization.LoadMccTranslation = true;
        ConfigurationValidationResult result = Validate(client);

        Assert.Equal(UiCulture.Auto, result.Config.Localization.Language);
        Assert.Contains(result.Warnings, w => w.Message.Contains("obsolete", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadMccTranslation_Absent_SaysNothing()
    {
        var client = new ClientTomlFile();
        client.Localization.LoadMccTranslation = null;

        Assert.DoesNotContain(Validate(client).Warnings, w => w.Message.Contains("LoadMccTranslation", StringComparison.Ordinal));
    }

    [Fact]
    public void TemporaryFixBadpacket_ForcesTerrain()
    {
        var client = new ClientTomlFile();
        client.Gameplay.Terrain = false;
        client.Gameplay.TemporaryFixBadpacket = true;
        ConfigurationValidationResult result = Validate(client);

        Assert.True(result.Config.Gameplay.Terrain);
        Assert.Contains(result.Warnings, w => w.Message.Contains("TemporaryFixBadpacket"));
    }

    /// <summary>
    /// The derivation runs downward here: a TOML file gives every key an explicit value, so Terrain = false beside a defaulted Physics/Pathfinding = true can only mean terrain (and everything that depends on it) is off.
    /// Contrast UMPK's <c>ClientFeatures.Normalized()</c>, which resolves the same dependency upward at <c>Build()</c> because a programmatic caller opts INTO one capability at a time instead of stating every key.
    /// </summary>
    [Fact]
    public void Gameplay_TerrainOff_DerivesPhysicsAndPathfindingOff_WithAWarning()
    {
        var client = new ClientTomlFile();
        client.Gameplay.Terrain = false;
        client.Gameplay.Physics = true;
        client.Gameplay.Pathfinding = true;
        ConfigurationValidationResult result = Validate(client);

        Assert.False(result.Config.Gameplay.Physics);
        Assert.False(result.Config.Gameplay.Pathfinding);

        // One combined warning naming both keys, not two separate ones.
        ConfigurationWarning warning = Assert.Single(
            result.Warnings, w => w.Message.Contains("Gameplay.Physics") || w.Message.Contains("Gameplay.Pathfinding"));
        Assert.Contains("Gameplay.Physics", warning.Message);
        Assert.Contains("Gameplay.Pathfinding", warning.Message);
    }

    [Fact]
    public void Gameplay_TerrainOn_LeavesPhysicsAndPathfindingAlone()
    {
        var client = new ClientTomlFile();
        client.Gameplay.Terrain = true;
        client.Gameplay.Physics = false;
        client.Gameplay.Pathfinding = false;
        ConfigurationValidationResult result = Validate(client);

        Assert.False(result.Config.Gameplay.Physics);
        Assert.False(result.Config.Gameplay.Pathfinding);
        Assert.DoesNotContain(result.Warnings, w => w.Message.Contains("Physics") || w.Message.Contains("Pathfinding"));
    }

    [Fact]
    public void UnknownEnum_FallsBackAndWarns()
    {
        var client = new ClientTomlFile();
        client.Connection.Brand = "definitely-not-a-brand";
        ConfigurationValidationResult result = Validate(client);

        Assert.Equal(BrandKind.Mcc, result.Config.Connection.Brand);
        Assert.Contains(result.Warnings, w => w.Message.Contains("Brand"));
    }

    [Theory]
    [InlineData("microsoft", DmcbkAccountKind.MicrosoftDeviceCode)]
    [InlineData("microsoft-browser", DmcbkAccountKind.MicrosoftBrowser)]
    [InlineData("yggdrasil", DmcbkAccountKind.Yggdrasil)]
    [InlineData("offline", DmcbkAccountKind.Offline)]
    public void AccountKind_Parses(string kind, DmcbkAccountKind expected)
    {
        var accounts = new AccountsTomlFile();
        accounts.Account.Add(new AccountsTomlFile.AccountEntry { Name = "a", Kind = kind, Login = "u" });
        Assert.Equal(expected, Validate(accounts: accounts).Config.ResolvedAccount.Kind);
    }

    [Fact]
    public void ActiveAccount_NotFound_UsesFirstAndWarns()
    {
        var accounts = new AccountsTomlFile { Active = "ghost" };
        accounts.Account.Add(new AccountsTomlFile.AccountEntry { Name = "real", Login = "RealPlayer" });
        ConfigurationValidationResult result = Validate(accounts: accounts);

        Assert.Equal("real", result.Config.ResolvedAccount.Name);
        Assert.Contains(result.Warnings, w => w.Message.Contains("ghost"));
    }

    [Fact]
    public void ActiveServer_OverridesConnectionBlock()
    {
        var client = new ClientTomlFile();
        client.Connection.Host = "fallback.example";
        client.Connection.Port = 25565;

        var servers = new ServersTomlFile { Active = "smp" };
        servers.Server.Add(new ServersTomlFile.ServerEntry
        {
            Name = "smp",
            Host = "play.example.net",
            Port = 12345,
            Version = "1.16.5",
        });

        DmcbkConfiguration config = Validate(client, servers: servers).Config;
        Assert.Equal("play.example.net", config.ResolvedHost);
        Assert.Equal(12345, config.ResolvedPort);
        Assert.Equal("1.16.5", config.ResolvedVersion);
    }

    [Fact]
    public void Connection_InlinePort_IsSplitOut()
    {
        var client = new ClientTomlFile();
        client.Connection.Host = "example.net:25599";
        DmcbkConfiguration config = Validate(client).Config;

        Assert.Equal("example.net", config.Connection.Host);
        Assert.Equal(25599, config.Connection.Port);
        Assert.Equal(25599, config.ResolvedPort);
    }
}
