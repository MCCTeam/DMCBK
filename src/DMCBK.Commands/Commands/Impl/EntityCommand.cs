using System.Globalization;
using System.Text;
using DMCBK.Core.Commands.ArgumentTypes;
using DMCBK.Core.Localization;
using Umpk;
using Umpk.Commands;
using Umpk.Game.Entities;
using Umpk.Game.Players;
using Umpk.Geometry;
using Umpk.Text;

namespace DMCBK.Core.Commands.Impl;

/// <summary>
/// The <c>entity</c> command, restored to the legacy grammar and output (MinecraftClient/Commands/Entitycmd.cs).
/// <para>
/// One deliberate deviation from legacy: <c>attack</c> only swings at targets the player can see (ahead of them and not behind blocks, the same test AutoAttack's <c>Only_In_Front</c> uses), because swinging at things you cannot see is what anti-cheat looks for.
/// <c>use</c> is unaffected: right-clicking something you named is harmless.
/// A trailing <c>-a</c> on an attack opts that run out (<c>entity zombie attack -a</c>), and listing/describing never filters.
/// </para>
/// <para>
/// The legacy command has THREE ways of selecting what to act on, and the first port kept only two of them: a bare id, a bare type, and the <c>near</c> prefix that turns either of those into "the closest one" (Entitycmd.cs:40-57).
/// <c>entity near &lt;id|type&gt; &lt;attack|use|list&gt;</c> did not parse at all and fell through to the server as chat.
/// </para>
/// <para>
/// The one asymmetry worth naming, because it looks like a bug and is not: a BARE <c>entity &lt;type&gt;</c> runs with <c>near: true</c> (Entitycmd.cs:67) and prints the closest matching entity in detail, while <c>entity &lt;type&gt; list</c> runs with <c>near: false</c> and lists every matching entity in short form.
/// Both shapes are reproduced here as they were.
/// </para>
/// </summary>
public sealed class EntityCommand : CommandBase
{
    /// <summary>What a parsed line does with the entity (or entities) it selected. Legacy's ActionType.</summary>
    private enum ActionType
    {
        /// <summary>Left-click the entity.</summary>
        Attack,

        /// <summary>Right-click the entity.</summary>
        Use,

        /// <summary>Print the entity instead of touching it.</summary>
        List,
    }

    // Legacy hangs four no-op children off "help entity" (Entitycmd.cs:27-34).
    // Each prints the same usage block; they exist so the lines PARSE, directly and through the "_help" redirect.
    private static readonly string[] HelpTopics = ["ui", "near", "attack", "use", "list"];

    // The equipment slots legacy printed, in legacy's order and under legacy's labels (Entitycmd.cs:266-277, which addressed them by the vanilla slot ordinals 0,1,5,4,3,2).
    // The keys are the names UMPK's EquipmentSlot enum stringifies to, which is how the snapshot dictionary is keyed.
    private static readonly (string Slot, string LabelKey)[] EquipmentOrder =
    [
        (nameof(EquipmentSlot.MainHand), "cmd.entityCmd.mainhand"),
        (nameof(EquipmentSlot.OffHand), "cmd.entityCmd.offhand"),
        (nameof(EquipmentSlot.Head), "cmd.entityCmd.helmet"),
        (nameof(EquipmentSlot.Chest), "cmd.entityCmd.chestplate"),
        (nameof(EquipmentSlot.Legs), "cmd.entityCmd.leggings"),
        (nameof(EquipmentSlot.Feet), "cmd.entityCmd.boots"),
    ];

    /// <inheritdoc/>
    public override string CmdName => "entity";

    /// <inheritdoc/>
    // Legacy's CmdDesc is the empty string (Entitycmd.cs:18) and the corpus has no cmd.entityCmd.desc key, so the usage header prints as the bare "/entity ..." line with no trailing ": description".
    public override string CmdDesc => CommandStrings.EntityDesc;

