using System.Globalization;
using DMCBK.Core;
using DMCBK.Core.Commands;
using DMCBK.Core.Configuration;
using DMCBK.Core.Configuration.Toml;
using DMCBK.Core.Plugins;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging.Abstractions;
using Tomlet;
using Tomlet.Attributes;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The shared comment expander, the culture-aware plugin string table and its coverage report, plugin settings comments resolved out of <c>lang/</c>, and the two settings-rewrite verbs.
/// <para>
/// The load-bearing promise here is COMPATIBILITY: a plugin whose comments are plain English literals, which is every plugin written before this existed, must keep generating exactly the <c>settings.toml</c> it always generated, byte for byte.
/// That is what <see cref="Save_WithoutPlaceholders_IsByteIdenticalToTomlet"/> pins, and it is why the expander only ever touches a comment delimited with <c>$</c>.
/// </para>
/// </summary>
public sealed class PluginTranslationTests
{
    /// <summary>A settings POCO whose comments are corpus placeholders, the new localized form.</summary>
    public sealed class LocalizedSettings
    {
        [TomlInlineComment("$settings.enabled$")]
        public bool Enabled { get; set; } = true;

        [TomlPrecedingComment("$settings.threshold$")]
        public int Threshold { get; set; } = 6;
    }

    /// <summary>A settings POCO with plain literal comments, the shape every existing plugin has.</summary>
    public sealed class LiteralSettings
    {
        [TomlInlineComment("Set to false to disable it without unloading it.")]
        public bool Enabled { get; set; } = true;

        [TomlPrecedingComment("Eat when food (0-20) is at or below this. Clamped to 0-20.")]
        public int Threshold { get; set; } = 6;
    }

    #region ConfigCommentExpander

    [Fact]
    public void Expand_ResolvesAStandalonePlaceholder()
    {
        string expanded = ConfigCommentExpander.Expand(
            "# $settings.threshold$\nThreshold = 6\n", key => key == "settings.threshold" ? "Eat below this." : null);

        Assert.Equal("# Eat below this.\nThreshold = 6\n", expanded);
    }

    [Fact]
    public void Expand_MovesAnInlinePlaceholderAboveItsKey()
    {
        string expanded = ConfigCommentExpander.Expand(
            "Enabled = true # $settings.enabled$\n", _ => "Master switch.");

        Assert.Equal("# Master switch.\nEnabled = true\n", expanded);
    }

    [Fact]
    public void Expand_WritesAnUnresolvedKeyAsItself()
    {
        // Same behaviour as MCC's own corpus: a missing string shows up as its key, never as blank space.
        Assert.Equal("# settings.nope\n", ConfigCommentExpander.Expand("# $settings.nope$\n", _ => null));
    }

    [Theory]
    [InlineData("# a plain english comment\nEnabled = true\n")]
    [InlineData("Enabled = true # trailing prose, no dollars\n")]
    [InlineData("# a $ sign alone is not a placeholder\n")]
    [InlineData("# $two words$ is not a key either\n")]
    [InlineData("Matches = [ \"alert\", \"admin\" ]\n")]
    [InlineData("")]
    public void Expand_LeavesEverythingElseByteForByte(string toml)
    {
        // The resolver would rewrite anything it was asked about, so an unchanged result proves it was never consulted.
        Assert.Equal(toml, ConfigCommentExpander.Expand(toml, _ => "REPLACED"));
    }

    [Fact]
    public void Expand_KeepsTheInputsTrailingNewlineShape()
    {
        Assert.Equal("a = 1", ConfigCommentExpander.Expand("a = 1", _ => null));
        Assert.Equal("a = 1\n", ConfigCommentExpander.Expand("a = 1\n", _ => null));
        Assert.Equal("a = 1\n\n", ConfigCommentExpander.Expand("a = 1\n\n", _ => null));
    }

