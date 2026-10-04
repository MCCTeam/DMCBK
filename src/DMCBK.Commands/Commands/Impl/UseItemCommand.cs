using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using DMCBK.Core.Commands.ArgumentTypes;
using DMCBK.Core.Localization;
using Umpk.Client.Actions;
using Umpk.Client.Snapshots;
using Umpk.Commands;
using Umpk.Game.Players;
using Umpk.Geometry;

namespace DMCBK.Core.Commands.Impl;

/// <summary>
/// The <c>useitem</c> command: use the held item, in the air, on the block you are looking at, or at a coordinate.
///
/// <para>
/// The bare form is the legacy one and it is not a plain "use in the air": with no hand named it prefers an off-hand food over a non-food main hand, and otherwise it uses the item ON the block in front of it when the view raycast finds one (UseItem.cs:70-95).
/// Both branches swing the arm, as legacy did.
/// </para>
/// </summary>
public sealed class UseItemCommand : CommandBase
{
    private const int OffhandSlot = 45;
    private const int FirstHotbarSlot = 36;

    /// <summary>
    /// The food item ids, ported from legacy <c>ItemTypeExtensions.IsFood()</c> (ItemTypeExtensions.cs:8-42).
    /// The list is legacy's own; UMPK carries no food metadata a decoded stack could be asked for (the <c>minecraft:food</c> component is an item DEFAULT, and a decoded stack's component map has no prototype, so it never appears on the wire).
    /// </summary>
    private static readonly FrozenSet<string> FoodItems = new[]
    {
        "minecraft:apple",
        "minecraft:baked_potato",
        "minecraft:beef",
        "minecraft:beetroot",
        "minecraft:bread",
        "minecraft:carrot",
        "minecraft:chicken",
        "minecraft:cod",
        "minecraft:cooked_beef",
        "minecraft:cooked_chicken",
        "minecraft:cooked_cod",
        "minecraft:cooked_mutton",
        "minecraft:cooked_porkchop",
        "minecraft:cooked_rabbit",
        "minecraft:cooked_salmon",
        "minecraft:cookie",
        "minecraft:dried_kelp",
        "minecraft:enchanted_golden_apple",
        "minecraft:golden_apple",
        "minecraft:golden_carrot",
        "minecraft:melon_slice",
        "minecraft:mutton",
        "minecraft:porkchop",
        "minecraft:potato",
        "minecraft:pumpkin_pie",
        "minecraft:rabbit",
        "minecraft:salmon",
        "minecraft:sweet_berries",
        "minecraft:tropical_fish",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <inheritdoc/>
    public override string CmdName => "useitem";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.useitem.desc");

    /// <inheritdoc/>
    public override string CmdUsage => "useitem [mainhand|offhand] | useitem [x] [y] [z] [mainhand|offhand]";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.World;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new(string.Empty, "use what you are holding"),
        new("mainhand", "use the main-hand item"),
        new("offhand", "use the off-hand item"),
        new("<x> <y> <z>", "use it on a block"),
    ];

    /// <inheritdoc/>
    public override CommandFeature RequiredFeatures => CommandFeature.Inventory;

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["useitem", "useitem offhand"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["useblock", "changeslot"];

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .Executes(ctx => UseHand(ctx.Source, null))
            .ThenLiteral("mainhand", h => h.Executes(ctx => UseHand(ctx.Source, Hand.Main)))
            .ThenLiteral("offhand", h => h.Executes(ctx => UseHand(ctx.Source, Hand.Off)))
            .ThenArgument("Location", DmcbkArguments.Location(), h => h
                .Executes(ctx => UseAt(ctx.Source, ctx.GetArgument<CommandLocation>("Location"), Hand.Main))
                .ThenLiteral("mainhand", a => a.Executes(ctx => UseAt(ctx.Source, ctx.GetArgument<CommandLocation>("Location"), Hand.Main)))
                .ThenLiteral("offhand", a => a.Executes(ctx => UseAt(ctx.Source, ctx.GetArgument<CommandLocation>("Location"), Hand.Off))))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private int UseHand(CommandContext ctx, Hand? requested)
    {
        if (!ctx.InventoryEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedInventory);

        if (!ctx.InSession)
            return ctx.Result.Fail(WorldCommandText.NotConnected(ctx));

        Hand hand = requested ?? (ShouldUseOffhandFood(ctx) ? Hand.Off : Hand.Main);
        bool useOffhandFood = requested is null && hand == Hand.Off;

        if (!useOffhandFood && ctx.TerrainEnabled)
        {
            BlockRaycastHit? hit = ctx.Run(ct => ctx.Game.World.RaycastAsync(4.5, false, ct));
            if (hit is { } h)
            {
                UseOnBlock(ctx, h.Position, hand);
                return ctx.Result.Ok(McStrings.Get("cmd.useitem.use"));
            }
        }

        ctx.Run(ct => ctx.Game.World.UseItemAsync(hand, ct));
        return ctx.Result.Ok(McStrings.Get("cmd.useitem.use"));
    }

    private int UseAt(CommandContext ctx, CommandLocation location, Hand hand)
    {
        if (!ctx.TerrainEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedTerrain);

        if (!ctx.InSession)
            return ctx.Result.Fail(WorldCommandText.NotConnected(ctx));

        PlayerPose pose = ctx.Run(ct => ctx.Game.Movement.GetPoseAsync(ct));
        UseOnBlock(ctx, BlockPos.Containing(location.ToAbsolute(pose.Position, pose.Yaw, pose.Pitch)), hand);
        return ctx.Result.Ok(McStrings.Get("cmd.useitem.use"));
    }

    /// <summary>
    /// Legacy UseItem.cs:87-89 and :105-107: look at the block, place/use against its top face, swing.
    /// The face is <see cref="Direction.Up"/> in both legacy call sites regardless of where the player stands, so the cursor is the top-face centre.
    /// </summary>
    private static void UseOnBlock(CommandContext ctx, BlockPos block, Hand hand)
    {
        ctx.Run(ct => ctx.Game.Movement.LookAtAsync(WorldCommandText.BlockCenter(block), ct));
        ctx.Run(ct => ctx.Game.World.PlaceBlockAsync(
            block, Direction.Up, WorldCommandText.FaceHitCursor(Direction.Up), hand, ct));
        ctx.Run(ct => ctx.Game.Movement.SwingAsync(hand == Hand.Main, ct));
    }

    /// <summary>
    /// Legacy UseItem.cs:59-72: with no hand named, an off-hand food beats a main hand that is not food.
    /// </summary>
    private static bool ShouldUseOffhandFood(CommandContext ctx)
    {
        DMCBK.Core.PlayerInventorySnapshot inventory = ctx.Run(ct => ctx.Game.Inventory.GetPlayerInventoryAsync(ct));
        if (inventory.Slots.Count <= OffhandSlot)
            return false;

        ItemStackInfo offhand = inventory.Slots[OffhandSlot];
        if (offhand.IsEmpty || !FoodItems.Contains(offhand.ItemId))
            return false;

        int mainHandSlot = FirstHotbarSlot + inventory.HeldSlot;
        if (mainHandSlot < 0 || mainHandSlot >= inventory.Slots.Count)
            return true;

        ItemStackInfo mainHand = inventory.Slots[mainHandSlot];
        return mainHand.IsEmpty || !FoodItems.Contains(mainHand.ItemId);
    }

    /// <summary>The hand name used in the report text.</summary>
    internal static string HandName(Hand hand) => hand == Hand.Off ? CommandStrings.HandOff : CommandStrings.HandMain;
}