    /// <inheritdoc/>
    public override string CmdUsage => "entity [ui|near] <id|entitytype> <attack [-a]|use>";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Entities;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new(string.Empty, "list nearby entities"),
        new("<id|type>", "describe one entity"),
        new("<id|type> attack", "attack it if you can see it"),
        new("<id|type> attack -a", "attack it even when you cannot see it"),
        new("<id|type> use", "right-click it"),
        new("near <type>", "restrict to entities near you"),
        new("ui", CommandStrings.EntityUiUsage),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<UsageFlag> Flags =>
    [
        new("-a", "attack even when the target is hidden or behind you"),
    ];

    /// <inheritdoc/>
    public override CommandFeature RequiredFeatures => CommandFeature.Entity;

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["entity", "entity ui", "entity zombie attack", "entity 21339", "entity zombie attack -a"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["list", "tab"];

    /// <inheritdoc/>
    public override string? ManTopic => "entities";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        foreach (string topic in HelpTopics)
            help.ThenLiteral(topic, h => h.Executes(ctx => ShowUsage(ctx.Source)));

        builder.Literal(CmdName, l =>
        {
            l.Executes(ctx => GetFullEntityList(ctx.Source));
            l.ThenLiteral("ui", u => u.Executes(ctx => Ui(ctx.Source)));

            // "near" makes both selectors mean "the closest match" (Entitycmd.cs:40-57).
            l.ThenLiteral("near", n =>
            {
                n.Executes(ctx => GetClosestEntity(ctx.Source));
                n.ThenArgument("EntityID", Arguments.Integer(), a => RegisterIdActions(a));
                n.ThenArgument("EntityType", MccArguments.EntityType(), a => RegisterTypeActions(a, near: true));
            });

            // The id selector ignores "near" in legacy too: both paths call the same OperateWithId.
            l.ThenArgument("EntityID", Arguments.Integer(), a => RegisterIdActions(a));
            l.ThenArgument("EntityType", MccArguments.EntityType(), a => RegisterTypeActions(a, near: false));

            l.ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help));
        });
    }

    private static int Ui(CommandContext ctx)
    {
        if (!ctx.EntityEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedEntity);
        if (!ctx.InSession)
            return ctx.Result.Fail(CommandText.NotConnected(ctx));
        return ctx.Ui?.TryOpenEntityBrowser() == true
            ? ctx.Result.Ok()
            : ctx.Result.Fail(CommandStrings.UiRequiresTui);
    }

    /// <summary>The verbs legacy hangs off an id selector (Entitycmd.cs:42-49 and :58-65).</summary>
    private void RegisterIdActions(CommandNodeBuilder<CommandContext> node)
    {
        node.Executes(ctx => OperateWithId(ctx.Source, ctx.GetArgument<int>("EntityID"), ActionType.List));
        node.ThenLiteral("attack", h => h
            .Executes(ctx => OperateWithId(ctx.Source, ctx.GetArgument<int>("EntityID"), ActionType.Attack))
            .ThenLiteral("-a", f => f.Executes(
                ctx => OperateWithId(ctx.Source, ctx.GetArgument<int>("EntityID"), ActionType.Attack, allDirections: true))));
        node.ThenLiteral("use", h => h.Executes(
            ctx => OperateWithId(ctx.Source, ctx.GetArgument<int>("EntityID"), ActionType.Use)));
        node.ThenLiteral("list", h => h.Executes(
            ctx => OperateWithId(ctx.Source, ctx.GetArgument<int>("EntityID"), ActionType.List)));
    }

    /// <summary>
    /// The verbs legacy hangs off a type selector (Entitycmd.cs:50-57 and :66-73).
    /// The bare form is <c>near: true</c> in BOTH subtrees, which is why <paramref name="near"/> only reaches the verbs.
    /// </summary>
    private void RegisterTypeActions(CommandNodeBuilder<CommandContext> node, bool near)
    {
        node.Executes(ctx => OperateWithType(
            ctx.Source, true, ctx.GetArgument<Identifier>("EntityType"), ActionType.List));
        node.ThenLiteral("attack", h => h
            .Executes(ctx => OperateWithType(
                ctx.Source, near, ctx.GetArgument<Identifier>("EntityType"), ActionType.Attack))
            .ThenLiteral("-a", f => f.Executes(ctx => OperateWithType(
                ctx.Source, near, ctx.GetArgument<Identifier>("EntityType"), ActionType.Attack, allDirections: true))));
        node.ThenLiteral("use", h => h.Executes(ctx => OperateWithType(
            ctx.Source, near, ctx.GetArgument<Identifier>("EntityType"), ActionType.Use)));
        node.ThenLiteral("list", h => h.Executes(ctx => OperateWithType(
            ctx.Source, near, ctx.GetArgument<Identifier>("EntityType"), ActionType.List)));
    }

    /// <summary>Legacy's single precondition (Entitycmd.cs:97-98), plus the session check the facade needs.</summary>
    private int Gate(CommandContext ctx)
    {
        if (!ctx.EntityEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedEntity);

        if (!ctx.InSession)
            return ctx.Result.Fail(CommandText.NotConnected(ctx));

        return int.MinValue;
    }

    /// <summary>
    /// Bare <c>/entity</c> (Entitycmd.cs:94-109): the "Entities" header, one short line per tracked entity, then this command's own usage block.
    /// Printed even when nothing is tracked, as legacy did.
    /// </summary>
    private int GetFullEntityList(CommandContext ctx)
    {
        int gate = Gate(ctx);
        if (gate != int.MinValue)
            return gate;

        IReadOnlyList<EntitySnapshot> entities = ctx.Run(ct => ctx.Game.Entities.AllAsync(ct));
        WriteListing(ctx, entities);
        return ctx.Result.Set(CmdStatus.Done);
    }

    /// <summary><c>/entity near</c> (Entitycmd.cs:111-124): the closest tracked entity, in detail.</summary>
    private int GetClosestEntity(CommandContext ctx)
    {
        int gate = Gate(ctx);
        if (gate != int.MinValue)
            return gate;

        Vec3d self = ctx.Run(ct => ctx.Game.Movement.GetPoseAsync(ct)).Position;
        IReadOnlyList<EntitySnapshot> entities = ctx.Run(ct => ctx.Game.Entities.AllAsync(ct));
        if (Closest(entities, self) is not { } closest)
            return ctx.Result.Fail(McStrings.Get("cmd.entityCmd.not_found"));

        ctx.Output.WriteLine(DetailedInfo(ctx, closest, self));
        return ctx.Result.Set(CmdStatus.Done);
    }

    /// <summary><c>/entity [near] &lt;id&gt; [attack|use|list]</c> (Entitycmd.cs:126-139).</summary>
    private int OperateWithId(CommandContext ctx, int entityId, ActionType action, bool allDirections = false)
    {
        int gate = Gate(ctx);
        if (gate != int.MinValue)
            return gate;

        EntitySnapshot? entity = ctx.Run(ct => ctx.Game.Entities.ByIdAsync(entityId, ct));
        if (entity is null)
            return ctx.Result.Fail(McStrings.Get("cmd.entityCmd.not_found"));

        if (action == ActionType.Attack && !allDirections && !IsInFront(ctx, entity.Position))
            return ctx.Result.Fail(McStrings.Get("cmd.entityCmd.not_in_front"));

        if (action == ActionType.Attack && !allDirections && !IsVisible(ctx, entity.Position))
            return ctx.Result.Fail(McStrings.Get("cmd.entityCmd.not_visible"));

        return InteractionWithEntity(ctx, entity, action);
    }

    /// <summary>
    /// <c>/entity [near] &lt;type&gt; [attack|use|list]</c> (Entitycmd.cs:141-199).
    /// With <paramref name="near"/> it acts on the closest match; without it, <c>attack</c>/<c>use</c> act on EVERY match and report the count, and <c>list</c> prints every match in short form.
    /// <para>
    /// <c>attack</c> additionally keeps only the matches the player can see (in front of them and not behind blocks) unless <paramref name="allDirections"/> (the trailing <c>-a</c>) opts out.
    /// <c>use</c> and <c>list</c> never filter.
    /// </para>
    /// </summary>
    private int OperateWithType(
        CommandContext ctx, bool near, Identifier entityType, ActionType action, bool allDirections = false)
    {
        int gate = Gate(ctx);
        if (gate != int.MinValue)
            return gate;

        IReadOnlyList<EntitySnapshot> matches =
            ctx.Run(ct => ctx.Game.Entities.OfTypeAsync(entityType.ToString(), ct));

        if (action == ActionType.Attack && !allDirections)
        {
            PlayerPose self = ctx.Run(ct => ctx.Game.Movement.GetPoseAsync(ct));
            List<EntitySnapshot> ahead = matches.Where(e => IsInFront(self, e.Position)).ToList();
            if (matches.Count > 0 && ahead.Count == 0)
                return ctx.Result.Fail(McStrings.Get("cmd.entityCmd.not_in_front"));

            matches = ahead;
        }

        if (near)
        {
            Vec3d self = ctx.Run(ct => ctx.Game.Movement.GetPoseAsync(ct)).Position;
            if (Closest(matches, self) is not { } closest)
                return ctx.Result.Fail(McStrings.Get("cmd.entityCmd.not_found"));

            if (action == ActionType.Attack && !allDirections && !IsVisible(ctx, closest.Position))
                return ctx.Result.Fail(McStrings.Get("cmd.entityCmd.not_visible"));

            if (action != ActionType.List)
                return InteractionWithEntity(ctx, closest, action);

            ctx.Output.WriteLine(DetailedInfo(ctx, closest, self));
            return ctx.Result.Set(CmdStatus.Done);
        }

        if (action is ActionType.Attack or ActionType.Use)
        {
            // Legacy seeds the label with "attacked" and only rewrites it INSIDE the loop (Entitycmd.cs:161-180), so a "use" that matched nothing reports "0 Entity attacked".
            // Kept.
            string actionText = McStrings.Get("cmd.entityCmd.attacked");
            int count = 0;
            foreach (EntitySnapshot entity in matches)
            {
                if (action == ActionType.Attack)
                {
                    if (!allDirections && !IsVisible(ctx, entity.Position))
                        continue;

                    ctx.Run(ct => ctx.Game.Entities.AttackAsync(entity.Id, ct));
                    actionText = McStrings.Get("cmd.entityCmd.attacked");
                }
                else
                {
                    ctx.Run(ct => ctx.Game.Entities.InteractAsync(entity.Id, Umpk.Client.Actions.Hand.Main, ct));
                    actionText = McStrings.Get("cmd.entityCmd.used");
                }

                count++;
            }

            if (action == ActionType.Attack && !allDirections && count == 0 && matches.Count > 0)
                return ctx.Result.Fail(McStrings.Get("cmd.entityCmd.not_visible"));

            return ctx.Result.Ok(string.Create(CultureInfo.CurrentCulture, $"{count} {actionText}"));
        }

        WriteListing(ctx, matches);
        return ctx.Result.Set(CmdStatus.Done);
    }

    /// <summary>Legacy's InteractionWithEntity (Entitycmd.cs:309-328).</summary>
    private int InteractionWithEntity(CommandContext ctx, EntitySnapshot entity, ActionType action)
    {
        switch (action)
        {
            case ActionType.Attack:
                ctx.Run(ct => ctx.Game.Entities.AttackAsync(entity.Id, ct));
                return ctx.Result.Ok(McStrings.Get("cmd.entityCmd.attacked"));

            case ActionType.Use:
                // Legacy sent interact_at (rather than interact) for ArmorStand, ChestMinecart and ChestBoat.
                // UMPK's interaction surface has no interact-at action, so every use is a plain interact.
                ctx.Run(ct => ctx.Game.Entities.InteractAsync(entity.Id, Umpk.Client.Actions.Hand.Main, ct));
                return ctx.Result.Ok(McStrings.Get("cmd.entityCmd.used"));

            case ActionType.List:
            default:
                Vec3d self = ctx.Run(ct => ctx.Game.Movement.GetPoseAsync(ct)).Position;
                ctx.Output.WriteLine(DetailedInfo(ctx, entity, self));
                return ctx.Result.Set(CmdStatus.Done);
        }
    }

    /// <summary>
    /// The entity listing, as a Markdown document: a count, a tally by type, and a table sorted by distance.
    /// <para>
    /// Legacy printed one line per entity with the field names repeated on every one of them (Entitycmd.cs:101-106), in whatever order the tracker happened to hold, and then appended the whole usage block underneath.
    /// Sixty entities is a realistic count on an ordinary server, and sixty of those lines is not something a person reads: the ids are noise, the repeated "Type:" and "Location:" labels are noise, and nothing says which of them is near you, which is the only question anyone runs this to answer.
    /// </para>
    /// <para>
    /// A table sorted by distance answers it.
    /// It goes through <see cref="DocumentRendering"/>, so it is a bordered table in the classic console, aligned Consolonia text in the TUI, and aligned plain text anywhere else, from one description.
    /// </para>
    /// </summary>
    private static void WriteListing(CommandContext ctx, IReadOnlyList<EntitySnapshot> entities)
    {
        if (entities.Count == 0)
        {
            ctx.Output.WriteLine(CommandStrings.NothingToShow(
                ctx.Glyphs, McStrings.Get("cmd.entityCmd.empty_nearby")));
            return;
        }

        Vec3d self = ctx.Run(ct => ctx.Game.Movement.GetPoseAsync(ct)).Position;

        List<ListingRow> rows =
        [
            .. entities
                .Select(e => new ListingRow(
                    e.Id,
                    TypeName(ctx.Translations, e.TypeId),
                    RawName(e),
                    Distance(e.Position, self),
                    e.Position))
                .OrderBy(r => r.Distance)
        ];

        DocumentRendering.Write(ctx, BuildListingMarkdown(rows));
    }

    /// <summary>One row of the listing, resolved: no snapshot, no translations, no session.</summary>
    /// <param name="Id">The entity id.</param>
    /// <param name="Type">The resolved type name.</param>
    /// <param name="Name">A player or custom name, already escaped. Empty when there is none.</param>
    /// <param name="Distance">Distance from the player, in blocks.</param>
    /// <param name="Position">The entity's position.</param>
    internal readonly record struct ListingRow(
        int Id, string Type, string Name, double Distance, Vec3d Position);

    /// <summary>
    /// The listing as a Markdown document.
    /// Separated from the session so it can be tested directly: the column set depends on the data, and that decision is worth pinning.
    /// </summary>
    internal static string BuildListingMarkdown(IReadOnlyList<ListingRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        // The name column costs width on EVERY row, so it only exists when something actually has a name.
        // On a normal server nothing does, and an empty column is just a stripe of wasted width.
        bool named = rows.Any(r => !string.IsNullOrEmpty(r.Name));

        var sb = new StringBuilder();
        sb.Append("# ").Append(McStrings.Get("cmd.entityCmd.entities")).Append('\n').Append('\n');
        sb.Append(McStrings.Format("cmd.entityCmd.count", rows.Count)).Append('\n').Append('\n');
        sb.Append(Tally(rows)).Append('\n').Append('\n');

        sb.Append("| ").Append(McStrings.Get("cmd.entityCmd.column_id"))
          .Append(" | ").Append(McStrings.Get("cmd.entityCmd.type"));
        if (named)
            sb.Append(" | ").Append(McStrings.Get("cmd.entityCmd.nickname"));

        sb.Append(" | ").Append(McStrings.Get("cmd.entityCmd.column_distance"))
          .Append(" | ").Append(McStrings.Get("cmd.entityCmd.location")).Append(" |\n");

        // Left-aligned throughout.
        // Markdown's `---:` right-align marker is honoured differently by the three renderers this document passes through, which left the ID and Dist headers sitting over values aligned the other way.
        // One alignment everywhere beats a nicer one that holds in only one.
        sb.Append("| --- | --- |").Append(named ? " --- |" : string.Empty).Append(" --- | --- |\n");

        foreach (ListingRow row in rows)
        {
            sb.Append("| ").Append(row.Id.ToString(CultureInfo.InvariantCulture))
              .Append(" | ").Append(Cell(row.Type));

            if (named)
                sb.Append(" | ").Append(Cell(row.Name));

            sb.Append(" | ").Append(row.Distance.ToString("F1", CultureInfo.InvariantCulture))
              .Append(" | ").Append(Coordinates(row.Position)).Append(" |\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// The count per type, most common first.
    /// On a listing this long the tally is the part most people actually want: "12 Sheep, 10 Bat" answers "what is around me" without reading sixty rows.
    /// </summary>
    private static string Tally(IReadOnlyList<ListingRow> rows)
    {
        IEnumerable<string> parts = rows
            .GroupBy(r => r.Type, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Count()} {g.Key}"));

        return string.Join(", ", parts);
    }

    /// <summary>A player name, or a custom name, or nothing.</summary>
    private static string RawName(EntitySnapshot entity)
        => !string.IsNullOrEmpty(entity.PlayerName) ? entity.PlayerName : entity.CustomName ?? string.Empty;

    /// <summary>
    /// Makes server-controlled text safe to put in a table cell.
    /// </summary>
    /// <remarks>
    /// A custom name is whatever a player typed on a name tag.
    /// An unescaped pipe splits the row into extra columns and silently shifts every cell after it, so a player could corrupt the table by renaming a mob; a backtick or an asterisk would restyle the cell.
    /// Escaping happens HERE, in the renderer, rather than at the one call site that happened to build rows, so a row constructed any other way cannot skip it.
    /// </remarks>
    private static string Cell(string text)
        => text.Replace("|", "/", StringComparison.Ordinal)
               .Replace("`", "'", StringComparison.Ordinal)
               .Replace("*", "+", StringComparison.Ordinal)
               .Replace("\n", " ", StringComparison.Ordinal);

    /// <summary>
    /// A position, invariant.
    /// The old form used the current culture, so on a comma-decimal locale it read "X:127,25, Y:56,92, Z:287,66": three numbers that parse to the eye as six.
    /// </summary>
    private static string Coordinates(Vec3d position)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{position.X:F1} {position.Y:F1} {position.Z:F1}");

    private static double Distance(Vec3d a, Vec3d b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        double dz = a.Z - b.Z;
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    /// <summary>
    /// One listing line: legacy's GetEntityInfoShort (Entitycmd.cs:201-221), minus the two fields the UMPK snapshot does not carry.
    /// Health is not exposed for tracked entities at all, and the carried stack of an item/projectile entity is not either, which is what legacy's separate item-entity line shape existed to print; without it that shape and the general one are the same line, so only one is built.
    /// </summary>
    private static string ShortInfo(CommandContext ctx, EntitySnapshot entity, TabListSnapshot tabList)
    {
        string type = TypeName(ctx.Translations, entity.TypeId);
        string location = LocationText(entity.Position);
        string typeLabel = McStrings.Get("cmd.entityCmd.type");
        string locationLabel = McStrings.Get("cmd.entityCmd.location");

        if (IsPlayer(entity))
        {
            string latencyLabel = McStrings.Get("cmd.entityCmd.latency");
            string poseLabel = McStrings.Get("cmd.entityCmd.pose");
            int latency = Latency(tabList, entity);

            if (!string.IsNullOrEmpty(entity.PlayerName))
            {
                return string.Create(
                    CultureInfo.CurrentCulture,
                    $" #{entity.Id}: {typeLabel}: {type}, {McStrings.Get("cmd.entityCmd.nickname")}: §8{entity.PlayerName}§8, {latencyLabel}: {latency}, {poseLabel}: {entity.Pose}, {locationLabel}: {location}");
            }

            if (!string.IsNullOrEmpty(entity.CustomName))
            {
                return string.Create(
                    CultureInfo.CurrentCulture,
                    $" #{entity.Id}: {typeLabel}: {type}, {McStrings.Get("cmd.entityCmd.customname")}: §8{entity.CustomName.Replace('&', '§')}§8, {latencyLabel}: {latency}, {poseLabel}: {entity.Pose}, {locationLabel}: {location}");
            }
        }

        return string.Create(
            CultureInfo.CurrentCulture,
            $" #{entity.Id}: {typeLabel}: {type}, {locationLabel}: {location}");
    }

    /// <summary>
    /// The multi-line entity dump: legacy's GetEntityInfoDetailed (Entitycmd.cs:223-286).
    /// Legacy prefixed every continuation line with "[MCC]" to fake its logger's prefix onto lines the logger never saw; the new host prints command body text unprefixed, so only legacy's indentation is kept.
    /// </summary>
    private static string DetailedInfo(CommandContext ctx, EntitySnapshot entity, Vec3d selfPosition)
    {
        var sb = new StringBuilder();

        sb.Append(CultureInfo.CurrentCulture, $"{McStrings.Get("cmd.entityCmd.entity")}: {entity.Id}");
        sb.Append(CultureInfo.CurrentCulture,
            $"\n {McStrings.Get("cmd.entityCmd.type")}: {TypeName(ctx.Translations, entity.TypeId)}");

        if (!string.IsNullOrEmpty(entity.PlayerName))
        {
            sb.Append(CultureInfo.CurrentCulture,
                $"\n {McStrings.Get("cmd.entityCmd.nickname")}: {entity.PlayerName}");
        }
        else if (!string.IsNullOrEmpty(entity.CustomName))
        {
            sb.Append(CultureInfo.CurrentCulture,
                $"\n {McStrings.Get("cmd.entityCmd.customname")}: {entity.CustomName.Replace('&', '§')}§8");
        }

        if (IsPlayer(entity))
        {
            TabListSnapshot tabList = ctx.Run(ct => ctx.Game.Player.GetTabListAsync(ct));
            sb.Append(CultureInfo.CurrentCulture,
                $"\n {McStrings.Get("cmd.entityCmd.latency")}: {Latency(tabList, entity)}");
        }

        AppendEquipment(sb, ctx, entity);

        sb.Append(CultureInfo.CurrentCulture, $"\n {McStrings.Get("cmd.entityCmd.pose")}: {entity.Pose}");
        sb.Append(CultureInfo.CurrentCulture,
            $"\n {McStrings.Get("cmd.entityCmd.distance")}: {Distance(entity, selfPosition)}");
        sb.Append(CultureInfo.CurrentCulture,
            $"\n {McStrings.Get("cmd.entityCmd.location")}: {LocationText(entity.Position)}");

        return sb.ToString();
    }

    /// <summary>
    /// The equipment block (Entitycmd.cs:263-278).
    /// Legacy removed a slot's key when the server sent an empty stack for it, so an empty stack here is the same "not worn" and prints nothing; the header is only emitted once something is actually worn.
    /// </summary>
    private static void AppendEquipment(StringBuilder sb, CommandContext ctx, EntitySnapshot entity)
    {
        bool headerWritten = false;
        foreach ((string slot, string labelKey) in EquipmentOrder)
        {
            if (!entity.Equipment.TryGetValue(slot, out ItemStackInfo? stack) || stack is null || stack.IsEmpty)
                continue;

            if (!headerWritten)
            {
                sb.Append(CultureInfo.CurrentCulture, $"\n {McStrings.Get("cmd.entityCmd.equipment")}:");
                headerWritten = true;
            }

            sb.Append(CultureInfo.CurrentCulture,
                $"\n   {McStrings.Get(labelKey)}: {InventoryRendering.TypeName(ctx.Translations, stack.ItemId)} x{stack.Count}");
        }
    }

    /// <summary>
    /// True when <paramref name="target"/> (feet position) lies anywhere ahead of the player: the same facing-hemisphere test AutoAttack's <c>Only_In_Front</c> uses, over the live pose.
    /// </summary>
    private bool IsInFront(CommandContext ctx, Vec3d target)
        => IsInFront(ctx.Run(ct => ctx.Game.Movement.GetPoseAsync(ct)), target);

    /// <summary>
    /// True when the player's eye has an unblocked sight line to <paramref name="target"/> (feet position): the <c>WorldApi.HasLineOfSightAsync</c> half of the visibility gate, over the live world.
    /// The facing half is <see cref="IsInFront(CommandContext, Vec3d)"/>.
    /// </summary>
    private bool IsVisible(CommandContext ctx, Vec3d target)
        => ctx.Run(ct => ctx.Game.World.HasLineOfSightAsync(target, ct));

    /// <summary>
    /// True when <paramref name="target"/> (feet position) lies anywhere ahead of <paramref name="self"/>: the angle between the yaw/pitch look vector and the eye-to-target direction is under 90 degrees.
    /// A hemisphere, not the crosshair.
    /// Kept static and internal so the geometry pins directly in tests without a session.
    /// </summary>
    internal static bool IsInFront(PlayerPose self, Vec3d target)
        => EntityTargeting.IsInFront(self, target);

    /// <summary>Legacy's TryGetClosetEntity (Entitycmd.cs:288-307): nearest by distance, no radius cap.</summary>
    private static EntitySnapshot? Closest(IReadOnlyList<EntitySnapshot> entities, Vec3d location)
    {
        EntitySnapshot? closest = null;
        double closestDistance = double.PositiveInfinity;
        foreach (EntitySnapshot entity in entities)
        {
            double distance = Distance(entity, location);
            if (distance < closestDistance)
            {
                closest = entity;
                closestDistance = distance;
            }
        }

        return closest;
    }

    /// <summary>Legacy rounds to two decimals before both comparing and printing (Entitycmd.cs:234, :298).</summary>
    private static double Distance(EntitySnapshot entity, Vec3d location)
        => Math.Round(entity.Position.Subtract(location).Length(), 2);

    private static bool IsPlayer(EntitySnapshot entity)
        => string.Equals(entity.TypeId, "minecraft:player", StringComparison.Ordinal);

    /// <summary>
    /// The player's ping.
    /// Legacy carried it on the entity itself, filled from the player list; here it is read back out of the tab list by uuid, and an entity with no list entry reports 0 exactly as an un-filled legacy entity did.
    /// </summary>
    private static int Latency(TabListSnapshot tabList, EntitySnapshot entity)
    {
        foreach (TabListEntryInfo entry in tabList.Entries)
        {
            if (entry.Uuid == entity.Uuid)
                return entry.Latency;
        }

        return 0;
    }

    private static string LocationText(Vec3d position)
        => string.Create(
            CultureInfo.CurrentCulture,
            $"X:{Math.Round(position.X, 2)}, Y:{Math.Round(position.Y, 2)}, Z:{Math.Round(position.Z, 2)}");

    /// <summary>
    /// The entity type name, resolved the way legacy did (Mapping/Entity.cs:167-172): the vanilla <c>entity.&lt;ns&gt;.&lt;path&gt;</c> lang entry when the loaded table has one, else the PascalCase form of the id, which is what legacy's enum name was.
    /// </summary>
    private static string TypeName(ITranslationSource translations, string typeId)
        => EntityPresentation.TypeName(translations, typeId);

    // The same fallback InventoryRendering applies to an unnamed item id, kept local because that copy is private to it and this file is the only other caller.
    private static string Pascal(string path)
    {
        var sb = new StringBuilder(path.Length);
        bool upper = true;
        foreach (char c in path)
        {
            if (c == '_')
            {
                upper = true;
                continue;
            }

            sb.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }

        return sb.ToString();
    }
}