    [Fact]
    public void DefaultConfigWriter_StillResolvesThroughTheCorpus()
    {
        // The core writer now delegates to the expander; this is the proof its own output did not change shape when the method moved out of it.
        string toml = new DefaultConfigWriter(new ConfigCommentSource()).Serialize(new ClientTomlFile());

        Assert.Contains("[Localization]", toml, StringComparison.Ordinal);
        Assert.DoesNotContain("$Main.Advanced.language$", toml, StringComparison.Ordinal);
    }

    #endregion
    #region PluginLocalization

    [Fact]
    public void Localization_ReadsTheCultureItWasGivenNotTheAmbientOne()
    {
        using var folder = new TempFolder();
        folder.WriteLang("en", "greeting = \"Hello.\"\n");
        folder.WriteLang("de", "greeting = \"Hallo.\"\n");

        Assert.Equal("Hallo.", new PluginLocalization(folder.Path, new CultureInfo("de")).Get("greeting"));
        Assert.Equal("Hello.", new PluginLocalization(folder.Path, new CultureInfo("en")).Get("greeting"));
    }

    [Fact]
    public void Localization_FallsBackThroughTheCultureChainPerKey()
    {
        using var folder = new TempFolder();
        folder.WriteLang("en", "a = \"A\"\nb = \"B\"\n");
        folder.WriteLang("de", "a = \"A-de\"\n");

        var strings = new PluginLocalization(folder.Path, new CultureInfo("de-AT"));

        Assert.Equal("A-de", strings.Get("a"));   // de-AT -> de
        Assert.Equal("B", strings.Get("b"));      // de-AT -> de -> en
        Assert.Equal("c", strings.Get("c"));      // nothing has it: the key itself
    }

    [Fact]
    public void Localization_CultureIsSettableSoLangCanRetargetALoadedPlugin()
    {
        using var folder = new TempFolder();
        folder.WriteLang("en", "greeting = \"Hello.\"\n");
        folder.WriteLang("de", "greeting = \"Hallo.\"\n");

        var strings = new PluginLocalization(folder.Path, new CultureInfo("en"));
        Assert.Equal("Hello.", strings.Get("greeting"));

        strings.Culture = new CultureInfo("de");
        Assert.Equal("Hallo.", strings.Get("greeting"));
    }

    [Fact]
    public void Report_NamesTheShippedTagsTheActiveOneAndWhatItIsMissing()
    {
        using var folder = new TempFolder();
        folder.WriteLang("en", "a = \"A\"\nb = \"B\"\nc = \"C\"\n");
        folder.WriteLang("de", "a = \"A-de\"\n");

        PluginLocalizationReport report =
            new PluginLocalization(folder.Path).Report(new CultureInfo("de"));

        Assert.Equal(["de", "en"], report.Languages);
        Assert.Equal("de", report.Active);
        Assert.Equal(["b", "c"], report.MissingKeys);
    }

    [Fact]
    public void Report_HasNothingMissingWhenTheActiveLanguageIsEnglish()
    {
        using var folder = new TempFolder();
        folder.WriteLang("en", "a = \"A\"\n");

        PluginLocalizationReport report =
            new PluginLocalization(folder.Path).Report(new CultureInfo("en-GB"));

        Assert.Equal("en", report.Active);
        Assert.Empty(report.MissingKeys);
    }

    [Fact]
    public void Report_HasNoActiveLanguageWhenThePluginShipsNone()
    {
        using var folder = new TempFolder();

        PluginLocalizationReport report =
            new PluginLocalization(folder.Path).Report(new CultureInfo("de"));

        Assert.Empty(report.Languages);
        Assert.Null(report.Active);
    }

    #endregion
    #region PluginSettings.Save

    [Fact]
    public void Save_WithoutPlaceholders_IsByteIdenticalToTomlet()
    {
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Path, "settings.toml");
        PluginSettings settings = NewSettings(path, new PluginLocalization(folder.Path));

        settings.Save(new LiteralSettings());

