using System.Globalization;
using DMCBK.Core.Commands.ArgumentTypes;
using DMCBK.Core.Localization;
using Umpk;
using Umpk.Client;
using Umpk.Commands;
using Umpk.Game.Inventory;

namespace DMCBK.Core.Commands.Impl;

/// <summary>The <c>nameitem</c> command: rename the item in an open anvil via the rename action.</summary>
public sealed class NameItemCommand : CommandBase
{
    /// <inheritdoc/>
    public override string CmdName => "nameitem";

    /// <inheritdoc/>
    public override string CmdDesc => CommandStrings.NameItemDesc;

    /// <inheritdoc/>
    public override string CmdUsage => "nameitem <name>";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Inventory;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("<name>", "rename the item in an open anvil"),
    ];

    /// <inheritdoc/>
    public override CommandFeature RequiredFeatures => CommandFeature.Inventory;

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["nameitem Sharp Stick"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["inventory", "enchant"];

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .ThenArgument("name", Arguments.GreedyString(), h => h
                .Executes(ctx => Run(ctx.Source, ctx.GetArgument<string>("name"))))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private int Run(CommandContext ctx, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ctx.Result.Fail(CommandStrings.NameItemEmpty);

        if (!ctx.InventoryEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedInventory);

        if (!ctx.InSession)
            return ctx.Result.Fail(CommandText.NotConnected(ctx));

        OpenContainerSnapshot? container = ctx.Run(ct => ctx.Game.Inventory.GetOpenContainerAsync(ct));
        if (container is null)
            return ctx.Result.Fail(CommandStrings.NameItemNoAnvil);

        if (container.Slots.Count == 0 || container.Slots[0].IsEmpty)
            return ctx.Result.Fail(CommandStrings.NameItemFirstSlotEmpty);

        // UMPK carries the rename on both sides of the 1.13 boundary.
        // From 1.13 it is the dedicated rename_item packet, and on 1.8-1.12.2 it is the MC|ItemName plugin channel, which is what vanilla itself does there.
        // The send reports whether it went out and this branches on that report rather than assuming.
        return ctx.Run(ct => ctx.Game.Inventory.RenameItemAsync(name, ct))
            ? ctx.Result.Ok(CommandStrings.NameItemDone(name))
            : ctx.Result.Fail(CommandStrings.NameItemUnsupported);
    }
}
