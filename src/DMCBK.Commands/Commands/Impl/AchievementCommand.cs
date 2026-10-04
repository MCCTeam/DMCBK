using System.Globalization;
using System.Text;
using DMCBK.Core.Localization;
using DMCBK.Core.Presentation;
using Umpk.Commands;
using Umpk.Text;

namespace DMCBK.Core.Commands.Impl;

/// <summary>
/// The <c>achievement</c> command: lists achievements/advancements (all, locked or unlocked).
/// Ported from the legacy <c>MinecraftClient/Commands/AchievementCommand.cs</c>.
/// </summary>
public sealed class AchievementCommand : CommandBase
{
    /// <inheritdoc/>
    public override string CmdName => "achievement";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.achievement.desc");

    /// <inheritdoc/>
    public override string CmdUsage => "achievement <list|locked|unlocked|ui>";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Client;

    /// <inheritdoc/>
    public override IReadOnlyList<string> Aliases => [AliasName];

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("list", "every advancement the server sent"),
        new("list <scope>", "only one branch: story, adventure, recipes/tools"),
        new("unlocked", "only completed ones"),
        new("locked", "only incomplete ones"),
        new("ui", CommandStrings.AdvancementUiUsage),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples =>
        ["achievement list", "achievement list story", "achievement locked", "achievement ui"];

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);

        help.ThenLiteral("list", h => h.Executes(ctx => ShowUsage(ctx.Source)))
            .ThenLiteral("locked", h => h.Executes(ctx => ShowUsage(ctx.Source)))
            .ThenLiteral("unlocked", h => h.Executes(ctx => ShowUsage(ctx.Source)))
            .ThenLiteral("ui", h => h.Executes(ctx => ShowUsage(ctx.Source)));

        foreach (string name in new[] { CmdName, AliasName })
        {
            builder.Literal(name, l => l
                .Executes(ctx => ListAchievements(ctx.Source, null))
                .ThenLiteral("ui", h => h.Executes(ctx => Ui(ctx.Source)))
                .ThenLiteral("list", h => h
                    .Executes(ctx => ListAchievements(ctx.Source, null))
                    // A scope is a path prefix: `story`, `adventure`, `recipes/tools`.
                    // It is how a listing of 1584 becomes one a person reads, now that paging is not on the table.
                    .ThenArgument("scope", Arguments.GreedyString(), a => a
                        .Suggests(SuggestScopes)
                        .Executes(ctx => ListAchievements(ctx.Source, null, ctx.GetArgument<string>("scope")))))
                .ThenLiteral("locked", h => h
                    .Executes(ctx => ListAchievements(ctx.Source, false))
                    .ThenArgument("scope", Arguments.GreedyString(), a => a
                        .Suggests(SuggestScopes)
                        .Executes(ctx => ListAchievements(ctx.Source, false, ctx.GetArgument<string>("scope")))))
                .ThenLiteral("unlocked", h => h
                    .Executes(ctx => ListAchievements(ctx.Source, true))
                    .ThenArgument("scope", Arguments.GreedyString(), a => a
                        .Suggests(SuggestScopes)
                        .Executes(ctx => ListAchievements(ctx.Source, true, ctx.GetArgument<string>("scope")))))
                .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
        }
    }

    private static int Ui(CommandContext ctx)
    {
        if (!ctx.InSession)
            return ctx.Result.Fail(CommandText.NotConnected(ctx));
        return ctx.Ui?.TryOpenAdvancementsBrowser() == true
            ? ctx.Result.Ok()
            : ctx.Result.Fail(CommandStrings.UiRequiresTui);
    }

    /// <summary>The alias. "Advancement" is what the game has called these since 1.12.</summary>
    private const string AliasName = "advancements";

    /// <summary>The top-level scopes a vanilla pack has, plus whatever the server actually sent.</summary>
    private static ValueTask SuggestScopes(ICommandContext<CommandContext> ctx, ISuggestionSink sink)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AdvancementInfo advancement in
                 ctx.Source.Run(ct => ctx.Source.Game.Player.GetAdvancementsAsync(ct)).Entries)
        {
            string path = ShortId(advancement.Id);
            int slash = path.IndexOf('/', StringComparison.Ordinal);
            string root = slash > 0 ? path[..slash] : path;
            if (seen.Add(root) && root.StartsWith(sink.Remaining, StringComparison.OrdinalIgnoreCase))
                sink.Suggest(root);
        }

        return ValueTask.CompletedTask;
    }

    /// <param name="scope">The scope value.</param>
    /// <param name="ctx">The execution context.</param>
    /// <param name="completed">null = all, true = unlocked only, false = locked only.</param>
    private static int ListAchievements(CommandContext ctx, bool? completed, string? scope = null)
    {
        if (!ctx.InSession)
            return ctx.Result.Fail(CommandText.NotConnected(ctx));

        AdvancementsSnapshot snapshot = ctx.Run(ct => ctx.Game.Player.GetAdvancementsAsync(ct));
        if (!snapshot.StructuredDataAvailable && snapshot.Entries.Count == 0)
            return ctx.Result.Ok(CommandStrings.AdvancementsUnavailable);

        var items = new List<AdvancementInfo>();
        foreach (AdvancementInfo advancement in snapshot.Entries)
        {
            // Legacy computed completion from the AND-of-ORs requirement groups and treated an empty group set as complete (McClient.ComputeAchievementCompleted, McClient.cs:5041-5060).
            // UMPK projects the criteria totals rather than the groups, so "every criterion obtained" is the same test, and a criteria-less advancement still reads complete.
            bool done = advancement.CriteriaCompleted >= advancement.CriteriaCount;
            if (completed is null || completed == done)
                items.Add(advancement);
        }

        if (scope is { Length: > 0 })
            items.RemoveAll(a => !ShortId(a.Id).StartsWith(scope, StringComparison.OrdinalIgnoreCase));

        if (items.Count == 0)
        {
            return ctx.Result.Ok(completed switch
            {
                true => McStrings.Get("cmd.achievement.none_unlocked"),
                false => McStrings.Get("cmd.achievement.none_locked"),
                _ => McStrings.Get("cmd.achievement.none"),
            });
        }

        items.Sort(static (a, b) => string.Compare(a.Id, b.Id, StringComparison.Ordinal));

        // Progress is over EVERY advancement the server sent, not over the filtered view: "2 of 1584" is the fact a player wants, and it must not change because they typed `locked`.
        int total = snapshot.Entries.Count;
        int completedCount = snapshot.Entries.Count(a => a.CriteriaCompleted >= a.CriteriaCount);
        double fraction = total == 0 ? 0 : (double)completedCount / total;

        string p = ctx.Commands.NoPrefix ? string.Empty : ctx.Commands.Prefix.ToString();
        var sb = new StringBuilder();

        sb.Append("# ").Append(McStrings.Get("cmd.achievement.header")).Append('\n').Append('\n');
        sb.Append(McStrings.Format("cmd.achievement.progress", completedCount, total))
          .Append("  ").Append(ctx.Ui?.FormatProgress(fraction, ctx.Glyphs)
              ?? (fraction * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%").Append('\n').Append('\n');

        // A cap rather than paging: 1584 rows is not a listing anyone reads, and the footer says how to narrow it.
        // Paging was declined, and scoping answers the same need without a stateful cursor.
        List<AdvancementInfo> shown = items.Count > MaxRows ? items.GetRange(0, MaxRows) : items;

        sb.Append("| ").Append(McStrings.Get("cmd.achievement.column_state"))
          .Append(" | ").Append(McStrings.Get("cmd.achievement.column_name"))
          .Append(" | ").Append(McStrings.Get("cmd.achievement.column_id"))
          .Append(" | ").Append(McStrings.Get("cmd.achievement.column_kind")).Append(" |\n");
        sb.Append("| --- | --- | --- | --- |\n");

        foreach (AdvancementInfo advancement in shown)
        {
            bool complete = advancement.CriteriaCompleted >= advancement.CriteriaCount;
            sb.Append("| ").Append(complete ? ctx.Glyphs.AdvancementDone : ctx.Glyphs.AdvancementTodo)
              .Append(" | ").Append(Title(ctx, advancement))
              .Append(" | ").Append(ShortId(advancement.Id))
              .Append(" | ").Append(Kind(ctx, advancement)).Append(" |\n");
        }

        sb.Append('\n').Append(McStrings.Format("cmd.achievement.showing", shown.Count, items.Count));
        if (items.Count > shown.Count)
            sb.Append("  ").Append(McStrings.Format("cmd.achievement.hint_scope", $"{p}achievement list <scope>"));

        if (completed is null)
            sb.Append("  ").Append(McStrings.Format("cmd.achievement.hint_filter", $"{p}achievement locked"));

        DocumentRendering.Write(ctx, sb.ToString());
        return ctx.Result.Set(CmdStatus.Done);
    }

    /// <summary>
    /// How many rows one listing prints.
    /// A vanilla data pack carries 1584 advancements, of which 1459 are generated recipe unlocks; printing all of them is not a listing, it is a wall.
    /// The footer says how to narrow it.
    /// </summary>
    private const int MaxRows = 40;

    /// <summary>The id without its <c>minecraft:</c> namespace, which is the same on almost every row.</summary>
    private static string ShortId(string id)
    {
        int colon = id.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 && id.AsSpan(0, colon).SequenceEqual("minecraft") ? id[(colon + 1)..] : id;
    }

    /// <summary>
    /// The name for a row: the advancement's own title, or for a recipe unlock the name of the thing it unlocks.
    /// </summary>
    /// <remarks>
    /// Recipe advancements have no display block, so they have no title and no frame, and the listing used to print their raw id: 1459 rows of "minecraft:recipes/transportation/oak_boat []".
    /// The recipe is named after its result, and the result's own translation key IS in the vanilla table, so the name is recoverable: "Oak Boat".
    /// That is the difference between an untranslated wall and a readable list.
    /// </remarks>
    private static string Title(CommandContext ctx, AdvancementInfo advancement)
    {
        if (ResolveTitle(ctx.Translations, advancement) is { Length: > 0 } title)
            return title;

        if (RecipeResultName(ctx.Translations, advancement.Id) is { Length: > 0 } recipe)
            return recipe;

        return ShortId(advancement.Id);
    }

    /// <summary>
    /// The translated name of what a recipe advancement unlocks, from the last segment of its id.
    /// Tries the item table then the block table, because a recipe result can be either and the two use different key prefixes.
    /// Null for anything that is not a recipe advancement.
    /// </summary>
    private static string? RecipeResultName(ITranslationSource translations, string id)
    {
        string path = ShortId(id);
        if (!path.StartsWith("recipes/", StringComparison.Ordinal))
            return null;

        int slash = path.LastIndexOf('/');
        string result = slash >= 0 ? path[(slash + 1)..] : path;
        if (result.Length == 0 || result == "root")
            return null;

        foreach (string prefix in new[] { "item.minecraft.", "block.minecraft." })
        {
            if (TryTranslate(translations, prefix + result, out string value))
                return value;
        }

        return null;
    }

    /// <summary>
    /// The kind column: the frame for a displayed advancement, or "recipe" for a recipe unlock.
    /// Never blank, because an empty cell reads as missing data rather than as "this is a recipe".
    /// </summary>
    private static string Kind(CommandContext ctx, AdvancementInfo advancement)
    {
        string frame = FrameName(advancement.Frame);
        if (frame.Length > 0)
            return frame.ToLowerInvariant();

        _ = ctx;
        return ShortId(advancement.Id).StartsWith("recipes/", StringComparison.Ordinal)
            ? McStrings.Get("cmd.achievement.kind_recipe")
            : string.Empty;
    }

    /// <summary>
    /// The advancement frame types, named and numbered as vanilla names and numbers them: <c>AdvancementType.TASK/CHALLENGE/GOAL</c> (<c>advancements/AdvancementType.java:10-13</c>), written to the wire as the enum ordinal by <c>DisplayInfo.serializeToNetwork</c>.
    /// The same three words are the legacy <c>AchievementType</c> member names that the legacy listing printed verbatim (<c>MinecraftClient/Achievement.cs:8-15</c>, <c>Commands/AchievementCommand.cs:96-97</c>), so they are enum identifiers rather than localizable prose, which is why they carry no <c>McStrings</c> key.
    /// <para>
    /// Legacy had a fourth member, <c>Legacy</c>, and it is deliberately absent here: it was never a frame off the wire.
    /// It tagged the pre-1.12 achievements that <c>Protocol18.CreateLegacyAchievement</c> (<c>Protocol18.cs:3711-3718</c>) synthesised from the Statistics packet, a path this stack has no equivalent of, so no advancement here can ever be of that kind.
    /// </para>
    /// </summary>
    private enum AdvancementFrame
    {
        Task = 0,
        Challenge = 1,
        Goal = 2,
    }

    /// <summary>
    /// The frame word for one advancement, or an empty string when the server sent no display block and so no frame at all.
    /// A frame number vanilla does not define (a future or modded value) prints as the number: that is what the server said, and picking a word for it would be a guess.
    /// Internal so <c>AchievementFrameTests</c> can pin the mapping without a live session.
    /// </summary>
    internal static string FrameName(int? frame)
    {
        if (frame is not int value)
            return string.Empty;

        var named = (AdvancementFrame)value;
        return Enum.IsDefined(named) ? named.ToString() : value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The human title for one advancement, or null when there is none to print (legacy then falls back to the raw id through the untitled <c>cmd.achievement.entry</c> form).
    /// <para>
    /// The server only sends a title inside the optional display block, and vanilla omits that block for every generated recipe advancement, so most of a real listing arrives with no title at all.
    /// When the display block is missing (or its title component was a translate key nothing resolved), the vanilla lang key is derived from the id the same way vanilla names its own advancements (<c>minecraft:story/mine_stone</c> -> <c>advancements.story.mine_stone.title</c>) and resolved through the shipped tables, exactly as <see cref="InventoryRendering.TypeName"/> resolves item ids.
    /// Recipe advancements have no such key, which is why they keep printing their id.
    /// </para>
    /// </summary>
    private static string? ResolveTitle(ITranslationSource translations, AdvancementInfo advancement)
    {
        string? vanillaKey = VanillaTitleKey(advancement.Id);

        // A translate component whose key resolved nowhere flattens to the key itself; that is not a title.
        if (advancement.Title is { Length: > 0 } title && !string.Equals(title, vanillaKey, StringComparison.Ordinal))
            return title;

        return vanillaKey is not null && TryTranslate(translations, vanillaKey, out string resolved) ? resolved : null;
    }

    /// <summary>
    /// The vanilla <c>advancements.&lt;path&gt;.title</c> key for an advancement id, or null when the id is not a vanilla one.
    /// Only the <c>minecraft</c> namespace uses that key shape, so a datapack id in another namespace must not borrow a vanilla title that happens to share its path.
    /// </summary>
    private static string? VanillaTitleKey(string id)
    {
        if (string.IsNullOrEmpty(id))
            return null;

        string path = id;
        int colon = id.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            if (!id.AsSpan(0, colon).SequenceEqual("minecraft"))
                return null;

            path = id[(colon + 1)..];
        }

        return path.Length == 0 ? null : $"advancements.{path.Replace('/', '.')}.title";
    }

    // A vanilla table returns the key itself when it has no entry, so "resolved" means "came back different" (the same test InventoryRendering.TryTranslate makes).
    private static bool TryTranslate(ITranslationSource translations, string key, out string value)
    {
        string resolved = Component.Translatable(key).ToPlainText(translations);
        if (string.IsNullOrEmpty(resolved) || string.Equals(resolved, key, StringComparison.Ordinal))
        {
            value = string.Empty;
            return false;
        }

        value = resolved;
        return true;
    }
}