        Assert.Equal(TomletMain.TomlStringFrom(new LiteralSettings()), File.ReadAllText(path));
    }

    [Fact]
    public void Save_ResolvesPlaceholdersThroughThePluginsOwnLangTable()
    {
        using var folder = new TempFolder();
        folder.WriteLang("en", """
            [settings]
            enabled = "Master switch."
            threshold = "Eat below this."
            """);

        string path = Path.Combine(folder.Path, "settings.toml");
        NewSettings(path, new PluginLocalization(folder.Path, new CultureInfo("en"))).Save(new LocalizedSettings());

        string toml = File.ReadAllText(path);
        Assert.Contains("# Master switch.\nEnabled = true\n", toml, StringComparison.Ordinal);
        Assert.Contains("# Eat below this.\nThreshold = 6\n", toml, StringComparison.Ordinal);
        Assert.DoesNotContain("$settings.", toml, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_UsesTheGermanTableWhenTheCultureIsGerman()
    {
        using var folder = new TempFolder();
        folder.WriteLang("en", "[settings]\nenabled = \"Master switch.\"\nthreshold = \"Eat below this.\"\n");
        folder.WriteLang("de", "[settings]\nenabled = \"Hauptschalter.\"\nthreshold = \"Essen ab diesem Wert.\"\n");

        string path = Path.Combine(folder.Path, "settings.toml");
        NewSettings(path, new PluginLocalization(folder.Path, new CultureInfo("de"))).Save(new LocalizedSettings());

        string toml = File.ReadAllText(path);
        Assert.Contains("# Hauptschalter.", toml, StringComparison.Ordinal);
        Assert.Contains("# Essen ab diesem Wert.", toml, StringComparison.Ordinal);
        Assert.DoesNotContain("Master switch.", toml, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_WithNoLangFolder_WritesThePlaceholderKey()
    {
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Path, "settings.toml");
        NewSettings(path, new PluginLocalization(folder.Path)).Save(new LocalizedSettings());

        Assert.Contains("# settings.enabled\n", File.ReadAllText(path), StringComparison.Ordinal);
    }

    #endregion
    #region the host end to end

    /// <summary>
    /// The phase gate: a plugin folder carrying <c>lang/de.toml</c> produces German settings comments when the host's culture is German, with no ambient-culture change anywhere.
    /// </summary>
    [Fact]
    public async Task Host_WithGermanCulture_GeneratesGermanSettingsComments()
    {
        using var fixture = new TranslatedPluginFixture(new CultureInfo("de"));

        PluginActionResult result = await fixture.Host.LoadAllAsync();

        Assert.True(result.Success, result.Message);
        string toml = fixture.ReadSettings();
        Assert.Contains("# Hauptschalter.", toml, StringComparison.Ordinal);
        Assert.DoesNotContain("Master switch.", toml, StringComparison.Ordinal);
        Assert.DoesNotContain("$settings.", toml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_WithEnglishCulture_GeneratesEnglishSettingsComments()
    {
        using var fixture = new TranslatedPluginFixture(new CultureInfo("en"));

        await fixture.Host.LoadAllAsync();

        Assert.Contains("# Master switch.", fixture.ReadSettings(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Languages_ReportsShippedTagsAndTheMissingKeyCount()
    {
        using var fixture = new TranslatedPluginFixture(new CultureInfo("de"));
        await fixture.Host.LoadAllAsync();

        PluginLanguageInfo info = Assert.Single(fixture.Host.Languages());

        Assert.Equal(TranslatedPluginFixture.PluginId, info.Id);
        Assert.Equal(["de", "en"], info.Languages);
        Assert.Equal("de", info.Active);

        // en.toml carries one key de.toml does not (settings.extra).
        Assert.Equal(1, info.MissingKeys);
    }

    [Fact]
    public async Task UseCulture_RetargetsALoadedPluginWithoutReloadingIt()
    {
        using var fixture = new TranslatedPluginFixture(new CultureInfo("en"));
        await fixture.Host.LoadAllAsync();
        Assert.Equal("en", Assert.Single(fixture.Host.Languages()).Active);

        fixture.Host.UseCulture(new CultureInfo("de"));

        Assert.Equal("de", Assert.Single(fixture.Host.Languages()).Active);

        // And a regeneration after the switch writes the new language, which is what /lang promises when it points at `plugins settings all regen`.
        await fixture.Host.RegenerateSettingsAsync(TranslatedPluginFixture.PluginId);
        Assert.Contains("# Hauptschalter.", fixture.ReadSettings(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegenerateSettings_RewritesCommentsAndKeepsValues()
    {
        using var fixture = new TranslatedPluginFixture(new CultureInfo("en"));
        await fixture.Host.LoadAllAsync();

        // The user edits a value and mangles the comments, as a user with an editor would.
        File.WriteAllText(fixture.SettingsPath, "# stale\nEnabled = false\nThreshold = 17\n");

        PluginActionResult result = await fixture.Host.RegenerateSettingsAsync(TranslatedPluginFixture.PluginId);

        Assert.True(result.Success, result.Message);
        string toml = fixture.ReadSettings();
        Assert.Contains("Enabled = false", toml, StringComparison.Ordinal);
        Assert.Contains("Threshold = 17", toml, StringComparison.Ordinal);
        Assert.Contains("# Master switch.", toml, StringComparison.Ordinal);
        Assert.DoesNotContain("# stale", toml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetSettings_ThrowsTheValuesAwayAndWritesDefaults()
    {
        using var fixture = new TranslatedPluginFixture(new CultureInfo("en"));
        await fixture.Host.LoadAllAsync();
        File.WriteAllText(fixture.SettingsPath, "Enabled = false\nThreshold = 17\n");

        PluginActionResult result = await fixture.Host.ResetSettingsAsync(TranslatedPluginFixture.PluginId);

        Assert.True(result.Success, result.Message);
        string toml = fixture.ReadSettings();
        Assert.Contains("Enabled = true", toml, StringComparison.Ordinal);
        Assert.Contains("Threshold = 6", toml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegenerateSettings_RefusesAnUnknownPlugin()
    {
        using var fixture = new TranslatedPluginFixture(new CultureInfo("en"));
        await fixture.Host.LoadAllAsync();

        PluginActionResult result = await fixture.Host.RegenerateSettingsAsync("nope");

        Assert.False(result.Success);
        Assert.Contains("nope", result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A plugin's `/man` index line follows the UI culture.
    /// It used to read the English page outright, so a plugin whose page opened translated still had an English row above it in the index.
    /// </summary>
    /// <remarks>
    /// This drives <c>PluginHost.Summarize</c> directly rather than loading a plugin that declares a page.
    /// <c>ManualCatalog</c> is process-wide, and a registered non-English plugin page would make <c>ManualCatalog.Languages()</c> report a language the CORE manual does not ship every topic in, failing <c>ManualCoverageTests</c> in whichever class happened to run beside it.
    /// </remarks>
    [Fact]
    public void ManualSummary_FollowsTheCulture()
    {
        using var folder = new TempFolder();
        WriteManualPage(folder.Path, "en", "# Translated\n\nThe English summary line.\n");
        WriteManualPage(folder.Path, "de", "# Uebersetzt\n\nDie deutsche Zusammenfassung.\n");

        string summary = PluginHost.Summarize(
            Path.Combine(folder.Path, "man"), "translated", "translated", new CultureInfo("de"));

        Assert.Equal("Die deutsche Zusammenfassung.", summary);
    }

    [Fact]
    public void ManualSummary_FallsBackToEnglishWhenTheCultureHasNoPage()
    {
        using var folder = new TempFolder();
        WriteManualPage(folder.Path, "en", "# Translated\n\nThe English summary line.\n");

        string summary = PluginHost.Summarize(
            Path.Combine(folder.Path, "man"), "translated", "translated", new CultureInfo("de"));

        Assert.Equal("The English summary line.", summary);
    }

    [Fact]
    public void ManualSummary_FallsBackToThePluginIdWhenThereIsNoPageAtAll()
    {
        using var folder = new TempFolder();

        string summary = PluginHost.Summarize(
            Path.Combine(folder.Path, "man"), "translated", "translated", new CultureInfo("de"));

        Assert.Equal("translated", summary);
    }

    private static void WriteManualPage(string pluginFolder, string language, string markdown)
    {
        string directory = Path.Combine(pluginFolder, "man", language);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "translated.md"), markdown);
    }

    #endregion
    #region the command grammar

    [Fact]
    public async Task PluginsSettingsAllRegen_RewritesEveryLoadedPluginsFile()
    {
        using var fixture = new TranslatedPluginFixture(new CultureInfo("en"));
        await fixture.Host.LoadAllAsync();
        File.WriteAllText(fixture.SettingsPath, "# stale\nEnabled = false\nThreshold = 17\n");

        CmdResult result = await fixture.Client.Commands.DispatchAsync("plugins settings all regen");

        Assert.Equal(CmdStatus.Done, result.Status);
        Assert.Contains(TranslatedPluginFixture.PluginId, result.Message, StringComparison.Ordinal);

        string toml = fixture.ReadSettings();
        Assert.Contains("Threshold = 17", toml, StringComparison.Ordinal);
        Assert.Contains("# Master switch.", toml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PluginsSettingsReset_TakesAPluginIdToo()
    {
        using var fixture = new TranslatedPluginFixture(new CultureInfo("en"));
        await fixture.Host.LoadAllAsync();
        File.WriteAllText(fixture.SettingsPath, "Threshold = 17\n");

        CmdResult result = await fixture.Client.Commands.DispatchAsync(
            "plugins settings " + TranslatedPluginFixture.PluginId + " reset");

        Assert.Equal(CmdStatus.Done, result.Status);
        Assert.Contains("Threshold = 6", fixture.ReadSettings(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PluginsSettings_WithoutAVerb_ChangesNothing()
    {
        using var fixture = new TranslatedPluginFixture(new CultureInfo("en"));
        await fixture.Host.LoadAllAsync();
        File.WriteAllText(fixture.SettingsPath, "Threshold = 17\n");

        // The verb is not optional: rewriting a file is destructive enough that "settings all" alone must not pick one of the two meanings on the user's behalf.
        CmdResult result = await fixture.Client.Commands.DispatchAsync("plugins settings all");

        Assert.NotEqual(CmdStatus.Done, result.Status);
        Assert.Equal("Threshold = 17\n", fixture.ReadSettings());
    }

    /// <summary>
    /// <c>ActivateAsync</c> is the only place most plugins ever read <c>context.Strings</c>: the first line they log, the <c>CmdDesc</c> of every command they register.
    /// The host used to assign it after activation returned, so all of those resolved through the empty table and came out as their own key names, which is what made <c>/hello</c> answer with the literal text "hello.reply".
    /// </summary>
    [Fact]
    public async Task Strings_AreTheirOwnTable_DuringActivate()
    {
        string root = Path.Combine(Path.GetTempPath(), "mcc-lang-activate-" + Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(root, "early");
        Directory.CreateDirectory(Path.Combine(folder, PluginLocalization.FolderName));
        File.WriteAllText(
            Path.Combine(folder, "Early.cs"),
            """
            using System.Threading.Tasks;
            using DMCBK.PluginSdk;

            public sealed class EarlyPlugin : IPlugin
            {
                public void Configure(PluginDescriptor descriptor) => descriptor.Id = "early";

                public Task ActivateAsync(PluginContext context)
                {
                    context.Storage.Set("greeting", context.Strings.Get("greeting"));
                    context.Storage.Save();
                    return Task.CompletedTask;
                }
            }
            """);
        File.WriteAllText(
            Path.Combine(folder, PluginManifest.FileName), TestManifest.Upgrade("id = \"early\"\nversion = \"1.0.0\"\nentry = \"Early.cs\"\napi-version = \"1.0\"\nenabled = true\n"));
        File.WriteAllText(
            Path.Combine(folder, PluginLocalization.FolderName, "en.toml"),
            "greeting = \"read at activation\"\n");

        Client client = new ClientBuilder().UseCommands().UseBeacon().UseUsername("Tester").UseServer("localhost").Build();
        try
        {
            var host = new PluginHost(
                client, root, NullLoggerFactory.Instance, client.Translations, client.Variables,
                new CultureInfo("en"));
            host.InstallationSource = _ => Task.FromResult(TestPackages.Read(root));
            await host.LoadAllAsync();

            PluginContext? context = host.GetContext("early");
            Assert.NotNull(context);
            Assert.True(context.Storage.TryGet("greeting", out string? greeting));
            Assert.Equal("read at activation", greeting);
        }
        finally
        {
            await client.DisposeAsync();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static PluginSettings NewSettings(string path, IPluginLocalization strings)
    {
        // PluginSettings' constructor is internal to the SDK; the tests see it through InternalsVisibleTo.
        return (PluginSettings)Activator.CreateInstance(
            typeof(PluginSettings),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: [path, NullLogger.Instance, strings],
            culture: null)!;
    }

    /// <summary>A throwaway folder that can be given <c>lang/&lt;tag&gt;.toml</c> files.</summary>
    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcc-lang-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void WriteLang(string tag, string toml)
        {
            string root = System.IO.Path.Combine(Path, PluginLocalization.FolderName);
            Directory.CreateDirectory(root);
            File.WriteAllText(System.IO.Path.Combine(root, tag + ".toml"), toml);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// A plugins root holding one single-file plugin that ships English and German strings and documents its settings with <c>$settings.*$</c> placeholders, plus a host pinned to a chosen culture.
    /// </summary>
    private sealed class TranslatedPluginFixture : IDisposable
    {
        public const string PluginId = "translated";

        private const string Source = """
            using System.Threading.Tasks;
            using DMCBK.PluginSdk;
            using Tomlet.Attributes;

            public sealed class TranslatedSettings
            {
                [TomlInlineComment("$settings.enabled$")]
                public bool Enabled { get; set; } = true;

                [TomlPrecedingComment("$settings.threshold$")]
                public int Threshold { get; set; } = 6;
            }

            public sealed class TranslatedPlugin : IPlugin
            {
                public void Configure(PluginDescriptor descriptor)
                {
                    descriptor.Id = "translated";
                    descriptor.WithSettings<TranslatedSettings>();
                }

                public Task ActivateAsync(PluginContext context) => Task.CompletedTask;
            }
            """;

        public TranslatedPluginFixture(CultureInfo culture)
        {
            Root = Path.Combine(Path.GetTempPath(), "mcc-plugin-lang-" + Guid.NewGuid().ToString("N"));
            string folder = Path.Combine(Root, PluginId);
            Directory.CreateDirectory(Path.Combine(folder, PluginLocalization.FolderName));

            File.WriteAllText(Path.Combine(folder, "Translated.cs"), Source);
            File.WriteAllText(
                Path.Combine(folder, PluginManifest.FileName), TestManifest.Upgrade("id = \"translated\"\nversion = \"1.0.0\"\nentry = \"Translated.cs\"\napi-version = \"1\"\nenabled = true\n"));
            File.WriteAllText(
                Path.Combine(folder, PluginLocalization.FolderName, "en.toml"),
                "[settings]\nenabled = \"Master switch.\"\nthreshold = \"Eat below this.\"\nextra = \"Only English has this.\"\n");
            File.WriteAllText(
                Path.Combine(folder, PluginLocalization.FolderName, "de.toml"),
                "[settings]\nenabled = \"Hauptschalter.\"\nthreshold = \"Essen ab diesem Wert.\"\n");

            Client = new ClientBuilder().UseCommands().UseBeacon().UseUsername("Tester").UseServer("localhost").Build();
            Host = new PluginHost(
                Client, Root, NullLoggerFactory.Instance, Client.Translations, Client.Variables, culture);
            Host.InstallationSource = _ => Task.FromResult(TestPackages.Read(Root));
            SettingsPath = Path.Combine(Root, "userdata", "translated", "settings.toml");
        }

        public string Root { get; }

        public string SettingsPath { get; }

        public Client Client { get; }

        public PluginHost Host { get; }

        public string ReadSettings() => File.ReadAllText(SettingsPath);

        public void Dispose()
        {
            Client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
    #endregion
}
