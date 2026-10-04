using System.Globalization;
using System.Text;
using DMCBK.Core.Commands.ArgumentTypes;
using DMCBK.Core.Configuration;
using DMCBK.Core.Localization;
using DMCBK.Core.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Umpk.Commands;

namespace DMCBK.Core.Commands.Impl;

/// <summary>
/// The <c>lang</c> command: report the language MCC is reading in, or change it.
/// <para>
/// Three settings used to mention language and only one of them did anything.
/// There is one now, and this is the command that shows what it resolved to and what each loaded plugin can offer in it.
/// Changing it writes <c>Localization.Language</c> back into <c>client.toml</c> (one line, every other byte untouched) and applies it live to MCC's own corpus and to every loaded plugin's string table.
/// Settings files already on disk keep the comments they were generated with, which is the repository's rule for every generated file, so the reply points at <c>plugins settings all regen</c>.
/// </para>
/// </summary>
public sealed class LangCommand : CommandBase
{
    /// <inheritdoc/>
    public override string CmdName => "lang";

    /// <inheritdoc/>
    public override string CmdDesc => LangCommandText.Desc;

    /// <inheritdoc/>
    public override string CmdUsage => LangCommandText.Usage;

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Client;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new(string.Empty, "show the UI language, the server locale, and plugin coverage"),
        new("<tag>", "set the UI language, for example de or pt-BR"),
        new("auto", "follow the operating system"),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["lang", "lang de", "lang auto"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["plugins", "reload"];

    /// <inheritdoc/>
    public override string? ManTopic => "config";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .Executes(ctx => Show(ctx.Source))
            .ThenArgument("tag", Arguments.QuotableString(), a => a
                .Executes(ctx => Set(ctx.Source, ctx.GetArgument<string>("tag"))))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private int Show(CommandContext ctx)
    {
        string configured = ctx.Config?.Localization.Language ?? UiCulture.Auto;
        CultureInfo resolved = UiCulture.Resolve(configured);

        var sb = new StringBuilder();
        sb.Append(LangCommandText.Current(configured, Describe(resolved)));

        // The locale announced to the server is a different axis: a server-side plugin acts on it, MCC does not render anything from it.
        // Showing both here is the point of the command.
        string locale = ctx.Config?.ClientSettings.Locale ?? UiCulture.Auto;
        sb.Append('\n').Append(LangCommandText.ServerLocale(
            locale,
            UiCulture.IsAuto(locale) ? UiCulture.ToMinecraftTag(resolved) : locale));

        if (ctx.Client.PluginHost is not { } host)
            return ctx.Result.Ok(sb.ToString());

        IReadOnlyList<PluginLanguageInfo> plugins = host.Languages();
        if (plugins.Count == 0)
        {
            sb.Append('\n').Append(LangCommandText.NoPlugins);
            return ctx.Result.Ok(sb.ToString());
        }

        sb.Append('\n').Append(LangCommandText.PluginsHeader(plugins.Count));
        foreach (PluginLanguageInfo plugin in plugins)
        {
            sb.Append('\n').Append(plugin.Active is null
                ? LangCommandText.PluginNoLanguages(plugin.Id)
                : LangCommandText.PluginRow(
                    plugin.Id,
                    string.Join(", ", plugin.Languages),
                    plugin.Active,
                    plugin.MissingKeys));
        }

        return ctx.Result.Ok(sb.ToString());
    }

    private int Set(CommandContext ctx, string tag)
    {
        string requested = tag.Trim();
        bool auto = UiCulture.IsAuto(requested);
        if (!auto && !UiCulture.TryParseTag(requested, out _))
            return ctx.Result.Fail(LangCommandText.UnknownTag(requested));

        string stored = auto ? UiCulture.Auto : requested;
        if (ctx.Config?.SourceFolder is not { } folder)
            return ctx.Result.Fail(LangCommandText.NoConfig);

        var loader = ctx.Client.GetModule<IConfigurationStorage>();
        if (!loader.TrySaveLanguage(stored, out string? error))
            return ctx.Result.Fail(LangCommandText.SaveFailed(error ?? string.Empty));

        // Live, so the next line printed is already in the new language.
        // The plugin host retargets every loaded plugin's lang/ table on top of what Apply does for the host's own text.
        CultureInfo resolved = UiCulture.Apply(stored);
        ctx.Client.UiCulture = resolved;
        ctx.Client.PluginHost?.UseCulture(resolved);

        // The snapshot the commands read is what `lang` with no argument reports, so it moves with the file.
        // Without this the very next `lang` still names the language the client started with.
        if (ctx.Config is { } current)
        {
            ctx.Commands.ReloadConfiguration(
                current with { Localization = current.Localization with { Language = stored } });
        }

        return ctx.Result.Ok(
            LangCommandText.Changed(stored, Describe(resolved)) + "\n" + LangCommandText.RegenHint);
    }

    /// <summary>The culture as a reader recognises it. The invariant culture has no name, so it says so.</summary>
    private static string Describe(CultureInfo culture)
        => culture.Name.Length == 0 ? "en" : culture.Name;
}
