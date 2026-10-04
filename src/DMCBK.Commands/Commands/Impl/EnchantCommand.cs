using System.Globalization;
using DMCBK.Core.Commands.ArgumentTypes;
using DMCBK.Core.Localization;
using Umpk;
using Umpk.Client;
using Umpk.Commands;
using Umpk.Game.Inventory;

namespace DMCBK.Core.Commands.Impl;

/// <summary>The <c>enchant</c> command: click an enchant-table option via the container-button action.</summary>
public sealed class EnchantCommand : CommandBase
{
    /// <inheritdoc/>
    public override string CmdName => "enchant";

    /// <inheritdoc/>
    public override string CmdDesc => CommandStrings.EnchantDesc;

    /// <inheritdoc/>
    public override string CmdUsage => "enchant <top|middle|bottom>";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Inventory;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("top", "click the first enchantment option"),
        new("middle", "click the second"),
        new("bottom", "click the third"),
    ];

    /// <inheritdoc/>
    public override CommandFeature RequiredFeatures => CommandFeature.Inventory;

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["enchant top"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["inventory", "nameitem"];

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .ThenLiteral("top", h => h.Executes(ctx => Run(ctx.Source, 0)))
            .ThenLiteral("middle", h => h.Executes(ctx => Run(ctx.Source, 1)))
            .ThenLiteral("bottom", h => h.Executes(ctx => Run(ctx.Source, 2)))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private int Run(CommandContext ctx, int button)
    {
        if (!ctx.InventoryEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedInventory);

        if (!ctx.InSession)
            return ctx.Result.Fail(CommandText.NotConnected(ctx));

        OpenContainerSnapshot? container = ctx.Run(ct => ctx.Game.Inventory.GetOpenContainerAsync(ct));
        if (container is null)
            return ctx.Result.Fail(CommandStrings.EnchantNoTable);

        ctx.Run(ct => ctx.Game.Inventory.ClickContainerButtonAsync(button, container.WindowId, ct));
        return ctx.Result.Ok(CommandStrings.EnchantDone);
    }
}
