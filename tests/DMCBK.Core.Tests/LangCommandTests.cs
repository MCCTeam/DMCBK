using System.Globalization;
using DMCBK.Core;
using DMCBK.Core.Commands;
using DMCBK.Core.Configuration;
using DMCBK.Core.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The <c>lang</c> command and the write-back behind it.
/// <para>
/// The rule under test is narrow and load-bearing: setting the language edits ONE line of <c>client.toml</c> and leaves every other byte, including every comment the user wrote, exactly as it was.
/// Re-serializing the file would have been less code and would have quietly deleted their notes.
/// </para>
/// </summary>
public sealed class LangCommandTests : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
    private readonly CultureInfo? _corpusCulture = McStrings.Culture;

    /// <summary>Restores the process-wide culture the command deliberately changes.</summary>
    public void Dispose()
    {
        McStrings.Culture = _corpusCulture;
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _culture;
    }

    #region ClientLanguageWriter: the byte-preservation promise

    [Fact]
    public void Rewrite_ChangesOnlyTheLanguageLine()
    {
        const string Original = """
            # my own note at the top
            [Connection]
            Host = "example.org"

            [Localization]
            # Fill in with in-game locale code
            Language = "en_us"
            # Load translations applied to MCC when available.
            LoadMccTranslation = true

            [Logging]
            Level = "info"

            """;

        string rewritten = ClientLanguageWriter.Rewrite(Original, "de");

        Assert.Equal(Original.Replace("Language = \"en_us\"", "Language = \"de\"", StringComparison.Ordinal), rewritten);
        Assert.Contains("# my own note at the top", rewritten, StringComparison.Ordinal);
        Assert.Contains("# Load translations applied to MCC when available.", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void Rewrite_KeepsAnInlineCommentOnTheLine()
    {
        string rewritten = ClientLanguageWriter.Rewrite(
            "[Localization]\nLanguage = \"en_us\" # picked by hand\n", "pt-BR");

        Assert.Equal("[Localization]\nLanguage = \"pt-BR\"  # picked by hand\n", rewritten);
    }

    [Fact]
    public void Rewrite_IgnoresAKeyOfTheSameNameInAnotherTable()
    {
        const string Original = """
            [Something]
            Language = "keep-me"

            [Localization]
            Language = "en_us"

            """;

        string rewritten = ClientLanguageWriter.Rewrite(Original, "fr");

        Assert.Contains("Language = \"keep-me\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("Language = \"fr\"", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void Rewrite_InsertsTheKeyUnderAnExistingTableThatLacksIt()
    {
        string rewritten = ClientLanguageWriter.Rewrite(
            "[Localization]\nLoadResourcePackTranslations = true\n\n[Logging]\nLevel = \"info\"\n", "de");

        Assert.Equal(
            "[Localization]\nLanguage = \"de\"\nLoadResourcePackTranslations = true\n\n[Logging]\nLevel = \"info\"\n",
            rewritten);
    }

    [Fact]
    public void Rewrite_AppendsTheTableWhenTheFileHasNone()
    {
        Assert.Equal(
            "[Logging]\nLevel = \"info\"\n[Localization]\nLanguage = \"de\"\n",
            ClientLanguageWriter.Rewrite("[Logging]\nLevel = \"info\"\n", "de"));
    }

    [Fact]
    public void Rewrite_KeepsCrlfLineEndings()
    {
        string rewritten = ClientLanguageWriter.Rewrite(
            "[Localization]\r\nLanguage = \"en_us\"\r\n", "de");

        Assert.Equal("[Localization]\r\nLanguage = \"de\"\r\n", rewritten);
    }

    #endregion
    #region MccConfigurationLoader.TrySaveLanguage

    [Fact]
    public void TrySaveLanguage_RewritesTheGeneratedFileAndReloadsWithTheNewValue()
    {
        using var folder = new TempConfigFolder();
        var loader = new MccConfigurationLoader(folder.Path, loggerFactory: NullLoggerFactory.Instance);
        loader.Load(generateMissing: true);

        string before = File.ReadAllText(folder.ClientFile);
        Assert.True(loader.TrySaveLanguage("de", out string? error), error);

        string after = File.ReadAllText(folder.ClientFile);
        Assert.Equal(
            before.Replace("Language = \"auto\"", "Language = \"de\"", StringComparison.Ordinal),
            after);
        Assert.Equal("de", new MccConfigurationLoader(folder.Path, loggerFactory: NullLoggerFactory.Instance)
            .Load(generateMissing: true).Config.Localization.Language);
    }

    #endregion
    #region the command

    [Fact]
    public async Task Lang_WithNoArgument_ReportsBothLanguageAxes()
    {
        await using Client client = BuildClient(new MccConfiguration
        {
            Localization = new LocalizationConfig { Language = "de" },
            ClientSettings = new ClientSettingsConfig { Locale = "auto" },
        });

        CmdResult result = await client.Commands.DispatchAsync("lang");

        Assert.Equal(CmdStatus.Done, result.Status);
        Assert.Contains("UI language: de", result.Message, StringComparison.Ordinal);
        Assert.Contains("Locale sent to the server: auto (sending de)", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lang_WithAnUnknownTag_RefusesAndWritesNothing()
    {
        using var folder = new TempConfigFolder();
        new MccConfigurationLoader(folder.Path, loggerFactory: NullLoggerFactory.Instance).Load(generateMissing: true);
        string before = File.ReadAllText(folder.ClientFile);

        await using Client client = BuildClient(new MccConfiguration { SourceFolder = folder.Path });
        CmdResult result = await client.Commands.DispatchAsync("lang qq-zz");

        Assert.Equal(CmdStatus.Fail, result.Status);
        Assert.Contains("qq-zz", result.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(folder.ClientFile));
    }

    [Fact]
    public async Task Lang_WithATag_SavesItAndAppliesItLive()
    {
        using var folder = new TempConfigFolder();
        new MccConfigurationLoader(folder.Path, loggerFactory: NullLoggerFactory.Instance).Load(generateMissing: true);

        await using Client client = BuildClient(new MccConfiguration { SourceFolder = folder.Path });
        CmdResult result = await client.Commands.DispatchAsync("lang de");

        Assert.Equal(CmdStatus.Done, result.Status);
        Assert.Contains("Language = \"de\"", File.ReadAllText(folder.ClientFile), StringComparison.Ordinal);
        Assert.Equal("de", client.UiCulture.Name);

        // The hint matters: settings comments are frozen at generation time, so the language does not reach an existing file until the user asks.
        Assert.Contains("plugins settings all regen", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lang_AfterAChange_ReportsTheLanguageInForceRatherThanTheStartupSnapshot()
    {
        using var folder = new TempConfigFolder();
        new MccConfigurationLoader(folder.Path, loggerFactory: NullLoggerFactory.Instance).Load(generateMissing: true);

        // The snapshot the client was built from says auto and stays saying it: it is immutable.
        await using Client client = BuildClient(new MccConfiguration { SourceFolder = folder.Path });
        await client.Commands.DispatchAsync("lang de");

        CmdResult result = await client.Commands.DispatchAsync("lang");

        Assert.Equal(CmdStatus.Done, result.Status);
        Assert.Contains("UI language: de", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("UI language: auto", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lang_Auto_IsStoredAsAutoNotAsAResolvedTag()
    {
        using var folder = new TempConfigFolder();
        new MccConfigurationLoader(folder.Path, loggerFactory: NullLoggerFactory.Instance).Load(generateMissing: true);

        await using Client client = BuildClient(new MccConfiguration { SourceFolder = folder.Path });
        await client.Commands.DispatchAsync("lang auto");

        Assert.Contains("Language = \"auto\"", File.ReadAllText(folder.ClientFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lang_WithoutAConfigurationFolder_SaysSoRatherThanGuessing()
    {
        await using Client client = BuildClient(new MccConfiguration());

        CmdResult result = await client.Commands.DispatchAsync("lang de");

        Assert.Equal(CmdStatus.Fail, result.Status);
        Assert.Contains("configuration folder", result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A client over a configuration snapshot.
    /// The endpoint and account are what <see cref="ClientBuilder.Build"/> insists on; nothing here ever connects.
    /// </summary>
    private static Client BuildClient(MccConfiguration config)
        => new ClientBuilder().UseCommands().UseBeacon().UseConfiguration(config with
        {
            ResolvedHost = "localhost",
            ResolvedPort = 25565,
            ResolvedVersion = "auto",
            ResolvedAccount = new ConfiguredAccount { Name = "Tester", Kind = MccAccountKind.Offline },
        }).UseModule<DMCBK.Core.Configuration.IConfigurationStorage>(_ => new MccConfigurationLoader(config.SourceFolder ?? Path.GetTempPath())).Build();

    /// <summary>A throwaway configurations folder with a freshly generated set of files.</summary>
    private sealed class TempConfigFolder : IDisposable
    {
        public TempConfigFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcc-lang-cfg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string ClientFile => ConfigurationPaths.ClientFile(Path);

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
    #endregion
}
