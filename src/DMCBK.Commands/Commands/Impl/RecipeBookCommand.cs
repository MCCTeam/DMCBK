using DMCBK.Core.Commands.ArgumentTypes;
using DMCBK.Core.Localization;
using Umpk.Text;
using Umpk;
using Umpk.Client;
using Umpk.Commands;
using Umpk.Game.Inventory;

namespace DMCBK.Core.Commands.Impl;

/// <summary>
/// The <c>recipebook</c> command: enumerate the unlocked recipes from the recipe-book state, or place one into the open menu via the place-recipe action.
/// </summary>
public sealed class RecipeBookCommand : CommandBase
{
    /// <inheritdoc/>
    public override string CmdName => "recipebook";

    /// <inheritdoc/>
    public override string CmdDesc => CommandStrings.RecipeBookDesc;

    /// <inheritdoc/>
    public override string CmdUsage => "recipebook list | recipebook craft|craftall <recipe> | recipebook ui";

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Inventory;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("list", "list the recipes you have unlocked"),
        new("craft <recipe>", "place one recipe into the open menu"),
        new("craftall <recipe>", "place as many as fit"),
        new("ui", CommandStrings.RecipeUiUsage),
    ];

    /// <inheritdoc/>
    public override CommandFeature RequiredFeatures => CommandFeature.Inventory;

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["recipebook list", "recipebook craft minecraft:torch", "recipebook ui"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["inventory"];

    /// <inheritdoc/>
    public override string? ManTopic => "inventory";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .ThenLiteral("ui", h => h.Executes(ctx => Ui(ctx.Source)))
            .ThenLiteral("list", h => h.Executes(ctx => ListRecipes(ctx.Source)))
            .ThenLiteral("craft", h => h
                .ThenArgument("recipe", Arguments.GreedyString(), a => a
                    .Executes(ctx => Craft(ctx.Source, ctx.GetArgument<string>("recipe"), false))))
            .ThenLiteral("craftall", h => h
                .ThenArgument("recipe", Arguments.GreedyString(), a => a
                    .Executes(ctx => Craft(ctx.Source, ctx.GetArgument<string>("recipe"), true))))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private static int Ui(CommandContext ctx)
    {
        if (!ctx.InventoryEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedInventory);
        if (!ctx.InSession)
            return ctx.Result.Fail(CommandText.NotConnected(ctx));
        return ctx.Ui?.TryOpenRecipeBrowser() == true
            ? ctx.Result.Ok()
            : ctx.Result.Fail(CommandStrings.UiRequiresTui);
    }

    /// <summary>
    /// Renders a recipe-book snapshot as display lines.
    /// Truthfulness rule: an empty name/id list only means "nothing unlocked" when the snapshot also reports no opaque additions.
    /// On 1.21.2+ the wire sends additions as a nested recipe-display tree UMPK keeps opaque, so those are counted, not named, and the output has to say the list is incomplete rather than imply the book is empty.
    /// <para>
    /// The opaque number is a count of UNDECODED UPDATE FRAMES, not of recipes: UMPK increments it once per recipe_book_add frame with a non-empty payload.
    /// A normal 1.21.2+ join is one such frame carrying the whole book, so the text must not present the number as a recipe count.
    /// </para>
    /// </summary>
    /// <param name="book">The recipe-book snapshot.</param>
    /// <exception cref="ArgumentNullException"><paramref name="book"/> is null.</exception>
    public static IReadOnlyList<string> FormatList(RecipeBookSnapshot book)
        => FormatList(book, translations: null);

    /// <inheritdoc cref="FormatList(RecipeBookSnapshot)"/>
    /// <param name="book">The recipe-book snapshot.</param>
    /// <param name="translations">
    /// The vanilla table, used to name each recipe after what it makes.
    /// Null keeps the raw identifiers, which is what a caller with no session has.
    /// </param>
    public static IReadOnlyList<string> FormatList(RecipeBookSnapshot book, ITranslationSource? translations)
    {
        ArgumentNullException.ThrowIfNull(book);

        int named = book.Recipes.Count + book.RecipeIds.Count;
        if (named == 0)
        {
            return book.OpaqueAdditions > 0
                ? [CommandStrings.RecipeBookOpaqueOnly(book.OpaqueAdditions)]
                : [CommandStrings.RecipeBookNone];
        }

        var lines = new List<string>(named + 2) { CommandStrings.RecipeBookHeader(named) };

        // A recipe is named after what it makes, so the listing leads with that name rather than the wire id: "Acacia Boat", not "minecraft:acacia_boat".
        // InventoryRendering.TypeName is the same resolution the inventory listing uses, so an item reads identically wherever it appears.
        // The id stays on the row because it is what `recipebook craft` takes.
        var rows = new List<(string Name, string Id)>(book.Recipes.Count);
        foreach (string recipe in book.Recipes)
            rows.Add((translations is null ? recipe : InventoryRendering.TypeName(translations, recipe), recipe));

        rows.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));

        int pad = rows.Count == 0 ? 0 : rows.Max(r => r.Name.Length);
        foreach ((string name, string id) in rows)
        {
            lines.Add(translations is null
                ? CommandStrings.RecipeBookEntry(id)
                : CommandStrings.RecipeBookNamedEntry(name, id, pad));
        }

        var ids = new List<int>(book.RecipeIds);
        ids.Sort();
        foreach (int id in ids)
            lines.Add(CommandStrings.RecipeBookNumericEntry(id));

        if (book.OpaqueAdditions > 0)
            lines.Add(CommandStrings.RecipeBookOpaqueNote(book.OpaqueAdditions));

        return lines;
    }

    private int ListRecipes(CommandContext ctx)
    {
        if (!ctx.InventoryEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedInventory);

        if (!ctx.InSession)
            return ctx.Result.Fail(CommandText.NotConnected(ctx));

        RecipeBookSnapshot book = ctx.Run(ct => ctx.Game.Inventory.GetRecipeBookAsync(ct));
        IReadOnlyList<string> lines = FormatList(book, ctx.Translations);
        if (lines.Count == 1)
            return ctx.Result.Ok(lines[0]);

        foreach (string line in lines)
            ctx.Output.WriteLine(line);

        return ctx.Result.Set(CmdStatus.Done);
    }

    /// <summary>
    /// Turns a <see cref="Umpk.Client.RecipeCraftPlan"/> refusal into the command's wording.
    /// The wire form is a property of the VERSION and not of the shape of what the user typed, so a refusal names the version that refused: "could not place the recipe" does not tell anyone what to do next.
    /// </summary>
    internal static string DescribeRefusal(RecipeCraftRefusal refusal, RecipePlacementSupport placement, string typed) => refusal switch
    {
        RecipeCraftRefusal.NeedsNetworkId => CommandStrings.RecipeBookNeedsNetworkId(placement.Describe(), typed),
        RecipeCraftRefusal.NeedsResourceName => CommandStrings.RecipeBookNeedsResourceName(placement.Describe(), typed),
        RecipeCraftRefusal.VersionCannotPlace => CommandStrings.RecipeBookUnsupported(placement.Describe()),
        _ => CommandStrings.RecipeBookFail,
    };

    private int Craft(CommandContext ctx, string recipe, bool makeAll)
    {
        if (!ctx.InventoryEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedInventory);

        if (!ctx.InSession)
            return ctx.Result.Fail(CommandText.NotConnected(ctx));

        RecipePlacementOutcome outcome = ctx.Run(ct => ctx.Game.Inventory.PlaceRecipeAsync(recipe, makeAll, ct));
        return outcome.Kind switch
        {
            RecipePlacementOutcomeKind.Success => ctx.Result.Ok(CommandStrings.RecipeBookPlaced(outcome.Recipe)),
            RecipePlacementOutcomeKind.UnsupportedRecipeForm =>
                ctx.Result.Fail(DescribeRefusal(outcome.Refusal, outcome.Support, recipe.Trim())),
            RecipePlacementOutcomeKind.MissingMenu => ctx.Result.Fail(CommandStrings.RecipeBookNoMenu),
            _ => ctx.Result.Fail(CommandStrings.RecipeBookFail),
        };
    }
}
